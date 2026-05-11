// SimulationMode.cs
// Simulation and network routing enums.
// NOTE: ProjectileMovementType and ProjectilePiercingType are defined in ProjectileLib.cs
// to keep all FFI-adjacent enums in one place.

namespace MidManStudio.Projectiles.Core
{
    /// <summary>
    /// Determines how a projectile is simulated and networked.
    /// Assigned per-spawn by ProjectileTypeRouter.
    /// </summary>
    public enum SimulationMode : byte
    {
        /// <summary>Instant hitscan. Server casts ray; client visual travels to endpoint.</summary>
        Raycast = 0,

        /// <summary>Rust 2D tick + spatial-grid collision every FixedUpdate. Clients predict.</summary>
        RustSim2D = 1,

        /// <summary>Rust 3D tick loop with NativeProjectile3D buffer.</summary>
        RustSim3D = 2,

        /// <summary>Unity Rigidbody2D/3D. Server owns physics; clients via NetworkTransform.</summary>
        PhysicsObject = 3,

        /// <summary>Single-player / offline. Full Rust sim, no NGO, no RPCs.</summary>
        LocalOnly = 4
    }

    /// <summary>
    /// Network authority model for a projectile batch.
    /// </summary>
    public enum NetworkVariant : byte
    {
        /// <summary>No network — LocalOnly mode.</summary>
        None = 0,

        /// <summary>Server authoritative — server runs sim, clients predict + reconcile.</summary>
        ServerAuth = 1
    }
}
