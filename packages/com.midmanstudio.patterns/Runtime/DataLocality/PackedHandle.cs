// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/data-locality.md, section "PackedHandle.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.DataLocality
{
    /// <summary>
    /// Stable reference to an item in a <see cref="PackedList{T}"/>. It stays valid while the item moves
    /// inside the list, and becomes invalid once the item is removed, even if its slot is reused.
    /// <c>default</c> is never valid.
    /// </summary>
    public struct PackedHandle : IEquatable<PackedHandle>
    {
        internal readonly int Slot;
        internal readonly int Generation;

        internal PackedHandle(int slot, int generation)
        {
            Slot = slot;
            Generation = generation;
        }

        /// <summary>False for <c>default</c>. A true value does not mean the item still exists; ask the list.</summary>
        public bool IsAssigned => Generation != 0;

        public bool Equals(PackedHandle other) { return Slot == other.Slot && Generation == other.Generation; }
        public override bool Equals(object obj) { return obj is PackedHandle && Equals((PackedHandle)obj); }
        public override int GetHashCode() { unchecked { return (Slot * 397) ^ Generation; } }
        public static bool operator ==(PackedHandle a, PackedHandle b) { return a.Equals(b); }
        public static bool operator !=(PackedHandle a, PackedHandle b) { return !a.Equals(b); }
        public override string ToString() { return IsAssigned ? "PackedHandle(" + Slot + ":" + Generation + ")" : "PackedHandle(none)"; }
    }
}
