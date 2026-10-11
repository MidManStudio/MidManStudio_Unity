// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/data-locality.md, section "PackedList.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.DataLocality
{
    /// <summary>
    /// Items stored back to back in one array so a loop over them walks memory in order (good cache
    /// behaviour), with <see cref="PackedHandle"/> references that survive removals. Removing an item moves
    /// the last item into its place, so the array never has holes and item order is not stable.
    /// </summary>
    public sealed class PackedList<T>
    {
        private T[] _items;
        private int[] _slotOfItem;     // item index -> slot
        private int[] _itemOfSlot;     // slot -> item index, or -1 when the slot is free
        private int[] _generation;     // slot -> current generation (never 0)
        private int[] _freeSlots;
        private int _freeCount;
        private int _slotCount;
        private int _count;

        public PackedList(int capacity = 16)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new T[capacity];
            _slotOfItem = new int[capacity];
            _itemOfSlot = new int[capacity];
            _generation = new int[capacity];
            _freeSlots = new int[capacity];
        }

        public int Count => _count;

        /// <summary>
        /// The backing array for tight loops: valid indices are 0 to <see cref="Count"/> - 1. It is replaced
        /// when the list grows and items move when one is removed, so do not keep it across Add or Remove.
        /// </summary>
        public T[] Items => _items;

        public PackedHandle Add(T item)
        {
            if (_count == _items.Length) Grow();

            int slot;
            if (_freeCount > 0)
            {
                slot = _freeSlots[--_freeCount];
            }
            else
            {
                slot = _slotCount++;
                _generation[slot] = 1;
            }

            _items[_count] = item;
            _slotOfItem[_count] = slot;
            _itemOfSlot[slot] = _count;
            _count++;
            return new PackedHandle(slot, _generation[slot]);
        }

        public bool IsValid(PackedHandle handle) { return IndexOf(handle) >= 0; }

        /// <summary>Index of the item in <see cref="Items"/> right now, or -1 when the handle is not valid.</summary>
        public int IndexOf(PackedHandle handle)
        {
            if (handle.Generation == 0 || handle.Slot < 0 || handle.Slot >= _slotCount) return -1;
            if (_generation[handle.Slot] != handle.Generation) return -1;
            return _itemOfSlot[handle.Slot];
        }

        public bool TryGet(PackedHandle handle, out T item)
        {
            int index = IndexOf(handle);
            if (index < 0) { item = default(T); return false; }
            item = _items[index];
            return true;
        }

        /// <exception cref="ArgumentException">The handle is not valid.</exception>
        public T this[PackedHandle handle]
        {
            get { return _items[Require(handle)]; }
            set { _items[Require(handle)] = value; }
        }

        /// <summary>Removes the item. Returns false when the handle was not valid.</summary>
        public bool Remove(PackedHandle handle)
        {
            int index = IndexOf(handle);
            if (index < 0) return false;

            int slot = handle.Slot;
            int last = _count - 1;
            if (index != last)
            {
                _items[index] = _items[last];
                int movedSlot = _slotOfItem[last];
                _slotOfItem[index] = movedSlot;
                _itemOfSlot[movedSlot] = index;
            }
            _items[last] = default(T);
            _count--;

            _itemOfSlot[slot] = -1;
            ReleaseSlot(slot);
            return true;
        }

        /// <summary>Removes every item and invalidates every handle.</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                _items[i] = default(T);
                int slot = _slotOfItem[i];
                _itemOfSlot[slot] = -1;
                ReleaseSlot(slot);
            }
            _count = 0;
        }

        private int Require(PackedHandle handle)
        {
            int index = IndexOf(handle);
            if (index < 0) throw new ArgumentException("The handle is not valid. Its item was removed.");
            return index;
        }

        private void ReleaseSlot(int slot)
        {
            unchecked { _generation[slot]++; }
            if (_generation[slot] == 0) _generation[slot] = 1;
            _freeSlots[_freeCount++] = slot;
        }

        private void Grow()
        {
            int size = _items.Length * 2;
            Array.Resize(ref _items, size);
            Array.Resize(ref _slotOfItem, size);
            Array.Resize(ref _itemOfSlot, size);
            Array.Resize(ref _generation, size);
            Array.Resize(ref _freeSlots, size);
        }
    }
}
