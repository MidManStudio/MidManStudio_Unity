// packages/com.midmanstudio.projectilesystem/Runtime/Managers/LocalProjectileManager.cs
//
// ARCHITECTURE CHANGE: LocalProjectileManager now serves as the client-side
// Rust sim buffer for networked play in addition to offline use.
//
// In networked play on pure clients:
//   - No collision targets are registered (MasterSystem guards this per IsNetworked)
//   - No LocalProjectileData registered for network visual projectiles
//   - ProjectileRenderer2D/3D renders from this buffer (same GPU instanced path as host)
//   - Firing client: SpawnFiringClientBatch* immediately → LinkNetworkProjectileBatch on confirm
//   - Other clients: SpawnNetworkBatch* with server-assigned IDs on SpawnConfirmed RPC
//   - All clients: KillNetworkProjectile on HitConfirmed RPC
//   - All clients: ReconcileSnapshots on SendSnapshot RPC
//
// This replaces the per-projectile pool-object ClientPredictionManager approach
// for the Rust sim path entirely. All machines now use the same rendering pipeline.
//
// Offline mode is unchanged — Spawn2D/3D with LocalProjectileData and damage events.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Visuals;
using MidManStudio.Projectiles.Network;

namespace MidManStudio.Projectiles.Managers
{
    // ── Lightweight offline damage target ─────────────────────────────────────

    public class LocalDamageTarget
    {
        public uint       LocalId;
        public Vector3    Position;
        public float      Radius;
        public bool       Active;
        public GameObject SourceObject;
        public int        UnityLayer;
    }

    // ── Offline hit payload ────────────────────────────────────────────────────

    public struct LocalHitPayload
    {
        public uint              ProjId;
        public ushort            ConfigId;
        public bool              Is3D;
        public LocalDamageTarget Target;
        public uint              RawTargetId;
        public float             Damage;
        public bool              IsHeadshot;
        public bool              IsCrit;
        public Vector3           HitPosition;
        public uint              OwnerLocalId;
    }

    // ── LocalProjectileManager ────────────────────────────────────────────────

    public class LocalProjectileManager : Singleton<LocalProjectileManager>
    {
        #region Configuration

        [Header("Buffer Capacity")]
        [SerializeField] private int _maxProjectiles2D = 2048;
        [SerializeField] private int _maxProjectiles3D = 512;
        [SerializeField] private int _maxTargets       = 128;
        [SerializeField] private int _maxHitsPerTick   = 256;

        [Header("Collision")]
        [SerializeField] private float _cellSize = 4f;

        [Header("References")]
        [SerializeField] private ProjectileRenderer2D _renderer2D;
        [SerializeField] private ProjectileRenderer3D _renderer3D;
        [SerializeField] private TrailObjectPool      _trailPool;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

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

        #region Permanent Per-Projectile Pierce Immunity (offline only)

        private readonly Dictionary<uint, HashSet<uint>> _hitTargets2D = new(128);
        private readonly Dictionary<uint, HashSet<uint>> _hitTargets3D = new(128);

        private static readonly Stack<HashSet<uint>> _setPool = new(32);

        private static HashSet<uint> RentSet()
            => _setPool.Count > 0 ? _setPool.Pop() : new HashSet<uint>(4);

        private static void ReturnSet(HashSet<uint> s) { s.Clear(); _setPool.Push(s); }

        private bool AlreadyHit2D(uint projId, uint targetId)
        {
            if (!_hitTargets2D.TryGetValue(projId, out var set)) return false;
            return set.Contains(targetId);
        }

        private bool AlreadyHit3D(uint projId, uint targetId)
        {
            if (!_hitTargets3D.TryGetValue(projId, out var set)) return false;
            return set.Contains(targetId);
        }

        private void RecordHit2D(uint projId, uint targetId)
        {
            if (!_hitTargets2D.TryGetValue(projId, out var set))
            {
                set = RentSet();
                _hitTargets2D[projId] = set;
            }
            set.Add(targetId);
        }

        private void RecordHit3D(uint projId, uint targetId)
        {
            if (!_hitTargets3D.TryGetValue(projId, out var set))
            {
                set = RentSet();
                _hitTargets3D[projId] = set;
            }
            set.Add(targetId);
        }

        private void ClearHitRecord2D(uint projId)
        {
            if (_hitTargets2D.TryGetValue(projId, out var set))
            {
                ReturnSet(set);
                _hitTargets2D.Remove(projId);
            }
        }

