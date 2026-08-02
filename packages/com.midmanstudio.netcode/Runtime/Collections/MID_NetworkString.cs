// MID_NetworkString — ergonomic networked string for NetworkVariable /
// MID_NetworkDictionary, where TValue must be unmanaged and a plain C# string
// can't be used directly.
//
// WHY NOT NetworkVariable<string> DIRECTLY:
// Unity's own docs are explicit about this — string is a supported managed
// type in principle, but every Value assignment on the receiving end would
// have to allocate a new managed string to deserialize into (strings are
// immutable, so there's no in-place update), which is a GC allocation on
// every single sync. Their own recommendation is to use one of the
// Unity.Collections.FixedString value types instead — FixedString32Bytes,
// FixedString64Bytes, FixedString128Bytes, FixedString512Bytes,
// FixedString4096Bytes — which serialize "intelligently": only the bytes
// actually in use go over the wire, not the full fixed capacity, and there's
// no allocation since the whole thing is a blittable value type.
// (https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.5/manual/basics/networkvariable.html)
//
// WHY THIS ISN'T JUST "a struct wrapping a FixedString128Bytes field":
// If MID_NetworkString were a plain unmanaged struct with a FixedString128Bytes
// field and nothing else, NetworkVariable's default blittable-value-type
// fast path would very likely just memcpy the whole struct — meaning the
// full 128-byte capacity every time, every sync, regardless of how short the
// actual string is. FixedString's own smart serialization (send only the used
// length) is a SPECIAL CASE Netcode's serializer has for the FixedString
// types specifically, not something a wrapper struct inherits automatically
// by containing one as a field. Implementing INetworkSerializable and
// explicitly calling serializer.SerializeValue(ref _value) on the inner
// FixedString restores that special-case handling — the wrapper delegates
// to it instead of silently falling back to a raw memcpy.
//
// VERSION NOTE — worth checking before relying on this: this package pins
// com.unity.netcode.gameobjects to 1.7.1. There's a confirmed upstream
// regression (NullReferenceException in FixedStringSerializer.WriteDelta on
// NetworkVariable<FixedString64Bytes>) reported between 1.6.0 (working) and
// 1.10.0 (broken), fixed in 1.11.0
// (https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/issues/3018).
// Whether 1.7.1 specifically has it isn't confirmed either way in that
// report — worth an actual sync test on this exact pinned version before
// shipping anything that leans on this, and bumping to 1.11.0+ if you hit
// the same NullReferenceException on WriteDelta.
//
// Default capacity is 128 bytes — enough for names/labels/short chat lines.
// For longer text, just use NetworkVariable<FixedString512Bytes> or
// FixedString4096Bytes directly; this wrapper is for the common short-string
// case where the implicit string conversion is worth having.
//
// USAGE:
//   private readonly NetworkVariable<MID_NetworkString> _displayName = new(
//       new MID_NetworkString("Player"),
//       NetworkVariableReadPermission.Everyone,
//       NetworkVariableWritePermission.Owner);
//
//   _displayName.Value = "New Name";              // implicit string -> MID_NetworkString
//   string current = _displayName.Value;          // implicit MID_NetworkString -> string
//
//   // Also usable as MID_NetworkDictionary's TValue, since it's unmanaged:
//   private readonly MID_NetworkDictionary<ulong, MID_NetworkString> _playerNames = new();

using System;
using Unity.Collections;
using Unity.Netcode;

namespace MidManStudio.Netcode.Collections
{
    [Serializable]
    public struct MID_NetworkString : INetworkSerializable, IEquatable<MID_NetworkString>
    {
        private FixedString128Bytes _value;

        public MID_NetworkString(string value)
        {
            _value = default;
            CopyFromTruncated(value);
        }

        public int Length => _value.Length;
        public int Capacity => _value.Capacity;

        /// <summary>
        /// True if the string passed in was longer than the 128-byte capacity
        /// and got truncated to fit. Check this after construction/assignment
        /// if silent truncation would be a problem for your use case (e.g. a
        /// chat message vs. a display name where a couple of clipped
        /// characters don't matter).
        /// </summary>
        public bool WasTruncated { get; private set; }

        private void CopyFromTruncated(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _value = default;
                WasTruncated = false;
                return;
            }

            // CopyFromTruncated's return type changed across com.unity.collections
            // versions — void in 1.4.0 (when it was added), CopyError from ~2.1.4
            // onward. This package pins collections to 2.2.1 (see package.json),
            // where it returns CopyError — if that dependency version ever drops
            // below ~2.0, this line stops compiling and needs to go back to a
            // plain `_value.CopyFromTruncated(value); WasTruncated = false;` (no
            // error signal available in the older signature).
            var result = _value.CopyFromTruncated(value);
            WasTruncated = result != CopyError.None;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            // Delegates to FixedString128Bytes's own serialization — this is
            // the actual point of this class. See the file header for why a
            // plain unmanaged wrapper struct wouldn't get this for free.
            serializer.SerializeValue(ref _value);
        }

        public override string ToString() => _value.ToString();

        public bool Equals(MID_NetworkString other) => _value.Equals(other._value);
        public override bool Equals(object obj) => obj is MID_NetworkString other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();

        public static implicit operator MID_NetworkString(string value) => new MID_NetworkString(value);
        public static implicit operator string(MID_NetworkString value) => value.ToString();

        public static bool operator ==(MID_NetworkString left, MID_NetworkString right) => left.Equals(right);
        public static bool operator !=(MID_NetworkString left, MID_NetworkString right) => !left.Equals(right);
    }
}
