// MID_ProjectileNetworkBridge.cs
// CHANGE: Added FirePhysicsProjectileServerRpc with RequireOwnership=false.
// Physics fire previously used a [ServerRpc] on NetworkedDimensionPlayer — if the
// player NetworkObject ownership is mismatched (lobby ID ≠ NGO client ID), that RPC
// is silently dropped and the client appears to not fire at all. Routing through the
// bridge bypasses the player ownership check entirely, exactly like FireServerRpc does
// for Rust sim projectiles.

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Data;
using MidManStudio.Projectiles.Visuals;
using MidManStudio.Projectiles.Managers;

namespace MidManStudio.Projectiles.Network
{
    public struct ProjectileFireRequest : INetworkSerializable
    {
        public ushort  ConfigId;
        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
        public uint    RngSeed;
        public byte    ProjectileCount;
        public ulong   OwnerMidId;
        public ulong   FiredByNetworkObjectId;
        public bool    IsBotOwner;
        public byte    WeaponLevel;
        public float   DamageMultiplier;
        public int     ClientFireTick;
        public byte      ExtraDirectionCount;
        public Vector3[] ExtraDirections;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ConfigId); s.SerializeValue(ref Origin); s.SerializeValue(ref Direction);
            s.SerializeValue(ref Speed); s.SerializeValue(ref RngSeed); s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref OwnerMidId); s.SerializeValue(ref FiredByNetworkObjectId);
            s.SerializeValue(ref IsBotOwner); s.SerializeValue(ref WeaponLevel);
            s.SerializeValue(ref DamageMultiplier); s.SerializeValue(ref ClientFireTick);
            s.SerializeValue(ref ExtraDirectionCount);
            if (s.IsReader) ExtraDirections = ExtraDirectionCount > 0 ? new Vector3[ExtraDirectionCount] : null;
            for (int i = 0; i < ExtraDirectionCount; i++)
            {
                Vector3 d = (s.IsWriter && ExtraDirections != null && i < ExtraDirections.Length) ? ExtraDirections[i] : Vector3.zero;
                s.SerializeValue(ref d);
                if (s.IsReader && ExtraDirections != null) ExtraDirections[i] = d;
            }
        }
    }

    public struct SpawnConfirmation : INetworkSerializable
    {
        public uint    BaseProjId;
        public byte    ProjectileCount;
        public ushort  ConfigId;
        public int     ServerSpawnTick;
        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
        public ulong   OwnerMidId;
        public byte      ExtraDirectionCount;
        public Vector3[] ExtraDirections;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref BaseProjId); s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref ConfigId); s.SerializeValue(ref ServerSpawnTick);
            s.SerializeValue(ref Origin); s.SerializeValue(ref Direction); s.SerializeValue(ref Speed);
            s.SerializeValue(ref OwnerMidId); s.SerializeValue(ref ExtraDirectionCount);
            if (s.IsReader) ExtraDirections = ExtraDirectionCount > 0 ? new Vector3[ExtraDirectionCount] : null;
            for (int i = 0; i < ExtraDirectionCount; i++)
            {
                Vector3 d = (s.IsWriter && ExtraDirections != null && i < ExtraDirections.Length) ? ExtraDirections[i] : Vector3.zero;
                s.SerializeValue(ref d);
                if (s.IsReader && ExtraDirections != null) ExtraDirections[i] = d;
            }
        }

        public Vector3 GetDirection(int i)
        {
            if (i == 0) return Direction;
            int extra = i - 1;
            return (ExtraDirections != null && extra < ExtraDirections.Length) ? ExtraDirections[extra] : Direction;
        }
    }

    public struct HitConfirmation : INetworkSerializable
    {
        public uint    ProjId;
        public ulong   TargetNetworkId;
        public float   Damage;
        public Vector3 HitPosition;
        public bool    IsHeadshot;
        public bool    IsCrit;
        public ushort  ConfigId;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ProjId); s.SerializeValue(ref TargetNetworkId);
            s.SerializeValue(ref Damage); s.SerializeValue(ref HitPosition);
            s.SerializeValue(ref IsHeadshot); s.SerializeValue(ref IsCrit); s.SerializeValue(ref ConfigId);
        }
    }

    public sealed class MID_ProjectileNetworkBridge : NetworkBehaviour
    {
        #region References

        public ServerProjectileAuthority Authority      { get; set; }
        public ClientPredictionManager   Prediction     { get; set; }
        public RaycastProjectileHandler  RaycastHandler { get; set; }
        public ProjectileImpactHandler   ImpactHandler  { get; set; }

        #endregion

        public event Action<HitConfirmation> OnHitConfirmedLocal;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #region Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer && Authority != null)
                Authority.Adapter.OnProjectileHit += ServerOnProjectileHit;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && Authority != null)
                Authority.Adapter.OnProjectileHit -= ServerOnProjectileHit;
            base.OnNetworkDespawn();
        }

        private void ServerOnProjectileHit(ProjectileHitPayload payload)
        {
            if (!IsServer) return;
            HitConfirmedClientRpc(new HitConfirmation
            {
                ProjId = payload.ProjId, TargetNetworkId = payload.TargetId, Damage = payload.Damage,
                HitPosition = payload.HitPosition, IsHeadshot = payload.IsHeadshot,
                IsCrit = payload.IsCrit, ConfigId = payload.ConfigId
            });
        }

        #endregion

        #region Client → Server: Rust Sim

        [ServerRpc(RequireOwnership = false)]
        public void FireServerRpc(ProjectileFireRequest request, ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            var cfg = ProjectileRegistry.Instance.Get(request.ConfigId);
            if (cfg == null) { MID_Logger.LogWarning(_logLevel, $"FireServerRpc: unknown configId {request.ConfigId}", nameof(MID_ProjectileNetworkBridge)); return; }

            float clampedSpeed = Mathf.Clamp(request.Speed, cfg.MinSpeed, cfg.MaxSpeed);
            var context = new WeaponFireContext
            {
                ProjectileCount = request.ProjectileCount, IsNetworked = true, IsRaycastWeapon = false,
                LatencyCompensation = ComputeLatencyComp(rpcParams, request.ClientFireTick),
                OwnerMidId = request.OwnerMidId, FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner = request.IsBotOwner, WeaponLevel = request.WeaponLevel, DamageMultiplier = request.DamageMultiplier
            };

            var spawnPts   = BuildServerSpawnPoints(request);
            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(request.ConfigId, clampedSpeed);
            uint baseId    = Authority.AllocateProjIds(request.ProjectileCount);
            var dataTemplate = new ServerProjectileData(request.OwnerMidId, request.FiredByNetworkObjectId,
                request.IsBotOwner, request.WeaponLevel,
                new Vector2(request.Origin.x, request.Origin.y), request.DamageMultiplier, cfg);

            int written;
            if (!cfg.Is3D)
            {
                var (ptr, rem) = Authority.Get2DWriteHead();
                written = BatchSpawnHelper.SpawnBatch2D(spawnPts, request.ProjectileCount, null, rustParams,
                    request.ConfigId, 0, baseId, ptr, rem, context.LatencyCompensation);
                Authority.NotifyBatchSpawned2D(written, baseId, dataTemplate);
            }
            else
            {
                var (ptr, rem) = Authority.Get3DWriteHead();
                written = BatchSpawnHelper.SpawnBatch3D(spawnPts, request.ProjectileCount, rustParams,
                    request.ConfigId, 0, baseId, ptr, rem, context.LatencyCompensation);
                Authority.NotifyBatchSpawned3D(written, baseId, dataTemplate);
            }

            if (written <= 0) return;

            SpawnConfirmedClientRpc(new SpawnConfirmation
            {
                BaseProjId = baseId, ProjectileCount = (byte)written, ConfigId = request.ConfigId,
                ServerSpawnTick = GetServerTick(), Origin = request.Origin, Direction = request.Direction,
                Speed = clampedSpeed, OwnerMidId = request.OwnerMidId,
                ExtraDirectionCount = request.ExtraDirectionCount, ExtraDirections = request.ExtraDirections
            });
        }

        #endregion

        #region Client → Server: Raycast

        [ServerRpc(RequireOwnership = false)]
        public void RaycastFireServerRpc(
            ProjectileFireRequest request,
            Vector3 clientHitPoint, bool clientDidHit, bool clientIsHeadshot,
            ulong clientHitTargetId, bool clientIs3D,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || RaycastHandler == null) return;
            RaycastHandler.ServerHandleFire(new RaycastFireResult
            {
                Origin = request.Origin, Direction = request.Direction, HitPoint = clientHitPoint,
                DidHit = clientDidHit, HitTargetNetworkId = clientHitTargetId,
                IsHeadshot = clientIsHeadshot, Is3D = clientIs3D
            }, new WeaponFireContext
            {
                IsRaycastWeapon = true, IsNetworked = true, OwnerMidId = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId, IsBotOwner = request.IsBotOwner,
                WeaponLevel = request.WeaponLevel, DamageMultiplier = request.DamageMultiplier
            }, request.ConfigId, rpcParams.Receive.SenderClientId);
        }

        #endregion

        #region Client → Server: Physics (NEW)

        /// <summary>
        /// Spawns a physics NetworkObject projectile on the server. RequireOwnership=false so
        /// any client can call it regardless of who owns the player NetworkObject — this is
        /// why physics fire was broken for clients: the old [ServerRpc] on NetworkedDimensionPlayer
        /// required ownership which could fail if lobby IDs don't match NGO client IDs.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void FirePhysicsProjectileServerRpc(
            Vector3 origin, Quaternion rotation,
            PoolableNetworkObjectType poolType,
            float speed, float damageMultiplier,
            ulong ownerMidId, ulong firedByNetObjId,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (!MID_MasterProjectileSystem.HasInstance) return;

            var netObj = MID_MasterProjectileSystem.Instance
                .SpawnPhysicsProjectile(poolType, origin, rotation);

            if (netObj == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FirePhysicsProjectileServerRpc: pool returned null for {poolType}. " +
                    "Ensure MID_NetworkObjectPool is assigned and the blueprint prefabs are registered.",
                    nameof(MID_ProjectileNetworkBridge));
                return;
            }

            var proj = netObj.GetComponent<PhysicsProjectileBase>();
            if (proj != null)
            {
                proj.SetOwnerContext(ownerMidId, firedByNetObjId, false, 1, damageMultiplier);
                proj.InitialiseProjectile(ownerMidId, firedByNetObjId, speed, false, 1);
            }
            else
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FirePhysicsProjectileServerRpc: no PhysicsProjectileBase on '{netObj.name}'.",
                    nameof(MID_ProjectileNetworkBridge));
            }
        }

        #endregion

        #region Server → Clients

        [ClientRpc]
        public void SpawnConfirmedClientRpc(SpawnConfirmation confirmation)
        {
            // Host renders from Rust buffer directly (ServerProjectileAuthority.LateUpdate).
            // Pure clients use the prediction manager.
            if (IsServer) return;
            MID_Logger.LogDebug(_logLevel,
                $"SpawnConfirmedClientRpc: baseId={confirmation.BaseProjId} count={confirmation.ProjectileCount}",
                nameof(MID_ProjectileNetworkBridge));
            Prediction?.OnSpawnConfirmed(confirmation);
        }

        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            if (IsClient) Prediction?.OnHitConfirmed(confirmation);
            ImpactHandler?.PlayImpact(confirmation.HitPosition, confirmation.ConfigId, confirmation.IsHeadshot);
            OnHitConfirmedLocal?.Invoke(confirmation);
        }

        [ClientRpc]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            if (IsServer && !IsClient) return;
            Prediction?.ReconcileSnapshot(snapshots2D, count2D, snapshots3D, count3D);
        }

        #endregion

        #region Utility

        public int GetServerTick()
            => NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Tick : 0;

        private float ComputeLatencyComp(ServerRpcParams rpc, int clientTick)
        {
            if (NetworkManager.Singleton == null) return 0f;
            int deltaTicks = GetServerTick() - clientTick;
            float tickInterval = 1f / NetworkManager.Singleton.NetworkTickSystem.TickRate;
            return Mathf.Clamp(deltaTicks * tickInterval, 0f, 0.5f);
        }

        private static SpawnPoint[] BuildServerSpawnPoints(ProjectileFireRequest req)
        {
            var pts = new SpawnPoint[req.ProjectileCount];
            for (int i = 0; i < req.ProjectileCount; i++)
            {
                Vector3 dir = i == 0 ? req.Direction.normalized
                    : (req.ExtraDirections != null && i - 1 < req.ExtraDirections.Length
                        ? req.ExtraDirections[i - 1].normalized : req.Direction.normalized);
                pts[i] = new SpawnPoint { Origin = req.Origin, Direction = dir, Speed = req.Speed };
            }
            return pts;
        }

        #endregion
    }
}
