// ProjectileImpactHandler.cs
// Client-side only. Manages impact effects without VFX Graph or GPU instancing.
//
// Three strategies (from the plan):
//   1. PooledParticleSystem  — LocalParticlePool for standard hits (dust, sparks, blood)
//   2. SpriteSheetFlipbook  — pooled GameObjects with SpriteRenderer for explosions
//   3. SharedEmit           — single ParticleSystem.Emit() for very high hit rates (SMG)
//
// Strategy selection per configId:
//   Registered at startup via RegisterImpactStrategy().
//   Default (no registration): PooledParticleSystem.
//
// Flipbook:
//   FlipbookPool holds pre-instantiated GameObjects with SpriteRenderer.
//   ImpactFlipbook component steps through frames in Update.
//   Returned to pool when animation completes.
//
// SharedEmit:
//   One ParticleSystem per impact type, shared across ALL hits of that type.
//   Call ps.Emit(params) at hit position — do NOT call Play().
//   Zero allocation. Ideal for high-rate weapons hitting surfaces.

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.PoolSystems;
using MidManStudio.Core.HelperFunctions;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Impact strategy enum
    // ─────────────────────────────────────────────────────────────────────────

    public enum ImpactStrategy
    {
        /// Use LocalParticlePool — good for most impacts.
        PooledParticleSystem,

        /// Use a pooled sprite-sheet flipbook — good for explosions.
        SpriteSheetFlipbook,

        /// Use ParticleSystem.Emit() on a shared system — best for SMG/shotgun high rate.
        SharedEmit
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Flipbook component — drives sprite-sheet animation on a pooled GO
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attached to flipbook pool GameObjects. Steps through sprite frames in Update.
    /// Returns itself to pool when animation completes.
    /// </summary>
    public sealed class ImpactFlipbook : MonoBehaviour
    {
        private Sprite[]       _frames;
        private SpriteRenderer _rend;
        private float          _frameDuration;
        private float          _timer;
        private int            _currentFrame;
        private PoolableObjectType _poolType;
        private bool           _active;

        public void Initialise(
            Sprite[] frames, float frameDuration, PoolableObjectType poolType)
        {
            _frames        = frames;
            _frameDuration = frameDuration;
            _poolType      = poolType;
            _rend          = GetComponent<SpriteRenderer>();
            _timer         = 0f;
            _currentFrame  = 0;
            _active        = true;

            if (_rend != null && _frames.Length > 0)
                _rend.sprite = _frames[0];
        }

        private void Update()
        {
            if (!_active || _frames == null || _frames.Length == 0) return;

            _timer += Time.deltaTime;
            int frame = Mathf.FloorToInt(_timer / _frameDuration);

            if (frame >= _frames.Length)
            {
                _active = false;
                LocalObjectPool.Instance?.ReturnObject(gameObject, _poolType);
                return;
            }

            if (frame != _currentFrame)
            {
                _currentFrame = frame;
                if (_rend != null) _rend.sprite = _frames[_currentFrame];
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Impact registration data
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    public sealed class ImpactRegistration
    {
        public ImpactStrategy Strategy = ImpactStrategy.PooledParticleSystem;

        // PooledParticleSystem
        public PoolableParticleType ParticleType;

        // SpriteSheetFlipbook
        public PoolableObjectType   FlipbookPoolType;
        public Sprite[]             FlipbookFrames;
        [Range(0.01f, 0.2f)]
        public float                FlipbookFrameDuration = 0.05f;

        // SharedEmit
        public ParticleSystem       SharedSystem;
        public int                  EmitCount = 10;
        public float                EmitSpeed = 3f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ProjectileImpactHandler
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Client-only singleton. Plays impact effects for confirmed hits.
    /// MID_ProjectileNetworkBridge calls PlayImpact() on HitConfirmedClientRpc.
    /// </summary>
    public sealed class ProjectileImpactHandler : Singleton<ProjectileImpactHandler>
    {
        #region Configuration

        [Header("Default Strategy")]
        [Tooltip("Used for configs with no registered strategy.")]
        [SerializeField] private PoolableParticleType _defaultParticleType;

        [Header("Per-Config Registrations")]
        [Tooltip("Assign in inspector or call RegisterStrategy() at runtime.\n" +
                 "Key = configId (ushort) as int for inspector compatibility.")]
        [SerializeField] private List<ConfigImpactBinding> _bindings
            = new List<ConfigImpactBinding>();

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        [Serializable]
        private struct ConfigImpactBinding
        {
            public int               ConfigIdInt; // ushort as int for inspector
            public ImpactRegistration Registration;
        }

        #endregion

        #region State

        private readonly Dictionary<ushort, ImpactRegistration> _strategies
            = new Dictionary<ushort, ImpactRegistration>(32);

        #endregion

        #region Initialisation

        protected override void Awake()
        {
            base.Awake();

            // Load inspector bindings
            foreach (var b in _bindings)
            {
                if (b.Registration != null)
                    _strategies[(ushort)b.ConfigIdInt] = b.Registration;
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Register an impact strategy for a configId at runtime.
        /// Call from your game's weapon setup flow.
        /// </summary>
        public void RegisterStrategy(ushort configId, ImpactRegistration registration)
        {
            _strategies[configId] = registration;
        }

        /// <summary>
        /// Play impact effect at the given world position.
        /// Called by MID_ProjectileNetworkBridge on HitConfirmedClientRpc.
        /// </summary>
        public void PlayImpact(Vector3 position, ushort configId, bool isHeadshot)
        {
            if (!_strategies.TryGetValue(configId, out var reg))
            {
                // Default: pool particle
                var cfg = ProjectileRegistry.Instance.Get(configId);
                PoolableParticleType pType = cfg != null
                    ? cfg.ImpactEffectType
                    : _defaultParticleType;

                LocalParticlePool.Instance?.GetObject(pType, position, Quaternion.identity);

                Log($"Impact (default pool): configId={configId} pos={position}");
                return;
            }

            switch (reg.Strategy)
            {
                case ImpactStrategy.PooledParticleSystem:
                    PlayPooled(reg, position, isHeadshot);
                    break;

                case ImpactStrategy.SpriteSheetFlipbook:
                    PlayFlipbook(reg, position);
                    break;

                case ImpactStrategy.SharedEmit:
                    PlaySharedEmit(reg, position);
                    break;
            }
        }

        #endregion

        #region Strategy Implementations

        private void PlayPooled(ImpactRegistration reg, Vector3 pos, bool isHeadshot)
        {
            LocalParticlePool.Instance?.GetObject(
                reg.ParticleType, pos, Quaternion.identity);

            Log($"Impact (pool): type={reg.ParticleType} headshot={isHeadshot}");
        }

        private void PlayFlipbook(ImpactRegistration reg, Vector3 pos)
        {
            if (reg.FlipbookFrames == null || reg.FlipbookFrames.Length == 0) return;

            var obj = LocalObjectPool.Instance?.GetObject(
                reg.FlipbookPoolType, pos, Quaternion.identity);

            if (obj == null) return;

            var fb = obj.GetComponent<ImpactFlipbook>();
            if (fb == null) fb = obj.AddComponent<ImpactFlipbook>();

            fb.Initialise(reg.FlipbookFrames, reg.FlipbookFrameDuration, reg.FlipbookPoolType);

            Log($"Impact (flipbook): frames={reg.FlipbookFrames.Length}");
        }

        private void PlaySharedEmit(ImpactRegistration reg, Vector3 pos)
        {
            if (reg.SharedSystem == null) return;

            var emitParams = new ParticleSystem.EmitParams
            {
                position   = pos,
                applyShapeToPosition = true
            };

            // Vary speed slightly for natural look
            for (int i = 0; i < reg.EmitCount; i++)
            {
                emitParams.velocity = UnityEngine.Random.onUnitSphere * reg.EmitSpeed;
                reg.SharedSystem.Emit(emitParams, 1);
            }

            Log($"Impact (shared emit): count={reg.EmitCount} pos={pos}");
        }

        #endregion

        #region Logging

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(ProjectileImpactHandler));
        }

        #endregion
    }
}
