// Pre-warms every registered ProjectileConfigSO's pooled visual rendering
// state (sprite atlas resolution + shape-mesh MaterialPropertyBlock) at
// scene load, instead of paying that cost on the player's first few real
// shots.
//
// BACKGROUND — this is the same underlying issue ProjectileRegistry.Register()
// already has a partial fix for (see the "FIX" comment block on that method):
// Unity's Sprite Atlas system resolves a packed Sprite's `.texture` LAZILY,
// and that resolution isn't guaranteed complete by the time any particular
// script's Awake/Start runs. Register() mitigates this by touching every
// config's `sprite.texture` once at registration, which is enough for the
// plain SpriteRenderer path — it re-resolves every frame through Unity's own
// rendering pipeline, so a late-arriving correct texture just... arrives.
//
// It is NOT enough for the shape-mesh path (ProjectileVisual_2D.
// ApplyShapeMeshOptimised / ProjectileVisual_3D's instanced material) —
// those snapshot sprite.texture + a computed UV rect ONCE per OnInitialise
// call into a MaterialPropertyBlock/Material, so if that snapshot happens to
// land before Unity's atlas resolution has caught up, the wrong
// texture/UV stays baked in until the NEXT OnInitialise call for THAT
// SPECIFIC pooled instance — i.e. the next time a projectile that happens to
// draw that same pooled slot fires.
//
// IMPORTANT — this is per POOLED INSTANCE, not per physics NetworkObject or
// per config: LocalObjectPool.GetObject can hand back ANY idle instance for
// a given PoolableObjectType, and PhysicsProjectileBase.SpawnPoolVisual (and
// NetworkProjectileBase's own raycast/RustSim visual spawn) draws from that
// same shared pool on every single shot. With a prewarmCount of N, there are
// N independent instances in rotation — cycling only ONE of them through
// every config (an earlier version of this manager did exactly that) only
// warms that one; the other N-1 stay cold and still show the bug on
// whichever shot happens to draw them first. This version sweeps the pool's
// FULL current idle count for each type (LocalObjectPool.GetQueuedCount),
// so every instance gets touched, not just one.
//
// HOW: for each pool type, borrow-and-return exactly as many times as there
// are currently idle instances (LocalObjectPool uses a plain FIFO Queue, so
// that many Get/Return cycles visits every instance exactly once). Each
// borrowed instance is cycled through InitializeClientVisual() against
// EVERY registered config that matches that pool type's dimensionality
// before being returned — forcing the real MaterialPropertyBlock/Material
// snapshot to happen now, at load, against each config in turn, against
// every instance, instead of on the player's first several real shots.
//
// Both render paths pull from the exact same LocalObjectPool
// PoolableObjectType pools:
//   • the raycast/RustSim path, via NetworkProjectileBase._visualPoolType
//   • physics projectiles' cosmetic visual, via PhysicsProjectileBase.
//     SpawnPoolVisual — _visual2DPoolType/_visual3DPoolType
// so warming the pool types themselves covers both without any
// networking-specific handling — this never touches the actual networked
// physics body, only the plain local GameObject pool its cosmetic child
// visual is pulled from.
//
// SETUP: drop this on any persistent GameObject in the same scene as
// ProjectileRegistry / LocalObjectPool — order relative to them doesn't
// matter, this waits for both automatically. No inspector wiring required
// for the common case: it reads every config straight from
// ProjectileRegistry.AllConfigs and warms the two default pool types
// (Projectile_Visual2D / Projectile_Visual3D). If any prefab variant in your
// project overrides its visual pool type away from those defaults
// (PhysicsProjectileBase._visual2DPoolType/_visual3DPoolType, or
// NetworkProjectileBase._visualPoolType), add those extra types to
// _extraPoolTypesToWarm — configs don't carry a PoolableObjectType
// themselves, so those can't be discovered automatically.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Core.Singleton;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Visuals;

