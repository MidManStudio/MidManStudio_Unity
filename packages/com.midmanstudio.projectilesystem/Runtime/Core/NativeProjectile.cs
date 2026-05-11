// NativeProjectile.cs
// Core 2D projectile simulation struct.
// MUST match Rust NativeProjectile (src/lib.rs) exactly — 72 bytes.
// All other 2D/3D structs and enums live in ProjectileLib.cs.

using System.Runtime.InteropServices;

namespace MidManStudio.Projectiles.Core
{
    // ── NativeProjectile — 72 bytes ──────────────────────────────────────────
    // Layout: 15×f32(60) + 2×u16(4) + u32(4) + 4×u8(4) = 72 bytes
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct NativeProjectile
    {
        // ── Physics (Rust writes every tick) ──────────────────────────────────
        [FieldOffset( 0)] public float  X;
        [FieldOffset( 4)] public float  Y;
        [FieldOffset( 8)] public float  Vx;
        [FieldOffset(12)] public float  Vy;
        [FieldOffset(16)] public float  Ax;         // lateral accel / homing dir X
        [FieldOffset(20)] public float  Ay;         // gravity / homing dir Y
        [FieldOffset(24)] public float  AngleDeg;   // visual rotation
        [FieldOffset(28)] public float  CurveT;     // arc / phase accumulator

        // ── Visual (Rust updates, renderer reads) ─────────────────────────────
        [FieldOffset(32)] public float  ScaleX;
        [FieldOffset(36)] public float  ScaleY;
        [FieldOffset(40)] public float  ScaleTarget;
        [FieldOffset(44)] public float  ScaleSpeed;

        // ── Lifetime / distance ───────────────────────────────────────────────
        [FieldOffset(48)] public float  Lifetime;
        [FieldOffset(52)] public float  MaxLifetime;
        [FieldOffset(56)] public float  TravelDist;

        // ── Identity (C# writes once on spawn) ────────────────────────────────
        [FieldOffset(60)] public ushort ConfigId;
        [FieldOffset(62)] public ushort OwnerId;
        [FieldOffset(64)] public uint   ProjId;

        // ── State flags ────────────────────────────────────────────────────────
        [FieldOffset(68)] public byte   CollisionCount;
        [FieldOffset(69)] public byte   MovementType;  // ProjectileMovementType cast to byte
        [FieldOffset(70)] public byte   PiercingType;  // ProjectilePiercingType cast to byte
        [FieldOffset(71)] public byte   Alive;         // 0 = dead, 1 = alive
    }
}
