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

using System.Collections;
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

            // GROWTH FIX companion: stop mid-flight growth explicitly rather
            // than relying solely on Unity auto-stopping coroutines when the
            // GameObject deactivates later in this call chain — that timing
            // is implicit/pool-internal, this is guaranteed.
            if (_scaleGrowthCoroutine != null)
            {
                StopCoroutine(_scaleGrowthCoroutine);
                _scaleGrowthCoroutine = null;
            }

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

        // ── Scale growth ───────────────────────────────────────────────────────
        // GROWTH FIX ("physics projectiles get spawned full scale rather than
        // scaling up as intended"): ProjectileConfigSO.UseScaleGrowth /
        // SpawnScaleFraction / GrowthSpeed already existed and were being
        // applied for RustSim-driven projectiles (consumed natively — see
        // GetRustSpawnParams and rust_lib/projectile_core/src/simulation.rs's
        // tick_scale, whose exact formula this reproduces:
        // current += (target - current) * speed * dt), but a pooled
        // ProjectileVisualBase instance (used by physics projectiles' cosmetic
        // visual, and as the raycast/RustSim path's own pooled visual) always
        // jumped straight to full size in a single OnInitialise call — there
        // was no per-frame growth loop here at all.
        //
        // Sub-classes call RefreshScaleGrowth(cfg) from their own OnInitialise,
        // AFTER their own one-shot "jump to full size" scale application
        // (ProjectileVisual_2D's ApplySpriteOptimised/ApplyShapeMeshOptimised,
        // ProjectileVisual_3D's ApplyScale) — if cfg.UseScaleGrowth is false
        // this immediately returns and that one-shot value stands unchanged,
        // zero per-frame cost. If it's true, this coroutine takes over and
        // animates from there. Coroutines execute synchronously up to their
        // first yield, so the very first ApplyScaleAtSize call below runs in
        // the same frame as the one-shot call it's overriding — no visible
        // one-frame "flash" at full size.
        //
        // Purely cosmetic/local — every peer (server and every client) runs
        // this independently off the same shared config data, the same way
        // the rest of this class's rendering already works without any
        // network traffic. For physics projectiles specifically, the ACTUAL
        // collider (hit-detection, server-authoritative) is grown separately
        // by PhysicsProjectileBase using the identical formula — see that
        // class's GrowColliderRoutine.

        private Coroutine _scaleGrowthCoroutine;

        /// <summary>
        /// Applies a given (possibly growth-interpolated) size to this visual.
        /// Default matches the plain 2D convention (X/Y scale, Z=1) — this is
        /// exactly what ProjectileVisual_2D's own scale lines already do, so
        /// it doesn't need to override this. ProjectileVisual_3D overrides it
        /// to reproduce its own length/width/aspect-ratio mapping (see that
        /// class's ApplyScale, which this must match exactly when sizeX/sizeY
        /// equal the config's full FullSizeX/FullSizeY).
        /// </summary>
        protected virtual void ApplyScaleAtSize(float sizeX, float sizeY)
        {
            transform.localScale = new Vector3(sizeX, sizeY, 1f);
        }

        /// <summary>(Re)starts or stops scale-growth for the given config — see the section comment above.</summary>
        protected void RefreshScaleGrowth(ProjectileConfigSO cfg)
        {
            if (_scaleGrowthCoroutine != null)
            {
                StopCoroutine(_scaleGrowthCoroutine);
                _scaleGrowthCoroutine = null;
            }

            if (cfg == null || !cfg.UseScaleGrowth) return;

            _scaleGrowthCoroutine = StartCoroutine(GrowScaleRoutine(cfg));
        }

        private IEnumerator GrowScaleRoutine(ProjectileConfigSO cfg)
        {
            float targetX = Mathf.Max(cfg.FullSizeX, 0.001f);
            float targetY = Mathf.Max(cfg.FullSizeY, 0.001f);
            float speed   = cfg.GrowthSpeed;

            float curX = targetX * cfg.SpawnScaleFraction;
            float curY = targetY * cfg.SpawnScaleFraction;
            ApplyScaleAtSize(curX, curY);

            while (true)
            {
                float dt    = Time.deltaTime;
                float diffX = targetX - curX;
                float diffY = targetY - curY;
                bool  doneX = Mathf.Abs(diffX) <= 0.001f;
                bool  doneY = Mathf.Abs(diffY) <= 0.001f;
                if (doneX && doneY) break;

                if (!doneX) curX += diffX * speed * dt;
                if (!doneY) curY += diffY * speed * dt;
                ApplyScaleAtSize(curX, curY);
                yield return null;
            }

            ApplyScaleAtSize(targetX, targetY);
            _scaleGrowthCoroutine = null;
        }
    }
}