        private void ClearHitRecord3D(uint projId)
        {
            if (_hitTargets3D.TryGetValue(projId, out var set))
            {
                ReturnSet(set);
                _hitTargets3D.Remove(projId);
            }
        }

        #endregion

        #region Network Visual — Temp ID Tracking

        // Temp IDs for firing client's immediately-spawned projectiles.
        // Range 0xFFFE0000+ avoids collision with server IDs (start at 1) and
        // offline IDs (_nextProjId from 1). Server confirms the real ID via RPC.
        private uint _nextTempId = 0xFFFE0000u;

        // Queue of (baseTempId, count) enqueued by SpawnFiringClientBatch.
        // Dequeued FIFO by LinkNetworkProjectileBatch when server confirm arrives.
        private readonly Queue<(uint baseTempId, int count)> _pendingTempBases
            = new Queue<(uint, int)>(16);

        #endregion

        #region Local State (offline path)

        private uint _nextProjId = 1;

        private readonly Dictionary<uint, LocalProjectileData> _localData
            = new Dictionary<uint, LocalProjectileData>(256);

        private readonly Dictionary<uint, LocalDamageTarget> _targets
            = new Dictionary<uint, LocalDamageTarget>(64);

        private readonly Dictionary<uint, int> _targetLayers2D
            = new Dictionary<uint, int>(64);
        private readonly Dictionary<uint, int> _targetLayers3D
            = new Dictionary<uint, int>(64);

        #endregion

        #region Properties

        public int ActiveCount2D => _count2D;
        public int ActiveCount3D => _count3D;

        #endregion

        #region Events

        public event Action<LocalHitPayload> OnHit;
        public event Action<uint>            OnProjectileDied;

        #endregion

        #region Initialisation

