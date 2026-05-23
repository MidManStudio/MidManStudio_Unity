// MID_ProjectileNetworkBridge.cs
// FIX (Rust Sim visuals not showing on host):
//   SpawnConfirmedClientRpc had `if (IsServer) return` — in host mode the
//   machine is BOTH server AND client (IsServer=true, IsClient=true).
//   This caused the host to skip spawning its own prediction visuals entirely.
//   Fixed: `if (IsServer && !IsClient) return` — only dedicated servers skip.
//
// FIX (projectile hits not confirming visuals on host):
//   Same problem in HitConfirmedClientRpc and SendSnapshotClientRpc.
//   Changed all `!IsServer` / `IsServer` guards to use `IsClient` instead,
//   which correctly includes the host machine.
//
// FIX (duplicate SendSnapshotClientRpc):
//   Removed the internal overload with ClientRpcParams — NGO does not allow
//   two [ClientRpc] methods with the same name; it caused silent routing bugs.
//   ServerProjectileAuthority calls the 4-param version directly.

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
    // ── Network-serialisable fire request ─────────────────────────────────────

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

        /// <summary>
        /// FIX: Changed guard from `if (IsServer) return` to `if (IsServer && !IsClient) return`.
        /// In host mode the machine is both server and client — the old guard caused the host
        /// to skip spawning its own local prediction visuals, so Rust Sim projectiles were
        /// invisible for the player firing them in host/offline-networked sessions.
        /// </summary>
        [ClientRpc]
        public void SpawnConfirmedClientRpc(SpawnConfirmation confirmation)
        {
            // Skip on dedicated server (server only, no local client).
            // Host machines (IsServer && IsClient) MUST NOT skip — they need visuals.
            if (IsServer && !IsClient) return;

            MID_Logger.LogDebug(_logLevel,
                $"SpawnConfirmedClientRpc: baseId={confirmation.BaseProjId} " +
                $"count={confirmation.ProjectileCount}",
                nameof(MID_ProjectileNetworkBridge));

            Prediction?.OnSpawnConfirmed(confirmation);
        }

        #endregion

        #region Server → Clients: Hit Confirmed

        /// <summary>
        /// FIX: Changed `if (!IsServer)` to `if (IsClient)` for the Prediction call.
        /// In host mode !IsServer is false, so the host's prediction manager was never
        /// notified of confirmed hits — prediction visuals would linger past impact.
        /// </summary>
        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            MID_Logger.LogDebug(_logLevel,
                $"HitConfirmedClientRpc: projId={confirmation.ProjId} " +
                $"damage={confirmation.Damage:F1} headshot={confirmation.IsHeadshot}",
                nameof(MID_ProjectileNetworkBridge));

            // IsClient is true on both dedicated clients AND hosts.
            // This ensures hosts update their own prediction visuals on confirmed hits.
            if (IsClient)
                Prediction?.OnHitConfirmed(confirmation);

            ImpactHandler?.PlayImpact(
                confirmation.HitPosition,
                confirmation.ConfigId,
                confirmation.IsHeadshot);

            OnHitConfirmedLocal?.Invoke(confirmation);
        }

        #endregion

        #region Server → Clients: Position Snapshot

        /// <summary>
        /// FIX: Changed `if (IsServer) return` to `if (IsServer && !IsClient) return`.
        /// Hosts need to reconcile their own prediction state with server snapshots,
        /// same as remote clients. The old guard skipped reconciliation on host.
        ///
        /// FIX: Removed the duplicate internal overload with ClientRpcParams — NGO
        /// cannot have two [ClientRpc] methods with the same name; it caused silent
        /// message routing bugs where snapshots would call themselves recursively.
        /// </summary>
        [ClientRpc]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            // Skip dedicated server — it doesn't predict, it authorises.
            if (IsServer && !IsClient) return;

            Prediction?.ReconcileSnapshot(snapshots2D, count2D, snapshots3D, count3D);
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
            int   serverTick   = GetServerTick();
            int   deltaTicks   = serverTick - clientTick;
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
