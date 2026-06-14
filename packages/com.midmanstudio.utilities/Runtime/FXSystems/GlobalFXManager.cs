// GlobalFXManager.cs
// Unified CPU-based visual + audio effect manager.
//
// ADDED: Convenience overloads for TriggerImpact, TriggerMuzzleFlash, and EjectShell
//   that do not require an EffectType argument. These default to EffectType.Generic
//   (Impact/Explosion) or EffectType.SmallMuzzle (MuzzleFlash) / EffectType.BrassShell
//   (ShellEjection). This preserves backward compatibility with call sites that were
//   written before EffectType was added to the API.
//
// ADDED: volumeOverride parameter alias on the no-type TriggerImpact overload to
//   match the named-parameter call style used in TestSceneBootstrapper.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Audio;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.FX;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Audio
{
    public class GlobalFXManager : Singleton<GlobalFXManager>
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Effects  —  set Simulation Space = World on every ParticleSystem")]
        [MID_NamedList]
        [SerializeField] private List<FXEntry> _effects = new();

        [Header("Spatial Throttling")]
        [Tooltip("Minimum world-space distance between same-category emissions in the same frame.")]
        [SerializeField] private float _spatialThrottleRadius = 0.4f;

        [Header("Per-Category Frame Caps")]
        [SerializeField] private int _maxImpactPerFrame      = 12;
        [SerializeField] private int _maxMuzzlePerFrame      = 8;
        [SerializeField] private int _maxShellPerFrame       = 16;
        [SerializeField] private int _maxExplosionPerFrame   = 4;
        [Tooltip("Cap applied to any user-added category not listed above.")]
        [SerializeField] private int _maxOtherPerFrame       = 8;

        [Header("Audio Integration")]
        [SerializeField] private MID_NativeAudioBridge _audio;

        [Tooltip("Clip index (in NativeAudioBridge) for each default category.\n" +
                 "Set to -1 to skip audio for that category.")]
        [SerializeField] private int _impactAudioClipIndex    = 0;
        [SerializeField] private int _muzzleAudioClipIndex    = 1;
        [SerializeField] private int _shellAudioClipIndex     = 2;
        [SerializeField] private int _explosionAudioClipIndex = -1;

        // ── Runtime state ─────────────────────────────────────────────────────

        private readonly Dictionary<long, FXEntry> _lookup = new();
        private readonly List<(EffectCategory cat, Vector3 pos)> _frameEmissions = new(64);
        private readonly Dictionary<int, int> _frameCounts = new();
        private ParticleSystem.EmitParams _emitParams;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            BuildLookup();
            ValidateSystems();

            if (_audio == null)
                _audio = FindObjectOfType<MID_NativeAudioBridge>();
        }

        private void LateUpdate()
        {
            _frameEmissions.Clear();
            _frameCounts.Clear();
        }

        // ── Public API — primary (with EffectType) ────────────────────────────

        /// <summary>Trigger an effect by category and type at the given world position.</summary>
        public void TriggerEffect(EffectCategory category, EffectType effectType,
                                   Vector3 position, Vector3 normal ,
                                   int particleCount = -1, float audioVolume = 1f)
            => TriggerEffect((int)category, (int)effectType, position, normal, particleCount, audioVolume);

        public void TriggerEffect(int category, int effectType,
                                   Vector3 position, Vector3 normal,
                                   int particleCount = -1, float audioVolume = 1f)
        {
            if (!CanEmit((EffectCategory)category, position)) return;

            long key = MakeKey(category, effectType);
            if (!_lookup.TryGetValue(key, out var entry))
            {
                long fallback = MakeKey(category, (int)EffectType.Generic);
                if (!_lookup.TryGetValue(fallback, out entry))
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"No FXEntry for category={category} type={effectType} (no Generic fallback either).",
                        nameof(GlobalFXManager));
                    return;
                }
            }

            if (entry.particleSystem == null) return;

            int count = particleCount >= 0 ? particleCount : entry.defaultParticleCount;

            _emitParams.position             = position;
            if (normal != Vector3.zero)
            {
                _emitParams.rotation3D = Quaternion.LookRotation(normal).eulerAngles;
            }
           _emitParams.applyShapeToPosition = true;
            entry.particleSystem.Emit(_emitParams, count);

            RecordEmission((EffectCategory)category, position);
            PlayCategoryAudio((EffectCategory)category, audioVolume);
        }

        // ── Public API — with EffectType (explicit) ───────────────────────────

        /// <summary>Trigger an impact effect at a surface hit point.</summary>
        public void TriggerImpact(EffectType type, Vector3 position, Vector3 normal,
                                   int particleCount = -1, float audioVolume = 1f)
            => TriggerEffect(EffectCategory.Impact, type, position, normal, particleCount, audioVolume);

        /// <summary>Trigger a muzzle flash at a weapon barrel position.</summary>
        public void TriggerMuzzleFlash(EffectType type, Vector3 position, Vector3 direction,
                                        int particleCount = -1, float audioVolume = 0.8f)
            => TriggerEffect(EffectCategory.MuzzleFlash, type, position, direction, particleCount, audioVolume);

        /// <summary>Eject a shell casing with explicit EffectType.</summary>
        public void EjectShell(EffectType type, Vector3 position, Vector3 ejectionVelocity,
                                float audioVolume = 0.4f)
        {
            if (!CanEmit(EffectCategory.ShellEjection, position)) return;

            long key = MakeKey((int)EffectCategory.ShellEjection, (int)type);
            if (!_lookup.TryGetValue(key, out var entry)) return;
            if (entry.particleSystem == null) return;

            _emitParams.position             = position;
            _emitParams.velocity             = ejectionVelocity;
            _emitParams.applyShapeToPosition = false;
            entry.particleSystem.Emit(_emitParams, 1);

            RecordEmission(EffectCategory.ShellEjection, position);
            PlayCategoryAudio(EffectCategory.ShellEjection, audioVolume);
        }

        // ── Public API — convenience overloads (no EffectType) ───────────────
        // These default to Generic/SmallMuzzle/BrassShell for backward compatibility
        // with call sites that do not specify an EffectType.

        /// <summary>
        /// Trigger a generic impact effect. Defaults to EffectType.Generic.
        /// Use TriggerImpact(EffectType, ...) for surface-specific effects.
        /// </summary>
        public void TriggerImpact(Vector3 position, Vector3 normal,
                                   int particleCount = -1, float volumeOverride = 1f)
            => TriggerImpact(EffectType.Generic, position, normal, particleCount, volumeOverride);

        /// <summary>
        /// Trigger a generic muzzle flash. Defaults to EffectType.SmallMuzzle.
        /// Use TriggerMuzzleFlash(EffectType, ...) for weapon-specific flashes.
        /// </summary>
        public void TriggerMuzzleFlash(Vector3 position, Vector3 direction,
                                        int particleCount = -1, float audioVolume = 0.8f)
            => TriggerMuzzleFlash(EffectType.SmallMuzzle, position, direction, particleCount, audioVolume);

        /// <summary>
        /// Eject a generic brass shell. Defaults to EffectType.BrassShell.
        /// </summary>
        public void EjectShell(Vector3 position, Vector3 ejectionVelocity, float audioVolume = 0.4f)
            => EjectShell(EffectType.BrassShell, position, ejectionVelocity, audioVolume);

        // ── TryGetEffect ──────────────────────────────────────────────────────

        public bool TryGetEffect(EffectCategory category, EffectType type, out ParticleSystem ps)
        {
            ps = null;
            if (!_lookup.TryGetValue(MakeKey((int)category, (int)type), out var entry)) return false;
            ps = entry.particleSystem;
            return ps != null;
        }

        // ── Private — throttling ──────────────────────────────────────────────

        private bool CanEmit(EffectCategory category, Vector3 position)
        {
            int cat = (int)category;
            int max = category switch
            {
                EffectCategory.Impact        => _maxImpactPerFrame,
                EffectCategory.MuzzleFlash   => _maxMuzzlePerFrame,
                EffectCategory.ShellEjection => _maxShellPerFrame,
                EffectCategory.Explosion     => _maxExplosionPerFrame,
                _                            => _maxOtherPerFrame
            };

            _frameCounts.TryGetValue(cat, out int currentCount);
            if (currentCount >= max) return false;

            if (category == EffectCategory.Impact || category == EffectCategory.Explosion)
            {
                float rSq = _spatialThrottleRadius * _spatialThrottleRadius;
                for (int i = 0; i < _frameEmissions.Count; i++)
                {
                    var (emittedCat, emittedPos) = _frameEmissions[i];
                    if (emittedCat == category &&
                        Vector3.SqrMagnitude(emittedPos - position) < rSq)
                        return false;
                }
            }

            return true;
        }

        private void RecordEmission(EffectCategory category, Vector3 position)
        {
            _frameEmissions.Add((category, position));
            int cat = (int)category;
            _frameCounts[cat] = (_frameCounts.TryGetValue(cat, out int c) ? c : 0) + 1;
        }

        // ── Private — audio routing ───────────────────────────────────────────

        private void PlayCategoryAudio(EffectCategory category, float volume)
        {
            if (_audio == null) return;
            int clipIndex = category switch
            {
                EffectCategory.Impact        => _impactAudioClipIndex,
                EffectCategory.MuzzleFlash   => _muzzleAudioClipIndex,
                EffectCategory.ShellEjection => _shellAudioClipIndex,
                EffectCategory.Explosion     => _explosionAudioClipIndex,
                _                            => -1
            };
            if (clipIndex >= 0) _audio.PlayClip(clipIndex, volume);
        }

        // ── Private — setup ───────────────────────────────────────────────────

        private void BuildLookup()
        {
            _lookup.Clear();
            int duplicates = 0;

            foreach (var entry in _effects)
            {
                if (entry.particleSystem == null)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"FXEntry '{entry.Name}' has null ParticleSystem — skipping.",
                        nameof(GlobalFXManager));
                    continue;
                }

                long key = MakeKey((int)entry.category, (int)entry.effectType);
                if (_lookup.ContainsKey(key))
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Duplicate FXEntry for category={entry.category} type={entry.effectType}. " +
                        "First entry wins — remove the duplicate.",
                        nameof(GlobalFXManager));
                    duplicates++;
                    continue;
                }
                _lookup[key] = entry;
            }

            MID_Logger.LogInfo(_logLevel,
                $"FX lookup built — {_lookup.Count} entries" +
                (duplicates > 0 ? $", {duplicates} duplicate(s) skipped" : "") + ".",
                nameof(GlobalFXManager));
        }

        private void ValidateSystems()
        {
            foreach (var entry in _effects)
            {
                if (entry.particleSystem == null) continue;
                var main = entry.particleSystem.main;

                if (main.simulationSpace != ParticleSystemSimulationSpace.World)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"FXEntry '{entry.Name}': ParticleSystem must use Simulation Space = World. " +
                        "Auto-correcting.",
                        nameof(GlobalFXManager));
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                }

                if (main.loop)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"FXEntry '{entry.Name}': Loop is enabled — disabling.",
                        nameof(GlobalFXManager));
                    main.loop = false;
                }
            }
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static long MakeKey(int category, int effectType) =>
            (long)category * 100_000L + effectType;
    }
}
