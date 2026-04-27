// ProjectileImpactHandler.cs
// Client-side only. Manages impact effects for confirmed projectile hits.
// MID_ProjectileNetworkBridge calls PlayImpact() on HitConfirmedClientRpc.
//
// IMPACT STRATEGIES:
//   PooledParticleSystem — LocalParticlePool (standard hits)
//   SpriteSheetFlipbook  — pooled GameObjects with SpriteRenderer + ImpactFlipbook
//   SharedEmit           — ParticleSystem.Emit() for very high hit rates
//
// REGISTRATION:
//   Assign strategies in the inspector via ConfigImpactBindings,
//   or call RegisterStrategy() at runtime from your weapon setup flow.
//
// POOL TYPES:
//   All particle types use the generated PoolableParticleType enum.
//   Flipbook objects use PoolableObjectType.

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Pools;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles
{
    // ── Impact strategy ───────────────────────────────────────────────────────

    public enum ImpactStrategy
    {
        PooledParticleSystem,
        SpriteSheetFlipbook,
        SharedEmit
    }

    // ── Flipbook component ────────────────────────────────────────────────────

    /// <summary>
    /// Drives sprite-sheet animation on a pooled GameObject.
    /// Returns to pool when animation ends.
    /// </summary>
    public sealed class ImpactFlipbook : MonoBehaviour
    {
        private Sprite[]        _frames;
        private SpriteRenderer  _rend;
        private float           _frameDuration;
        private float           _timer;
        private int             _frame;
        private PoolableObjectType _poolType;
        private bool            _active;

        public void Initialise(Sprite[] frames, float frameDuration,
                               PoolableObjectType poolType)
        {
            _frames        = frames;
            _frameDuration = frameDuration;
            _poolType      = poolType;
            _rend          = GetComponent<SpriteRenderer>();
            _timer         = 0f;
            _frame         = 0;
            _active        = true;

            if (_rend != null && frames.Length > 0)
                _rend.sprite = frames[0];
        }

        private void Update()
        {
            if (!_active || _frames == null || _frames.Length == 0) return;

            _timer += Time.deltaTime;
            int f   = Mathf.FloorToInt(_timer / _frameDuration);

            if (f >= _frames.Length)
            {
                _active = false;
                LocalObjectPool.Instance?.ReturnObject(gameObject, _poolType);
                return;
            }

            if (f != _frame)
            {
                _frame = f;
                if (_rend != null) _rend.sprite = _frames[_frame];
            }
        }
    }

    // ── Registration data ─────────────────────────────────────────────────────

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
        public ParticleSystem SharedSystem;
        public int            EmitCount = 10;
        public float          EmitSpeed = 3f;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public sealed class ProjectileImpactHandler : Singleton<ProjectileImpactHandler>
    {
        #region Serialized

        [Header("Default Particle Type")]
        [Tooltip("Used when no strategy is registered for a configId.\n" +
                 "Set to your generic hit particle type.")]
        [SerializeField] private PoolableParticleType _defaultParticleType;

        [Header("Per-Config Bindings")]
        [Tooltip("Assign in inspector or call RegisterStrategy() at runtime.\n" +
                 "ConfigIdInt = ushort config ID cast to int (inspector limitation).")]
        [SerializeField] private List<ConfigBinding> _bindings = new();

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        [Serializable]
        private struct ConfigBinding
        {
            public int               ConfigIdInt;
            public ImpactRegistration Registration;
        }

        #endregion

        #region State

        private readonly Dictionary<ushort, ImpactRegistration> _strategies = new(32);

        #endregion

        #region Init

        protected override void Awake()
        {
            base.Awake();
            foreach (var b in _bindings)
                if (b.Registration != null)
                    _strategies[(ushort)b.ConfigIdInt] = b.Registration;
        }

        #endregion

        #region Public API

        /// <summary>Register an impact strategy for a projectile config ID.</summary>
        public void RegisterStrategy(ushort configId, ImpactRegistration registration)
        {
            _strategies[configId] = registration;
        }

        public void UnregisterStrategy(ushort configId) => _strategies.Remove(configId);

        /// <summary>
        /// Play an impact effect at the given world position.
        /// Called by the network bridge on HitConfirmedClientRpc.
        /// </summary>
        public void PlayImpact(Vector3 position, ushort configId, bool isHeadshot = false)
        {
            if (!_strategies.TryGetValue(configId, out var reg))
            {
                PlayDefault(position, configId);
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

        #region Strategies

        private void PlayDefault(Vector3 pos, ushort configId)
        {
            // Try to get a particle type from the registry config; fall back to default
            PoolableParticleType pType = _defaultParticleType;
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg != null && cfg.ImpactEffectType != _defaultParticleType)
                pType = cfg.ImpactEffectType;

            LocalParticlePool.Instance?.GetObject(pType, pos, Quaternion.identity);

            MID_Logger.LogDebug(_logLevel,
                $"Impact (default pool) configId={configId} type={pType}",
                nameof(ProjectileImpactHandler));
        }

        private void PlayPooled(ImpactRegistration reg, Vector3 pos, bool headshot)
        {
            LocalParticlePool.Instance?.GetObject(
                reg.ParticleType, pos, Quaternion.identity);

            MID_Logger.LogDebug(_logLevel,
                $"Impact (pool) type={reg.ParticleType} headshot={headshot}",
                nameof(ProjectileImpactHandler));
        }

        private void PlayFlipbook(ImpactRegistration reg, Vector3 pos)
        {
            if (reg.FlipbookFrames == null || reg.FlipbookFrames.Length == 0) return;

            var obj = LocalObjectPool.Instance?.GetObject(
                reg.FlipbookPoolType, pos, Quaternion.identity);
            if (obj == null) return;

            var fb = obj.GetComponent<ImpactFlipbook>()
                  ?? obj.AddComponent<ImpactFlipbook>();
            fb.Initialise(reg.FlipbookFrames, reg.FlipbookFrameDuration,
                          reg.FlipbookPoolType);

            MID_Logger.LogDebug(_logLevel,
                $"Impact (flipbook) frames={reg.FlipbookFrames.Length}",
                nameof(ProjectileImpactHandler));
        }

        private void PlaySharedEmit(ImpactRegistration reg, Vector3 pos)
        {
            if (reg.SharedSystem == null) return;

            for (int i = 0; i < reg.EmitCount; i++)
            {
                var ep = new ParticleSystem.EmitParams
                {
                    position             = pos,
                    velocity             = UnityEngine.Random.onUnitSphere * reg.EmitSpeed,
                    applyShapeToPosition = true
                };
                reg.SharedSystem.Emit(ep, 1);
            }

            MID_Logger.LogDebug(_logLevel,
                $"Impact (shared emit) count={reg.EmitCount}",
                nameof(ProjectileImpactHandler));
        }

        #endregion
    }
}
