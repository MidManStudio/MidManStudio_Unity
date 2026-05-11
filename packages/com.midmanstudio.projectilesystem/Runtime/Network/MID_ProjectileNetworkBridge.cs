// MID_ProjectileNetworkBridge.cs
// ALL NGO RPCs live here and ONLY RPCs. Zero game logic — pure messaging layer.
//
// MID ID note: ownerMidId is NOT NetworkObject.OwnerClientId.
//   Players:  ownerMidId == their NGO client ID.
//   Bots:     ownerMidId is a stable 100-999 range ID assigned at bot spawn.

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
    // ─────────────────────────────────────────────────────────────────────────
    //  Network-serialisable fire request — client → server on every weapon fire
    // ─────────────────────────────────────────────────────────────────────────

    public struct ProjectileFireRequest : INetworkSerializable
    {
        public ushort ConfigId;
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

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ConfigId);
            s.SerializeValue(ref Origin);
            s.SerializeValue(ref Direction);
            s.SerializeValue(ref Speed);
            s.SerializeValue(ref RngSeed);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref OwnerMidId);
            s.SerializeValue(ref FiredByNetworkObjectId);
            s.SerializeValue(ref IsBotOwner);
            s.SerializeValue(ref WeaponLevel);
            s.SerializeValue(ref DamageMultiplier);
            s.SerializeValue(ref ClientFireTick);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Spawn confirmation — server → all clients
    // ─────────────────────────────────────────────────────────────────────────

    public struct SpawnConfirmation : INetworkSerializable
    {
        public uint   BaseProjId;
        public byte   ProjectileCount;
        public ushort ConfigId;
        public int    ServerSpawnTick;
        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
        public ulong   OwnerMidId;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref BaseProjId);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref ConfigId);
            s.SerializeValue(ref ServerSpawnTick);
            s.SerializeValue(ref Origin);
            s.SerializeValue(ref Direction);
            s.SerializeValue(ref Speed);
            s.SerializeValue(ref OwnerMidId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Hit confirmation — server → all clients
    // ─────────────────────────────────────────────────────────────────────────

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
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref TargetNetworkId);
            s.SerializeValue(ref Damage);
            s.SerializeValue(ref HitPosition);
            s.SerializeValue(ref IsHeadshot);
            s.SerializeValue(ref IsCrit);
            s.SerializeValue(ref ConfigId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Bridge
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All NGO RPCs for the projectile system.
    /// Attach to the same persistent networked GameObject as ServerProjectileAuthority.
    /// </summary>
    public sealed class MID_ProjectileNetworkBridge : NetworkBehaviour
    {
        #region References

        public ServerProjectileAuthority Authority       { get; set; }
        public ClientPredictionManager   Prediction      { get; set; }
        public RaycastProjectileHandler  RaycastHandler  { get; set; }
        public ProjectileImpactHandler   ImpactHandler   { get; set; }

        #endregion

        #region Events

        /// <summary>
        /// Fired on all clients when the server confirms a hit.
        /// Subscribe from HUD, audio, and screen-shake systems.
        /// </summary>
        public event Action<HitConfirmation> OnHitConfirmedLocal;

        #endregion

        #region Debug

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

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

        #endregion

        #region Server → Client hit routing

        private void ServerOnProjectileHit(ProjectileHitPayload payload)
        {
            if (!IsServer) return;

            var confirm = new HitConfirmation
            {
                ProjId          = payload.ProjId,
                TargetNetworkId = payload.TargetId,
                Damage          = payload.Damage,
                HitPosition     = payload.HitPosition,
                IsHeadshot      = payload.IsHeadshot,
                IsCrit          = payload.IsCrit,
                ConfigId        = payload.ConfigId
            };

            HitConfirmedClientRpc(confirm);
        }

        #endregion

        #region Client → Server: Sim Projectile Fire

        [ServerRpc(RequireOwnership = false)]
        public void FireServerRpc(
            ProjectileFireRequest request,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;

            var cfg = ProjectileRegistry.Instance.Get(request.ConfigId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FireServerRpc: unknown configId {request.ConfigId}",
                    nameof(MID_ProjectileNetworkBridge));
                return;
            }

            float clampedSpeed = Mathf.Clamp(request.Speed, cfg.MinSpeed, cfg.MaxSpeed);

            var context = new WeaponFireContext
            {
                FireRate               = 0f,
                ProjectileCount        = request.ProjectileCount,
                IsNetworked            = true,
                IsRaycastWeapon        = false,
                LatencyCompensation    = ComputeLatencyComp(rpcParams, request.ClientFireTick),
                OwnerMidId             = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner             = request.IsBotOwner,
                WeaponLevel            = request.WeaponLevel,
                DamageMultiplier       = request.DamageMultiplier
            };

            var spawnPts = BuildServerSpawnPoints(
                request.Origin, request.Direction, clampedSpeed, request.ProjectileCount);

            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);

            uint baseId = Authority.AllocateProjIds(request.ProjectileCount);

            // Correct ServerProjectileData constructor — no game-specific name param.
            var dataTemplate = new ServerProjectileData(
                ownerMidId:         request.OwnerMidId,
                firedById:          request.FiredByNetworkObjectId,
                isBot:              request.IsBotOwner,
                level:              request.WeaponLevel,
                spawnPos2D:         new Vector2(request.Origin.x, request.Origin.y),
                damageMultiplierIn: request.DamageMultiplier,
                config:             cfg);

            int written;
            if (!cfg.Is3D)
            {
                var (writePtr, remaining) = Authority.Get2DWriteHead();
                written = BatchSpawnHelper.SpawnBatch2D(
                    spawnPts, request.ProjectileCount, null, rustParams,
                    request.ConfigId, 0, baseId, writePtr, remaining,
                    context.LatencyCompensation);
                Authority.NotifyBatchSpawned2D(written, baseId, dataTemplate);
            }
            else
            {
                var (writePtr, remaining) = Authority.Get3DWriteHead();
                written = BatchSpawnHelper.SpawnBatch3D(
                    spawnPts, request.ProjectileCount, rustParams,
                    request.ConfigId, 0, baseId, writePtr, remaining,
                    context.LatencyCompensation);
                Authority.NotifyBatchSpawned3D(written, baseId, dataTemplate);
            }

            if (written <= 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    "FireServerRpc: no projectiles written (buffer full?).",
                    nameof(MID_ProjectileNetworkBridge));
                return;
            }

            MID_Logger.LogDebug(_logLevel,
                $"FireServerRpc confirmed: configId={request.ConfigId} " +
                $"count={written} baseId={baseId} owner={request.OwnerMidId}",
                nameof(MID_ProjectileNetworkBridge));

            var confirm = new SpawnConfirmation
            {
                BaseProjId      = baseId,
                ProjectileCount = (byte)written,
                ConfigId        = request.ConfigId,
                ServerSpawnTick = GetServerTick(),
                Origin          = request.Origin,
                Direction       = request.Direction,
                Speed           = clampedSpeed,
                OwnerMidId      = request.OwnerMidId
            };

            SpawnConfirmedClientRpc(confirm);
        }

        #endregion

        #region Client → Server: Raycast Fire

        [ServerRpc(RequireOwnership = false)]
        public void RaycastFireServerRpc(
            ProjectileFireRequest request,
            Vector3 clientHitPoint,
            bool    clientDidHit,
            bool    clientIsHeadshot,
            ulong   clientHitTargetId,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || RaycastHandler == null) return;

            var result = new RaycastFireResult
            {
                Origin             = request.Origin,
                Direction          = request.Direction,
                HitPoint           = clientHitPoint,
                DidHit             = clientDidHit,
                HitTargetNetworkId = clientHitTargetId,
                IsHeadshot         = clientIsHeadshot
            };

            var context = new WeaponFireContext
            {
                IsRaycastWeapon        = true,
                IsNetworked            = true,
                OwnerMidId             = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner             = request.IsBotOwner,
                WeaponLevel            = request.WeaponLevel,
                DamageMultiplier       = request.DamageMultiplier
            };

            RaycastHandler.ServerHandleFire(result, context, request.ConfigId);
        }

        #endregion

        #region Server → Clients: Spawn Confirmed

        [ClientRpc]
        public void SpawnConfirmedClientRpc(SpawnConfirmation confirmation)
        {
            if (IsServer) return;

            MID_Logger.LogDebug(_logLevel,
                $"SpawnConfirmedClientRpc: baseId={confirmation.BaseProjId} " +
                $"count={confirmation.ProjectileCount}",
                nameof(MID_ProjectileNetworkBridge));

            Prediction?.OnSpawnConfirmed(confirmation);
        }

        #endregion

        #region Server → Clients: Hit Confirmed

        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            MID_Logger.LogDebug(_logLevel,
                $"HitConfirmedClientRpc: projId={confirmation.ProjId} " +
                $"damage={confirmation.Damage:F1} headshot={confirmation.IsHeadshot}",
                nameof(MID_ProjectileNetworkBridge));

            if (!IsServer)
                Prediction?.OnHitConfirmed(confirmation);

            // LocalParticlePool is used internally by ProjectileImpactHandler.
            ImpactHandler?.PlayImpact(
                confirmation.HitPosition,
                confirmation.ConfigId,
                confirmation.IsHeadshot);

            OnHitConfirmedLocal?.Invoke(confirmation);
        }

        #endregion

        #region Server → Clients: Position Snapshot

        [ClientRpc]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            if (IsServer) return;
            Prediction?.ReconcileSnapshot(snapshots2D, count2D, snapshots3D, count3D);
        }

        internal void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D,
            ClientRpcParams _ = default)
        {
            var slice2D = new ProjectileSnapshot2D[count2D];
            var slice3D = new ProjectileSnapshot3D[count3D];
            Array.Copy(snapshots2D, slice2D, count2D);
            Array.Copy(snapshots3D, slice3D, count3D);
            SendSnapshotClientRpc(slice2D, count2D, slice3D, count3D);
        }

        #endregion

        #region Utility

        public int GetServerTick()
            => NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Tick
                : 0;

        private float ComputeLatencyComp(ServerRpcParams rpc, int clientTick)
        {
            if (NetworkManager.Singleton == null) return 0f;
            int   serverTick  = GetServerTick();
            int   deltaTicks  = serverTick - clientTick;
            float tickInterval = 1f / NetworkManager.Singleton.NetworkTickSystem.TickRate;
            return Mathf.Clamp(deltaTicks * tickInterval, 0f, 0.5f);
        }

        private static SpawnPoint[] BuildServerSpawnPoints(
            Vector3 origin, Vector3 direction, float speed, int count)
        {
            var pts = new SpawnPoint[count];
            for (int i = 0; i < count; i++)
                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = direction.normalized,
                    Speed     = speed
                };
            return pts;
        }

        #endregion
    }
}