namespace MidManStudio.Projectiles.Managers
{
    [AddComponentMenu("MidMan Studio/Projectile System/Projectile Visual Warmup Manager")]
    public sealed class ProjectileVisualWarmupManager : Singleton<ProjectileVisualWarmupManager>
    {
        #region Inspector

        [Header("Extra Pool Types")]
        [Tooltip("PoolableObjectType values to warm IN ADDITION to the two " +
                 "defaults (Projectile_Visual2D, Projectile_Visual3D). Only " +
                 "needed if some prefab variant overrides its visual pool " +
                 "type away from those defaults — configs don't carry a " +
                 "PoolableObjectType themselves, so those can't be discovered " +
                 "automatically.")]
        [SerializeField] private PoolableObjectType[] _extraPoolTypesToWarm
            = System.Array.Empty<PoolableObjectType>();

        [Header("Timing")]
        [Tooltip("Frames to wait after ProjectileRegistry/LocalObjectPool are " +
                 "both ready before running the warm-up pass, giving Unity's " +
                 "background atlas-packing genuine wall-clock time to finish. " +
                 "See ProjectileRegistry.Register()'s own comment on why a " +
                 "same-frame touch alone isn't a reliable fix.")]
        [SerializeField, Range(1, 10)] private int _framesToWaitBeforeWarmup = 2;

        [Tooltip("Run a second full warm-up pass this many frames after the " +
                 "first, to catch any sprite whose atlas resolution was still " +
                 "mid-flight during the first pass. Set to 0 to disable the " +
                 "second pass.")]
        [SerializeField, Range(0, 10)] private int _framesBeforeSecondPass = 3;

        [Tooltip("Max seconds to wait for ProjectileRegistry to have at least " +
                 "one registered config, and for LocalObjectPool to finish " +
                 "initialising, before giving up and logging a warning.")]
        [SerializeField] private float _dependencyTimeoutSeconds = 10f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        private bool _warmupComplete;
        private bool _warmupRunning;

        #endregion

        #region Lifecycle

        private void Start()
        {
            StartCoroutine(WarmUpRoutine());
        }

        #endregion

        #region Public API

        /// <summary>True once at least one warm-up pass has finished.</summary>
        public bool WarmupComplete => _warmupComplete;

        /// <summary>
        /// Manually (re)run the warm-up pass — e.g. after registering new
        /// configs at runtime after startup (DLC, a late-loaded weapon set,
        /// etc.), or after growing a pool's prewarmCount at runtime. Safe to
        /// call even if an automatic pass already ran. No-ops if a pass is
        /// already in progress.
        /// </summary>
        public void RequestWarmup()
        {
            if (_warmupRunning) return;
            StartCoroutine(WarmUpRoutine());
        }

        #endregion

        #region Warmup Routine

        private IEnumerator WarmUpRoutine()
        {
            _warmupRunning = true;

            float deadline = Time.realtimeSinceStartup + _dependencyTimeoutSeconds;
            while (!DependenciesReady())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    MID_Logger.LogWarning(_logLevel,
                        "ProjectileVisualWarmupManager: gave up waiting for " +
                        "ProjectileRegistry/LocalObjectPool to be ready after " +
                        $"{_dependencyTimeoutSeconds:F1}s — skipping warm-up. " +
                        "Sprites may still lag on the first few real shots.",
                        nameof(ProjectileVisualWarmupManager));
                    _warmupRunning = false;
                    yield break;
                }
                yield return null;
            }

            for (int i = 0; i < _framesToWaitBeforeWarmup; i++)
                yield return null;

            RunWarmupPass("first");

            if (_framesBeforeSecondPass > 0)
            {
                for (int i = 0; i < _framesBeforeSecondPass; i++)
                    yield return null;

                RunWarmupPass("second");
            }

