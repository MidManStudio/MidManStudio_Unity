// MID_BitPacker — compact bit-packed storage for network sync.
//
// Two related tools in this file:
//
//   MID_BitMask32 / MID_BitMask64
//     Single-bit flag storage — "is slot N true or false". API is a direct
//     port of mid-math's storage mask family (Mid-D-Man/mid-engine,
//     crates/mid-math/src/storage/storage_mask.rs — BitMask8/16/32/64/128/256),
//     translated from Rust snake_case to C# PascalCase but otherwise the same
//     names and semantics: FromBits/ToBits, FromIndices, Get/Set/Clear/Toggle,
//     Any/All/None, CountOnes/CountZeros, Intersection/Union/Difference/
//     SymmetricDifference, Matches (ECS-style "has all required bits"),
//     IsDisjoint/IsSubsetOf, IterOnes. Only 32/64-bit widths are included
//     here; port BitMask128/256 the same way (two/four ulong words +
//     WideIterOnes, same macro shape as the Rust file) if a wider flag set is
//     ever needed.
//
//   MID_BitPacker64 + MID_BitFieldLayout
//     What was actually asked for: pack several small MULTI-BIT fields —
//     enums, small ints, bools — into a single ulong instead of syncing each
//     one as its own NetworkVariable. A NetworkVariable<T> has fixed overhead
//     per variable (dirty tracking, header, delta message) regardless of how
//     small T is, so five 3-bit enums as five separate NetworkVariable<byte>
//     costs five times that overhead for 15 bits of actual data. Packed into
//     one MID_BitPacker64, it's one NetworkVariable, one ulong on the wire,
//     one dirty check.
//
// USAGE — packing a handful of enums into one synced ulong:
//
//   public enum WeaponState : byte { Idle, Firing, Reloading, Jammed }
//   public enum AmmoType    : byte { Standard, Explosive, Incendiary, EMP, Tracer }
//
//   // 1. Build the layout ONCE (static/shared) — order doesn't matter, just
//   //    reserve every field you need. Widths are computed automatically.
//   private static readonly MID_BitFieldLayout Layout = new MID_BitFieldLayout();
//   private static readonly MID_BitField StateField = Layout.ReserveEnum<WeaponState>();
//   private static readonly MID_BitField AmmoField  = Layout.ReserveEnum<AmmoType>();
//   private static readonly MID_BitField ChargeField = Layout.Reserve(7); // 0-127 raw
//   private static readonly MID_BitField JammedFlag  = Layout.ReserveBool();
//
//   // 2. One NetworkVariable for the whole packed set:
//   private readonly NetworkVariable<MID_BitPacker64> n_Packed = new();
//
//   // 3. Write (server) — round-trips through a local copy, one assignment:
//   var packed = n_Packed.Value;
//   packed.SetEnum(StateField, WeaponState.Reloading);
//   packed.SetEnum(AmmoField, AmmoType.Incendiary);
//   packed.SetField(ChargeField, 42);
//   packed.SetBool(JammedFlag, false);
//   n_Packed.Value = packed; // single NetworkVariable write → single delta
//
//   // 4. Read (any peer) — after net sync, retrieve each field back out:
//   var state  = n_Packed.Value.GetEnum<WeaponState>(StateField);
//   var ammo   = n_Packed.Value.GetEnum<AmmoType>(AmmoField);
//   var charge = n_Packed.Value.GetField(ChargeField);
//
// MID_BitFieldLayout.Reserve throws if the total exceeds 64 bits — build the
// layout once at startup (e.g. a static readonly block) so a config mistake
// surfaces immediately instead of silently truncating a field at runtime.

using System;
using System.Collections.Generic;
using Unity.Netcode;

namespace MidManStudio.Netcode.Collections
{
    // ─────────────────────────────────────────────────────────────────────────
    //  MID_BitMask32 / MID_BitMask64 — single-bit flag storage
    //  (port of mid-math's storage_mask.rs BitMask family)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 32 single-bit flag slots packed into a uint. Direct API port of
    /// mid-math's BitMask32 (Mid-D-Man/mid-engine, storage/storage_mask.rs).
    /// Implements INetworkSerializable — safe to use directly as a
    /// NetworkVariable&lt;MID_BitMask32&gt; value.
    /// </summary>
    [Serializable]
    public struct MID_BitMask32 : INetworkSerializable, IEquatable<MID_BitMask32>
    {
        public const int CAPACITY = 32;
        public static readonly MID_BitMask32 NONE = new MID_BitMask32(0u);
        public static readonly MID_BitMask32 ALL  = new MID_BitMask32(uint.MaxValue);

