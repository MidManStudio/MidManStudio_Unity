// PoolableObjectType.cs
// DEFAULT STUB — shipped with the package so it compiles immediately on import.
// Run  MidManStudio > Pool Type Generator  to regenerate with your own entries.
// The generator will overwrite this file in-place.

namespace MidManStudio.Core.Pools
{
    /// <summary>Object pool type IDs. AUTO-GENERATED — do not edit manually.</summary>
    public enum PoolableObjectType
    {
        // ── com.midmanstudio.utilities  (block 0–99) ──────────────────────────
        SpawnableAudio = 0, // Pooled one-shot / looping audio source [pinned]
        Trail          = 1, // Pooled trail renderer object [pinned]

        // ── com.midmanstudio.projectilesystem  (block 100–199) ───────────────
        Projectile_Visual2D = 100, // 2D projectile sprite visual [pinned]
        Projectile_Visual3D = 101, // 3D projectile visual [pinned]
        Projectile_Flipbook = 102, // Sprite-sheet flipbook for impact explosions [pinned]
    }
}