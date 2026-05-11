// RustSpawnParams.cs
// Minimal Rust-facing spawn parameters derived from ProjectileConfigSO.
// Passed from ProjectileRegistry / ProjectileConfigSO to BatchSpawnHelper.
// Only contains what the Rust sim and Burst fill jobs need — no Unity objects.

namespace MidManStudio.Projectiles.Core
{
    /// <summary>
    /// Minimal data extracted from a ProjectileConfigSO for use by BatchSpawnHelper
    /// and Burst fill jobs. Passed once per spawn event (not per projectile).
    /// </summary>
    public struct RustSpawnParams
    {
        /// <summary>Resolved speed (may be random range resolved at call site).</summary>
        public float  Speed;

        /// <summary>ProjectileMovementType cast to byte.</summary>
        public byte   MovementType;

        /// <summary>ProjectilePiercingType cast to byte.</summary>
        public byte   PiercingType;

        /// <summary>Maximum collision count (piercing).</summary>
        public byte   MaxCollisions;

        /// <summary>Projectile lifetime in seconds.</summary>
        public float  Lifetime;

        /// <summary>Gravity acceleration (maps to NativeProjectile.Ay).</summary>
        public float  GravityAy;

        /// <summary>Starting scale (0..1 fraction of FullSize, or 1 if no growth).</summary>
        public float  ScaleStart;

        /// <summary>Target scale to grow toward (FullSize, or 1 if no growth).</summary>
        public float  ScaleTarget;

        /// <summary>Scale lerp speed per second. 0 = no growth (skip tick_scale).</summary>
        public float  ScaleSpeed;

        /// <summary>True when this config uses the 3D sim buffer.</summary>
        public bool   Is3D;
    }
}
