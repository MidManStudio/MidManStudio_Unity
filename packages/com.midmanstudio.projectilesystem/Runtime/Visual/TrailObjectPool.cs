

using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    [RequireComponent(typeof(ProjectileManager))]
    public class TrailObjectPool : MonoBehaviour
    {
        [Header("Trail Pool")]
        [SerializeField] private TrailRendererPool _trailPool;

        private readonly System.Collections.Generic.Dictionary<uint, int>
            _projToSlot = new(256);

        private void Awake()
        {
            if (_trailPool == null)
                _trailPool = TrailRendererPool.Instance;

            if (_trailPool == null)
                MID_Logger.LogWarning( MID_LogLevel.Error,
                    "[TrailObjectPool] No TrailRendererPool found — " +
                    "trails will not render.");
        }

        // ── 2D sync ───────────────────────────────────────────────────────────

        /// <summary>Sync active 2D projectile positions to trail slots. Call every FixedUpdate.</summary>
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
                    slot = AcquireSlot2D(p, cfg);
                    if (slot < 0) continue;
                    _projToSlot[p.ProjId] = slot;
                }

                _trailPool.SetPosition(slot, new Vector3(p.X, p.Y, 0f));
            }
        }

        // ── 3D sync (NEW) ─────────────────────────────────────────────────────

        /// <summary>Sync active 3D projectile positions to trail slots. Call every FixedUpdate.</summary>
        public void SyncToSimulation3D(NativeProjectile3D[] projs, int count)
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
                    slot = AcquireSlot3D(p, cfg);
                    if (slot < 0) continue;
                    _projToSlot[p.ProjId] = slot;
                }

                _trailPool.SetPosition(slot, new Vector3(p.X, p.Y, p.Z));
            }
        }

        /// <summary>Notify that a projectile has died so its trail slot can fade out.</summary>
        public void NotifyDead(uint projId)
        {
            if (!_projToSlot.TryGetValue(projId, out int slot)) return;
            _trailPool.Release(slot);
            _projToSlot.Remove(projId);
        }

        /// <summary>Release all active trail slots.</summary>
        public void ReleaseAll()
        {
            if (_trailPool == null) return;
            foreach (var slot in _projToSlot.Values)
                _trailPool.ForceRelease(slot);
            _projToSlot.Clear();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private int AcquireSlot2D(in NativeProjectile p, ProjectileConfigSO cfg)
            => _trailPool.Acquire(BuildTrailConfig(cfg), ownerId: (int)p.ProjId);

        private int AcquireSlot3D(in NativeProjectile3D p, ProjectileConfigSO cfg)
            => _trailPool.Acquire(BuildTrailConfig(cfg), ownerId: (int)p.ProjId);

        private static TrailConfig BuildTrailConfig(ProjectileConfigSO cfg)
        {
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
