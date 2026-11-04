// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "RustSpawnParams.cs"
// ============================================================================
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

        /// <summary>
        /// Maximum collision count (piercing). Carried with the config data;
        /// BatchSpawnHelper does not read it (the collision budget lives in ServerProjectileData).
        /// </summary>
        public byte   MaxCollisions;

        /// <summary>Projectile lifetime in seconds.</summary>
        public float  Lifetime;

        /// <summary>
        /// Gravity acceleration (NativeProjectile.Ay). Wave and Circular
        /// projectiles use Ax/Ay for their perpendicular axis instead.
        /// </summary>
        public float  GravityAy;

        /// <summary>
        /// Starting scale in world units: FullSizeX times the spawn scale fraction
        /// when growth is on, FullSizeX when it is off. Written to every scale axis.
        /// </summary>
        public float  ScaleStart;

        /// <summary>Scale the projectile grows toward, in world units (FullSizeX).</summary>
        public float  ScaleTarget;

        /// <summary>
        /// Growth rate. Each tick the scale moves by (ScaleTarget - scale) * ScaleSpeed * dt
        /// until it is within 0.001 of the target. 0 = no growth (the native tick skips it).
        /// </summary>
        public float  ScaleSpeed;

        /// <summary>True when this config uses the 3D sim buffer.</summary>
        public bool   Is3D;
    }
}
