// ProjectileVisualBase.cs
// Abstract base MonoBehaviour for ALL pooled projectile visuals (2D and 3D).
//
// WHY ABSTRACT CLASS OVER INTERFACE:
//   MonoBehaviour cannot be "implemented" from a pure interface in Unity;
//   GetComponent<IFoo>() works but you lose inheritance of Unity lifecycle
//   and common shared state. An abstract MonoBehaviour gives us:
//     • Shared pool-return logic (LocalPoolReturn)
//     • Common state (configId, origin, direction, speed)
//     • GetComponent-friendly type hierarchy
//     • Virtual hooks so end-users override only what they need
//
// END-USER EXTENSION PATTERN:
//   // In your game assembly:
//   public class MyTrailVisual : ProjectileVisualBase
//   {
//       [SerializeField] private TrailRenderer _trail;
//
//       protected override void OnInitialise(ProjectileConfigSO cfg)
//       {
//           _trail.startColor = cfg.TrailGradient?.Evaluate(0f) ?? Color.white;
//       }
//
//       protected override void OnReturnToPool()
//       {
//           _trail.Clear();
//       }
//   }
//
// NETWORKED vs LOCAL:
//   This base class is pool-based (LocalObjectPool / LocalParticlePool).
//   Network-owned projectile visuals that travel with a NetworkObject use
//   INetworkProjectileVisual (NetworkProjectileBase.cs) instead.
//   The two systems are intentionally separate — pool visuals are fire-and-forget
//   client cosmetics, network visuals are authority-driven.

using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    [DisallowMultipleComponent]
    public abstract class ProjectileVisualBase : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Pool Return (auto-found if null)")]
        [SerializeField] protected LocalPoolReturn _poolReturn;

        [Header("Debug")]
        [SerializeField] protected MID_LogLevel _logLevel = MID_LogLevel.None;

        // ── Shared state (readable by sub-classes) ────────────────────────────

        public ushort  ConfigId   { get; private set; }
        public Vector3 Origin     { get; private set; }
        public Vector3 Direction  { get; private set; }
        public float   Speed      { get; private set; }
        public bool    IsActive   { get; private set; }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        protected virtual void Awake()
        {
            if (_poolReturn == null)
                _poolReturn = GetComponent<LocalPoolReturn>();
        }

        // ── Public API — called by ClientPredictionManager / RaycastHandler ──

        /// <summary>
        /// Initialise this visual for a newly spawned projectile.
        /// Automatically resolves the config from ProjectileRegistry and calls
        /// the virtual <see cref="OnInitialise"/> hook for sub-class setup.
        /// </summary>
        public void InitializeClientVisual(
            ushort  configId,
            Vector3 origin,
            Vector3 direction,
            float   speed)
        {
            ConfigId  = configId;
            Origin    = origin;
            Direction = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.forward;
            Speed     = speed;
            IsActive  = true;

            transform.position = origin;
            ApplyRotation(Direction);

            var cfg = ProjectileRegistry.HasInstance
                ? ProjectileRegistry.Instance.Get(configId)
                : null;

            OnInitialise(cfg);

            MID_Logger.LogDebug(_logLevel,
                $"{GetType().Name} init configId={configId} origin={origin}",
                nameof(ProjectileVisualBase));
        }

        /// <summary>
        /// Immediately return this visual to the object pool and reset state.
        /// </summary>
        public void ReturnToPoolImmediate()
        {
            if (this == null) return;
            IsActive = false;
            OnReturnToPool();
            _poolReturn?.ReturnToPoolNow();

            MID_Logger.LogDebug(_logLevel,
                $"{GetType().Name} returned to pool.", nameof(ProjectileVisualBase));
        }

        /// <summary>
        /// Hide rendering without returning to pool (e.g. during reconcile).
        /// </summary>
        public virtual void HideProjectile() { }

        // ── Abstract / virtual hooks for sub-classes ──────────────────────────

        /// <summary>
        /// Called during <see cref="InitializeClientVisual"/> after common state is set.
        /// Override to apply sprite, mesh, trail, particles from the config.
        /// <paramref name="cfg"/> may be null if the configId is not registered.
        /// </summary>
        protected abstract void OnInitialise(ProjectileConfigSO cfg);

        /// <summary>
        /// Called just before the object is returned to the pool.
        /// Override to clear trails, stop particles, reset materials etc.
        /// </summary>
        protected abstract void OnReturnToPool();

        /// <summary>
        /// Applies a world-space rotation so the visual faces its travel direction.
        /// Override if your visual uses a different forward convention.
        /// </summary>
        protected virtual void ApplyRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;

            // Sub-classes override for 2D (Z-angle) vs 3D (LookRotation)
            // Default: identity — let sub-class handle it
        }
    }
}
