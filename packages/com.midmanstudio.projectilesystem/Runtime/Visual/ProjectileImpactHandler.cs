// Client-side only. Manages impact effects for confirmed projectile hits.
// MID_ProjectileNetworkBridge calls PlayImpact() on HitConfirmedClientRpc.
//
// UPDATED: GlobalFXManager integration.
//   When _preferGlobalFX = true (default) AND GlobalFXManager.Instance is present,
//   PlayImpact routes through GlobalFXManager.TriggerImpact using a configurable
//   EffectType mapping. This uses the shared in-scene ParticleSystem pool rather
//   than the separate LocalParticlePool, keeping effects unified.
//
//   If GlobalFXManager is absent or _preferGlobalFX = false, the existing
//   strategy-based path (PooledParticleSystem / SpriteSheetFlipbook / SharedEmit)
//   is used unchanged.
//
//   Per-config EffectType overrides can be set via _configEffectTypeBindings in the
//   inspector, or registered at runtime via RegisterConfigEffectType(). Unregistered
//   configs fall back to _defaultGlobalFXType (Generic by default).
//
// IMPACT STRATEGIES (when GlobalFXManager is not used):
//   PooledParticleSystem — LocalParticlePool (standard hits)
//   SpriteSheetFlipbook  — pooled GameObjects with SpriteRenderer + ImpactFlipbook
//   SharedEmit           — ParticleSystem.Emit() for very high hit rates
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
using MidManStudio.Core.FX;
using MidManStudio.Projectiles.Config;
using MidManStudio.Core.Audio;

