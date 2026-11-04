// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileVisualWarmupManager.cs"
// ============================================================================
// Pre-warms the rendering state of the pooled projectile visuals for every
// registered ProjectileConfigSO at scene load, so the cost is not paid on the
// player's first few real shots.
//
// Why it is needed: Unity's Sprite Atlas system resolves a packed Sprite's
// .texture lazily, and that resolution is not guaranteed to be complete by the
// time any given Awake or Start runs. ProjectileRegistry.Register() touches
// every config's sprite.texture once, which is enough for the plain
// SpriteRenderer path, because Unity re-resolves it every frame. It is not
// enough for the shape-mesh path (ProjectileVisual_2D.ApplyShapeMeshOptimised
// and ProjectileVisual_3D's instanced material). Those take a snapshot of
// sprite.texture and a computed UV rect once per OnInitialise call, into a
// MaterialPropertyBlock or Material. If the snapshot lands before the atlas
// has resolved, the wrong texture or UV stays baked in until the next
// OnInitialise call on that same pooled instance.
//
// The snapshot is per pooled instance, not per config or per physics
// NetworkObject. LocalObjectPool.GetObject can return any idle instance of a
// PoolableObjectType, and both PhysicsProjectileBase.SpawnPoolVisual and
// NetworkProjectileBase's own visual spawn draw from that shared pool on every
// shot. With a prewarm count of N there are N independent instances, so
// warming one of them leaves the other N-1 cold. Each pass therefore sweeps
// the pool's full idle count for each type (LocalObjectPool.GetQueuedCount).
//
// How: for each pool type, borrow and return as many times as there are idle
// instances (LocalObjectPool uses a plain FIFO queue, so that many Get/Return
// cycles visits every instance once). Each borrowed instance is initialised
// with InitializeClientVisual against every registered config that matches the
// pool type's dimensionality, which forces the real MaterialPropertyBlock or
// Material snapshot to happen now, against each config in turn. Both render
// paths use the same LocalObjectPool pools (the raycast and RustSim path
// through NetworkProjectileBase._visualPoolType, physics projectiles' cosmetic
// visual through PhysicsProjectileBase._visual2DPoolType/_visual3DPoolType),
// so warming the pool types covers both. It never touches a networked physics
// body, only the local GameObject pool its cosmetic child is taken from.
//
// Setup: put it on any persistent GameObject in the scene that has
// ProjectileRegistry and LocalObjectPool. Order relative to them does not
// matter, it waits for both. No inspector wiring is needed in the common case:
// it reads every config from ProjectileRegistry.AllConfigs and warms the two
// default pool types (Projectile_Visual2D and Projectile_Visual3D). If a prefab
// variant overrides its visual pool type, add that type to
// _extraPoolTypesToWarm, because configs carry no PoolableObjectType and the
// extra types cannot be discovered.

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
                // Sweep the pool's full current idle count so every idle instance is
                // touched once per pass, not just one borrowed instance.
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
