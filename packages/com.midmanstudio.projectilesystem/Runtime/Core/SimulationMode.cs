// SimulationMode.cs
// CHANGE: merged RustSim2D and RustSim3D into RustSim.
// ProjectileConfigSO.Is3D already controls which Rust buffer (2D/3D) is used —
// there is no reason to duplicate that choice in the simulation-mode override.
// Byte value 1 (RustSim2D) is preserved so existing serialised configs are not corrupted.
// Value 2 (RustSim3D) is intentionally left as a gap; any SO that had it will show "(2)"
// in the inspector and can simply be re-assigned to RustSim.

namespace MidManStudio.Projectiles.Core
{
    /// <summary>
    /// Determines how a projectile is simulated and networked.
    /// Assigned per-spawn by ProjectileTypeRouter.
    /// </summary>
    public enum SimulationMode : byte
    {
        /// <summary>Instant hitscan. Server casts ray; client visual travels to endpoint.</summary>
        Raycast       = 0,

        /// <summary>
        /// Rust tick + spatial-grid collision every FixedUpdate.
        /// ProjectileConfigSO.Is3D selects the 2D or 3D Rust buffer automatically.
        /// Clients predict; server is authoritative.
        /// </summary>
        RustSim       = 1,

        // 2 was RustSim3D — removed; Is3D on the config handles the distinction.

        /// <summary>Unity Rigidbody2D/3D. Server owns physics; clients via NetworkTransform.</summary>
        PhysicsObject = 3,

        /// <summary>Single-player / offline. Full Rust sim, no NGO, no RPCs.</summary>
        LocalOnly     = 4
    }

    /// <summary>
    /// Network authority model for a projectile batch.
    /// </summary>
    public enum NetworkVariant : byte
    {
        /// <summary>No network — LocalOnly mode.</summary>
        None       = 0,

        /// <summary>Server authoritative — server runs sim, clients predict + reconcile.</summary>
        ServerAuth = 1
    }
}
