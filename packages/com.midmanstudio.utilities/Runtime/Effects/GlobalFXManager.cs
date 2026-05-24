// GlobalFXManager.cs
// Unified CPU-based impact/muzzle/shell effect manager.
// Uses EmitParams to burst particles from a single world-space ParticleSystem
// per effect category, without moving transforms.
//
// Integrates with LocalParticlePool for particle type management.
// All particle systems should be set to Simulation Space = World in Inspector.
//
// SETUP:
//   1. Add to your Managers prefab (DontDestroyOnLoad).
//   2. Assign ParticleSystem references in Inspector.
//   3. Set Simulation Space = World on every assigned ParticleSystem.
//   4. Disable Loop and Play On Awake on every assigned ParticleSystem.
//   5. Call TriggerImpact / TriggerMuzzleFlash / EjectShell from game code.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Audio;
namespace MidManStudio.Core.Effects
{
    public class GlobalFXManager : Singleton<GlobalFXManager>
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("Particle Systems — set Simulation Space = World on each")]
        [SerializeField] private ParticleSystem _impactSystem;
        [SerializeField] private ParticleSystem _muzzleFlashSystem;
        [SerializeField] private ParticleSystem _shellEjectionSystem;

        [Header("Throttling")]
        [Tooltip("Minimum world-space distance between simultaneous impacts " +
                 "in the same frame before the second is dropped.")]
        [SerializeField] private float _spatialThrottleRadius = 0.4f;

        [Tooltip("Hard cap on total impacts emitted per frame across all positions.")]
        [SerializeField] private int _maxImpactsPerFrame = 12;

        [Tooltip("Hard cap on muzzle flashes per frame.")]
        [SerializeField] private int _maxMuzzlePerFrame = 8;

        [Header("Audio Integration")]
        [SerializeField] private MID_NativeAudioBridge _nativeAudio;

        [Tooltip("Index into NativeAudioBridge._clips for impact sounds.")]
        [SerializeField] private int _impactClipIndex = 0;

        [Tooltip("Index into NativeAudioBridge._clips for muzzle sounds.")]
        [SerializeField] private int _muzzleClipIndex = 1;

        [SerializeField] private int _shellClipIndex = 2;

        // ── Private State ─────────────────────────────────────────────────────

        // Spatial throttle tracking — cleared every LateUpdate
        private readonly List<Vector3> _frameImpacts = new(32);
        private int _frameImpactCount;
        private int _frameMuzzleCount;

        // Pre-allocated EmitParams structs — never allocate in hot path
        private ParticleSystem.EmitParams _impactParams;
        private ParticleSystem.EmitParams _muzzleParams;
        private ParticleSystem.EmitParams _shellParams;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            ValidateSystems();
        }

        private void LateUpdate()
        {
            _frameImpacts.Clear();
            _frameImpactCount = 0;
            _frameMuzzleCount  = 0;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Spawn a burst of impact particles at the hit position, oriented along
        /// the surface normal. Spatially throttled — duplicate nearby hits are dropped.
        /// Also triggers the impact sound via NativeAudioBridge if assigned.
        /// </summary>
        /// <param name="position">World-space hit position.</param>
        /// <param name="normal">Surface normal at the hit point.</param>
        /// <param name="particleCount">Number of particles to emit.</param>
        /// <param name="volumeOverride">0–1 volume for the impact sound. -1 = default.</param>
        public void TriggerImpact(Vector3 position, Vector3 normal,
                                   int particleCount = 6, float volumeOverride = -1f)
        {
            if (_impactSystem == null) return;
            if (_frameImpactCount >= _maxImpactsPerFrame) return;

            // Spatial throttle
            float radiusSq = _spatialThrottleRadius * _spatialThrottleRadius;
            foreach (var prev in _frameImpacts)
            {
                if (Vector3.SqrMagnitude(prev - position) < radiusSq)
                    return; // Too close to an existing hit this frame
            }

            _frameImpacts.Add(position);
            _frameImpactCount++;

            // Emit — no transform move, no allocations
            // applyShapeToPosition = false → particles come from exact hit point
            _impactParams.position           = position;
            _impactParams.rotation3D         = Quaternion.LookRotation(normal).eulerAngles;
            _impactParams.applyShapeToPosition = false;

            _impactSystem.Emit(_impactParams, particleCount);

            // Sound
            if (_nativeAudio != null)
            {
                float vol = volumeOverride >= 0f ? volumeOverride : 1f;
                _nativeAudio.PlayClip(_impactClipIndex, vol);
            }
        }

        /// <summary>
        /// Burst a muzzle flash at the gun tip position, oriented along the fire direction.
        /// </summary>
        public void TriggerMuzzleFlash(Vector3 position, Vector3 direction,
                                        int particleCount = 3, float volume = 0.8f)
        {
            if (_muzzleFlashSystem == null) return;
            if (_frameMuzzleCount >= _maxMuzzlePerFrame) return;

            _frameMuzzleCount++;

            _muzzleParams.position             = position;
            _muzzleParams.rotation3D           = Quaternion.LookRotation(direction).eulerAngles;
            _muzzleParams.applyShapeToPosition = false;

            _muzzleFlashSystem.Emit(_muzzleParams, particleCount);

            if (_nativeAudio != null)
                _nativeAudio.PlayClip(_muzzleClipIndex, volume);
        }

        /// <summary>
        /// Eject a shell casing from a weapon ejection port.
        /// ejectionVelocity drives the particle's initial velocity.
        /// </summary>
        public void EjectShell(Vector3 position, Vector3 ejectionVelocity,
                                float volume = 0.4f)
        {
            if (_shellEjectionSystem == null) return;

            _shellParams.position             = position;
            _shellParams.velocity             = ejectionVelocity;
            _shellParams.applyShapeToPosition = false;

            _shellEjectionSystem.Emit(_shellParams, 1);

            if (_nativeAudio != null)
                _nativeAudio.PlayClip(_shellClipIndex, volume);
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void ValidateSystems()
        {
            CheckSystem(_impactSystem,      "Impact");
            CheckSystem(_muzzleFlashSystem, "MuzzleFlash");
            CheckSystem(_shellEjectionSystem, "ShellEjection");
        }

        private void CheckSystem(ParticleSystem ps, string label)
        {
            if (ps == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"GlobalFXManager: {label} ParticleSystem not assigned.",
                    nameof(GlobalFXManager));
                return;
            }

            var main = ps.main;
            if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"GlobalFXManager: {label} ParticleSystem must have " +
                    "Simulation Space = World. Fixing automatically.",
                    nameof(GlobalFXManager));
                main.simulationSpace = ParticleSystemSimulationSpace.World;
            }

            if (main.loop)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"GlobalFXManager: {label} ParticleSystem has Loop enabled — disabling.",
                    nameof(GlobalFXManager));
                main.loop = false;
            }
        }
    }
}
