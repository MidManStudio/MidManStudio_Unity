// MID_ProjectileNetworkBridge.cs
// CHANGE: RaycastFireServerRpc now passes rpcParams.Receive.SenderClientId to
// RaycastHandler.ServerHandleFire so it can exclude the firing client from
// SpawnVisualClientRpc. Previously the server sent the visual to ALL clients,
// causing the firing client to see two bullet visuals (their local one from
// OfflineHandleFire + the server's).

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
    // ── Fire request ──────────────────────────────────────────────────────────

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
            s.SerializeValue(ref ExtraDirectionCount);

            if (s.IsReader)
                ExtraDirections = ExtraDirectionCount > 0 ? new Vector3[ExtraDirectionCount] : null;

            for (int i = 0; i < ExtraDirectionCount; i++)
            {
                Vector3 d = (s.IsWriter && ExtraDirections != null && i < ExtraDirections.Length)
                    ? ExtraDirections[i] : Vector3.zero;
                s.SerializeValue(ref d);
                if (s.IsReader && ExtraDirections != null) ExtraDirections[i] = d;
            }
        }
    }

    // ── Spawn confirmation ────────────────────────────────────────────────────

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
            s.SerializeValue(ref BaseProjId);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref ConfigId);
            s.SerializeValue(ref ServerSpawnTick);
            s.SerializeValue(ref Origin);
            s.SerializeValue(ref Direction);
            s.SerializeValue(ref Speed);
            s.SerializeValue(ref OwnerMidId);
            s.SerializeValue(ref ExtraDirectionCount);

            if (s.IsReader)
                ExtraDirections = ExtraDirectionCount > 0 ? new Vector3[ExtraDirectionCount] : null;

            for (int i = 0; i < ExtraDirectionCount; i++)
            {
                Vector3 d = (s.IsWriter && ExtraDirections != null && i < ExtraDirections.Length)
                    ? ExtraDirections[i] : Vector3.zero;
                s.SerializeValue(ref d);
                if (s.IsReader && ExtraDirections != null) ExtraDirections[i] = d;
            }
        }

        public Vector3 GetDirection(int i)
        {
            if (i == 0) return Direction;
            int extra = i - 1;
            return (ExtraDirections != null && extra < ExtraDirections.Length)
                ? ExtraDirections[extra] : Direction;
        }
    }

    // ── Hit confirmation ──────────────────────────────────────────────────────

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

    // ── Bridge ────────────────────────────────────────────────────────────────

    public sealed class MID_ProjectileNetworkBridge : NetworkBehaviour
    {
        #region References

        public ServerProjectileAuthority Authority      { get; set; }
        public ClientPredictionManager   Prediction     { get; set; }
        public RaycastProjectileHandler  RaycastHandler { get; set; }
        public ProjectileImpactHandler   ImpactHandler  { get; set; }

        #endregion

        #region Events

        public event Action<HitConfirmation> OnHitConfirmedLocal;

        #endregion

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

        #endregion

        #region Server → client hit routing

        private void ServerOnProjectileHit(ProjectileHitPayload payload)
        {
            if (!IsServer) return;
            HitConfirmedClientRpc(new HitConfirmation
            {
                ProjId          = payload.ProjId,
                TargetNetworkId = payload.TargetId,
                Damage          = payload.Damage,
                HitPosition     = payload.HitPosition,
                IsHeadshot      = payload.IsHeadshot,
                IsCrit          = payload.IsCrit,
                ConfigId        = payload.ConfigId
            });
        }

        #endregion

        #region Client → Server: Fire (Rust Sim)

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

            var spawnPts   = BuildServerSpawnPoints(request);
            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);
            uint baseId    = Authority.AllocateProjIds(request.ProjectileCount);

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

            if (written <= 0) return;

            SpawnConfirmedClientRpc(new SpawnConfirmation
            {
                BaseProjId          = baseId,
                ProjectileCount     = (byte)written,
                ConfigId            = request.ConfigId,
                ServerSpawnTick     = GetServerTick(),
                Origin              = request.Origin,
                Direction           = request.Direction,
                Speed               = clampedSpeed,
                OwnerMidId          = request.OwnerMidId,
                ExtraDirectionCount = request.ExtraDirectionCount,
                ExtraDirections     = request.ExtraDirections
            });
        }

        #endregion

        #region Client → Server: Raycast

        [ServerRpc(RequireOwnership = false)]
        public void RaycastFireServerRpc(
            ProjectileFireRequest request,
            Vector3 clientHitPoint,
            bool    clientDidHit,
            bool    clientIsHeadshot,
            ulong   clientHitTargetId,
            bool    clientIs3D,
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
                IsHeadshot         = clientIsHeadshot,
                Is3D               = clientIs3D
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

            // Pass the sender's client ID so ServerHandleFire can exclude them from
            // SpawnVisualClientRpc — the firing client already has their local visual.
            RaycastHandler.ServerHandleFire(
                result, context, request.ConfigId,
                rpcParams.Receive.SenderClientId);
        }

        #endregion

        #region Server → Clients

        /// <summary>
        /// Returns early on the host (IsServer) because the host renders projectiles
        /// directly from the Rust buffer in ServerProjectileAuthority.LateUpdate.
        /// Pure clients spawn prediction visuals to get immediate visual feedback.
        /// </summary>
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

        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            if (IsClient)
                Prediction?.OnHitConfirmed(confirmation);

            ImpactHandler?.PlayImpact(
                confirmation.HitPosition, confirmation.ConfigId, confirmation.IsHeadshot);

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
            => NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Tick : 0;

        private float ComputeLatencyComp(ServerRpcParams rpc, int clientTick)
        {
            if (NetworkManager.Singleton == null) return 0f;
            int   serverTick   = GetServerTick();
            int   deltaTicks   = serverTick - clientTick;
            float tickInterval = 1f / NetworkManager.Singleton.NetworkTickSystem.TickRate;
            return Mathf.Clamp(deltaTicks * tickInterval, 0f, 0.5f);
        }

        private static SpawnPoint[] BuildServerSpawnPoints(ProjectileFireRequest request)
        {
            int count = request.ProjectileCount;
            var pts   = new SpawnPoint[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 dir;
                if (i == 0)
                    dir = request.Direction.normalized;
                else
                {
                    int extra = i - 1;
                    dir = (request.ExtraDirections != null && extra < request.ExtraDirections.Length)
                        ? request.ExtraDirections[extra].normalized
                        : request.Direction.normalized;
                }
                pts[i] = new SpawnPoint
                {
                    Origin = request.Origin, Direction = dir, Speed = request.Speed
                };
            }
            return pts;
        }

        #endregion
    }
}
