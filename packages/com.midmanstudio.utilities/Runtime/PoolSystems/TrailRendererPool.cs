// TrailRendererPool.cs
// Generic pooled trail renderer manager for utilities package.
// Manages a fixed pool of TrailRenderer GameObjects.
// Any moving object can request a trail slot by calling Acquire(),
// update it each frame via SetPosition(), and release via Release().
//
// NOT projectile-specific — works for any moving entity.
// The projectile system's TrailObjectPool (which reads NativeProjectile[])
// lives in the projectile package and uses this as its underlying pool.
//
// USAGE:
//   int slot = TrailRendererPool.Instance.Acquire(config, ownerInstanceId);
//   TrailRendererPool.Instance.SetPosition(slot, transform.position);
//   TrailRendererPool.Instance.Release(slot);

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Pools
{
    // ── Config passed when acquiring a trail slot ─────────────────────────────

    public struct TrailConfig
    {
        public Material  Material;
        public Gradient  ColorGradient;
        public float     Time;           // trail lifetime in seconds
        public float     StartWidth;
        public float     EndWidth;
        public int       CapVertices;    // 0 = flat, 2–4 = rounded

        /// <summary>Sensible defaults for a basic white trail.</summary>
        public static TrailConfig Default => new TrailConfig
        {
            Time       = 0.25f,
            StartWidth = 0.1f,
            EndWidth   = 0f,
            CapVertices = 0
        };
    }

    // ── Per-slot state ────────────────────────────────────────────────────────

    internal class TrailSlot
    {
        public TrailRenderer Renderer;
        public int           OwnerId;    // GetInstanceID() of owning object, or 0
        public bool          InUse;
        public float         FadeUntil;  // Time.time value when the fade-out ends
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class TrailRendererPool : Singleton<TrailRendererPool>
    {
        [SerializeField] private MID_LogLevel _logLevel   = MID_LogLevel.Info;
        [SerializeField] private int          _poolSize   = 256;

        [Tooltip("Extra seconds the trail lingers after Release() is called,\n" +
                 "so the visual fades out naturally before the slot is recycled.")]
        [SerializeField] private float _fadePad = 0.1f;

        private TrailSlot[] _slots;
        private bool        _initialized;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            Initialise();
        }

        private void Update()
        {
            float now = Time.time;
            // Retire fully-faded slots
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var slot = ref _slots[i];
                if (slot.InUse || slot.FadeUntil <= 0f) continue;
                if (now >= slot.FadeUntil)
                {
                    slot.Renderer.enabled  = false;
                    slot.Renderer.emitting = false;
                    slot.FadeUntil         = 0f;
                }
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Acquire a trail slot.
        /// Returns the slot index, or -1 if the pool is exhausted.
        /// </summary>
        public int Acquire(TrailConfig config, int ownerId = 0)
        {
            if (!_initialized) Initialise();

            int slot = FindFreeSlot();
            if (slot < 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    "Trail pool exhausted — no free slot. Increase pool size.",
                    nameof(TrailRendererPool), nameof(Acquire));
                return -1;
            }

            ref var s = ref _slots[slot];
            s.OwnerId   = ownerId;
            s.InUse     = true;
            s.FadeUntil = 0f;

            ApplyConfig(s.Renderer, config);
            s.Renderer.Clear();
            s.Renderer.enabled  = true;
            s.Renderer.emitting = true;

            MID_Logger.LogDebug(_logLevel,
                $"Acquired slot {slot} owner={ownerId}",
                nameof(TrailRendererPool), nameof(Acquire));

            return slot;
        }

        /// <summary>Move the trail to a new world position (call every frame / fixed update).</summary>
        public void SetPosition(int slot, Vector3 worldPosition)
        {
            if (!IsValidActiveSlot(slot)) return;
            _slots[slot].Renderer.transform.position = worldPosition;
        }

        /// <summary>
        /// Release a trail slot.
        /// The renderer keeps emitting for (trail.time + fadePad) seconds so the
        /// visual fades out, then the slot is recycled automatically.
        /// </summary>
        public void Release(int slot)
        {
            if (!IsValidActiveSlot(slot)) return;

            ref var s = ref _slots[slot];
            s.InUse         = false;
            s.OwnerId       = 0;
            s.Renderer.emitting = false;
            s.FadeUntil     = Time.time + s.Renderer.time + _fadePad;

            MID_Logger.LogDebug(_logLevel,
                $"Released slot {slot} fade-until={s.FadeUntil:F2}",
                nameof(TrailRendererPool), nameof(Release));
        }

        /// <summary>Release all slots owned by a specific object (by GetInstanceID()).</summary>
        public void ReleaseByOwner(int ownerId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].InUse && _slots[i].OwnerId == ownerId)
                    Release(i);
            }
        }

        /// <summary>Immediately disable a slot with no fade.</summary>
        public void ForceRelease(int slot)
        {
            if (slot < 0 || slot >= _slots.Length) return;
            ref var s = ref _slots[slot];
            s.InUse             = false;
            s.OwnerId           = 0;
            s.FadeUntil         = 0f;
            s.Renderer.emitting = false;
            s.Renderer.enabled  = false;
            s.Renderer.Clear();
        }

        public int  PoolSize   => _slots?.Length ?? 0;
        public bool IsAcquired(int slot) => IsValidActiveSlot(slot);

        // ── Private ───────────────────────────────────────────────────────────

        private void Initialise()
        {
            if (_initialized) return;

            _slots = new TrailSlot[_poolSize];
            for (int i = 0; i < _poolSize; i++)
            {
                var go = new GameObject($"Trail_{i:D3}");
                go.transform.SetParent(transform);
                go.hideFlags = HideFlags.HideInHierarchy;

                var tr = go.AddComponent<TrailRenderer>();
                tr.enabled       = false;
                tr.emitting      = false;
                tr.autodestruct  = false;
                // Performance defaults — override per-slot via ApplyConfig
                tr.shadowCastingMode           = UnityEngine.Rendering.ShadowCastingMode.Off;
                tr.receiveShadows              = false;
                tr.generateLightingData        = false;
                tr.motionVectorGenerationMode  = MotionVectorGenerationMode.ForceNoMotion;
                tr.alignment                   = LineAlignment.View;

                _slots[i] = new TrailSlot { Renderer = tr };
            }

            _initialized = true;

            MID_Logger.LogInfo(_logLevel,
                $"TrailRendererPool ready — {_poolSize} slots.",
                nameof(TrailRendererPool), nameof(Initialise));
        }

        private int FindFreeSlot()
        {
            // Pass 1: completely free (not in use, not fading)
            for (int i = 0; i < _slots.Length; i++)
                if (!_slots[i].InUse && _slots[i].FadeUntil <= 0f)
                    return i;

            // Pass 2: LRU eviction — slot closest to finishing its fade
            int   best   = -1;
            float soonest = float.MaxValue;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].InUse) continue;
                if (_slots[i].FadeUntil < soonest)
                {
                    soonest = _slots[i].FadeUntil;
                    best    = i;
                }
            }

            if (best >= 0) ForceRelease(best);
            return best;
        }

        private static void ApplyConfig(TrailRenderer tr, TrailConfig cfg)
        {
            if (cfg.Material != null) tr.material = cfg.Material;
            if (cfg.ColorGradient != null) tr.colorGradient = cfg.ColorGradient;
            tr.time       = cfg.Time > 0f  ? cfg.Time       : 0.25f;
            tr.startWidth = cfg.StartWidth >= 0f ? cfg.StartWidth : 0.1f;
            tr.endWidth   = cfg.EndWidth   >= 0f ? cfg.EndWidth   : 0f;
            tr.numCapVertices = Mathf.Clamp(cfg.CapVertices, 0, 4);
        }

        private bool IsValidActiveSlot(int slot) =>
            _initialized            &&
            slot >= 0               &&
            slot < _slots.Length    &&
            _slots[slot].InUse;
    }
}
