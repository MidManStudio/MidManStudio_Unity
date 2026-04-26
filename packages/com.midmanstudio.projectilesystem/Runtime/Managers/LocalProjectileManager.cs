// LocalProjectileManager.cs
// Single-player / offline / practice mode projectile manager.
// No NGO. No RPCs. No snapshots. No reconciliation.
// Full Rust tick + collision + render + trail — all local, all this class.
//
// ID model (offline — no NGO):
//   ProjId:   uint counter starting at 1. Local only.
//   TargetId: (uint)target.GetInstanceID(). Local process-unique.
//   OwnerId:  0 = local player, 1+ = enemies (sequential, you assign).
//   No ulong NGO IDs anywhere in this file.
//
// Rendering:
//   LateUpdate calls ProjectileRenderer2D.Render() — same renderer as online.
//   TrailObjectPool.SyncToSimulation() called each FixedUpdate.
//
// Damage:
//   Hits are processed directly — no event indirection needed for offline.
//   Override OnHit() in a derived class to route damage to your game system.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Lightweight offline damage target registration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A local (offline) collision target. No NetworkObject required.
    /// Register with LocalProjectileManager.RegisterTarget().
    /// </summary>
    public class LocalDamageTarget
    {
        /// Process-unique ID from GetInstanceID().
        public uint   LocalId;

        /// World-space position updated each frame by the target's Update.
        public Vector3 Position;

        /// Collision sphere radius.
        public float   Radius;

        /// True while the target is alive and hittable.
        public bool    Active;

        /// Game-specific — set by the registering object, passed back in LocalHitPayload.
        public GameObject SourceObject;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Offline hit payload — no NGO IDs, no network data
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hit event fired by LocalProjectileManager for each projectile collision.
    /// Subscribe from your game's local damage system.
    /// </summary>
    public struct LocalHitPayload
    {
        public uint   ProjId;
        public ushort ConfigId;
        public bool   Is3D;

        public LocalDamageTarget Target;

        /// Final damage after curve + crit + headshot evaluation.
        public float  Damage;
        public bool   IsHeadshot;
        public bool   IsCrit;
        public Vector3 HitPosition;

        /// Owner ID passed in at spawn (0 = local player, 1+ = enemy).
        public uint   OwnerLocalId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LocalProjectileManager
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Offline projectile manager. Singleton. Owns the full local sim pipeline.
    /// Attach to a persistent GameObject in your offline/practice scene.
    /// </summary>
    public class LocalProjectileManager : Singleton<LocalProjectileManager>
    {
        #region Configuration

        [Header("Buffer Capacity")]
        [SerializeField] private int _maxProjectiles2D = 2048;
        [SerializeField] private int _maxProjectiles3D = 256;
        [SerializeField] private int _maxTargets       = 64;
        [SerializeField] private int _maxHitsPerTick   = 128;

        [Header("Collision")]
        [SerializeField] private float _cellSize = 4f;

        [Header("References")]
        [SerializeField] private ProjectileRenderer2D _renderer2D;
        [SerializeField] private ProjectileRenderer3D _renderer3D;
        [SerializeField] private TrailObjectPool      _trailPool;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region 2D Sim Buffer

        private NativeProjectile[]  _projs2D;
        private CollisionTarget[]   _targets2D;
        private HitResult[]         _hits2D;
        private int                 _count2D;
        private int                 _targetCount2D;

        private GCHandle _pinProjs2D;
        private GCHandle _pinTargets2D;
        private GCHandle _pinHits2D;

        #endregion

        #region 3D Sim Buffer

        private NativeProjectile3D[] _projs3D;
        private CollisionTarget3D[]  _targets3D;
        private HitResult3D[]        _hits3D;
        private int                  _count3D;
        private int                  _targetCount3D;

        private GCHandle _pinProjs3D;
        private GCHandle _pinTargets3D;
        private GCHandle _pinHits3D;

        #endregion

        #region Local State

        private uint _nextProjId = 1;

        // Maps local uint ID to gameplay data for damage resolution
        private readonly Dictionary<uint, LocalProjectileData> _localData
            = new Dictionary<uint, LocalProjectileData>(256);

        // Maps local target uint ID to LocalDamageTarget
        private readonly Dictionary<uint, LocalDamageTarget> _targets
            = new Dictionary<uint, LocalDamageTarget>(64);

        #endregion

        #region Events

        /// <summary>
        /// Fired for each projectile hit. Subscribe from your game's damage system.
        /// </summary>
        public event Action<LocalHitPayload> OnHit;

        /// <summary>Fired when a projectile dies (lifetime / pierce exhausted).</summary>
        public event Action<uint> OnProjectileDied;

        #endregion

        #region Initialisation

        protected override void Awake()
        {
            base.Awake();
            AllocateBuffers();
            BatchSpawnHelper.Initialise();
        }

        private void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            FreeBuffers();
        }

        private void AllocateBuffers()
        {
            _projs2D   = new NativeProjectile[_maxProjectiles2D];
            _targets2D = new CollisionTarget[_maxTargets];
            _hits2D    = new HitResult[_maxHitsPerTick];
            _pinProjs2D   = GCHandle.Alloc(_projs2D,   GCHandleType.Pinned);
            _pinTargets2D = GCHandle.Alloc(_targets2D, GCHandleType.Pinned);
            _pinHits2D    = GCHandle.Alloc(_hits2D,    GCHandleType.Pinned);

            _projs3D   = new NativeProjectile3D[_maxProjectiles3D];
            _targets3D = new CollisionTarget3D[_maxTargets];
            _hits3D    = new HitResult3D[_maxHitsPerTick];
            _pinProjs3D   = GCHandle.Alloc(_projs3D,   GCHandleType.Pinned);
            _pinTargets3D = GCHandle.Alloc(_targets3D, GCHandleType.Pinned);
            _pinHits3D    = GCHandle.Alloc(_hits3D,    GCHandleType.Pinned);
        }

        private void FreeBuffers()
        {
            if (_pinProjs2D.IsAllocated)   _pinProjs2D.Free();
            if (_pinTargets2D.IsAllocated) _pinTargets2D.Free();
            if (_pinHits2D.IsAllocated)    _pinHits2D.Free();
            if (_pinProjs3D.IsAllocated)   _pinProjs3D.Free();
            if (_pinTargets3D.IsAllocated) _pinTargets3D.Free();
            if (_pinHits3D.IsAllocated)    _pinHits3D.Free();
        }

        #endregion

        #region FixedUpdate — Sim Loop

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            if (_count2D > 0)
            {
                ProjectileLib.tick_projectiles(
                    _pinProjs2D.AddrOfPinnedObject(), _count2D, dt);

                if (_targetCount2D > 0)
                {
                    ProjectileLib.check_hits_grid_ex(
                        _pinProjs2D.AddrOfPinnedObject(),   _count2D,
                        _pinTargets2D.AddrOfPinnedObject(), _targetCount2D,
                        _pinHits2D.AddrOfPinnedObject(),    _hits2D.Length,
                        _cellSize,
                        out int hitCount2D);

                    for (int i = 0; i < hitCount2D; i++)
                        ProcessHit2D(in _hits2D[i]);
                }

                _trailPool?.SyncToSimulation(_projs2D, _count2D);
                CompactDead2D();
            }

            if (_count3D > 0)
            {
                ProjectileLib.tick_projectiles_3d(
                    _pinProjs3D.AddrOfPinnedObject(), _count3D, dt);

                if (_targetCount3D > 0)
                {
                    ProjectileLib.check_hits_grid_3d(
                        _pinProjs3D.AddrOfPinnedObject(),   _count3D,
                        _pinTargets3D.AddrOfPinnedObject(), _targetCount3D,
                        _pinHits3D.AddrOfPinnedObject(),    _hits3D.Length,
                        _cellSize,
                        out int hitCount3D);

                    for (int i = 0; i < hitCount3D; i++)
                        ProcessHit3D(in _hits3D[i]);
                }

                CompactDead3D();
            }
        }

        #endregion

        #region LateUpdate — Render

        private void LateUpdate()
        {
            _renderer2D?.Render(_projs2D, _count2D);
            _renderer3D?.Render(_projs3D, _count3D);
        }

        #endregion

        #region Hit Processing

        private void ProcessHit2D(in HitResult hit)
        {
            if (!_localData.TryGetValue(hit.ProjId, out var data)) return;

            var cfg = ProjectileRegistry.Instance.Get(data.ConfigId);
            if (cfg == null) return;

            if (!_targets.TryGetValue(hit.TargetId, out var target)) return;
            if (!target.Active) return;

            bool headshot = CheckHeadshotLocal(target, hit.HitX, hit.HitY, 0f);
            bool crit     = data.IsCrit;

            float normDist = cfg.MaxRange > 0f
                ? Mathf.Clamp01(hit.TravelDist / cfg.MaxRange) : 0f;
            float damage   = cfg.EvaluateDamage(normDist);
            if (headshot) damage *= cfg.HeadshotMultiplier;
            if (crit)     damage *= cfg.CritMultiplier;
            damage *= data.DamageMultiplier;

            var payload = new LocalHitPayload
            {
                ProjId      = hit.ProjId,
                ConfigId    = data.ConfigId,
                Is3D        = false,
                Target      = target,
                Damage      = damage,
                IsHeadshot  = headshot,
                IsCrit      = crit,
                HitPosition = new Vector3(hit.HitX, hit.HitY, 0f),
                OwnerLocalId = data.OwnerLocalId
            };

            OnHit?.Invoke(payload);

            // Handle piercing
            data.CollisionsRemaining--;
            if (data.CollisionsRemaining <= 0)
            {
                // Kill the NativeProjectile
                int idx = (int)hit.ProjIndex;
                if (idx < _count2D)
                    _projs2D[idx].Alive = 0;
                _localData.Remove(hit.ProjId);
            }
            else
            {
                _localData[hit.ProjId] = data;
            }
        }

        private void ProcessHit3D(in HitResult3D hit)
        {
            if (!_localData.TryGetValue(hit.ProjId, out var data)) return;

            var cfg = ProjectileRegistry.Instance.Get(data.ConfigId);
            if (cfg == null) return;

            if (!_targets.TryGetValue(hit.TargetId, out var target)) return;
            if (!target.Active) return;

            bool headshot = CheckHeadshotLocal(target, hit.HitX, hit.HitY, hit.HitZ);
            bool crit     = data.IsCrit;

            float normDist = cfg.MaxRange > 0f
                ? Mathf.Clamp01(hit.TravelDist / cfg.MaxRange) : 0f;
            float damage   = cfg.EvaluateDamage(normDist);
            if (headshot) damage *= cfg.HeadshotMultiplier;
            if (crit)     damage *= cfg.CritMultiplier;
            damage *= data.DamageMultiplier;

            var payload = new LocalHitPayload
            {
                ProjId      = hit.ProjId,
                ConfigId    = data.ConfigId,
                Is3D        = true,
                Target      = target,
                Damage      = damage,
                IsHeadshot  = headshot,
                IsCrit      = crit,
                HitPosition = new Vector3(hit.HitX, hit.HitY, hit.HitZ),
                OwnerLocalId = data.OwnerLocalId
            };

            OnHit?.Invoke(payload);

            data.CollisionsRemaining--;
            if (data.CollisionsRemaining <= 0)
            {
                int idx = (int)hit.ProjIndex;
                if (idx < _count3D)
                    _projs3D[idx].Alive = 0;
                _localData.Remove(hit.ProjId);
            }
            else
            {
                _localData[hit.ProjId] = data;
            }
        }

        /// <summary>
        /// Override in a derived class for game-specific headshot detection.
        /// Default: returns false.
        /// </summary>
        protected virtual bool CheckHeadshotLocal(
            LocalDamageTarget target, float hitX, float hitY, float hitZ)
            => false;

        #endregion

        #region Compaction

        private void CompactDead2D()
        {
            int write = 0;
            for (int read = 0; read < _count2D; read++)
            {
                if (_projs2D[read].Alive == 0)
                {
                    uint id = _projs2D[read].ProjId;
                    _localData.Remove(id);
                    _trailPool?.NotifyDead(id);
                    OnProjectileDied?.Invoke(id);
                    continue;
                }
                if (write != read) _projs2D[write] = _projs2D[read];
                write++;
            }
            _count2D = write;
        }

        private void CompactDead3D()
        {
            int write = 0;
            for (int read = 0; read < _count3D; read++)
            {
                if (_projs3D[read].Alive == 0)
                {
                    uint id = _projs3D[read].ProjId;
                    _localData.Remove(id);
                    OnProjectileDied?.Invoke(id);
                    continue;
                }
                if (write != read) _projs3D[write] = _projs3D[read];
                write++;
            }
            _count3D = write;
        }

        #endregion

        #region Public API — Spawn

        /// <summary>
        /// Spawn a batch of 2D projectiles. Main entry point for offline weapons.
        ///
        /// spawnPoints — pre-computed from ProjectilePatternSO.SampleDirections()
        ///               + weapon transform. BatchSpawnHelper reads these.
        /// configId    — registered config ID from ProjectileRegistry
        /// ownerLocalId — 0 for local player, 1+ for enemies
        /// damageMultiplier — from power-ups or difficulty scaling
        /// </summary>
        public void Spawn2D(
            SpawnPoint[] spawnPoints,
            int          count,
            ushort       configId,
            uint         ownerLocalId    = 0,
            float        damageMultiplier = 1f)
        {
            if (_count2D >= _maxProjectiles2D) return;

            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(configId);
            uint baseId    = AllocateProjIds(count);

            var (writePtr, remaining) = GetWriteHead2D();

            int written = BatchSpawnHelper.SpawnBatch2D(
                spawnPoints, count, null, rustParams,
                configId, 0, baseId, writePtr, remaining);

            if (written <= 0) return;

            // Register local gameplay data for each spawned projectile
            var cfg = ProjectileRegistry.Instance.Get(configId);
            for (int i = 0; i < written; i++)
            {
                uint projId = baseId + (uint)i;
                bool isCrit = cfg != null && UnityEngine.Random.value < cfg.CritChance;

                _localData[projId] = new LocalProjectileData
                {
                    ConfigId              = configId,
                    OwnerLocalId          = ownerLocalId,
                    DamageMultiplier      = damageMultiplier,
                    IsCrit                = isCrit,
                    CollisionsRemaining   = rustParams.MaxCollisions
                };
            }

            _count2D += written;
        }

        /// <summary>Spawn a batch of 3D projectiles in offline mode.</summary>
        public void Spawn3D(
            SpawnPoint[] spawnPoints,
            int          count,
            ushort       configId,
            uint         ownerLocalId    = 0,
            float        damageMultiplier = 1f)
        {
            if (_count3D >= _maxProjectiles3D) return;

            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(configId);
            uint baseId    = AllocateProjIds(count);

            var (writePtr, remaining) = GetWriteHead3D();

            int written = BatchSpawnHelper.SpawnBatch3D(
                spawnPoints, count, rustParams,
                configId, 0, baseId, writePtr, remaining);

            if (written <= 0) return;

            var cfg = ProjectileRegistry.Instance.Get(configId);
            for (int i = 0; i < written; i++)
            {
                uint projId = baseId + (uint)i;
                bool isCrit = cfg != null && UnityEngine.Random.value < cfg.CritChance;

                _localData[projId] = new LocalProjectileData
                {
                    ConfigId              = configId,
                    OwnerLocalId          = ownerLocalId,
                    DamageMultiplier      = damageMultiplier,
                    IsCrit                = isCrit,
                    CollisionsRemaining   = rustParams.MaxCollisions
                };
            }

            _count3D += written;
        }

        #endregion

        #region Public API — Targets

        /// <summary>
        /// Register a local collision target.
        /// Call from the target's Start() or OnEnable().
        /// TargetId is derived automatically from GetInstanceID().
        /// </summary>
        public uint RegisterTarget(LocalDamageTarget target)
        {
            if (target == null) return 0;

            // Offline target ID = GetInstanceID() of the source GO, cast to uint
            uint id = target.LocalId;
            _targets[id] = target;
            SyncTarget2D(target);
            return id;
        }

        /// <summary>Update target position (call each frame or FixedUpdate).</summary>
        public void UpdateTarget(LocalDamageTarget target)
        {
            if (target == null || !_targets.ContainsKey(target.LocalId)) return;
            _targets[target.LocalId] = target;
            SyncTarget2D(target);
        }

        /// <summary>Deactivate a target (e.g. it died).</summary>
        public void DeactivateTarget(uint localId)
        {
            if (!_targets.TryGetValue(localId, out var t)) return;
            t.Active = false;
            _targets[localId] = t;
            SyncTarget2D(t);
        }

        /// <summary>Remove a target entirely.</summary>
        public void RemoveTarget(uint localId)
        {
            _targets.Remove(localId);
            DeactivateInBuffer2D(localId);
        }

        private void SyncTarget2D(LocalDamageTarget t)
        {
            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != t.LocalId) continue;
                _targets2D[i] = new CollisionTarget
                {
                    X        = t.Position.x,
                    Y        = t.Position.y,
                    Radius   = t.Radius,
                    TargetId = t.LocalId,
                    Active   = t.Active ? (byte)1 : (byte)0
                };
                return;
            }

            if (_targetCount2D >= _maxTargets) return;
            _targets2D[_targetCount2D++] = new CollisionTarget
            {
                X        = t.Position.x,
                Y        = t.Position.y,
                Radius   = t.Radius,
                TargetId = t.LocalId,
                Active   = t.Active ? (byte)1 : (byte)0
            };
        }

        private void DeactivateInBuffer2D(uint localId)
        {
            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != localId) continue;
                _targets2D[i].Active = 0;
                return;
            }
        }

        #endregion

        #region Internal Helpers

        private uint AllocateProjIds(int count)
        {
            uint base_id = _nextProjId;
            _nextProjId += (uint)count;
            return base_id;
        }

        private (IntPtr ptr, int remaining) GetWriteHead2D()
        {
            IntPtr ptr = IntPtr.Add(
                _pinProjs2D.AddrOfPinnedObject(),
                _count2D * Marshal.SizeOf<NativeProjectile>());
            return (ptr, _maxProjectiles2D - _count2D);
        }

        private (IntPtr ptr, int remaining) GetWriteHead3D()
        {
            IntPtr ptr = IntPtr.Add(
                _pinProjs3D.AddrOfPinnedObject(),
                _count3D * Marshal.SizeOf<NativeProjectile3D>());
            return (ptr, _maxProjectiles3D - _count3D);
        }

        #endregion

        #region Supporting Data Type

        /// <summary>Minimal offline gameplay data per projectile.</summary>
        private struct LocalProjectileData
        {
            public ushort ConfigId;
            public uint   OwnerLocalId;
            public float  DamageMultiplier;
            public bool   IsCrit;
            public byte   CollisionsRemaining;
        }

        #endregion
    }
}