namespace MidManStudio.Projectiles.Visuals
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
        private Sprite[]           _frames;
        private SpriteRenderer     _rend;
        private float              _frameDuration;
        private float              _timer;
        private int                _frame;
        private PoolableObjectType _poolType;
        private bool               _active;

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

    // ── Per-config GlobalFX type override ─────────────────────────────────────

    [Serializable]
    public struct ConfigEffectTypeBinding
    {
        [Tooltip("ushort config ID cast to int (inspector limitation).")]
        public int        ConfigIdInt;
        public EffectType EffectType;
        public int        ParticleCount;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public sealed class ProjectileImpactHandler : Singleton<ProjectileImpactHandler>
    {
        #region Serialized

        [Header("GlobalFXManager Integration")]
        [Tooltip("When true and GlobalFXManager.Instance is present, impacts are routed\n" +
                 "through GlobalFXManager.TriggerImpact instead of LocalParticlePool.\n" +
                 "Keeps all particle effects managed by one system.")]
        [SerializeField] private bool _preferGlobalFX = true;

        [Tooltip("EffectType used for configs with no specific binding.\n" +
                 "Generic maps to any FXEntry with EffectType.Generic in GlobalFXManager.")]
        [SerializeField] private EffectType _defaultGlobalFXType = EffectType.Generic;

        [Tooltip("Default particle count when not specified by a binding.")]
        [SerializeField, Min(1)] private int _defaultGlobalFXParticleCount = 6;

        [Header("Per-Config GlobalFX Overrides")]
        [Tooltip("Assign specific EffectType and particle count per config ID.\n" +
                 "Unregistered configs use _defaultGlobalFXType.")]
        [SerializeField] private List<ConfigEffectTypeBinding> _configEffectTypeBindings
            = new List<ConfigEffectTypeBinding>();

        [Header("Headshot FX")]
        [Tooltip("EffectType used for headshot impacts (GlobalFX path only).")]
        [SerializeField] private EffectType _headshotEffectType = EffectType.FleshSurface;
        [SerializeField, Min(1)] private int _headshotParticleCount = 12;

        [Header("Default Particle Type (non-GlobalFX fallback)")]
        [Tooltip("Used when no strategy is registered for a configId and GlobalFX is disabled/absent.\n" +
                 "Set to your generic hit particle type.")]
        [SerializeField] private PoolableParticleType _defaultParticleType;

        [Header("Per-Config Strategy Bindings (non-GlobalFX path)")]
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

        // Non-GlobalFX strategies
        private readonly Dictionary<ushort, ImpactRegistration> _strategies = new(32);

        // GlobalFX per-config overrides
        private readonly Dictionary<ushort, ConfigEffectTypeBinding> _fxBindings = new(32);

        #endregion

        #region Init

        protected override void Awake()
        {
            base.Awake();

            // Register non-GlobalFX strategies from inspector
            foreach (var b in _bindings)
                if (b.Registration != null)
                    _strategies[(ushort)b.ConfigIdInt] = b.Registration;

            // Register GlobalFX per-config bindings from inspector
            foreach (var b in _configEffectTypeBindings)
                _fxBindings[(ushort)b.ConfigIdInt] = b;
        }

        #endregion

        #region Public API — Registration

        /// <summary>Register a non-GlobalFX impact strategy for a projectile config ID.</summary>
        public void RegisterStrategy(ushort configId, ImpactRegistration registration)
        {
            _strategies[configId] = registration;
        }

        public void UnregisterStrategy(ushort configId) => _strategies.Remove(configId);

        /// <summary>
        /// Register a GlobalFXManager EffectType override for a specific config ID.
        /// Only applies when _preferGlobalFX = true and GlobalFXManager.Instance != null.
        /// </summary>
        public void RegisterConfigEffectType(
            ushort configId, EffectType effectType, int particleCount = -1)
        {
            _fxBindings[configId] = new ConfigEffectTypeBinding
            {
                ConfigIdInt   = configId,
                EffectType    = effectType,
                ParticleCount = particleCount > 0 ? particleCount : _defaultGlobalFXParticleCount
            };
        }

        public void UnregisterConfigEffectType(ushort configId)
            => _fxBindings.Remove(configId);

        #endregion

        #region Public API — Play

        /// <summary>
        /// Play an impact effect at the given world position.
        /// Called by the network bridge on HitConfirmedClientRpc.
        ///
        /// Routing priority:
        ///   1. GlobalFXManager (when _preferGlobalFX and instance present)
        ///   2. Per-config ImpactRegistration strategy
        ///   3. Default LocalParticlePool fallback
        /// </summary>
        public void PlayImpact(Vector3 position, ushort configId, bool isHeadshot = false)
        {
            // ── Route 1: GlobalFXManager ──────────────────────────────────────
            if (_preferGlobalFX && GlobalFXManager.Instance != null)
            {
                PlayGlobalFX(position, configId, isHeadshot);
                return;
            }

            // ── Route 2 + 3: legacy strategy / LocalParticlePool ──────────────
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

        #region GlobalFX Path

        private void PlayGlobalFX(Vector3 position, ushort configId, bool isHeadshot)
        {
            // Headshot override takes priority over per-config binding
            if (isHeadshot && _headshotEffectType != _defaultGlobalFXType)
            {
                GlobalFXManager.Instance.TriggerImpact(
                    _headshotEffectType,
                    position,
                    Vector3.up,
                    _headshotParticleCount);

                MID_Logger.LogDebug(_logLevel,
                    $"Impact (GlobalFX headshot) configId={configId} type={_headshotEffectType}",
                    nameof(ProjectileImpactHandler));
                return;
            }

            // Per-config binding
            EffectType effectType    = _defaultGlobalFXType;
            int        particleCount = _defaultGlobalFXParticleCount;

            if (_fxBindings.TryGetValue(configId, out var binding))
            {
                effectType    = binding.EffectType;
                particleCount = binding.ParticleCount > 0
                    ? binding.ParticleCount
                    : _defaultGlobalFXParticleCount;
            }
            else
            {
                // Try to derive effect type from the config's ImpactEffectType particle type
                // (maps common PoolableParticleType names to EffectType when possible)
                var cfg = ProjectileRegistry.HasInstance
                    ? ProjectileRegistry.Instance.Get(configId)
                    : null;
                if (cfg != null)
                    effectType = DeriveEffectTypeFromConfig(cfg, isHeadshot);
            }

            GlobalFXManager.Instance.TriggerImpact(
                effectType,
                position,
                Vector3.up,         // normal — caller can override via RegisterConfigEffectType
                particleCount);

            MID_Logger.LogDebug(_logLevel,
                $"Impact (GlobalFX) configId={configId} type={effectType} " +
                $"count={particleCount} headshot={isHeadshot}",
                nameof(ProjectileImpactHandler));
        }

        /// <summary>
        /// Best-effort mapping from ProjectileConfigSO to a GlobalFX EffectType.
        /// If the config has a registered per-config FX binding that's already been
        /// checked, this is the fallback — returns Generic unless you add custom
        /// mapping logic here or use RegisterConfigEffectType() at startup.
        /// </summary>
        private EffectType DeriveEffectTypeFromConfig(ProjectileConfigSO cfg, bool isHeadshot)
        {
            if (isHeadshot) return _headshotEffectType;

            // Extend this switch to map your PoolableParticleType values to EffectTypes.
            // For example:
            //   case PoolableParticleType.Projectile_Headshot: return EffectType.FleshSurface;
            //   case PoolableParticleType.Projectile_Explosion_Large: return EffectType.LargeExplosion;
            //
            // The default is Generic which matches any FXEntry with EffectType.Generic.
            return cfg.ImpactEffectType switch
            {
                // Explosion variants → map to explosion EffectType
                PoolableParticleType.Projectile_Explosion_Small  => EffectType.SmallExplosion,
                PoolableParticleType.Projectile_Explosion_Medium => EffectType.MediumExplosion,
                PoolableParticleType.Projectile_Explosion_Large  => EffectType.LargeExplosion,
                // Headshot particle → flesh surface
                PoolableParticleType.Projectile_Headshot         => EffectType.FleshSurface,
                // Everything else → Generic
                _                                                => EffectType.Generic
            };
        }

        #endregion

        #region Non-GlobalFX Strategies

        private void PlayDefault(Vector3 pos, ushort configId)
        {
            // Try to get a particle type from the registry config; fall back to default
            PoolableParticleType pType = _defaultParticleType;
            var cfg = ProjectileRegistry.HasInstance
                ? ProjectileRegistry.Instance.Get(configId)
                : null;
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
