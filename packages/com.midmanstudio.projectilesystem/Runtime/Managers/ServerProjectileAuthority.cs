// ServerProjectileAuthority.cs
// Server-only. Owns the Rust sim buffers (2D and 3D).
// Runs tick + collision every FixedUpdate.
// Sends position snapshots every N ticks for client reconciliation.
//
// FIX: Added TrailPool?.SyncToSimulation(_projs2D, _count2D) after Tick2D so
//      trail renderer positions are updated every physics step on the server
//      (and in offline/host-mode where the server IS the local machine).
//      Without this call the TrailObjectPool never moved trail renderers and
//      all trails appeared frozen at their spawn origin.

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.HelperFunctions;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Data;
using MidManStudio.Projectiles.Visuals;
using MidManStudio.Projectiles.Network;

namespace MidManStudio.Projectiles.Managers
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Snapshot structs
    // ─────────────────────────────────────────────────────────────────────────

    public struct ProjectileSnapshot2D : INetworkSerializable
    {
        public uint  ProjId;
        public float X;
        public float Y;
        public int   ServerTick;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref X);
            s.SerializeValue(ref Y);
            s.SerializeValue(ref ServerTick);
        }
    }

    public struct ProjectileSnapshot3D : INetworkSerializable
    {
        public uint  ProjId;
        public float X;
        public float Y;
        public float Z;
        public int   ServerTick;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref X);
            s.SerializeValue(ref Y);
            s.SerializeValue(ref Z);
            s.SerializeValue(ref ServerTick);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ServerProjectileAuthority
    // ─────────────────────────────────────────────────────────────────────────

    public class ServerProjectileAuthority : MonoBehaviour
    {
        #region Configuration

        [Header("Buffer Capacity")]
        [SerializeField] private int _maxProjectiles2D = 2048;
        [SerializeField] private int _maxProjectiles3D = 512;
        [SerializeField] private int _maxTargets2D     = 128;
        [SerializeField] private int _maxTargets3D     = 64;
        [SerializeField] private int _maxHitsPerTick   = 256;

        [Header("Collision Tuning")]
        [Tooltip("World units per collision grid cell (~2× largest target radius). 0 = Rust default (4.0).")]
        [SerializeField] private float _cellSize2D = 4f;
        [SerializeField] private float _cellSize3D = 4f;

        [Header("Snapshot")]
        [Tooltip("Send position snapshot every N FixedUpdates.")]
        [SerializeField] private int _snapshotIntervalTicks = 4;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region Owned References

        public RustSimAdapter              Adapter       { get; private set; }
        public TrailObjectPool             TrailPool     { get; set; }
        public MID_ProjectileNetworkBridge NetworkBridge { get; set; }

        #endregion

        #region 2D Sim Buffer

        private NativeProjectile[]  _projs2D;
        private CollisionTarget[]   _targets2D;
        private HitResult[]         _hits2D;
        private int                 _count2D;
        private int                 _targetCount2D;
        private uint                _nextProjId = 1;

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

        #region Snapshot Staging

        private ProjectileSnapshot2D[] _snapshots2D;
        private ProjectileSnapshot3D[] _snapshots3D;
        private int _fixedUpdateCounter;

        #endregion

        #region Public API — Counts

        public int ActiveCount2D    => _count2D;
        public int ActiveCount3D    => _count3D;
        public int MaxProjectiles2D => _maxProjectiles2D;
        public int MaxProjectiles3D => _maxProjectiles3D;

        #endregion

        #region Initialisation

        private void Awake()
        {
            Adapter = new RustSimAdapter();
            Adapter.OnProjectileDied += OnAdapterProjectileDied;

            AllocateBuffers();
            BatchSpawnHelper.Initialise();

            Log("ServerProjectileAuthority initialised.");
        }

        private void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            FreeBuffers();
            ProjectileLib.clear_movement_params();
        }

        private void AllocateBuffers()
        {
            _projs2D   = new NativeProjectile[_maxProjectiles2D];
            _targets2D = new CollisionTarget[_maxTargets2D];
            _hits2D    = new HitResult[_maxHitsPerTick];
            _pinProjs2D   = GCHandle.Alloc(_projs2D,   GCHandleType.Pinned);
            _pinTargets2D = GCHandle.Alloc(_targets2D, GCHandleType.Pinned);
            _pinHits2D    = GCHandle.Alloc(_hits2D,    GCHandleType.Pinned);

            _projs3D   = new NativeProjectile3D[_maxProjectiles3D];
            _targets3D = new CollisionTarget3D[_maxTargets3D];
            _hits3D    = new HitResult3D[_maxHitsPerTick];
            _pinProjs3D   = GCHandle.Alloc(_projs3D,   GCHandleType.Pinned);
            _pinTargets3D = GCHandle.Alloc(_targets3D, GCHandleType.Pinned);
            _pinHits3D    = GCHandle.Alloc(_hits3D,    GCHandleType.Pinned);

            _snapshots2D = new ProjectileSnapshot2D[_maxProjectiles2D];
            _snapshots3D = new ProjectileSnapshot3D[_maxProjectiles3D];
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

        #region FixedUpdate — Main Sim Loop

        private void FixedUpdate()
        {
            if (!IsServer()) return;

            float dt = Time.fixedDeltaTime;
            _fixedUpdateCounter++;

            if (_count2D > 0)
            {
                Tick2D(dt);
                Collision2D();
                // FIX: sync trail renderer positions every physics step.
                // Without this the TrailObjectPool never moved its renderers
                // and all trails appeared frozen at their spawn origin.
                TrailPool?.SyncToSimulation(_projs2D, _count2D);
                CompactDead2D();
            }

            if (_count3D > 0)
            {
                Tick3D(dt);
                Collision3D();
                CompactDead3D();
            }

            if (_fixedUpdateCounter % _snapshotIntervalTicks == 0)
                SendSnapshots();
        }

        private void Tick2D(float dt)
            => ProjectileLib.tick_projectiles(_pinProjs2D.AddrOfPinnedObject(), _count2D, dt);

        private void Collision2D()
        {
            if (_targetCount2D == 0) return;

            ProjectileLib.check_hits_grid_ex(
                _pinProjs2D.AddrOfPinnedObject(),   _count2D,
                _pinTargets2D.AddrOfPinnedObject(), _targetCount2D,
                _pinHits2D.AddrOfPinnedObject(),    _hits2D.Length,
                _cellSize2D,
                out int hitCount);

            for (int i = 0; i < hitCount; i++)
            {
                ref var h   = ref _hits2D[i];
                int     idx = (int)h.ProjIndex;
                if (idx < 0 || idx >= _count2D) continue;

                bool headshot = CheckHeadshot2D(in h);
                Adapter.ProcessHit(in h, headshot);

                if (!Adapter.IsRegistered(h.ProjId))
                {
                    if (idx < _count2D) _projs2D[idx].Alive = 0;
                }
            }
        }

        private void CompactDead2D()
        {
            int write = 0;
            for (int read = 0; read < _count2D; read++)
            {
                if (_projs2D[read].Alive == 0)
                {
                    uint deadId = _projs2D[read].ProjId;
                    Adapter.NotifyDead(deadId);
                    TrailPool?.NotifyDead(deadId);
                    continue;
                }
                if (write != read) _projs2D[write] = _projs2D[read];
                write++;
            }
            _count2D = write;
        }

        private void Tick3D(float dt)
            => ProjectileLib.tick_projectiles_3d(_pinProjs3D.AddrOfPinnedObject(), _count3D, dt);

        private void Collision3D()
        {
            if (_targetCount3D == 0) return;

            ProjectileLib.check_hits_grid_3d(
                _pinProjs3D.AddrOfPinnedObject(),   _count3D,
                _pinTargets3D.AddrOfPinnedObject(), _targetCount3D,
                _pinHits3D.AddrOfPinnedObject(),    _hits3D.Length,
                _cellSize3D,
                out int hitCount);

            for (int i = 0; i < hitCount; i++)
            {
                ref var h   = ref _hits3D[i];
                int     idx = (int)h.ProjIndex;
                if (idx < 0 || idx >= _count3D) continue;

                bool headshot = CheckHeadshot3D(in h);
                Adapter.ProcessHit3D(in h, headshot);

                if (!Adapter.IsRegistered(h.ProjId))
                {
                    if (idx < _count3D) _projs3D[idx].Alive = 0;
                }
            }
        }

        private void CompactDead3D()
        {
            int write = 0;
            for (int read = 0; read < _count3D; read++)
            {
                if (_projs3D[read].Alive == 0)
                {
                    uint deadId = _projs3D[read].ProjId;
                    Adapter.NotifyDead(deadId);
                    TrailPool?.NotifyDead(deadId);
                    continue;
                }
                if (write != read) _projs3D[write] = _projs3D[read];
                write++;
            }
            _count3D = write;
        }

        #endregion

        #region Snapshot

        private void SendSnapshots()
        {
            if (NetworkBridge == null) return;

            int serverTick  = NetworkBridge.GetServerTick();
            int snap2DCount = 0;
            int snap3DCount = 0;

            for (int i = 0; i < _count2D; i++)
            {
                if (_projs2D[i].Alive == 0) continue;
                _snapshots2D[snap2DCount++] = new ProjectileSnapshot2D
                {
                    ProjId = _projs2D[i].ProjId, X = _projs2D[i].X,
                    Y = _projs2D[i].Y, ServerTick = serverTick
                };
            }

            for (int i = 0; i < _count3D; i++)
            {
                if (_projs3D[i].Alive == 0) continue;
                _snapshots3D[snap3DCount++] = new ProjectileSnapshot3D
                {
                    ProjId = _projs3D[i].ProjId, X = _projs3D[i].X,
                    Y = _projs3D[i].Y, Z = _projs3D[i].Z, ServerTick = serverTick
                };
            }

            if (snap2DCount > 0 || snap3DCount > 0)
                NetworkBridge.SendSnapshotClientRpc(
                    _snapshots2D, snap2DCount, _snapshots3D, snap3DCount);
        }

        #endregion

        #region Public API — Spawn

        public bool AddProjectile2D(in NativeProjectile proj, ServerProjectileData data)
        {
            if (_count2D >= _maxProjectiles2D) { LogWarning("2D buffer full."); return false; }
            _projs2D[_count2D] = proj;
            data.projectileId_u32 = proj.ProjId;
            data.configId         = proj.ConfigId;
            _count2D++;
            Adapter.Register(data);
            return true;
        }

        public bool AddProjectile3D(in NativeProjectile3D proj, ServerProjectileData data)
        {
            if (_count3D >= _maxProjectiles3D) { LogWarning("3D buffer full."); return false; }
            _projs3D[_count3D] = proj;
            data.projectileId_u32 = proj.ProjId;
            data.configId         = proj.ConfigId;
            data.is3D             = true;
            _count3D++;
            Adapter.Register(data);
            return true;
        }

        public void NotifyBatchSpawned2D(
            int spawned, uint baseId, ServerProjectileData templateData)
        {
            for (uint i = 0; i < (uint)spawned; i++)
            {
                int  bufIdx = _count2D + (int)i;
                uint projId = baseId + i;
                var  data   = templateData.CloneForSpawn(projId, _projs2D[bufIdx].ConfigId);
                data.isCrit = UnityEngine.Random.value < data.critChance;
                Adapter.Register(data);
            }
            _count2D += spawned;
        }

        public void NotifyBatchSpawned3D(
            int spawned, uint baseId, ServerProjectileData templateData)
        {
            for (uint i = 0; i < (uint)spawned; i++)
            {
                int  bufIdx = _count3D + (int)i;
                uint projId = baseId + i;
                var  data   = templateData.CloneForSpawn(projId, _projs3D[bufIdx].ConfigId);
                data.is3D   = true;
                data.isCrit = UnityEngine.Random.value < data.critChance;
                Adapter.Register(data);
            }
            _count3D += spawned;
        }

        public (IntPtr ptr, int remaining) Get2DWriteHead()
        {
            IntPtr ptr = IntPtr.Add(
                _pinProjs2D.AddrOfPinnedObject(),
                _count2D * Marshal.SizeOf<NativeProjectile>());
            return (ptr, _maxProjectiles2D - _count2D);
        }

        public (IntPtr ptr, int remaining) Get3DWriteHead()
        {
            IntPtr ptr = IntPtr.Add(
                _pinProjs3D.AddrOfPinnedObject(),
                _count3D * Marshal.SizeOf<NativeProjectile3D>());
            return (ptr, _maxProjectiles3D - _count3D);
        }

        public uint AllocateProjIds(int count)
        {
            uint baseId = _nextProjId;
            _nextProjId += (uint)count;
            return baseId;
        }

        #endregion

        #region Public API — Targets

        public void RegisterTarget2D(in CollisionTarget target)
        {
            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != target.TargetId) continue;
                _targets2D[i] = target; return;
            }
            if (_targetCount2D >= _maxTargets2D) { LogWarning("2D target buffer full."); return; }
            _targets2D[_targetCount2D++] = target;
        }

        public void RegisterTarget3D(in CollisionTarget3D target)
        {
            for (int i = 0; i < _targetCount3D; i++)
            {
                if (_targets3D[i].TargetId != target.TargetId) continue;
                _targets3D[i] = target; return;
            }
            if (_targetCount3D >= _maxTargets3D) { LogWarning("3D target buffer full."); return; }
            _targets3D[_targetCount3D++] = target;
        }

        public void DeactivateTarget2D(uint targetId)
        {
            for (int i = 0; i < _targetCount2D; i++)
                if (_targets2D[i].TargetId == targetId) { _targets2D[i].Active = 0; return; }
        }

        public void DeactivateTarget3D(uint targetId)
        {
            for (int i = 0; i < _targetCount3D; i++)
                if (_targets3D[i].TargetId == targetId) { _targets3D[i].Active = 0; return; }
        }

        public void ClearAllTargets()
        {
            _targetCount2D = 0;
            _targetCount3D = 0;
        }

        #endregion

        #region Public API — Guided / Wave / Circular Direction Updates

        public void SetAcceleration2D(uint projId, Vector2 accelDir)
        {
            for (int i = 0; i < _count2D; i++)
            {
                if (_projs2D[i].ProjId != projId) continue;
                Vector2 n = accelDir.normalized;
                _projs2D[i].Ax = n.x;
                _projs2D[i].Ay = n.y;
                return;
            }
        }

        public void SetAcceleration3D(uint projId, Vector3 accelDir)
        {
            for (int i = 0; i < _count3D; i++)
            {
                if (_projs3D[i].ProjId != projId) continue;
                Vector3 n = accelDir.normalized;
                _projs3D[i].Ax = n.x;
                _projs3D[i].Ay = n.y;
                _projs3D[i].Az = n.z;
                return;
            }
        }

        #endregion

        #region State Save / Restore

        public int SaveState2D(byte[] buf)
        {
            if (buf == null || buf.Length < _count2D * 72) return 0;
            var pin     = GCHandle.Alloc(buf, GCHandleType.Pinned);
            int written = ProjectileLib.save_state(
                _pinProjs2D.AddrOfPinnedObject(), _count2D,
                pin.AddrOfPinnedObject(), buf.Length);
            pin.Free();
            return written;
        }

        public int RestoreState2D(byte[] buf, int byteCount)
        {
            if (buf == null || byteCount <= 0) return 0;
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            ProjectileLib.restore_state(
                _pinProjs2D.AddrOfPinnedObject(), _maxProjectiles2D,
                pin.AddrOfPinnedObject(), byteCount,
                out int restored);
            pin.Free();
            _count2D = restored;
            return restored;
        }

        #endregion

        #region Headshot Detection — Virtual Hooks

        protected virtual bool CheckHeadshot2D(in HitResult   hit) => false;
        protected virtual bool CheckHeadshot3D(in HitResult3D hit) => false;

        #endregion

        #region Internal Events

        private void OnAdapterProjectileDied(uint projId)
        {
            // Trail notification happens in CompactDead — no double-notify needed here.
        }

        #endregion

        #region Utilities

        private static bool IsServer()
            => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        private void Log(string msg)
        {
            if (_enableLogs) MID_HelperFunctions.LogDebug(msg, nameof(ServerProjectileAuthority));
        }

        private void LogWarning(string msg)
            => MID_HelperFunctions.LogWarning(msg, nameof(ServerProjectileAuthority));

        #endregion
    }
}
