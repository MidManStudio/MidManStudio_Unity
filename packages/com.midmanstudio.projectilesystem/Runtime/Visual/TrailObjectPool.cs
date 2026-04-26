// TrailObjectPool.cs
using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(ProjectileManager))]
    public class TrailObjectPool : MonoBehaviour
    {
        [SerializeField] private int _poolSize = 512;

        private TrailRenderer[] _trails;
        private uint[] _assignedIds;
        private bool[] _inUse;
        private float[] _fadingUntil;

        private readonly Dictionary<uint, int> _idToSlot = new(512);

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            _trails = new TrailRenderer[_poolSize];
            _assignedIds = new uint[_poolSize];
            _inUse = new bool[_poolSize];
            _fadingUntil = new float[_poolSize];

            for (int i = 0; i < _poolSize; i++)
            {
                var go = new GameObject($"Trail_{i}");
                go.transform.SetParent(transform);
                go.hideFlags = HideFlags.HideInHierarchy;

                var tr = go.AddComponent<TrailRenderer>();
                tr.enabled = false;
                tr.autodestruct = false;
                tr.emitting = false;
                _trails[i] = tr;
            }
        }

        // ─── Called by ProjectileManager every FixedUpdate ────────────────────

        public void SyncToSimulation(NativeProjectile[] projs, int count)
        {
            float now = Time.time;

            // Pass 1: retire fully-faded trails
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i] || _fadingUntil[i] <= 0f) continue;
                if (now >= _fadingUntil[i])
                {
                    _trails[i].enabled = false;
                    _fadingUntil[i] = 0f;
                }
            }

            // Pass 2: move active trails to their projectile
            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (!cfg.HasTrail) continue;

                if (!_idToSlot.TryGetValue(p.ProjId, out int slot))
                {
                    slot = AcquireSlot(p.ProjId, cfg);
                    if (slot < 0) continue;
                }

                _trails[slot].transform.position = new Vector3(p.X, p.Y, 0f);
            }
        }

        // Called from ProjectileManager.CompactDeadSlots
        public void NotifyDead(uint projId)
        {
            if (!_idToSlot.TryGetValue(projId, out int slot)) return;

            _trails[slot].emitting = false;
            _fadingUntil[slot] = Time.time + _trails[slot].time + 0.05f;
            _inUse[slot] = false;
            _assignedIds[slot] = 0;
            _idToSlot.Remove(projId);
        }

        // ─── Internals ────────────────────────────────────────────────────────

        private int AcquireSlot(uint projId, ProjectileConfigSO cfg)
        {
            // Pass 1: find a completely free slot (not in use, not fading)
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i] || _fadingUntil[i] > 0f) continue;
                return InitSlot(i, projId, cfg);
            }

            // Pass 2: LRU eviction — steal the slot closest to finishing its fade
            int bestSlot = -1;
            float soonest = float.MaxValue;
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i]) continue;
                if (_fadingUntil[i] < soonest) { soonest = _fadingUntil[i]; bestSlot = i; }
            }

            if (bestSlot >= 0)
            {
                _trails[bestSlot].enabled = false;
                _fadingUntil[bestSlot] = 0f;
                return InitSlot(bestSlot, projId, cfg);
            }

            return -1; // pool exhausted
        }

        private int InitSlot(int i, uint projId, ProjectileConfigSO cfg)
        {
            _inUse[i] = true;
            _assignedIds[i] = projId;
            _fadingUntil[i] = 0f;
            _idToSlot[projId] = i;

            ApplyConfig(_trails[i], cfg);
            _trails[i].Clear();
            _trails[i].enabled = true;
            _trails[i].emitting = true;
            return i;
        }

        private static void ApplyConfig(TrailRenderer tr, ProjectileConfigSO cfg)
        {
            if (cfg.TrailMaterial == null)
            {
                Debug.LogWarning(
                    $"[TrailObjectPool] Config '{cfg.name}' HasTrail=true but TrailMaterial is null — " +
                    "assign a material on the ProjectileConfigSO or the trail will be invisible.");
            }

            tr.material = cfg.TrailMaterial;
            tr.colorGradient = cfg.TrailColorGradient;
            tr.time = cfg.TrailTime;
            tr.startWidth = cfg.TrailStartWidth;
            tr.endWidth = cfg.TrailEndWidth;
            tr.numCapVertices = cfg.TrailCapVertices; // 0=flat, 2-4=smooth rounded end caps
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows = false;
        }
    }
}