
using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
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

        /// <summary>
        /// Server-authoritative NetworkTime.TimeAsFloat at spawn.
        /// Used by other clients to compute elapsed flight time for position catch-up.
        /// Zero = not set (offline / old server).
        /// </summary>
        public float ServerNetworkTime;

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
            s.SerializeValue(ref ServerNetworkTime);
        }

        public Vector3 GetDirection(int i)
        {
            if (i == 0) return Direction;
            int extra = i - 1;
            return (ExtraDirections != null && extra < ExtraDirections.Length)
                ? ExtraDirections[extra] : Direction;
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
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref TargetNetworkId);
            s.SerializeValue(ref Damage);
            s.SerializeValue(ref HitPosition);
            s.SerializeValue(ref IsHeadshot);
            s.SerializeValue(ref IsCrit);
            s.SerializeValue(ref ConfigId);
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

        // Still stored for ClientPredictionManager physics visual routing.
        // No longer used for SpawnConfirmedClientRpc routing (that now uses TargetClientIds).
        private ulong _localPlayerMidId;
        private bool  _isShuttingDown;

        // ── Transport Configuration ───────────────────────────────────────────

        public static void ConfigureTransportForHighThroughput()
        {
            if (NetworkManager.Singleton == null) return;
            var transport = NetworkManager.Singleton.NetworkConfig?.NetworkTransport
                as UnityTransport;
            if (transport == null)
            {
                Debug.LogWarning("[MID_ProjectileNetworkBridge] " +
                    "ConfigureTransportForHighThroughput: not UnityTransport.");
                return;
            }
            transport.MaxSendQueueSize = 16 * 1024 * 1024;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Stored for ClientPredictionManager physics visual routing.
        /// No longer needed for Rust sim routing — that uses TargetClientIds now.
        /// </summary>
        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _isShuttingDown = false;

            if (IsServer && Authority != null)
                Authority.Adapter.OnProjectileHit += ServerOnProjectileHit;

            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        }

        public override void OnNetworkDespawn()
        {
            _isShuttingDown = true;

            if (IsServer && Authority != null)
                Authority.Adapter.OnProjectileHit -= ServerOnProjectileHit;

            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;

            base.OnNetworkDespawn();
        }

        public override void OnDestroy() => _isShuttingDown = true;

        private void OnClientDisconnect(ulong clientId)
        {
            if (NetworkManager.Singleton != null
                && clientId == NetworkManager.Singleton.LocalClientId)
                _isShuttingDown = true;
        }

        private void ServerOnProjectileHit(ProjectileHitPayload payload)
        {
            if (!IsServer || !IsSpawned || _isShuttingDown) return;
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

        // ── Client → Server: Rust Sim ─────────────────────────────────────────

        [ServerRpc(RequireOwnership = false)]
        public void FireServerRpc(
            ProjectileFireRequest request,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsSpawned || _isShuttingDown) return;

            var cfg = ProjectileRegistry.Instance.Get(request.ConfigId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FireServerRpc: unknown configId {request.ConfigId}",
                    nameof(MID_ProjectileNetworkBridge));
                return;
            }

            float clampedSpeed = Mathf.Clamp(request.Speed, cfg.MinSpeed, cfg.MaxSpeed);
            float latencyComp  = ComputeLatencyComp(rpcParams, request.ClientFireTick);

            var spawnPts     = BuildServerSpawnPoints(request);
            var rustParams   = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);
            uint baseId      = Authority.AllocateProjIds(request.ProjectileCount);
            var dataTemplate = new ServerProjectileData(
                request.OwnerMidId, request.FiredByNetworkObjectId,
                request.IsBotOwner, request.WeaponLevel,
                new Vector2(request.Origin.x, request.Origin.y),
                request.DamageMultiplier, cfg);

            int written;
            if (!cfg.Is3D)
            {
                var (ptr, rem) = Authority.Get2DWriteHead();
                written = BatchSpawnHelper.SpawnBatch2D(
                    spawnPts, request.ProjectileCount, null, rustParams,
                    request.ConfigId, 0, baseId, ptr, rem, latencyComp);
                Authority.NotifyBatchSpawned2D(written, baseId, dataTemplate);
            }
            else
            {
                var (ptr, rem) = Authority.Get3DWriteHead();
                written = BatchSpawnHelper.SpawnBatch3D(
                    spawnPts, request.ProjectileCount, rustParams,
                    request.ConfigId, 0, baseId, ptr, rem, latencyComp);
                Authority.NotifyBatchSpawned3D(written, baseId, dataTemplate);
            }

            if (written <= 0) return;

            float serverNetworkTime = NetworkManager.Singleton != null
                ? (float)NetworkManager.Singleton.ServerTime.TimeAsFloat
                : 0f;

            var confirmation = new SpawnConfirmation
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
                ExtraDirections     = request.ExtraDirections,
                ServerNetworkTime   = serverNetworkTime
            };

            ulong senderClientId = rpcParams.Receive.SenderClientId;

            // FIX: Route differently to sender vs all other clients.
            //
            // Sender (firing client): already has the projectile in their Rust buffer
            // under temp IDs. Send LinkProjectileIdsClientRpc to swap temp→real IDs.
            // No new projectiles spawned on their end — they already have theirs.
            //
            // All other clients: have no existing entries. Send SpawnConfirmedClientRpc
            // so they spawn fresh Rust buffer entries with the real server IDs.
            //
            // Host (IsServer && IsClient): fires via MasterProjectileSystem which
            // skips SpawnFiringClientBatch (IsServer guard) and renders from
            // ServerProjectileAuthority. Both RPCs return early via if(IsServer).
            // Including host in TargetClientIds for LinkProjectileIds is harmless
            // but we exclude it for clarity.

            // Send ID link to the sender only
            LinkProjectileIdsClientRpc(baseId, written, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { senderClientId }
                }
            });

            // Send spawn to all other connected clients
            var otherClients = new List<ulong>(
                NetworkManager.Singleton.ConnectedClientsIds.Count);
            foreach (ulong id in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (id != senderClientId) otherClients.Add(id);
            }

            if (otherClients.Count > 0)
            {
                SpawnConfirmedClientRpc(confirmation, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = otherClients
                    }
                });
            }
        }

        // ── Client → Server: Raycast ──────────────────────────────────────────

        [ServerRpc(RequireOwnership = false)]
        public void RaycastFireServerRpc(
            ProjectileFireRequest request,
            Vector3 clientHitPoint, bool clientDidHit, bool clientIsHeadshot,
            ulong clientHitTargetId, bool clientIs3D,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsSpawned || _isShuttingDown || RaycastHandler == null) return;

            RaycastHandler.ServerHandleFire(new RaycastFireResult
            {
                Origin             = request.Origin,
                Direction          = request.Direction,
                HitPoint           = clientHitPoint,
                DidHit             = clientDidHit,
                HitTargetNetworkId = clientHitTargetId,
                IsHeadshot         = clientIsHeadshot,
                Is3D               = clientIs3D
            }, new WeaponFireContext
            {
                IsRaycastWeapon        = true,
                IsNetworked            = true,
                OwnerMidId             = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner             = request.IsBotOwner,
                WeaponLevel            = request.WeaponLevel,
                DamageMultiplier       = request.DamageMultiplier
            }, request.ConfigId, rpcParams.Receive.SenderClientId);
        }

        // ── Client → Server: Physics ──────────────────────────────────────────

        [ServerRpc(RequireOwnership = false)]
        public void FirePhysicsProjectileServerRpc(
            Vector3 origin, Quaternion rotation,
            PoolableNetworkObjectType poolType,
            float speed, float damageMultiplier,
            ulong ownerMidId, ulong firedByNetObjId,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsSpawned || _isShuttingDown) return;
            if (!MID_MasterProjectileSystem.HasInstance) return;

            var netObj = MID_MasterProjectileSystem.Instance
                .SpawnPhysicsProjectile(poolType, origin, rotation);

            if (netObj == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FirePhysicsProjectileServerRpc: pool null for {poolType}.",
                    nameof(MID_ProjectileNetworkBridge));
                return;
            }

            var proj = netObj.GetComponent<PhysicsProjectileBase>();
            if (proj != null)
            {
                proj.SetOwnerContext(ownerMidId, firedByNetObjId, false, 1, damageMultiplier);
                proj.InitialiseProjectile(ownerMidId, firedByNetObjId, speed, false, 1);
            }
        }

        // ── Server → Clients ──────────────────────────────────────────────────

        /// <summary>
        /// FIX: Sent to OTHER clients only (not the firing client).
        /// Spawns fresh Rust buffer entries for projectiles fired by remote players.
        /// Position is advanced by elapsed server time to account for RPC travel time.
        /// </summary>
        [ClientRpc]
        public void SpawnConfirmedClientRpc(
            SpawnConfirmation confirmation,
            ClientRpcParams rpcParams = default)
        {
            // Host renders from ServerProjectileAuthority — skip entirely
            if (IsServer || !IsSpawned || _isShuttingDown) return;

            var localMgr = LocalProjectileManager.HasInstance
                ? LocalProjectileManager.Instance : null;
            if (localMgr == null) return;

            var cfg = ProjectileRegistry.Instance?.Get(confirmation.ConfigId);
            if (cfg == null) return;

            float elapsed = GetElapsedSinceServerSpawn(confirmation.ServerNetworkTime);

            MID_Logger.LogDebug(_logLevel,
                $"SpawnConfirmedClientRpc (other client): baseId={confirmation.BaseProjId} " +
                $"count={confirmation.ProjectileCount} elapsed={elapsed:F3}",
                nameof(MID_ProjectileNetworkBridge));

            if (!cfg.Is3D)
                localMgr.SpawnNetworkBatch2D(confirmation, elapsed);
            else
                localMgr.SpawnNetworkBatch3D(confirmation, elapsed);
        }

        /// <summary>
        /// FIX: Sent ONLY to the firing client.
        /// Swaps the temp IDs they spawned locally with the real server-assigned IDs.
        /// No new entries are spawned — they already have their projectiles running.
        /// </summary>
        [ClientRpc]
        private void LinkProjectileIdsClientRpc(
            uint realBaseId, int count,
            ClientRpcParams rpcParams = default)
        {
            // Host renders from ServerProjectileAuthority — skip
            if (IsServer || !IsSpawned || _isShuttingDown) return;

            MID_Logger.LogDebug(_logLevel,
                $"LinkProjectileIdsClientRpc: realBase={realBaseId} count={count}",
                nameof(MID_ProjectileNetworkBridge));

            LocalProjectileManager.Instance?.LinkNetworkProjectileBatch(realBaseId, count);
        }

        /// <summary>
        /// Forces projectile dead in client's Rust sim buffer on confirmed hit.
        /// Also notifies ClientPredictionManager for physics pool visuals.
        /// </summary>
        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            if (!IsSpawned || _isShuttingDown) return;

            if (IsClient)
                LocalProjectileManager.Instance?.KillNetworkProjectile(confirmation.ProjId);

            if (IsClient) Prediction?.OnHitConfirmed(confirmation);

            ImpactHandler?.PlayImpact(
                confirmation.HitPosition, confirmation.ConfigId, confirmation.IsHeadshot);

            OnHitConfirmedLocal?.Invoke(confirmation);
        }

        /// <summary>
        /// FIX: Passes currentServerTick and tickInterval to ReconcileSnapshots so
        /// the stale snapshot position can be extrapolated forward before comparing.
        /// See LocalProjectileManager.ReconcileSnapshots2D/3D for the math.
        /// </summary>
        [ClientRpc]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            // Skip dedicated server AND host — both render from ServerProjectileAuthority
            if (IsServer || !IsSpawned || _isShuttingDown) return;

            var localMgr = LocalProjectileManager.HasInstance
                ? LocalProjectileManager.Instance : null;
            if (localMgr == null) return;

            int   currentTick  = NetworkManager.Singleton?.ServerTime.Tick ?? 0;
            float tickInterval = NetworkManager.Singleton != null
                ? 1f / NetworkManager.Singleton.NetworkTickSystem.TickRate
                : Time.fixedDeltaTime;

            if (count2D > 0) localMgr.ReconcileSnapshots2D(
                snapshots2D, count2D, currentTick, tickInterval);
            if (count3D > 0) localMgr.ReconcileSnapshots3D(
                snapshots3D, count3D, currentTick, tickInterval);
        }

        // ── Utility ───────────────────────────────────────────────────────────

        public int GetServerTick()
            => NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Tick : 0;

        private float GetElapsedSinceServerSpawn(float serverNetworkTime)
        {
            if (serverNetworkTime <= 0f || NetworkManager.Singleton == null) return 0f;
            float now     = (float)NetworkManager.Singleton.ServerTime.TimeAsFloat;
            float elapsed = now - serverNetworkTime;
            return Mathf.Clamp(elapsed, 0f, 0.5f);
        }

        private float ComputeLatencyComp(ServerRpcParams rpc, int clientTick)
        {
            if (NetworkManager.Singleton == null) return 0f;
            int   deltaTicks   = GetServerTick() - clientTick;
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
                        ? req.ExtraDirections[i - 1].normalized
                        : req.Direction.normalized);
                pts[i] = new SpawnPoint
                {
                    Origin    = req.Origin,
                    Direction = dir,
                    Speed     = req.Speed
                };
            }
            return pts;
        }
    }
}
