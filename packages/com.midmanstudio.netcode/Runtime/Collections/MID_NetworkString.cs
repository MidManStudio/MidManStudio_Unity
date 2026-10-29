// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.netcode.md, section "MID_NetworkString.cs"
// ============================================================================

// MID_NetworkString<T> - a NetworkVariable-friendly string whose capacity is
// chosen by T, one of Unity's FixedString types:
//
//   MID_NetworkString<FixedString32Bytes>    - 29 usable bytes
//   MID_NetworkString<FixedString64Bytes>    - 61 usable bytes
//   MID_NetworkString<FixedString128Bytes>   - 125 usable bytes
//   MID_NetworkString<FixedString512Bytes>   - 509 usable bytes
//   MID_NetworkString<FixedString4096Bytes>  - 4093 usable bytes
//
// Usable BYTES, not characters: multi-byte UTF-8 characters use more than one,
// so the numbers are worst-case-ASCII character counts.
//
// One generic wrapper covers every size because all FixedString types implement
// INativeList<byte> and IUTF8Bytes, and BufferSerializer<T>.SerializeValue has an
// overload constrained to exactly those two interfaces, so every size takes the
// same path that sends only the used bytes:
//
//   public void SerializeValue<T>(ref T value, FastBufferWriter.ForFixedStrings unused = default)
//       where T : unmanaged, INativeList<byte>, IUTF8Bytes
//
// There is no growable option: a FixedString is a fixed buffer inside the
// struct, which is what keeps it blittable and allocation free. Unbounded text
// as continuously synced state would need Unity.Collections.NativeList<byte>,
// which the owner allocates and disposes.
//
// USAGE:
//   private readonly NetworkVariable<MID_NetworkString<FixedString64Bytes>> _displayName = new(
//       new MID_NetworkString<FixedString64Bytes>("Player"),
//       NetworkVariableReadPermission.Everyone,
//       NetworkVariableWritePermission.Owner);
//
//   _displayName.Value = "New Name";   // implicit string -> MID_NetworkString<T>
//   string current = _displayName.Value;
//
//   // Also usable as MID_NetworkDictionary's TValue, since it's unmanaged:
//   private readonly MID_NetworkDictionary<ulong, MID_NetworkString<FixedString64Bytes>> _playerNames = new();

using System;
using Unity.Collections;
using Unity.Netcode;

namespace MidManStudio.Netcode.Collections
{
    public struct MID_NetworkString<T> : INetworkSerializable, IEquatable<MID_NetworkString<T>>
        where T : unmanaged, INativeList<byte>, IUTF8Bytes
    {
        private T _value;

        public MID_NetworkString(string value)
        {
            _value = default;
            WasTruncated = false;
            CopyFromTruncated(value);
        }

        public int Length => _value.Length;
        public int Capacity => _value.Capacity;

        /// <summary>
        /// True if the string passed in was longer than T's capacity and got
        /// truncated to fit. Check this after construction/assignment if
        /// silent truncation would be a problem for your use case.
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

            // Resolves to FixedStringMethods.CopyFromTruncated<T>(ref T, string), which
            // returns CopyError on com.unity.collections 2.2.1 (this package's pin) and
            // void on 1.4.0. Below ~2.0 this line stops compiling and must become
            // `_value.CopyFromTruncated(value); WasTruncated = false;`.
            var result = _value.CopyFromTruncated(value);
            WasTruncated = result != CopyError.None;
        }

        public void NetworkSerialize<TReaderWriter>(BufferSerializer<TReaderWriter> serializer)
            where TReaderWriter : IReaderWriter
        {
            // Delegates to T's own FixedString serialization (see the file header).
            serializer.SerializeValue(ref _value);
        }

        // _value.ToString() here is a constrained generic call — it correctly
        // dispatches to whichever concrete FixedString type's own ToString()
        // override at runtime (e.g. FixedString128Bytes's UTF8-decoding one),
        // without boxing, even though ToString() isn't part of the interface
        // constraint itself. Standard C# generics behavior for value types,
        // not something specific to Collections/Netcode.
        public override string ToString() => _value.ToString();

        // Byte-by-byte comparison via INativeList<byte>'s indexer rather than
        // relying on T also implementing IEquatable<T> — that's not part of
        // this class's constraint list, so this avoids assuming it's there.
        public bool Equals(MID_NetworkString<T> other)
        {
            if (_value.Length != other._value.Length) return false;
            for (int i = 0; i < _value.Length; i++)
            {
                if (_value[i] != other._value[i]) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is MID_NetworkString<T> other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();

        public static implicit operator MID_NetworkString<T>(string value) => new MID_NetworkString<T>(value);
        public static implicit operator string(MID_NetworkString<T> value) => value.ToString();

        public static bool operator ==(MID_NetworkString<T> left, MID_NetworkString<T> right) => left.Equals(right);
        public static bool operator !=(MID_NetworkString<T> left, MID_NetworkString<T> right) => !left.Equals(right);
    }

    /// <summary>
    /// Short static factories so call sites don't have to spell out the full
    /// generic FixedString type every time — C# doesn't allow generic type
    /// aliases via `using`, so this is the closest equivalent.
    /// </summary>
    public static class MID_NetworkString
    {
        public static MID_NetworkString<FixedString32Bytes> Short(string value) => new(value);
        public static MID_NetworkString<FixedString64Bytes> Small(string value) => new(value);
        public static MID_NetworkString<FixedString128Bytes> Default(string value) => new(value);
        public static MID_NetworkString<FixedString512Bytes> Long(string value) => new(value);
        public static MID_NetworkString<FixedString4096Bytes> VeryLong(string value) => new(value);
    }
}