            _warmupComplete = true;
            _warmupRunning  = false;
        }

        private bool DependenciesReady()
        {
            if (!ProjectileRegistry.HasInstance)                return false;
            if (ProjectileRegistry.Instance.Count == 0)         return false;
            if (LocalObjectPool.Instance == null)               return false;
            if (!LocalObjectPool.Instance.HasBeenInitialized()) return false;
            return true;
        }

        private void RunWarmupPass(string passLabel)
        {
            if (!ProjectileRegistry.HasInstance) return;
            var configs = ProjectileRegistry.Instance.AllConfigs;
            if (configs == null || configs.Count == 0) return;

            var poolTypes = new HashSet<PoolableObjectType>
            {
                PoolableObjectType.Projectile_Visual2D,
                PoolableObjectType.Projectile_Visual3D
            };
            if (_extraPoolTypesToWarm != null)
                foreach (var t in _extraPoolTypesToWarm) poolTypes.Add(t);

            int instancesTouched   = 0;
            int warmedCombinations = 0;

            foreach (var poolType in poolTypes)
            {
                // FIX: cycling just one borrowed instance through every config
                // only warms THAT one instance — every other instance already
                // sitting idle in the pool (prewarmCount can be > 1, and every
                // projectile that fires can draw ANY of them from
                // LocalObjectPool) stays cold and still shows the bug on its
                // own first real use. Sweep the pool's full current idle
                // count instead, so every instance gets touched once per pass.
                int queuedCount = LocalObjectPool.Instance.GetQueuedCount(poolType);
                if (queuedCount == 0)
                {
                    MID_Logger.LogDebug(_logLevel,
                        $"WarmUp[{passLabel}]: nothing currently queued for " +
                        $"{poolType} (not registered, or prewarmCount is 0) — skipping.",
                        nameof(ProjectileVisualWarmupManager));
                    continue;
                }

                // Only the two DEFAULT pool types have a known dimensionality
                // (2D config → Projectile_Visual2D, 3D config →
                // Projectile_Visual3D). An "extra" custom pool type has no
                // way to know which configs it's meant to render, so it gets
                // every config unconditionally — harmless, just a few extra
                // cheap re-initialise calls on instances we're about to
                // return anyway.
                bool restrictTo2D = poolType == PoolableObjectType.Projectile_Visual2D;
                bool restrictTo3D = poolType == PoolableObjectType.Projectile_Visual3D;

                for (int i = 0; i < queuedCount; i++)
                {
                    GameObject borrowedGO = LocalObjectPool.Instance.GetObject(
                        poolType, Vector3.zero, Quaternion.identity);
                    if (borrowedGO == null) break; // pool ran dry mid-sweep — nothing more to warm

                    var visual = borrowedGO.GetComponent<ProjectileVisualBase>();
                    if (visual == null)
                    {
                        MID_Logger.LogWarning(_logLevel,
                            $"WarmUp[{passLabel}]: {poolType}'s prefab has no " +
                            "ProjectileVisualBase component — skipping this type entirely.",
                            nameof(ProjectileVisualWarmupManager));
                        LocalObjectPool.Instance.ReturnObject(borrowedGO, poolType);
                        break;
                    }

                    foreach (var cfg in configs)
                    {
                        if (cfg == null) continue;
                        if (restrictTo2D && cfg.Is3D)  continue;
                        if (restrictTo3D && !cfg.Is3D) continue;

                        visual.InitializeClientVisual(
                            cfg.ConfigId, Vector3.zero, Vector3.right, 1f);
                        warmedCombinations++;
                    }

                    visual.ReturnToPoolImmediate();
                    instancesTouched++;
                }
            }

            MID_Logger.LogInfo(_logLevel,
                $"WarmUp[{passLabel}]: touched {instancesTouched} pooled instance(s) " +
                $"across {poolTypes.Count} pool type(s), {warmedCombinations} " +
                "config/instance combination(s) total.",
                nameof(ProjectileVisualWarmupManager));
        }

        #endregion
    }
}
