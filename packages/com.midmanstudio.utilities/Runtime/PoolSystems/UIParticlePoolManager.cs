using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Pools
{

    /// <summary>
    /// UIParticlePoolManager — Manages UI particle systems placed directly in the HUD hierarchy.
    /// No instantiation — just plays/emits existing particle systems by enum type.
    ///
    /// USAGE:
    ///   Assign ParticleSystems in the inspector under Effect Configs.
    ///   Call TriggerEffect(UIEffectType.WaveComplete) from game code.
    ///   Uses Play() for continuous effects, Emit(count) for bursts — configured per-effect.
    /// </summary>
    public class UIParticlePoolManager : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("UI Particle Systems — Placed in HUD Hierarchy")]
        [SerializeField] private List<UIEffectConfig> effectConfigs = new List<UIEffectConfig>();

        #endregion

        #region Private Fields

        private Dictionary<UIEffectType, UIEffectConfig> _effectLookup = new Dictionary<UIEffectType, UIEffectConfig>();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeEffectLookup();
            DisableAllEffectsAtStart();
        }

        private void OnDestroy()
        {
            StopAllEffects(true);
        }

        #endregion

        #region Public Methods

        /// <summary>Plays a continuous particle effect via Play().</summary>
        public void PlayEffect(UIEffectType effectType)
        {
            if (!TryGetConfig(effectType, nameof(PlayEffect), out UIEffectConfig config)) return;

            if (config.particleSystem.isPlaying)
                config.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

            config.particleSystem.gameObject.SetActive(true);
            config.particleSystem.Play();

            MID_Logger.LogDebug(_logLevel, $"Playing: {effectType}.", nameof(UIParticlePoolManager), nameof(PlayEffect));
        }

        /// <summary>Emits a burst of particles via Emit(count).</summary>
        public void EmitEffect(UIEffectType effectType, int particleCount)
        {
            if (!TryGetConfig(effectType, nameof(EmitEffect), out UIEffectConfig config)) return;

            config.particleSystem.gameObject.SetActive(true);
            config.particleSystem.Emit(particleCount);

            MID_Logger.LogDebug(_logLevel, $"Emitted {particleCount} for: {effectType}.", nameof(UIParticlePoolManager), nameof(EmitEffect));
        }

        /// <summary>Triggers an effect using Play() or Emit() based on per-config useEmitMode setting.</summary>
        public void TriggerEffect(UIEffectType effectType, int particleCount = 10)
        {
            if (!TryGetConfig(effectType, nameof(TriggerEffect), out UIEffectConfig config)) return;

            if (config.useEmitMode) EmitEffect(effectType, particleCount);
            else PlayEffect(effectType);
        }

        /// <summary>Stops a specific particle effect.</summary>
        public void StopEffect(UIEffectType effectType, bool clearParticles = true)
        {
            if (!TryGetConfig(effectType, nameof(StopEffect), out UIEffectConfig config)) return;

            var stopBehavior = clearParticles
                ? ParticleSystemStopBehavior.StopEmittingAndClear
                : ParticleSystemStopBehavior.StopEmitting;

            config.particleSystem.Stop(true, stopBehavior);
            if (clearParticles) config.particleSystem.gameObject.SetActive(false);

            MID_Logger.LogDebug(_logLevel, $"Stopped: {effectType}.", nameof(UIParticlePoolManager), nameof(StopEffect));
        }

        /// <summary>Stops all active particle effects.</summary>
        public void StopAllEffects(bool clearParticles = true)
        {
            foreach (var config in effectConfigs)
            {
                if (config.particleSystem != null && config.particleSystem.isPlaying)
                {
                    var stopBehavior = clearParticles
                        ? ParticleSystemStopBehavior.StopEmittingAndClear
                        : ParticleSystemStopBehavior.StopEmitting;

                    config.particleSystem.Stop(true, stopBehavior);
                    if (clearParticles) config.particleSystem.gameObject.SetActive(false);
                }
            }

            MID_Logger.LogDebug(_logLevel, "Stopped all effects.", nameof(UIParticlePoolManager), nameof(StopAllEffects));
        }

        /// <summary>Returns true if the effect is currently playing.</summary>
        public bool IsEffectPlaying(UIEffectType effectType)
        {
            if (!_effectLookup.TryGetValue(effectType, out UIEffectConfig config)) return false;
            return config.particleSystem != null && config.particleSystem.isPlaying;
        }

        /// <summary>Returns the ParticleSystem for a given effect type for advanced control.</summary>
        public ParticleSystem GetParticleSystem(UIEffectType effectType)
        {
            if (_effectLookup.TryGetValue(effectType, out UIEffectConfig config))
                return config.particleSystem;

            MID_Logger.LogWarning(_logLevel, $"Effect type '{effectType}' not found.", nameof(UIParticlePoolManager), nameof(GetParticleSystem));
            return null;
        }

        #endregion

        #region Private Methods

        private void InitializeEffectLookup()
        {
            _effectLookup.Clear();

            foreach (var config in effectConfigs)
            {
                if (config.particleSystem == null)
                {
                    MID_Logger.LogError(_logLevel, $"Null ParticleSystem on effect type: {config.effectType}.", nameof(UIParticlePoolManager), nameof(InitializeEffectLookup));
                    continue;
                }

                if (_effectLookup.ContainsKey(config.effectType))
                {
                    MID_Logger.LogWarning(_logLevel, $"Duplicate effect type: {config.effectType} — using first entry.", nameof(UIParticlePoolManager), nameof(InitializeEffectLookup));
                    continue;
                }

                _effectLookup[config.effectType] = config;
            }

            MID_Logger.LogInfo(_logLevel, $"Initialized {_effectLookup.Count} UI particle effects.", nameof(UIParticlePoolManager), nameof(InitializeEffectLookup));
        }

        private void DisableAllEffectsAtStart()
        {
            foreach (var config in effectConfigs)
            {
                if (config.particleSystem != null)
                {
                    config.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    config.particleSystem.gameObject.SetActive(false);
                }
            }
        }

        private bool TryGetConfig(UIEffectType effectType, string callerName, out UIEffectConfig config)
        {
            if (!_effectLookup.TryGetValue(effectType, out config))
            {
                MID_Logger.LogError(_logLevel, $"Effect type '{effectType}' not found.", nameof(UIParticlePoolManager), callerName);
                return false;
            }

            if (config.particleSystem == null)
            {
                MID_Logger.LogError(_logLevel, $"ParticleSystem for '{effectType}' is null.", nameof(UIParticlePoolManager), callerName);
                return false;
            }

            return true;
        }

        #endregion

        #region Internal Types

        /// <summary>
        /// UI particle effect types for DuckDuckBara.
        /// These are particles already placed in the HUD hierarchy — not pooled GameObjects.
        /// </summary>
        public enum UIEffectType
        {
            DamageIndicator,    // Flash when player or capybara takes damage
            HealEffect,         // Visual feedback on heal upgrade
            WaveComplete,       // Plays between waves
            UpgradeSelect,      // Plays when upgrade card is chosen
            PlayerHurt,         // Screen-edge effect on player damage
        }

        [System.Serializable]
        public class UIEffectConfig
        {
            public UIEffectType effectType;
            public ParticleSystem particleSystem;
            [Tooltip("If true, uses Emit(count) instead of Play().")]
            public bool useEmitMode = false;
        }

        #endregion
    }
}