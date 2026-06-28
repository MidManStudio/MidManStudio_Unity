
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Adapters
{
    /// <summary>
    /// Context provided by the weapon system when requesting a projectile spawn.
    /// </summary>
    public struct WeaponFireContext
    {
        /// <summary>Rounds per second at which this weapon fires.</summary>
        public float FireRate;

        /// <summary>
        /// True when the weapon script handles Physics2D/3D.Raycast itself.
        /// The projectile system handles visual + RPC only.
        /// </summary>
        public bool IsRaycastWeapon;

        /// <summary>Projectiles fired in this single fire event (e.g. 8 for shotgun).</summary>
        public int  ProjectileCount;

        /// <summary>True if this is a networked session. False forces LocalOnly.</summary>
        public bool IsNetworked;

        /// <summary>Latency compensation in seconds (0 = none).</summary>
        public float LatencyCompensation;

        /// <summary>MID ID of the firing entity (player or bot).</summary>
        public ulong OwnerMidId;

        /// <summary>NetworkObject ID of the weapon/character that fired.</summary>
        public ulong FiredByNetworkObjectId;

        /// <summary>True if the owner is a bot.</summary>
        public bool IsBotOwner;

        /// <summary>Weapon level — affects kill effect probability.</summary>
        public byte WeaponLevel;

        /// <summary>Damage multiplier from power-ups or abilities.</summary>
        public float DamageMultiplier;
    }

    /// <summary>
    /// Routing result — SimulationMode plus metadata consumed by MID_MasterProjectileSystem.
    /// </summary>
    public struct RoutingResult
    {
        public SimulationMode Mode;
        public NetworkVariant Network;

        /// <summary>True when the result came from a per-config PreferredSimMode override.</summary>
        public bool WasOverridden;
    }

    /// <summary>
    /// Routes a fire event to the correct SimulationMode.
    /// All methods are pure functions — deterministic, no side effects.
    /// Game-specific routing logic lives in ProjectileConfigSO virtual methods.
    /// </summary>
    public static class ProjectileTypeRouter
    {
        // ── Primary routing entry point ───────────────────────────────────────

        /// <summary>
        /// Determine SimulationMode and NetworkVariant for a fire event.
        /// Called by MID_MasterProjectileSystem.Fire() before spawning anything.
        /// </summary>
        public static RoutingResult Route(ProjectileConfigSO config, WeaponFireContext context)
        {
            // Offline — no network, no authority
            if (!context.IsNetworked)
            {
                return new RoutingResult
                {
                    Mode          = SimulationMode.LocalOnly,
                    Network       = NetworkVariant.None,
                    WasOverridden = false
                };
            }

            // Per-config explicit override wins over all automatic routing
            if (config.HasSimModeOverride)
            {
                return new RoutingResult
                {
                    Mode          = config.PreferredSimMode,
                    Network       = NetworkVariant.ServerAuth,
                    WasOverridden = true
                };
            }

            return new RoutingResult
            {
                Mode          = ComputeMode(config, context),
                Network       = NetworkVariant.ServerAuth,
                WasOverridden = false
            };
        }

        // ── Internal routing logic ────────────────────────────────────────────

        private static SimulationMode ComputeMode(
            ProjectileConfigSO config, WeaponFireContext context)
        {
            // Physics-dependent types use Unity Rigidbody — not the Rust sim buffer.
            if (config.RequiresPhysicsObject())
                return SimulationMode.PhysicsObject;

            // Raycast: the weapon script owns Physics2D/3D.Raycast in its fire method.
            if (context.IsRaycastWeapon && config.IsRaycastEligible())
                return SimulationMode.Raycast;

            // Default Rust simulation — config.Is3D selects 2D or 3D buffer.
            return SimulationMode.RustSim;
        }

        // ── Convenience delegates (for editor tooling and debug panels) ────────

        /// <summary>
        /// True when the config requires Unity physics.
        /// Delegates to ProjectileConfigSO.RequiresPhysicsObject() — override there.
        /// </summary>
        public static bool RequiresPhysicsObject(ProjectileConfigSO config)
            => config.RequiresPhysicsObject();

        /// <summary>
        /// True when the config is safe for instant hitscan.
        /// Delegates to ProjectileConfigSO.IsRaycastEligible() — override there.
        /// </summary>
        public static bool IsRaycastEligible(ProjectileConfigSO config)
            => config.IsRaycastEligible();

        /// <summary>
        /// Human-readable explanation of a routing decision.
        /// Not called at runtime — used by editor tooling and debug panels.
        /// </summary>
        public static string ExplainRoute(ProjectileConfigSO config, WeaponFireContext context)
        {
            if (!context.IsNetworked)
                return "LocalOnly — not a networked session.";

            if (config.HasSimModeOverride)
                return $"Config override: {config.PreferredSimMode}.";

            if (config.RequiresPhysicsObject())
                return "PhysicsObject — config.RequiresPhysicsObject() returned true.\n" +
                       "Override in your derived ProjectileConfigSO to control this.";

            if (context.IsRaycastWeapon && config.IsRaycastEligible())
                return "Raycast — weapon owns the Physics2D/3D.Raycast call. " +
                       "System handles visual and RPC only.";

            return $"RustSim — Is3D={config.Is3D} selects the " +
                   (config.Is3D ? "3D" : "2D") + " Rust simulation buffer.";
        }
    }
}