        private uint _bits;

        public MID_BitMask32(uint bits) => _bits = bits;

        public static MID_BitMask32 FromBits(uint bits) => new MID_BitMask32(bits);
        public uint ToBits() => _bits;

        /// <summary>Build a mask with exactly the listed bit positions set (0-based). Indices ≥ CAPACITY are ignored.</summary>
        public static MID_BitMask32 FromIndices(IEnumerable<int> indices)
        {
            uint v = 0;
            foreach (var i in indices)
                if (i >= 0 && i < CAPACITY) v |= 1u << i;
            return new MID_BitMask32(v);
        }

        public bool Get(int index)
        {
            CheckRange(index);
            return ((_bits >> index) & 1u) != 0;
        }

        public void Set(int index)   { CheckRange(index); _bits |= 1u << index; }
        public void Clear(int index) { CheckRange(index); _bits &= ~(1u << index); }
        public void Toggle(int index){ CheckRange(index); _bits ^= 1u << index; }

        public bool Any()  => _bits != 0;
        public bool All()  => _bits == uint.MaxValue;
        public bool None() => _bits == 0;

        public int CountOnes()  { uint v = _bits; int c = 0; while (v != 0) { v &= v - 1; c++; } return c; }
        public int CountZeros() => CAPACITY - CountOnes();

        public MID_BitMask32 Intersection(MID_BitMask32 other) => new MID_BitMask32(_bits & other._bits);
        public MID_BitMask32 Union(MID_BitMask32 other)        => new MID_BitMask32(_bits | other._bits);
        public MID_BitMask32 Difference(MID_BitMask32 other)   => new MID_BitMask32(_bits & ~other._bits);
        public MID_BitMask32 SymmetricDifference(MID_BitMask32 other) => new MID_BitMask32(_bits ^ other._bits);

        /// <summary>ECS-style archetype check: true when every bit set in <paramref name="required"/> is also set in this mask.</summary>
        public bool Matches(MID_BitMask32 required) => (_bits & required._bits) == required._bits;
        public bool IsDisjoint(MID_BitMask32 other) => (_bits & other._bits) == 0;
        public bool IsSubsetOf(MID_BitMask32 other) => other.Matches(this);

        /// <summary>Indices of all set bits, ascending.</summary>
        public IEnumerable<int> IterOnes()
        {
            uint v = _bits;
            while (v != 0)
            {
                yield return TrailingZeroCount(v);
                v &= v - 1;
            }
        }

        private static int TrailingZeroCount(uint v)
        {
            if (v == 0) return 32;
            int n = 0;
            while ((v & 1u) == 0) { v >>= 1; n++; }
            return n;
        }

        private static void CheckRange(int index)
        {
            if ((uint)index >= CAPACITY)
                throw new ArgumentOutOfRangeException(nameof(index), $"BitMask32 index {index} out of range (capacity {CAPACITY}).");
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            => serializer.SerializeValue(ref _bits);

        public bool Equals(MID_BitMask32 other) => _bits == other._bits;
        public override bool Equals(object obj) => obj is MID_BitMask32 o && Equals(o);
        public override int GetHashCode() => (int)_bits;
        public static bool operator ==(MID_BitMask32 a, MID_BitMask32 b) => a._bits == b._bits;
        public static bool operator !=(MID_BitMask32 a, MID_BitMask32 b) => a._bits != b._bits;
        public static MID_BitMask32 operator &(MID_BitMask32 a, MID_BitMask32 b) => a.Intersection(b);
        public static MID_BitMask32 operator |(MID_BitMask32 a, MID_BitMask32 b) => a.Union(b);
        public static MID_BitMask32 operator ^(MID_BitMask32 a, MID_BitMask32 b) => a.SymmetricDifference(b);
        public static MID_BitMask32 operator ~(MID_BitMask32 a) => new MID_BitMask32(~a._bits);

        public override string ToString() => "0b" + Convert.ToString(_bits, 2).PadLeft(CAPACITY, '0');
    }

