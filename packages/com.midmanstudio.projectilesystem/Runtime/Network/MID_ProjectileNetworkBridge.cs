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
        public byte    ProjectileCount;
        public ushort  PatternId;    // 0 = no pattern (simple spread / single direction)
        public float   SpreadDeg;    // only meaningful when PatternId == 0 and ProjectileCount > 1
        public bool    PatternIs3D;  // see PatternIs3D note below
        public ulong   OwnerMidId;
        public ulong   FiredByNetworkObjectId;
        public bool    IsBotOwner;
        public byte    WeaponLevel;
        public float   DamageMultiplier;
        public int     ClientFireTick;

        // NOTE: no direction/speed arrays here, and no RngSeed either — the old
        // RngSeed field was populated with a fresh UnityEngine.Random.Range() every
        // shot but never actually read back out server-side (grep confirms zero
        // reads anywhere in Runtime/); it was dead weight. Direction and per-pellet
        // speed are regenerated on every recipient via
        // ProjectileDirectionResolver.Resolve(), which for pattern fire reads the
        // pattern asset's own fixed RngSeed property — the exact same value the
        // firing client's local predicted visual already uses.
        //
        // PatternIs3D: the client's own Use3DConvention() || cfg.Is3D result.
        // These two CAN diverge (a 2D-configured weapon fired while the player's
        // current mode/dimension is 3D, or vice versa) — cfg.Is3D alone, resolved
        // fresh server-side from ConfigId, is not guaranteed to match what the
        // firing client actually used for its own pattern/spread rotation basis.
        // Sending the resolved bit directly removes that ambiguity entirely rather
        // than trying to reconstruct player-local UI state server-side.
        //
        // WIRE COMPRESSION: every field here is still the plain type it always
        // was (Vector3 Origin, Vector3 Direction, float SpreadDeg, bool
        // IsBotOwner/PatternIs3D) — nothing outside this method changed at all.
        // Only how they're PACKED changed: Direction goes through octahedral
        // encoding (12 -> 4 bytes), Origin through half-precision (12 -> 6),
        // SpreadDeg through byte quantization (4 -> 1), and the two bools share
        // one packed byte instead of one each. See WireCompression.cs for the
        // encode/decode math and precision reasoning on each. 64 -> 46 bytes.
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ConfigId);

            if (s.IsWriter)
            {
                WireCompression.EncodePosition(Origin, out ushort ox, out ushort oy, out ushort oz);
                s.SerializeValue(ref ox);
                s.SerializeValue(ref oy);
                s.SerializeValue(ref oz);

                WireCompression.EncodeDirection(Direction, out short dx, out short dy);
                s.SerializeValue(ref dx);
                s.SerializeValue(ref dy);
            }
            else
            {
                ushort ox = 0, oy = 0, oz = 0;
                s.SerializeValue(ref ox);
                s.SerializeValue(ref oy);
                s.SerializeValue(ref oz);
                Origin = WireCompression.DecodePosition(ox, oy, oz);

                short dx = 0, dy = 0;
                s.SerializeValue(ref dx);
                s.SerializeValue(ref dy);
                Direction = WireCompression.DecodeDirection(dx, dy);
            }

            s.SerializeValue(ref Speed);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref PatternId);

            if (s.IsWriter)
            {
                byte spreadPacked = WireCompression.EncodeDegrees0to360(SpreadDeg);
                s.SerializeValue(ref spreadPacked);

                byte flags = 0;
                if (PatternIs3D) flags |= 0b01;
                if (IsBotOwner)  flags |= 0b10;
                s.SerializeValue(ref flags);
            }
            else
            {
                byte spreadPacked = 0;
                s.SerializeValue(ref spreadPacked);
                SpreadDeg = WireCompression.DecodeDegrees0to360(spreadPacked);

                byte flags = 0;
                s.SerializeValue(ref flags);
                PatternIs3D = (flags & 0b01) != 0;
                IsBotOwner  = (flags & 0b10) != 0;
            }

            s.SerializeValue(ref OwnerMidId);
            s.SerializeValue(ref FiredByNetworkObjectId);
            s.SerializeValue(ref WeaponLevel);
            s.SerializeValue(ref DamageMultiplier);
            s.SerializeValue(ref ClientFireTick);
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
        public ushort  PatternId;    // mirrors ProjectileFireRequest.PatternId
        public float   SpreadDeg;    // mirrors ProjectileFireRequest.SpreadDeg
        public bool    PatternIs3D;  // mirrors ProjectileFireRequest.PatternIs3D

        /// <summary>
        /// Server-authoritative NetworkTime.TimeAsFloat at spawn.
        /// Used by other clients to compute elapsed flight time for position catch-up.
        /// Zero = not set (offline / old server).
        /// </summary>
        public float ServerNetworkTime;

        // WIRE COMPRESSION: same treatment as ProjectileFireRequest — see that
        // struct's comment for the full reasoning. Public fields unchanged.
        // 58 -> 41 bytes. This one matters more than the fire request's own
        // shrink, since it's the one that gets sent to every OTHER connected
        // client, not just once to the server.
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref BaseProjId);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref ConfigId);
            s.SerializeValue(ref ServerSpawnTick);

            if (s.IsWriter)
            {
                WireCompression.EncodePosition(Origin, out ushort ox, out ushort oy, out ushort oz);
                s.SerializeValue(ref ox);
                s.SerializeValue(ref oy);
                s.SerializeValue(ref oz);

                WireCompression.EncodeDirection(Direction, out short dx, out short dy);
                s.SerializeValue(ref dx);
                s.SerializeValue(ref dy);
            }
            else
            {
                ushort ox = 0, oy = 0, oz = 0;
                s.SerializeValue(ref ox);
                s.SerializeValue(ref oy);
                s.SerializeValue(ref oz);
                Origin = WireCompression.DecodePosition(ox, oy, oz);

                short dx = 0, dy = 0;
                s.SerializeValue(ref dx);
                s.SerializeValue(ref dy);
                Direction = WireCompression.DecodeDirection(dx, dy);
            }

            s.SerializeValue(ref Speed);
            s.SerializeValue(ref OwnerMidId);
            s.SerializeValue(ref PatternId);

            if (s.IsWriter)
            {
                byte spreadPacked = WireCompression.EncodeDegrees0to360(SpreadDeg);
                s.SerializeValue(ref spreadPacked);
            }
            else
            {
                byte spreadPacked = 0;
                s.SerializeValue(ref spreadPacked);
                SpreadDeg = WireCompression.DecodeDegrees0to360(spreadPacked);
            }

            s.SerializeValue(ref PatternIs3D);
            s.SerializeValue(ref ServerNetworkTime);
        }

        // GetDirection(i) is gone — direction expansion now always goes through
        // ProjectileDirectionResolver.Resolve(PatternId, ..., ProjectileCount,
        // SpreadDeg, ...) so every recipient regenerates the full pellet set the
        // same way instead of indexing into a transmitted array. See
        // LocalProjectileManager.BuildSpawnPointsFromConfirmation.
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

        // Independent of any pattern's own count — just a sanity ceiling so a
        // corrupt/hostile ProjectileCount can't be used to over-allocate proj IDs
        // or spam the batch buffer. Comfortably above the pattern SO's own
        // [Range(1,64)] ProjectileCount ceiling.
        private const int MaxProjectileCountSanity = 128;

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

            // Server-authoritative direction/speed regeneration. For pattern fire
            // (PatternId != 0) this reads only the pattern asset's own baked data —
            // nothing the client sent can influence the actual pellet directions or
            // speeds beyond which registered pattern/spread it asked for. Also drop
            // any request whose declared ProjectileCount can't possibly be honest
            // (a config-max sanity check independent of pattern mechanics).
            if (request.ProjectileCount == 0 || request.ProjectileCount > MaxProjectileCountSanity)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FireServerRpc: rejected ProjectileCount {request.ProjectileCount} for configId {request.ConfigId}",
                    nameof(MID_ProjectileNetworkBridge));
                return;
            }

            var spawnPts = ProjectileDirectionResolver.Resolve(
                request.PatternId, request.Origin, request.Direction,
                request.ProjectileCount, request.SpreadDeg, clampedSpeed, request.PatternIs3D);

            var rustParams   = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);
            uint baseId      = Authority.AllocateProjIds(spawnPts.Length);
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
                    spawnPts, spawnPts.Length, null, rustParams,
                    request.ConfigId, 0, baseId, ptr, rem, latencyComp);
                Authority.NotifyBatchSpawned2D(written, baseId, dataTemplate);
            }
            else
            {
                var (ptr, rem) = Authority.Get3DWriteHead();
                written = BatchSpawnHelper.SpawnBatch3D(
                    spawnPts, spawnPts.Length, rustParams,
                    request.ConfigId, 0, baseId, ptr, rem, latencyComp);
                Authority.NotifyBatchSpawned3D(written, baseId, dataTemplate);
            }

            if (written <= 0) return;

            float serverNetworkTime = NetworkManager.Singleton != null
                ? (float)NetworkManager.Singleton.ServerTime.TimeAsFloat
                : 0f;

            var confirmation = new SpawnConfirmation
            {
                BaseProjId        = baseId,
                ProjectileCount   = (byte)written,
                ConfigId          = request.ConfigId,
                ServerSpawnTick   = GetServerTick(),
                Origin            = request.Origin,
                Direction         = request.Direction,
                Speed             = clampedSpeed,
                OwnerMidId        = request.OwnerMidId,
                PatternId         = request.PatternId,
                SpreadDeg         = request.SpreadDeg,
                PatternIs3D       = request.PatternIs3D,
                ServerNetworkTime = serverNetworkTime
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
        ///
        /// Delivery = Unreliable on purpose: this is a one-shot cosmetic spawn
        /// event for a client that isn't the shooter and isn't the authority for
        /// this projectile either. If the packet is dropped, that one client
        /// simply never renders that one shot's visual — no state elsewhere
        /// depends on it, nothing desyncs, and it isn't superseded/retried by
        /// anything later (unlike the periodic snapshot), so treat this as an
        /// occasional acceptable miss rather than paying for guaranteed delivery
        /// on every single shot in every large lobby.
        /// </summary>
        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
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
        ///
        /// Delivery = Unreliable on purpose: textbook case for it. Each snapshot
        /// supersedes the previous one a few ticks later regardless — that's
        /// exactly what the staleTime extrapolation in ReconcileSnapshots2D/3D
        /// already exists to smooth over. A dropped snapshot just means the next
        /// one (already coming shortly) catches up; paying reliable-delivery
        /// retransmission cost on a value that's about to be replaced anyway is
        /// pure waste, and at high player/projectile counts this is the highest-
        /// frequency RPC in the whole system.
        /// </summary>
        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D,
            ClientRpcParams rpcParams = default)
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

        // BuildServerSpawnPoints(ProjectileFireRequest) is gone — it used to trust
        // req.ExtraDirections wholesale. Replaced by
        // ProjectileDirectionResolver.Resolve(...) in FireServerRpc, which
        // regenerates directions from the registered pattern (or the spread
        // scalars) instead of anything the client claims about individual pellets.
    }
}
