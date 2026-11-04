// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "SimulationMode.cs"
// ============================================================================
// ProjectileConfigSO.Is3D controls which Rust buffer (2D or 3D) is used, so the
// simulation-mode override does not repeat that choice.
// Value 2 is unassigned. A config asset that still stores 2 shows "(2)" in the
// inspector and needs RustSim assigned again.

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