    /// <summary>
    /// 64 single-bit flag slots packed into a ulong. Direct API port of
    /// mid-math's BitMask64 (Mid-D-Man/mid-engine, storage/storage_mask.rs).
    /// Implements INetworkSerializable — safe to use directly as a
    /// NetworkVariable&lt;MID_BitMask64&gt; value.
    /// </summary>
    [Serializable]
    public struct MID_BitMask64 : INetworkSerializable, IEquatable<MID_BitMask64>
    {
        public const int CAPACITY = 64;
        public static readonly MID_BitMask64 NONE = new MID_BitMask64(0UL);
        public static readonly MID_BitMask64 ALL  = new MID_BitMask64(ulong.MaxValue);

        private ulong _bits;

        public MID_BitMask64(ulong bits) => _bits = bits;

        public static MID_BitMask64 FromBits(ulong bits) => new MID_BitMask64(bits);
        public ulong ToBits() => _bits;

        public static MID_BitMask64 FromIndices(IEnumerable<int> indices)
        {
            ulong v = 0;
            foreach (var i in indices)
                if (i >= 0 && i < CAPACITY) v |= 1UL << i;
            return new MID_BitMask64(v);
        }

        public bool Get(int index)
        {
            CheckRange(index);
            return ((_bits >> index) & 1UL) != 0;
        }

        public void Set(int index)   { CheckRange(index); _bits |= 1UL << index; }
        public void Clear(int index) { CheckRange(index); _bits &= ~(1UL << index); }
        public void Toggle(int index){ CheckRange(index); _bits ^= 1UL << index; }

        public bool Any()  => _bits != 0;
        public bool All()  => _bits == ulong.MaxValue;
        public bool None() => _bits == 0;

        public int CountOnes()  { ulong v = _bits; int c = 0; while (v != 0) { v &= v - 1; c++; } return c; }
        public int CountZeros() => CAPACITY - CountOnes();

        public MID_BitMask64 Intersection(MID_BitMask64 other) => new MID_BitMask64(_bits & other._bits);
        public MID_BitMask64 Union(MID_BitMask64 other)        => new MID_BitMask64(_bits | other._bits);
        public MID_BitMask64 Difference(MID_BitMask64 other)   => new MID_BitMask64(_bits & ~other._bits);
        public MID_BitMask64 SymmetricDifference(MID_BitMask64 other) => new MID_BitMask64(_bits ^ other._bits);

        public bool Matches(MID_BitMask64 required) => (_bits & required._bits) == required._bits;
        public bool IsDisjoint(MID_BitMask64 other) => (_bits & other._bits) == 0;
        public bool IsSubsetOf(MID_BitMask64 other) => other.Matches(this);

        public IEnumerable<int> IterOnes()
        {
            ulong v = _bits;
            while (v != 0)
            {
                int idx = TrailingZeroCount(v);
                yield return idx;
                v &= v - 1;
            }
        }

        private static int TrailingZeroCount(ulong v)
        {
            if (v == 0) return 64;
            int n = 0;
            while ((v & 1UL) == 0) { v >>= 1; n++; }
            return n;
        }

