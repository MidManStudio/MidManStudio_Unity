// packages/com.midmanstudio.projectilesystem/Runtime/Network/MID_ProjectileNetworkBridge.cs
//
// ARCHITECTURE CHANGE: SpawnConfirmedClientRpc now routes to LocalProjectileManager
//   instead of ClientPredictionManager for the Rust sim visual path.
//
//   Firing client  → LinkNetworkProjectileBatch (temp ID → real ID in Rust buffer)
//   Other clients  → SpawnNetworkBatch2D/3D (new entries in Rust buffer, position
//                    advanced by elapsed server time to match current expected position)
//
//   HitConfirmedClientRpc now calls KillNetworkProjectile on LocalProjectileManager
//   to force-clear the visual from the client's Rust sim buffer immediately.
//   ClientPredictionManager.OnHitConfirmed is still called for physics pool visuals.
//
//   SendSnapshotClientRpc now calls LocalProjectileManager.ReconcileSnapshots2D/3D
//   which corrects position directly in the Rust buffer. Rust sim continues smoothly
//   from the corrected position on the next tick — no C# lerp overhead.
//
//   ADDED: _localPlayerMidId + SetLocalPlayerMidId() so SpawnConfirmedClientRpc
//   can distinguish the firing client from all other clients without relying on
//   OwnerClientId (which is the NGO connection ID, not the game MID ID).

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
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
        /// Server-authoritative NetworkTime.TimeAsFloat captured immediately after
        /// BatchSpawnHelper completes. Used by clients to estimate elapsed flight time
        /// and advance projectile position before inserting into the local Rust buffer.
        /// Zero means the field was not set (old server or offline mode).
        /// </summary>
        public float ServerNetworkTime;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref BaseProjId); s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref ConfigId); s.SerializeValue(ref ServerSpawnTick);
            s.SerializeValue(ref Origin); s.SerializeValue(ref Direction); s.SerializeValue(ref Speed);
            s.SerializeValue(ref OwnerMidId); s.SerializeValue(ref ExtraDirectionCount);
            if (s.IsReader) ExtraDirections = ExtraDirectionCount > 0 ? new Vector3[ExtraDirectionCount] : null;
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

        // Game MID ID of the local player.
        // Used by SpawnConfirmedClientRpc to route: firing client gets LinkNetworkProjectileBatch,
        // all others get SpawnNetworkBatch (full spawn into their Rust buffer).
        private ulong _localPlayerMidId;

        private bool _isShuttingDown;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Set by MID_MasterProjectileSystem.SetLocalPlayerMidId().
        /// Must be set before the first Fire() call for routing to work correctly.
        /// </summary>
        public void SetLocalPlayerMidId(ulong midId) => _localPlayerMidId = midId;

        // ── Transport Configuration ───────────────────────────────────────────

        /// <summary>
        /// Configures UnityTransport for high-throughput projectile traffic.
        /// MUST be called before NetworkManager.StartHost() / StartServer() / StartClient().
        /// MID_MasterProjectileSystem.Initialise() calls this automatically.
        /// Also call from your lobby / connection code before starting the session.
        /// </summary>
        public static void ConfigureTransportForHighThroughput()
        {
            if (NetworkManager.Singleton == null) return;

            var transport = NetworkManager.Singleton.NetworkConfig?.NetworkTransport
                as UnityTransport;

            if (transport == null)
            {
                Debug.LogWarning(
                    "[MID_ProjectileNetworkBridge] ConfigureTransportForHighThroughput: " +
                    "NetworkTransport is not UnityTransport — skipping.");
                return;
            }

            transport.MaxSendQueueSize = 16 * 1024 * 1024; // 16 MB
        }

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

        private void OnDestroy() => _isShuttingDown = true;

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
        public void FireServerRpc(ProjectileFireRequest request, ServerRpcParams rpcParams = default)
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

            var context = new WeaponFireContext
            {
                ProjectileCount        = request.ProjectileCount,
                IsNetworked            = true,
                IsRaycastWeapon        = false,
                LatencyCompensation    = latencyComp,
                OwnerMidId             = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner             = request.IsBotOwner,
                WeaponLevel            = request.WeaponLevel,
                DamageMultiplier       = request.DamageMultiplier
            };

            var spawnPts     = BuildServerSpawnPoints(request);
            var rustParams   = ProjectileRegistry.Instance.GetRustSpawnParams(request.ConfigId, clampedSpeed);
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
                ExtraDirections     = request.ExtraDirections,
                ServerNetworkTime   = serverNetworkTime
            });
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
                    $"FirePhysicsProjectileServerRpc: pool returned null for {poolType}.",
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
        /// Routed by OwnerMidId:
        ///   Firing client → LinkNetworkProjectileBatch (temp ID → real ID swap in Rust buffer)
        ///   Other clients → SpawnNetworkBatch2D/3D (new Rust buffer entry, position pre-advanced)
        ///
        /// Host is excluded entirely (IsServer guard) — it renders from ServerProjectileAuthority.
        /// </summary>
        [ClientRpc]
        public void SpawnConfirmedClientRpc(SpawnConfirmation confirmation)
        {
            if (IsServer || !IsSpawned || _isShuttingDown) return;

            var localMgr = LocalProjectileManager.HasInstance
                ? LocalProjectileManager.Instance : null;
            if (localMgr == null) return;

            var cfg = ProjectileRegistry.Instance?.Get(confirmation.ConfigId);
            if (cfg == null) return;

            bool isFiringClient = _localPlayerMidId != 0
                               && confirmation.OwnerMidId == _localPlayerMidId;

            MID_Logger.LogDebug(_logLevel,
                $"SpawnConfirmedClientRpc: baseId={confirmation.BaseProjId} " +
                $"count={confirmation.ProjectileCount} " +
                $"firing={isFiringClient} serverNetTime={confirmation.ServerNetworkTime:F3}",
                nameof(MID_ProjectileNetworkBridge));

            if (isFiringClient)
            {
                // Swap temp IDs written by SpawnFiringClientBatch* to real server IDs.
                // Rust sim has been running locally since fire — position is already correct.
                localMgr.LinkNetworkProjectileBatch(
                    confirmation.BaseProjId, confirmation.ProjectileCount);
            }
            else
            {
                // Compute how long the projectile has been flying since server spawned it.
                // This is used to advance the starting position so it doesn't pop in at the barrel.
                float elapsed = GetElapsedSinceServerSpawn(confirmation.ServerNetworkTime);

                if (!cfg.Is3D)
                    localMgr.SpawnNetworkBatch2D(confirmation, elapsed);
                else
                    localMgr.SpawnNetworkBatch3D(confirmation, elapsed);
            }
        }

        /// <summary>
        /// Forces the projectile dead in the client's Rust sim buffer (all clients).
        /// For physics/raycast pool visuals, also notifies ClientPredictionManager.
        /// Plays the impact effect regardless.
        /// </summary>
        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            if (!IsSpawned || _isShuttingDown) return;

            // Kill the visual entry in the client's Rust sim buffer immediately.
            // If it already expired naturally this is a no-op.
            if (IsClient)
                LocalProjectileManager.Instance?.KillNetworkProjectile(confirmation.ProjId);

            // Physics/raycast pool visuals tracked by ClientPredictionManager
            if (IsClient) Prediction?.OnHitConfirmed(confirmation);

            // Impact effect (plays on all clients including host)
            ImpactHandler?.PlayImpact(
                confirmation.HitPosition, confirmation.ConfigId, confirmation.IsHeadshot);

            OnHitConfirmedLocal?.Invoke(confirmation);
        }

        /// <summary>
        /// Corrects projectile positions directly in the client's Rust buffer.
        /// If the error is below threshold the correction is skipped and Rust continues
        /// undisturbed. Wave/Circular types are never sent snapshots (filtered server-side).
        /// Host is excluded — it reads authoritative buffer from ServerProjectileAuthority.
        /// </summary>
        [ClientRpc]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            if ((IsServer && !IsClient) || !IsSpawned || _isShuttingDown) return;

            var localMgr = LocalProjectileManager.HasInstance
                ? LocalProjectileManager.Instance : null;
            if (localMgr == null) return;

            if (count2D > 0) localMgr.ReconcileSnapshots2D(snapshots2D, count2D);
            if (count3D > 0) localMgr.ReconcileSnapshots3D(snapshots3D, count3D);
        }

        // ── Utility ───────────────────────────────────────────────────────────

        public int GetServerTick()
            => NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Tick : 0;

        /// <summary>
        /// How long ago (in server time) did the server spawn this projectile?
        /// Used to advance other clients' starting position on spawn.
        /// Clamped to 0.5s to prevent runaway advancement on extreme latency.
        /// </summary>
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
                        ? req.ExtraDirections[i - 1].normalized : req.Direction.normalized);
                pts[i] = new SpawnPoint { Origin = req.Origin, Direction = dir, Speed = req.Speed };
            }
            return pts;
        }
    }
}