        protected override void Awake()
        {
            base.Awake();
            AllocateBuffers();
            BatchSpawnHelper.Initialise();
            MID_Logger.LogInfo(_logLevel, "LocalProjectileManager initialised.",
                nameof(LocalProjectileManager));
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

                // Collision only runs when targets are registered (offline mode).
                // In networked mode, MID_MasterProjectileSystem skips RegisterTarget2D
                // so _targetCount2D stays 0 — no collision on client visual buffer.
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

                _trailPool?.SyncToSimulation3D(_projs3D, _count3D);
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

        #region Public — Hit Event Relay

        public void FireHitEvent(LocalHitPayload payload) => OnHit?.Invoke(payload);

        #endregion

        #region Layer Filter Helpers

        private bool PassesLayerFilter2D(uint projId, uint targetId)
        {
            if (!_targetLayers2D.TryGetValue(targetId, out int layer)) return true;
            ushort configId = GetConfigId2D(projId);
            return PassesLayerMask(configId, layer);
        }

        private bool PassesLayerFilter3D(uint projId, uint targetId)
        {
            if (!_targetLayers3D.TryGetValue(targetId, out int layer)) return true;
            ushort configId = GetConfigId3D(projId);
            return PassesLayerMask(configId, layer);
        }

        private ushort GetConfigId2D(uint projId)
        {
            if (_localData.TryGetValue(projId, out var d)) return d.ConfigId;
            return 0;
        }

        private ushort GetConfigId3D(uint projId)
        {
            if (_localData.TryGetValue(projId, out var d)) return d.ConfigId;
            return 0;
        }

        private static bool PassesLayerMask(ushort configId, int targetLayer)
        {
            var cfg = ProjectileRegistry.HasInstance
                ? ProjectileRegistry.Instance.Get(configId) : null;
            if (cfg == null) return true;
            int mask = cfg.HitLayers.value;
            if (mask == -1) return true;
            return (mask & (1 << targetLayer)) != 0;
        }

        #endregion

        #region Hit Processing (offline path only)

        private void ProcessHit2D(in HitResult hit)
        {
            if (!_localData.TryGetValue(hit.ProjId, out var data)) return;

            if (AlreadyHit2D(hit.ProjId, hit.TargetId)) return;
            if (!PassesLayerFilter2D(hit.ProjId, hit.TargetId)) return;

            var config = ProjectileRegistry.Instance.Get(data.ConfigId);
            if (config == null) return;

            _targets.TryGetValue(hit.TargetId, out var target);
            if (target != null && !target.Active) return;

            bool  headshot = target != null
                && CheckHeadshotLocal(target, hit.HitX, hit.HitY, 0f);
            bool  crit     = data.IsCrit;
            float normDist = config.MaxRange > 0f
                ? Mathf.Clamp01(hit.TravelDist / config.MaxRange) : 0f;
            float damage   = config.EvaluateDamage(normDist);
            if (headshot) damage *= config.HeadshotMultiplier;
            if (crit)     damage *= config.CritMultiplier;
            damage *= data.DamageMultiplier;

            var payload = new LocalHitPayload
            {
                ProjId       = hit.ProjId,
                ConfigId     = data.ConfigId,
                Is3D         = false,
                Target       = target,
                RawTargetId  = hit.TargetId,
                Damage       = damage,
                IsHeadshot   = headshot,
                IsCrit       = crit,
                HitPosition  = new Vector3(hit.HitX, hit.HitY, 0f),
                OwnerLocalId = data.OwnerLocalId
            };

            OnHit?.Invoke(payload);

            RecordHit2D(hit.ProjId, hit.TargetId);

            data.CollisionsRemaining--;
            if (data.CollisionsRemaining <= 0)
            {
                int idx = (int)hit.ProjIndex;
                if (idx >= 0 && idx < _count2D) _projs2D[idx].Alive = 0;
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

            if (AlreadyHit3D(hit.ProjId, hit.TargetId)) return;
            if (!PassesLayerFilter3D(hit.ProjId, hit.TargetId)) return;

            var config = ProjectileRegistry.Instance.Get(data.ConfigId);
            if (config == null) return;

            _targets.TryGetValue(hit.TargetId, out var target);
            if (target != null && !target.Active) return;

            bool  headshot = target != null
                && CheckHeadshotLocal(target, hit.HitX, hit.HitY, hit.HitZ);
            bool  crit     = data.IsCrit;
            float normDist = config.MaxRange > 0f
                ? Mathf.Clamp01(hit.TravelDist / config.MaxRange) : 0f;
            float damage   = config.EvaluateDamage(normDist);
            if (headshot) damage *= config.HeadshotMultiplier;
            if (crit)     damage *= config.CritMultiplier;
            damage *= data.DamageMultiplier;

            var payload = new LocalHitPayload
            {
                ProjId       = hit.ProjId,
                ConfigId     = data.ConfigId,
                Is3D         = true,
                Target       = target,
                RawTargetId  = hit.TargetId,
                Damage       = damage,
                IsHeadshot   = headshot,
                IsCrit       = crit,
                HitPosition  = new Vector3(hit.HitX, hit.HitY, hit.HitZ),
                OwnerLocalId = data.OwnerLocalId
            };

            OnHit?.Invoke(payload);

            RecordHit3D(hit.ProjId, hit.TargetId);

            data.CollisionsRemaining--;
            if (data.CollisionsRemaining <= 0)
            {
                int idx = (int)hit.ProjIndex;
                if (idx >= 0 && idx < _count3D) _projs3D[idx].Alive = 0;
                _localData.Remove(hit.ProjId);
            }
            else
            {
                _localData[hit.ProjId] = data;
            }
        }

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
                    _localData.Remove(id);      // no-op for visual-only network projectiles
                    ClearHitRecord2D(id);
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
                    ClearHitRecord3D(id);
                    _trailPool?.NotifyDead(id);
                    OnProjectileDied?.Invoke(id);
                    continue;
                }
                if (write != read) _projs3D[write] = _projs3D[read];
                write++;
            }
            _count3D = write;
        }

        #endregion

        #region Public API — Offline Spawn

        public void Spawn2D(
            SpawnPoint[] spawnPoints,
            int          count,
            ushort       configId,
            uint         ownerLocalId    = 0,
            float        damageMultiplier = 1f)
        {
            if (_count2D >= _maxProjectiles2D)
            {
                MID_Logger.LogWarning(_logLevel,
                    "2D buffer full.", nameof(LocalProjectileManager));
                return;
            }

            var  rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(configId);
            uint baseId     = AllocateProjIds(count);
            var (writePtr, remaining) = GetWriteHead2D();

            int written = BatchSpawnHelper.SpawnBatch2D(
                spawnPoints, count, null, rustParams,
                configId, 0, baseId, writePtr, remaining);

            if (written <= 0) return;

            var cfg = ProjectileRegistry.Instance.Get(configId);
            for (int i = 0; i < written; i++)
            {
                uint projId = baseId + (uint)i;
                bool isCrit = cfg != null && UnityEngine.Random.value < cfg.CritChance;
                _localData[projId] = new LocalProjectileData
                {
                    ConfigId            = configId,
                    OwnerLocalId        = ownerLocalId,
                    DamageMultiplier    = damageMultiplier,
                    IsCrit              = isCrit,
                    CollisionsRemaining = rustParams.MaxCollisions
                };
            }
            _count2D += written;
        }

