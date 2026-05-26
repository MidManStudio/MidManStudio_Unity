// MID_ProjectileNetworkBridge.cs
// FIX: ProjectileFireRequest now carries per-projectile directions so patterns
//      work correctly in networked mode. ExtraDirections serialized manually.
// All previous host-mode fixes retained.

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
        public Vector3 Direction;       // direction for projectile[0]
        public float   Speed;
        public uint    RngSeed;
        public byte    ProjectileCount;
        public ulong   OwnerMidId;
        public ulong   FiredByNetworkObjectId;
        public bool    IsBotOwner;
        public byte    WeaponLevel;
        public float   DamageMultiplier;
        public int     ClientFireTick;

        // Per-projectile directions for pattern support.
        // ExtraDirectionCount = ProjectileCount - 1 (capped at 63).
        // ExtraDirections[i] is the direction for projectile[i+1].
        public byte      ExtraDirectionCount;
        public Vector3[] ExtraDirections;   // may be null when ExtraDirectionCount == 0

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
                ExtraDirections = ExtraDirectionCount > 0
                    ? new Vector3[ExtraDirectionCount] : null;

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

        // Per-projectile directions mirrored from request
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
                ExtraDirections = ExtraDirectionCount > 0
                    ? new Vector3[ExtraDirectionCount] : null;

            for (int i = 0; i < ExtraDirectionCount; i++)
            {
                Vector3 d = (s.IsWriter && ExtraDirections != null && i < ExtraDirections.Length)
                    ? ExtraDirections[i] : Vector3.zero;
                s.SerializeValue(ref d);
                if (s.IsReader && ExtraDirections != null) ExtraDirections[i] = d;
            }
        }

        /// <summary>Get the direction for projectile at index i.</summary>
        public Vector3 GetDirection(int i)
        {
            if (i == 0) return Direction;
            int extraIdx = i - 1;
            return (ExtraDirections != null && extraIdx < ExtraDirections.Length)
                ? ExtraDirections[extraIdx]
                : Direction;
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

        #region Client → Server: Fire

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

            // FIX: reconstruct all per-projectile spawn points from directions
            var spawnPts = BuildServerSpawnPoints(request);

            var rustParams = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);

            uint baseId = Authority.AllocateProjIds(request.ProjectileCount);

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

            var confirm = new SpawnConfirmation
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
            };
            SpawnConfirmedClientRpc(confirm);
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

        #region Server → Clients

        [ClientRpc]
        public void SpawnConfirmedClientRpc(SpawnConfirmation confirmation)
        {
            if (IsServer && !IsClient) return;

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

        /// <summary>
        /// FIX: Reconstruct per-projectile spawn points from the fire request.
        /// Each projectile now uses its own direction from ExtraDirections,
        /// not a duplicate of the first direction.
        /// </summary>
        private static SpawnPoint[] BuildServerSpawnPoints(ProjectileFireRequest request)
        {
            int count = request.ProjectileCount;
            var pts   = new SpawnPoint[count];

            for (int i = 0; i < count; i++)
            {
                Vector3 dir;
                if (i == 0)
                {
                    dir = request.Direction.normalized;
                }
                else
                {
                    int extraIdx = i - 1;
                    dir = (request.ExtraDirections != null && extraIdx < request.ExtraDirections.Length)
                        ? request.ExtraDirections[extraIdx].normalized
                        : request.Direction.normalized;
                }

                pts[i] = new SpawnPoint
                {
                    Origin    = request.Origin,
                    Direction = dir,
                    Speed     = request.Speed
                };
            }
            return pts;
        }

        #endregion
    }
}
