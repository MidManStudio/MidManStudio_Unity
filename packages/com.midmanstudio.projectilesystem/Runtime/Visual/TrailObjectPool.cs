// TrailObjectPool.cs
// Projectile-specific trail coordinator.
// Delegates all TrailRenderer lifecycle to TrailRendererPool (utilities package).
// Maps projectile IDs to trail slots and reads ProjectileConfigSO for trail config.
//
// Called by LocalProjectileManager / ServerProjectileAuthority every FixedUpdate
// via SyncToSimulation().

using UnityEngine;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    [RequireComponent(typeof(ProjectileManager))]
    public class TrailObjectPool : MonoBehaviour
    {
        [Header("Trail Pool")]
        [Tooltip("Shared TrailRendererPool instance. " +
                 "If null, TrailRendererPool.Instance is used.")]
        [SerializeField] private TrailRendererPool _trailPool;

        [Tooltip("Extra seconds a trail lingers after its projectile dies.\n" +
                 "Overrides the natural fade from TrailRendererPool.")]
        [SerializeField] private float _fadePad = 0.12f;

        // projId → trail slot index in TrailRendererPool
        private readonly System.Collections.Generic.Dictionary<uint, int>
            _projToSlot = new(256);

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            if (_trailPool == null)
                _trailPool = TrailRendererPool.Instance;

            if (_trailPool == null)
                Debug.LogWarning(
                    "[TrailObjectPool] No TrailRendererPool found — " +
                    "trails will not render. Add TrailRendererPool to the scene.");
        }

        // ── Called by sim managers ────────────────────────────────────────────

        /// <summary>
        /// Sync active 2D projectile positions to their trail slots.
        /// Call every FixedUpdate.
        /// </summary>
        public void SyncToSimulation(NativeProjectile[] projs, int count)
        {
            if (_trailPool == null) return;

            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (cfg == null || !cfg.HasTrail) continue;

                if (!_projToSlot.TryGetValue(p.ProjId, out int slot))
                {
                    slot = AcquireSlot(p, cfg);
                    if (slot < 0) continue;
                    _projToSlot[p.ProjId] = slot;
                }

                _trailPool.SetPosition(slot, new Vector3(p.X, p.Y, 0f));
            }
        }

        /// <summary>
        /// Notify that a projectile has died so its trail slot can fade out.
        /// Called by the sim manager during CompactDeadSlots.
        /// </summary>
        public void NotifyDead(uint projId)
        {
            if (!_projToSlot.TryGetValue(projId, out int slot)) return;

            _trailPool.Release(slot);
            _projToSlot.Remove(projId);
        }

        /// <summary>Release all active trail slots (e.g. on scene unload).</summary>
        public void ReleaseAll()
        {
            if (_trailPool == null) return;

            foreach (var slot in _projToSlot.Values)
                _trailPool.ForceRelease(slot);

            _projToSlot.Clear();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private int AcquireSlot(in NativeProjectile p, ProjectileConfigSO cfg)
        {
            var trailCfg = BuildTrailConfig(cfg);
            return _trailPool.Acquire(trailCfg, ownerId: (int)p.ProjId);
        }

        private static TrailConfig BuildTrailConfig(ProjectileConfigSO cfg)
        {
            // Determine gradient: use override if enabled, otherwise null (material default).
            Gradient gradient = cfg.UseGradientOverride ? cfg.TrailGradient : null;

            return new TrailConfig
            {
                Material      = cfg.TrailMaterial,
                ColorGradient = gradient,
                Time          = cfg.TrailTime > 0f ? cfg.TrailTime : 0.25f,
                StartWidth    = cfg.TrailStartWidth,
                EndWidth      = cfg.TrailEndWidth,
                CapVertices   = cfg.TrailCapVertices
            };
        }
    }
}