        public void Spawn3D(
            SpawnPoint[] spawnPoints,
            int          count,
            ushort       configId,
            uint         ownerLocalId    = 0,
            float        damageMultiplier = 1f)
        {
            if (_count3D >= _maxProjectiles3D)
            {
                MID_Logger.LogWarning(_logLevel,
                    "3D buffer full.", nameof(LocalProjectileManager));
                return;
            }

            var  rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(configId);
            uint baseId     = AllocateProjIds(count);
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
                    ConfigId            = configId,
                    OwnerLocalId        = ownerLocalId,
                    DamageMultiplier    = damageMultiplier,
                    IsCrit              = isCrit,
                    CollisionsRemaining = rustParams.MaxCollisions
                };
            }
            _count3D += written;
        }

        #endregion

        #region Public API — Network Visual (Client-Side Rust Sim)

        // ── Firing client: immediate prediction with temp IDs ─────────────────

        /// <summary>
        /// Spawns projectiles immediately on the firing client using temp IDs.
        /// Called from MID_MasterProjectileSystem.FireNetworkedSim BEFORE the server RPC.
        /// When SpawnConfirmedClientRpc arrives, call LinkNetworkProjectileBatch to
        /// replace temp IDs with real server IDs.
        /// Returns the base temp ID for logging/debugging.
        /// </summary>
        public uint SpawnFiringClientBatch2D(
            SpawnPoint[] spawnPoints, int count, ushort configId, float resolvedSpeed)
        {
            if (_count2D >= _maxProjectiles2D) return 0;

            var  rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(configId, resolvedSpeed);
            uint baseTempId = _nextTempId;
            _nextTempId += (uint)count;

            var (writePtr, remaining) = GetWriteHead2D();
            int written = BatchSpawnHelper.SpawnBatch2D(
                spawnPoints, count, null, rustParams,
                configId, 0, baseTempId, writePtr, remaining, 0f);

            if (written > 0)
            {
                _count2D += written;
                _pendingTempBases.Enqueue((baseTempId, written));
            }

            return baseTempId;
        }

        /// <summary>3D version of SpawnFiringClientBatch2D.</summary>
        public uint SpawnFiringClientBatch3D(
            SpawnPoint[] spawnPoints, int count, ushort configId, float resolvedSpeed)
        {
            if (_count3D >= _maxProjectiles3D) return 0;

            var  rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(configId, resolvedSpeed);
            uint baseTempId = _nextTempId;
            _nextTempId += (uint)count;

            var (writePtr, remaining) = GetWriteHead3D();
            int written = BatchSpawnHelper.SpawnBatch3D(
                spawnPoints, count, rustParams,
                configId, 0, baseTempId, writePtr, remaining, 0f);

            if (written > 0)
            {
                _count3D += written;
                _pendingTempBases.Enqueue((baseTempId, written));
            }

            return baseTempId;
        }

        // ── Other clients: spawn with real server IDs from SpawnConfirmed RPC ──

        /// <summary>
        /// Spawns a network visual batch into the 2D Rust buffer for other clients.
        /// Uses the server-assigned BaseProjId directly — no temp ID mapping needed.
        /// Applies a velocity-based position estimate to account for RPC travel time.
        /// No LocalProjectileData registered — visual only, no damage events.
        /// </summary>
        public void SpawnNetworkBatch2D(SpawnConfirmation conf, float serverNetworkTime)
        {
            if (_count2D >= _maxProjectiles2D) return;

            var cfg = ProjectileRegistry.Instance.Get(conf.ConfigId);
            if (cfg == null || cfg.Is3D) return;

            int count = Mathf.Min(conf.ProjectileCount, _maxProjectiles2D - _count2D);
            if (count <= 0) return;

            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(conf.ConfigId, conf.Speed);
            var spawnPts   = BuildSpawnPointsFromConfirmation(conf, count);
            var (writePtr, remaining) = GetWriteHead2D();

            int startIdx = _count2D;
            int written  = BatchSpawnHelper.SpawnBatch2D(
                spawnPts, count, null, rustParams,
                conf.ConfigId, 0, conf.BaseProjId,
                writePtr, remaining, 0f);

            if (written <= 0) return;

            // Estimate time-in-flight and advance positions so the projectile
            // appears at its expected current location rather than at the barrel.
            if (serverNetworkTime > 0f)
            {
                float catchUp = Mathf.Clamp(
                    serverNetworkTime, 0f, rustParams.Lifetime * 0.9f);

                for (int i = 0; i < written; i++)
                {
                    ref var p = ref _projs2D[startIdx + i];
                    // Simple Euler advance matching Rust straight-line integration
                    p.X += p.Vx * catchUp;
                    p.Y += p.Vy * catchUp;
                    p.Lifetime -= catchUp;
                    if (p.Lifetime <= 0f) p.Alive = 0;
                }
            }

            _count2D += written;
            // No LocalProjectileData — CompactDead's Remove() is a no-op for these
        }

        /// <summary>3D version of SpawnNetworkBatch2D.</summary>
        public void SpawnNetworkBatch3D(SpawnConfirmation conf, float serverNetworkTime)
        {
            if (_count3D >= _maxProjectiles3D) return;

            var cfg = ProjectileRegistry.Instance.Get(conf.ConfigId);
            if (cfg == null || !cfg.Is3D) return;

            int count = Mathf.Min(conf.ProjectileCount, _maxProjectiles3D - _count3D);
            if (count <= 0) return;

            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(conf.ConfigId, conf.Speed);
            var spawnPts   = BuildSpawnPointsFromConfirmation(conf, count);
            var (writePtr, remaining) = GetWriteHead3D();

            int startIdx = _count3D;
            int written  = BatchSpawnHelper.SpawnBatch3D(
                spawnPts, count, rustParams,
                conf.ConfigId, 0, conf.BaseProjId,
                writePtr, remaining, 0f);

            if (written <= 0) return;

            if (serverNetworkTime > 0f)
            {
                float catchUp = Mathf.Clamp(
                    serverNetworkTime, 0f, rustParams.Lifetime * 0.9f);

                for (int i = 0; i < written; i++)
                {
                    ref var p = ref _projs3D[startIdx + i];
                    p.X += p.Vx * catchUp;
                    p.Y += p.Vy * catchUp;
                    p.Z += p.Vz * catchUp;
                    p.Lifetime -= catchUp;
                    if (p.Lifetime <= 0f) p.Alive = 0;
                }
            }

            _count3D += written;
        }

        // ── Firing client: link temp ID to real server ID ─────────────────────

        /// <summary>
        /// Called when SpawnConfirmedClientRpc arrives for the local firing client.
        /// Dequeues the oldest pending temp batch and updates ProjId fields to real IDs.
        /// If the temp projectile already died (rare — expired before RPC arrived),
        /// the search silently finds nothing and the real ID is never tracked. That's fine —
        /// the server will send a dead notification shortly anyway.
        /// </summary>
        public void LinkNetworkProjectileBatch(uint realBaseId, int count)
        {
            if (_pendingTempBases.Count == 0) return;
            var (tempBase, tempCount) = _pendingTempBases.Dequeue();

            int n = Mathf.Min(tempCount, count);
            for (int i = 0; i < n; i++)
            {
                uint tempId = tempBase + (uint)i;
                uint realId = realBaseId + (uint)i;

                // Search 2D buffer
                bool found = false;
                for (int j = 0; j < _count2D && !found; j++)
                {
                    if (_projs2D[j].ProjId == tempId)
                    {
                        _projs2D[j].ProjId = realId;
                        found = true;
                    }
                }
                // Search 3D buffer if not found in 2D
                if (!found)
                {
                    for (int j = 0; j < _count3D; j++)
                    {
                        if (_projs3D[j].ProjId == tempId)
                        {
                            _projs3D[j].ProjId = realId;
                            break;
                        }
                    }
                }
            }
        }

        // ── All clients: kill on server hit confirmation ───────────────────────

        /// <summary>
        /// Forces a projectile dead in the local buffer when the server confirms a hit.
        /// Called from MID_ProjectileNetworkBridge.HitConfirmedClientRpc.
        /// No-op if the projectile already naturally expired (Alive==0 or already compacted).
        /// </summary>
        public void KillNetworkProjectile(uint projId)
        {
            for (int i = 0; i < _count2D; i++)
            {
                if (_projs2D[i].ProjId == projId)
                {
                    _projs2D[i].Alive = 0;
                    return;
                }
            }
            for (int i = 0; i < _count3D; i++)
            {
                if (_projs3D[i].ProjId == projId)
                {
                    _projs3D[i].Alive = 0;
                    return;
                }
            }
        }

        // ── All clients: reconcile positions from server snapshots ─────────────

        /// <summary>
        /// Reconciles 2D projectile positions from server authority snapshots.
        /// If error is below threshold: skip (Rust sim is tracking correctly).
        /// If error is above threshold: correct position directly. The Rust sim
        /// continues smoothly from the corrected position on the next tick.
        /// Wave/Circular types are never sent snapshots (filtered server-side).
        /// </summary>
        public void ReconcileSnapshots2D(ProjectileSnapshot2D[] snapshots, int count)
        {
            const float kSkipThreshold = 0.08f;  // world units — below this skip correction

            for (int s = 0; s < count; s++)
            {
                uint projId = snapshots[s].ProjId;
                for (int i = 0; i < _count2D; i++)
                {
                    if (_projs2D[i].ProjId != projId) continue;

                    float dx = snapshots[s].X - _projs2D[i].X;
                    float dy = snapshots[s].Y - _projs2D[i].Y;
                    float errSq = dx * dx + dy * dy;

                    if (errSq < kSkipThreshold * kSkipThreshold) break;

                    // Direct position correction — Rust sim continues from here
                    _projs2D[i].X = snapshots[s].X;
                    _projs2D[i].Y = snapshots[s].Y;
                    break;
                }
            }
        }

        /// <summary>3D version of ReconcileSnapshots2D.</summary>
        public void ReconcileSnapshots3D(ProjectileSnapshot3D[] snapshots, int count)
        {
            const float kSkipThreshold = 0.08f;

            for (int s = 0; s < count; s++)
            {
                uint projId = snapshots[s].ProjId;
                for (int i = 0; i < _count3D; i++)
                {
                    if (_projs3D[i].ProjId != projId) continue;

                    float dx    = snapshots[s].X - _projs3D[i].X;
                    float dy    = snapshots[s].Y - _projs3D[i].Y;
                    float dz    = snapshots[s].Z - _projs3D[i].Z;
                    float errSq = dx * dx + dy * dy + dz * dz;

                    if (errSq < kSkipThreshold * kSkipThreshold) break;

                    _projs3D[i].X = snapshots[s].X;
                    _projs3D[i].Y = snapshots[s].Y;
                    _projs3D[i].Z = snapshots[s].Z;
                    break;
                }
            }
        }

        #endregion

        #region Public API — Offline Targets

        public uint RegisterTarget(LocalDamageTarget target, int unityLayer = 0)
        {
            if (target == null) return 0;
            target.UnityLayer = unityLayer;
            _targets[target.LocalId] = target;
            _targetLayers2D[target.LocalId] = unityLayer;
            WriteToCollisionBuffer2D(target);
            return target.LocalId;
        }

        public void UpdateTarget(LocalDamageTarget target)
        {
            if (target == null || !_targets.ContainsKey(target.LocalId)) return;
            _targets[target.LocalId] = target;
            WriteToCollisionBuffer2D(target);
        }

        public void DeactivateTarget(uint localId)
        {
            if (!_targets.TryGetValue(localId, out var t)) return;
            t.Active = false;
            _targets[localId] = t;
            WriteToCollisionBuffer2D(t);
        }

        public void RemoveTarget(uint localId)
        {
            _targets.Remove(localId);
            _targetLayers2D.Remove(localId);
            _targetLayers3D.Remove(localId);
            DeactivateInBuffer2D(localId);
        }

        private void WriteToCollisionBuffer2D(LocalDamageTarget t)
        {
            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != t.LocalId) continue;
                _targets2D[i] = new CollisionTarget
                {
                    X = t.Position.x, Y = t.Position.y,
                    Radius = t.Radius, TargetId = t.LocalId,
                    Active = t.Active ? (byte)1 : (byte)0
                };
                return;
            }
            if (_targetCount2D >= _maxTargets) return;
            _targets2D[_targetCount2D++] = new CollisionTarget
            {
                X = t.Position.x, Y = t.Position.y,
                Radius = t.Radius, TargetId = t.LocalId,
                Active = t.Active ? (byte)1 : (byte)0
            };
        }

        private void DeactivateInBuffer2D(uint localId)
        {
            for (int i = 0; i < _targetCount2D; i++)
                if (_targets2D[i].TargetId == localId)
                { _targets2D[i].Active = 0; return; }
        }

        #endregion

        #region Public API — Direct CollisionTarget Structs (offline)

        public void RegisterTarget2D(in CollisionTarget target, int unityLayer = 0)
        {
            _targetLayers2D[target.TargetId] = unityLayer;

            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != target.TargetId) continue;
                _targets2D[i] = target;
                if (_targets.TryGetValue(target.TargetId, out var ex))
                {
                    ex.Position   = new Vector3(target.X, target.Y, 0f);
                    ex.Radius     = target.Radius;
                    ex.Active     = target.Active != 0;
                    ex.UnityLayer = unityLayer;
                    _targets[target.TargetId] = ex;
                }
                return;
            }
            if (_targetCount2D >= _maxTargets)
            {
                MID_Logger.LogWarning(_logLevel,
                    "2D target buffer full.", nameof(LocalProjectileManager));
                return;
            }
            _targets2D[_targetCount2D++] = target;

            if (!_targets.ContainsKey(target.TargetId))
            {
                _targets[target.TargetId] = new LocalDamageTarget
                {
                    LocalId      = target.TargetId,
                    Position     = new Vector3(target.X, target.Y, 0f),
                    Radius       = target.Radius,
                    Active       = target.Active != 0,
                    UnityLayer   = unityLayer,
                    SourceObject = null
                };
            }
        }

        public void RegisterTarget3D(in CollisionTarget3D target, int unityLayer = 0)
        {
            _targetLayers3D[target.TargetId] = unityLayer;

            for (int i = 0; i < _targetCount3D; i++)
            {
                if (_targets3D[i].TargetId != target.TargetId) continue;
                _targets3D[i] = target;
                if (_targets.TryGetValue(target.TargetId, out var ex))
                {
                    ex.Position   = new Vector3(target.X, target.Y, target.Z);
                    ex.Radius     = target.Radius;
                    ex.Active     = target.Active != 0;
                    ex.UnityLayer = unityLayer;
                    _targets[target.TargetId] = ex;
                }
                return;
            }
            if (_targetCount3D >= _maxTargets)
            {
                MID_Logger.LogWarning(_logLevel,
                    "3D target buffer full.", nameof(LocalProjectileManager));
                return;
            }
            _targets3D[_targetCount3D++] = target;

            if (!_targets.ContainsKey(target.TargetId))
            {
                _targets[target.TargetId] = new LocalDamageTarget
                {
                    LocalId      = target.TargetId,
                    Position     = new Vector3(target.X, target.Y, target.Z),
                    Radius       = target.Radius,
                    Active       = target.Active != 0,
                    UnityLayer   = unityLayer,
                    SourceObject = null
                };
            }
        }

        public void DeactivateTarget2D(uint targetId)
        {
            for (int i = 0; i < _targetCount2D; i++)
                if (_targets2D[i].TargetId == targetId)
                { _targets2D[i].Active = 0; break; }
            if (_targets.TryGetValue(targetId, out var t))
            {
                t.Active = false;
                _targets[targetId] = t;
            }
        }

        public void DeactivateTarget3D(uint targetId)
        {
            for (int i = 0; i < _targetCount3D; i++)
                if (_targets3D[i].TargetId == targetId)
                { _targets3D[i].Active = 0; break; }
            if (_targets.TryGetValue(targetId, out var t))
            {
                t.Active = false;
                _targets[targetId] = t;
            }
        }

        public void ClearAllTargets()
        {
            _targetCount2D = 0;
            _targetCount3D = 0;
            _targets.Clear();
            _targetLayers2D.Clear();
            _targetLayers3D.Clear();
        }

        #endregion

        #region Internal Helpers

        private uint AllocateProjIds(int count)
        {
            uint baseId = _nextProjId;
            _nextProjId += (uint)count;
            return baseId;
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

        private static SpawnPoint[] BuildSpawnPointsFromConfirmation(
            SpawnConfirmation conf, int count)
        {
            var pts = new SpawnPoint[count];
            for (int i = 0; i < count; i++)
                pts[i] = new SpawnPoint
                {
                    Origin    = conf.Origin,
                    Direction = conf.GetDirection(i).normalized,
                    Speed     = conf.Speed
                };
            return pts;
        }

        #endregion

        #region Supporting Data Type

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
