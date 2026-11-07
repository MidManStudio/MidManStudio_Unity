// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "UIParticlePoolManager.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Pools
{
    /// <summary>One UI effect: the string key and the scene ParticleSystem to play. <c>useEmitMode</c> chooses Emit(count) instead of Play().</summary>
    [System.Serializable]
    public class UIEffectConfig
    {
        [Tooltip("String key used to trigger this effect from code.")]
        public string         key;

        public ParticleSystem particleSystem;

        [Tooltip("If true, uses Emit(count) instead of Play().\n" +
                 "Use for burst effects that must not loop.")]
        public bool           useEmitMode = false;
    }

    /// <summary>
    /// Plays ParticleSystems that are already in the HUD or UI hierarchy, by string key. It creates nothing and pools
    /// nothing, and there is no enum, so any key works. Put the component on the HUD root, add one
    /// <see cref="UIEffectConfig"/> per particle system and call <see cref="TriggerEffect"/> with the key, for example
    /// <c>TriggerEffect("WaveComplete")</c>. In <c>Awake</c> it stops all configured systems and deactivates their
    /// GameObjects. Play mode per effect: Play() or Emit(count).
    /// </summary>
    public class UIParticlePoolManager : MonoBehaviour
    {
        [SerializeField] private MID_LogLevel         _logLevel     = MID_LogLevel.Info;
        [SerializeField] private List<UIEffectConfig> _effectConfigs = new();

        private readonly Dictionary<string, UIEffectConfig> _lookup = new();

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            BuildLookup();
            DisableAll();
        }

        private void OnDestroy() => StopAll(clear: true);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Trigger an effect by key using the configured play mode.
        /// </summary>
        public void TriggerEffect(string key, int emitCount = 10)
        {
            if (!TryGet(key, out var cfg)) return;

            if (cfg.useEmitMode) EmitEffect(key, emitCount);
            else                 PlayEffect(key);
        }

        /// <summary>Play an effect continuously via ParticleSystem.Play().</summary>
        public void PlayEffect(string key)
        {
            if (!TryGet(key, out var cfg)) return;

            if (cfg.particleSystem.isPlaying)
                cfg.particleSystem.Stop(true,
                    ParticleSystemStopBehavior.StopEmitting);

            cfg.particleSystem.gameObject.SetActive(true);
            cfg.particleSystem.Play();

            MID_Logger.LogDebug(_logLevel, $"Playing: {key}",
                nameof(UIParticlePoolManager));
        }

        /// <summary>Emit a burst via ParticleSystem.Emit().</summary>
        public void EmitEffect(string key, int count = 10)
        {
            if (!TryGet(key, out var cfg)) return;

            cfg.particleSystem.gameObject.SetActive(true);
            cfg.particleSystem.Emit(count);

            MID_Logger.LogDebug(_logLevel, $"Emitted {count}: {key}",
                nameof(UIParticlePoolManager));
        }

        /// <summary>Stop a specific effect.</summary>
        public void StopEffect(string key, bool clear = true)
        {
            if (!TryGet(key, out var cfg)) return;

            cfg.particleSystem.Stop(true,
                clear
                    ? ParticleSystemStopBehavior.StopEmittingAndClear
                    : ParticleSystemStopBehavior.StopEmitting);

            if (clear)
                cfg.particleSystem.gameObject.SetActive(false);

            MID_Logger.LogDebug(_logLevel, $"Stopped: {key}",
                nameof(UIParticlePoolManager));
        }

        /// <summary>Stop all registered effects.</summary>
        public void StopAll(bool clear = true)
        {
            foreach (var cfg in _effectConfigs)
            {
                if (cfg?.particleSystem == null) continue;
                cfg.particleSystem.Stop(true,
                    clear
                        ? ParticleSystemStopBehavior.StopEmittingAndClear
                        : ParticleSystemStopBehavior.StopEmitting);
                if (clear)
                    cfg.particleSystem.gameObject.SetActive(false);
            }
        }

        /// <summary>True when the effect exists and its particle system is playing. Logs an error for an unknown key.</summary>
        public bool IsPlaying(string key) =>
            TryGet(key, out var cfg) && cfg.particleSystem.isPlaying;

        /// <summary>Returns the ParticleSystem for a key — for advanced control.</summary>
        public ParticleSystem GetSystem(string key) =>
            TryGet(key, out var cfg) ? cfg.particleSystem : null;

        /// <summary>Register an effect at runtime (e.g. from a dynamically spawned HUD).</summary>
        public void RegisterEffect(UIEffectConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.key)) return;
            _lookup[config.key] = config;
            if (!_effectConfigs.Contains(config)) _effectConfigs.Add(config);
        }

        /// <summary>Removes the effect with this key. The particle system itself is not touched.</summary>
        public void UnregisterEffect(string key)
        {
            _lookup.Remove(key);
            _effectConfigs.RemoveAll(c => c.key == key);
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void BuildLookup()
        {
            _lookup.Clear();
            foreach (var cfg in _effectConfigs)
            {
                if (cfg == null || string.IsNullOrEmpty(cfg.key)) continue;

                if (cfg.particleSystem == null)
                {
                    MID_Logger.LogError(_logLevel,
                        $"Null ParticleSystem on effect key '{cfg.key}'.",
                        nameof(UIParticlePoolManager));
                    continue;
                }

                if (_lookup.ContainsKey(cfg.key))
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Duplicate key '{cfg.key}' — keeping first.",
                        nameof(UIParticlePoolManager));
                    continue;
                }

                _lookup[cfg.key] = cfg;
            }

            MID_Logger.LogInfo(_logLevel,
                $"Registered {_lookup.Count} UI particle effect(s).",
                nameof(UIParticlePoolManager));
        }

        private void DisableAll()
        {
            foreach (var cfg in _effectConfigs)
            {
                if (cfg?.particleSystem == null) continue;
                cfg.particleSystem.Stop(true,
                    ParticleSystemStopBehavior.StopEmittingAndClear);
                cfg.particleSystem.gameObject.SetActive(false);
            }
        }

        private bool TryGet(string key, out UIEffectConfig cfg)
        {
            if (_lookup.TryGetValue(key, out cfg)) return true;

            MID_Logger.LogError(_logLevel,
                $"Effect key '{key}' not found.",
                nameof(UIParticlePoolManager));
            return false;
        }
    }
}
