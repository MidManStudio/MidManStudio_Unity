// PoolableParticleType.cs
// DEFAULT STUB — shipped with the package so it compiles immediately on import.
// Run  MidManStudio > Pool Type Generator  to regenerate with your own entries.
// The generator will overwrite this file in-place.

namespace MidManStudio.Core.Pools
{
    /// <summary>Particle pool type IDs. AUTO-GENERATED — do not edit manually.</summary>
    public enum PoolableParticleType
    {
        // ── com.midmanstudio.projectilesystem  (block 0–99) ───────────────────
        Projectile_Impact           = 0,  // Generic hit / impact particle [pinned]
        Projectile_Explosion_Small  = 1,  // Small explosion [pinned]
        Projectile_Explosion_Medium = 2,  // Medium explosion [pinned]
        Projectile_Explosion_Large  = 3,  // Large explosion [pinned]
        Projectile_Headshot         = 4,  // Headshot / critical hit particle [pinned]
        Projectile_Tracer           = 5,  // Tracer round particle [pinned]
    }
}