        private static void CheckRange(int index)
        {
            if ((uint)index >= CAPACITY)
                throw new ArgumentOutOfRangeException(nameof(index), $"BitMask64 index {index} out of range (capacity {CAPACITY}).");
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            => serializer.SerializeValue(ref _bits);

        public bool Equals(MID_BitMask64 other) => _bits == other._bits;
        public override bool Equals(object obj) => obj is MID_BitMask64 o && Equals(o);
        public override int GetHashCode() => _bits.GetHashCode();
        public static bool operator ==(MID_BitMask64 a, MID_BitMask64 b) => a._bits == b._bits;
        public static bool operator !=(MID_BitMask64 a, MID_BitMask64 b) => a._bits != b._bits;
        public static MID_BitMask64 operator &(MID_BitMask64 a, MID_BitMask64 b) => a.Intersection(b);
        public static MID_BitMask64 operator |(MID_BitMask64 a, MID_BitMask64 b) => a.Union(b);
        public static MID_BitMask64 operator ^(MID_BitMask64 a, MID_BitMask64 b) => a.SymmetricDifference(b);
        public static MID_BitMask64 operator ~(MID_BitMask64 a) => new MID_BitMask64(~a._bits);

        public override string ToString() => "0b" + Convert.ToString((long)_bits, 2).PadLeft(CAPACITY, '0');
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MID_BitField / MID_BitFieldLayout — named multi-bit field descriptors
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A reserved {offset, width} slot inside a MID_BitPacker64, returned by
    /// MID_BitFieldLayout.Reserve*(). Immutable — build layouts once (e.g. a
    /// static readonly set of fields) and reuse the same MID_BitField handles
    /// everywhere you Get/Set that field.
    /// </summary>
    public readonly struct MID_BitField
    {
        public readonly int Offset;
        public readonly int Width;
        public readonly ulong Mask; // unshifted, e.g. width=3 → 0b111

        public MID_BitField(int offset, int width)
        {
            if (offset < 0 || offset >= MID_BitPacker64.CAPACITY)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (width <= 0 || offset + width > MID_BitPacker64.CAPACITY)
                throw new ArgumentOutOfRangeException(nameof(width),
                    $"Field [{offset}, {offset + width}) does not fit in {MID_BitPacker64.CAPACITY} bits.");

            Offset = offset;
            Width  = width;
            Mask   = width >= 64 ? ulong.MaxValue : (1UL << width) - 1UL;
        }
    }

    /// <summary>
    /// Sequentially assigns non-overlapping MID_BitField slots within a single
    /// 64-bit packer. Build one of these (typically static readonly) per group
    /// of fields you intend to pack together, call Reserve*() once per field in
    /// any order, and keep the returned MID_BitField handles to Get/Set with a
    /// MID_BitPacker64 later. Throws immediately if the fields you ask for
    /// don't fit in 64 bits, rather than silently truncating at runtime.
    /// </summary>
    public sealed class MID_BitFieldLayout
    {
        private int _usedBits;

        /// <summary>Total bits reserved so far.</summary>
        public int UsedBits => _usedBits;

        /// <summary>Bits still available before hitting MID_BitPacker64.CAPACITY.</summary>
        public int RemainingBits => MID_BitPacker64.CAPACITY - _usedBits;

        /// <summary>Reserve a raw field of the given bit width (1-64).</summary>
        public MID_BitField Reserve(int width)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Field width must be >= 1.");
            if (_usedBits + width > MID_BitPacker64.CAPACITY)
                throw new InvalidOperationException(
                    $"MID_BitFieldLayout overflow: requested {width} more bit(s) but only " +
                    $"{RemainingBits} remain ({_usedBits}/{MID_BitPacker64.CAPACITY} used). " +
                    "Split across a second MID_BitPacker64 (or NetworkVariable) instead.");

            var field = new MID_BitField(_usedBits, width);
            _usedBits += width;
            return field;
        }

        /// <summary>Reserve a single-bit boolean field.</summary>
        public MID_BitField ReserveBool() => Reserve(1);

        /// <summary>
        /// Reserve the minimum width that can hold every declared value of
        /// TEnum (based on the largest member's underlying integer value, not
        /// the member count — a sparse enum like {A=0, B=8} still needs 4 bits).
        /// Assumes non-negative underlying values, which covers the normal
        /// "state"/"type" enum case this packer targets.
        /// </summary>
        public MID_BitField ReserveEnum<TEnum>() where TEnum : unmanaged, Enum
            => Reserve(MID_BitPacker64.MinBitsForEnum<TEnum>());

        /// <summary>Reserve the minimum width that can hold 0..=maxInclusiveValue.</summary>
        public MID_BitField ReserveForMaxValue(ulong maxInclusiveValue)
            => Reserve(MID_BitPacker64.MinBitsForValue(maxInclusiveValue));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MID_BitPacker64 — the actual "mid packer": multi-bit VALUE fields
    //  (enums, small ints, bools) packed into one synced ulong.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Packs multiple small enum/integer fields into a single ulong for cheap
    /// network sync. Unlike MID_BitMask64 (one bit per boolean slot), each
    /// field here can be several bits wide — an enum with 5 members, a 0-127
    /// charge value, etc. Use MID_BitFieldLayout to reserve named fields, then
    /// Get/Set through this struct. Implements INetworkSerializable, so it
    /// drops straight into a NetworkVariable&lt;MID_BitPacker64&gt; and
    /// round-trips as a single ulong write/read — see the file header for a
    /// full usage example.
    /// </summary>
    [Serializable]
    public struct MID_BitPacker64 : INetworkSerializable, IEquatable<MID_BitPacker64>
    {
        public const int CAPACITY = 64;
        public static readonly MID_BitPacker64 Empty = new MID_BitPacker64(0UL);

        private ulong _bits;

        public MID_BitPacker64(ulong bits) => _bits = bits;

        public static MID_BitPacker64 FromBits(ulong bits) => new MID_BitPacker64(bits);
        public ulong ToBits() => _bits;

        // ── Raw field access ────────────────────────────────────────────────

        public ulong GetField(in MID_BitField field)
            => (_bits >> field.Offset) & field.Mask;

        /// <summary>
        /// Writes <paramref name="value"/> into <paramref name="field"/>. Values
        /// wider than the field are silently truncated to its low bits (masked,
        /// not clamped) — same "your responsibility to size the field right"
        /// contract as mid-math's BitMask index checks, minus the throw, since
        /// truncation here is recoverable and a thrown exception mid-tick on a
        /// hot networked write path is worse than a wrong-but-bounded value.
        /// </summary>
        public void SetField(in MID_BitField field, ulong value)
        {
            ulong masked = value & field.Mask;
            _bits = (_bits & ~(field.Mask << field.Offset)) | (masked << field.Offset);
        }

        // ── Bool convenience ────────────────────────────────────────────────

        public bool GetBool(in MID_BitField field) => GetField(field) != 0;
        public void SetBool(in MID_BitField field, bool value) => SetField(field, value ? 1UL : 0UL);

        // ── Enum convenience ────────────────────────────────────────────────
        // NOTE: boxes the enum value (Convert.ToUInt64 / Enum.ToObject) — fine
        // for config/state-style fields that change occasionally, which is what
        // this packer targets. If profiling ever shows this matters on a truly
        // hot per-tick field, replace with a Unity.Collections.LowLevel.Unsafe
        // .UnsafeUtility-based reinterpret cast sized off
        // Enum.GetUnderlyingType(typeof(TEnum)) — not done here to keep this
        // first pass straightforward and safe on any API compatibility level.

        public TEnum GetEnum<TEnum>(in MID_BitField field) where TEnum : unmanaged, Enum
            => (TEnum)Enum.ToObject(typeof(TEnum), GetField(field));

        public void SetEnum<TEnum>(in MID_BitField field, TEnum value) where TEnum : unmanaged, Enum
            => SetField(field, Convert.ToUInt64(value));

        // ── Int convenience ─────────────────────────────────────────────────

        public uint GetUInt(in MID_BitField field) => (uint)GetField(field);
        public void SetUInt(in MID_BitField field, uint value) => SetField(field, value);

        public byte GetByte(in MID_BitField field) => (byte)GetField(field);
        public void SetByte(in MID_BitField field, byte value) => SetField(field, value);

        // ── Width helpers (also used by MID_BitFieldLayout) ────────────────

        public static int MinBitsForValue(ulong maxInclusiveValue)
        {
            if (maxInclusiveValue == 0) return 1;
            int bits = 0;
            while (maxInclusiveValue > 0) { bits++; maxInclusiveValue >>= 1; }
            return bits;
        }

        public static int MinBitsForEnum<TEnum>() where TEnum : unmanaged, Enum
        {
            ulong max = 0;
            foreach (TEnum v in Enum.GetValues(typeof(TEnum)))
            {
                ulong raw = Convert.ToUInt64(v);
                if (raw > max) max = raw;
            }
            return MinBitsForValue(max);
        }

        // ── INetworkSerializable / equality ─────────────────────────────────

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            => serializer.SerializeValue(ref _bits);

        public bool Equals(MID_BitPacker64 other) => _bits == other._bits;
        public override bool Equals(object obj) => obj is MID_BitPacker64 o && Equals(o);
        public override int GetHashCode() => _bits.GetHashCode();
        public static bool operator ==(MID_BitPacker64 a, MID_BitPacker64 b) => a._bits == b._bits;
        public static bool operator !=(MID_BitPacker64 a, MID_BitPacker64 b) => a._bits != b._bits;

        public override string ToString() => "0b" + Convert.ToString((long)_bits, 2).PadLeft(CAPACITY, '0');
    }
}
