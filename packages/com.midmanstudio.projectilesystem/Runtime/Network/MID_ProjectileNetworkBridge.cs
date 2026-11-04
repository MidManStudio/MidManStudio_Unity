// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "MID_ProjectileNetworkBridge.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;
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

        /// <summary>
        /// 0 = no guided target (the default). A non-zero value is resolved
        /// server-side in FireServerRpc back to a live NetworkObject's Transform
        /// and handed to ProjectileGuidanceTracker for every proj_id this request
        /// spawns: the same target for the whole batch, including every pellet of
        /// a pattern or spread shot. Only used when the config's MovementType is
        /// Guided.
        /// </summary>
        public ulong   TargetNetworkObjectId;

        /// <summary>
        /// Per-shot seed from ProjectileDirectionResolver.NewFireSeed(). Every
        /// recipient feeds it to Resolve(), so random pattern shapes, speed
        /// variance and burst jitter differ from shot to shot and still match on
        /// all peers. 0 = no seed, the pattern asset's own fixed seed is used.
        /// </summary>
        public ushort  FireSeed;

        // NOTE: no direction/speed arrays here. Direction, per-pellet speed and
        // start delay are regenerated on every recipient via
        // ProjectileDirectionResolver.Resolve(), from the pattern asset and the
        // 16-bit FireSeed above.
        //
        // PatternIs3D: the client's own Use3DConvention() || cfg.Is3D result.
        // These two can differ (a 2D-configured weapon fired while the player's
        // current mode is 3D, or the other way round), so cfg.Is3D resolved on the
        // server from ConfigId is not guaranteed to match the rotation basis the
        // firing client used for its pattern or spread. Sending the resolved bit
        // removes that ambiguity.
        //
        // WIRE FORMAT: the public fields are plain types (Vector3 Origin, Vector3
        // Direction, float SpreadDeg, bool IsBotOwner and PatternIs3D) and only
        // the packing differs: Direction is octahedral (12 -> 4 bytes), Origin is
        // half precision (12 -> 6), SpreadDeg is byte quantized (4 -> 1), and the
        // two bools share one byte. See WireCompression.cs for the precision of
        // each. The serialized request is 56 bytes. What a receiver reads back is
        // the quantized value, so the server and other clients can differ slightly
        // from the exact values the firing client used locally.
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
            // Plain ulong, not compressed: a NetworkObjectId is not a spatial or
            // angular value, so WireCompression's lossy encodings do not apply.
            s.SerializeValue(ref TargetNetworkObjectId);
            s.SerializeValue(ref FireSeed);
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
        public ushort  FireSeed;     // mirrors ProjectileFireRequest.FireSeed

        /// <summary>
        /// Server-authoritative NetworkTime.TimeAsFloat at spawn.
        /// Used by other clients to compute elapsed flight time for position catch-up.
        /// Zero = not set (offline / old server).
        /// </summary>
        public float ServerNetworkTime;

        // WIRE FORMAT: Origin, Direction and SpreadDeg are packed the same way as
        // in ProjectileFireRequest (see the comment there); PatternIs3D keeps its
        // own byte here. The serialized confirmation is 43 bytes. It is the struct
        // that goes to every other connected client, so its size matters more than
        // the fire request's.
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
            s.SerializeValue(ref FireSeed);
        }

        // No per-pellet directions are stored. Every recipient expands the shot
        // with ProjectileDirectionResolver.Resolve(PatternId, ..., ProjectileCount,
        // SpreadDeg, ..., FireSeed), so all peers regenerate the same pellet set.
        // See LocalProjectileManager.BuildSpawnPointsFromConfirmation.
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

        // Independent of any pattern's own count: a sanity ceiling so a corrupt or
        // hostile ProjectileCount cannot be used to over-allocate proj ids or spam
        // the batch buffer. Above the pattern asset's own [Range(1,64)]
        // ProjectileCount ceiling.
        private const int MaxProjectileCountSanity = 128;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        // Set by SetLocalPlayerMidId. Nothing in this class reads it: routing uses
        // the sender's client id and TargetClientIds instead.
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

            // MaxPacketQueueSize is a different setting from MaxSendQueueSize
            // above. MaxSendQueueSize is the byte-size send buffer;
            // MaxPacketQueueSize is the number of discrete packets the transport
            // holds per internal send and receive queue, which bounds how many
            // packets can be processed in one frame. Per-client culled snapshot
            // dispatch (SendSnapshotsCulled) sends N separate targeted RPCs per
            // snapshot tick, each chunked on its own, so many packets can arrive
            // in one frame even when the total byte count is small. Too small a
            // queue logs "Receive queue is full, some packets could be dropped".
            // 2048 leaves headroom for tens to hundreds of players.
            transport.MaxPacketQueueSize = 2048;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Stores the local player's id. Nothing in this class reads the value
        /// (routing uses TargetClientIds), so calling it is optional.
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
                request.ProjectileCount, request.SpreadDeg, clampedSpeed, request.PatternIs3D,
                request.FireSeed);

            var rustParams   = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);
            uint baseId      = Authority.AllocateProjIds(spawnPts.Length);
            var dataTemplate = new ServerProjectileData(
                request.OwnerMidId, request.FiredByNetworkObjectId,
                request.IsBotOwner, request.WeaponLevel,
                new Vector2(request.Origin.x, request.Origin.y),
                request.DamageMultiplier, cfg);

            // A pattern with burst timing releases its projectiles one by one. The
            // whole id block is still reserved up front, so projectile i keeps id
            // baseId + i on every peer whether it spawns now or later.
            bool staggered = DelayedSpawnQueue.AnyDelayed(spawnPts, spawnPts.Length);

            int written;
            if (staggered)
            {
                written = Authority.SpawnStaggered(
                    spawnPts, spawnPts.Length, baseId, request.ConfigId, clampedSpeed,
                    cfg, dataTemplate, latencyComp, ResolveGuidedTarget(request, cfg));
            }
            else if (!cfg.Is3D)
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

            // Guided shots: baseId and written are the proj id range this request
            // just spawned (the same range the confirmation below carries). Each
            // id is registered with ProjectileGuidanceTracker, whose own Update()
            // loop steers it from then on. This runs on the server only, because
            // the tracker steers the authoritative copy in the server's Rust
            // buffer. A staggered shot is not registered here: it passes the
            // resolved target to Authority.SpawnStaggered instead.
            if (!staggered && request.TargetNetworkObjectId != 0 && cfg.MovementType == ProjectileMovementType.Guided
                && NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null
                && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                    request.TargetNetworkObjectId, out var targetNetObj)
                && targetNetObj != null)
            {
                var tracker = ProjectileGuidanceTracker.Instance;
                var targetTransform = targetNetObj.transform;
                if (cfg.Is3D)
                    for (uint i = 0; i < written; i++) tracker.RegisterGuidedTarget3D(baseId + i, targetTransform);
                else
                    for (uint i = 0; i < written; i++) tracker.RegisterGuidedTarget2D(baseId + i, targetTransform);
            }

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
                ServerNetworkTime = serverNetworkTime,
                FireSeed          = request.FireSeed
            };

            ulong senderClientId = rpcParams.Receive.SenderClientId;


            // The sender and every other client are routed differently.
            //
            // Sender (firing client): already has the projectile in its own Rust
            // buffer under temporary ids, so it gets LinkProjectileIdsClientRpc to
            // swap temporary ids for the real ones. No new projectile is spawned
            // on its side.
            //
            // All other clients: have no entry yet, so they get
            // SpawnConfirmedClientRpc and spawn fresh Rust buffer entries under
            // the real server ids.
            //
            // Host (IsServer and IsClient): fires through MID_MasterProjectileSystem,
            // which skips SpawnFiringClientBatch on the server and renders from
            // ServerProjectileAuthority. Both client RPCs return early when
            // IsServer, so including the host in TargetClientIds would be harmless;
            // when the host is the sender it is the only target of the link RPC.

            // Send ID link to the sender only
            LinkProjectileIdsClientRpc(baseId, written, new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { senderClientId }
                }
            });

            // Send the spawn to all other connected clients, distance-filtered when
            // culling is on. This is where culling takes effect: once a client
            // spawns a projectile from this RPC, its own local sim keeps it moving
            // and rendering without further snapshots. Culling only the periodic
            // snapshot RPC would leave every shot reaching every client at fire
            // time regardless of distance. A client outside the range never spawns
            // the shot at all, even if it later moves into range.
            var otherClients = new List<ulong>(
                NetworkManager.Singleton.ConnectedClientsIds.Count);

            bool culling = Authority != null && Authority.EnableDistanceCulling
                                               && Authority.ObserverProvider != null;
            float rangeSq = culling ? Authority.CullVisRange * Authority.CullVisRange : 0f;

            foreach (ulong id in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (id == senderClientId) continue;

                if (!culling)
                {
                    otherClients.Add(id);
                    continue;
                }

                // No resolvable position for this client — same safe fallback as
                // SendSnapshotsCulled: never silently withhold data just because
                // we don't know where they are yet.
                if (!Authority.ObserverProvider.TryGetObserverPosition(id, out Vector3 observerPos))
                {
                    otherClients.Add(id);
                    continue;
                }

                float dx = request.Origin.x - observerPos.x;
                float dy = request.Origin.y - observerPos.y;
                float distSq = dx * dx + dy * dy;
                if (cfg.Is3D)
                {
                    float dz = request.Origin.z - observerPos.z;
                    distSq += dz * dz;
                }
                if (distSq <= rangeSq)
                    otherClients.Add(id);
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

        private static Transform ResolveGuidedTarget(in ProjectileFireRequest request, ProjectileConfigSO cfg)
        {
            if (request.TargetNetworkObjectId == 0 || cfg.MovementType != ProjectileMovementType.Guided)
                return null;

            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;

            return nm.SpawnManager.SpawnedObjects.TryGetValue(request.TargetNetworkObjectId, out var obj)
                   && obj != null
                ? obj.transform
                : null;
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
            }, request.ConfigId, rpcParams.Receive.SenderClientId, request.ClientFireTick);
        }

        /// <summary>
        /// Raycast fire for a pattern or a spread. Resolves request.PatternId (or
        /// ProjectileCount and SpreadDeg for the no-pattern spread case) into N
        /// directions through the same ProjectileDirectionResolver every other
        /// fire path uses, and the server casts all N rays itself. Unlike
        /// RaycastFireServerRpc, no client hit claim is sent, so nothing needs
        /// validating: the result is fully server-authoritative. request.Direction
        /// is the raw, un-rotated aim direction, the same convention as the
        /// Rust sim path.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void RaycastPatternFireServerRpc(
            ProjectileFireRequest request,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsSpawned || _isShuttingDown || RaycastHandler == null) return;

            RaycastHandler.ServerHandleFirePattern(
                request.PatternId, request.Origin, request.Direction,
                request.ProjectileCount, request.SpreadDeg, request.PatternIs3D,
                new WeaponFireContext
                {
                    IsRaycastWeapon        = true,
                    IsNetworked            = true,
                    OwnerMidId             = request.OwnerMidId,
                    FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                    IsBotOwner             = request.IsBotOwner,
                    WeaponLevel            = request.WeaponLevel,
                    DamageMultiplier       = request.DamageMultiplier
                },
                request.ConfigId, rpcParams.Receive.SenderClientId, request.ClientFireTick);
        }

        // ── Client → Server: Physics ──────────────────────────────────────────

        /// <summary>
        /// Spawns one server-owned physics NetworkObject per resolved direction.
        ///
        /// configId tells SpawnPhysicsProjectile which config the projectile
        /// represents, so the visual and stats come from that config rather than
        /// from the prefab's inspector defaults.
        ///
        /// When patternId is not 0 (or spreadCount is above 1) the directions come
        /// from ProjectileDirectionResolver, the same deterministic resolution the
        /// Rust sim path uses, and one NetworkObject is spawned per direction. No
        /// raw direction data crosses the wire. No fire seed is sent on this RPC,
        /// so a pattern with random shapes uses its asset's own fixed seed.
        ///
        /// targetNetworkObjectId (0 = none) is resolved on the server to a live
        /// NetworkObject and handed to PhysicsProjectileBase.SetGuidedTarget once
        /// the projectile exists. Every projectile of one call shares the target.
        /// It only matters when the config's MovementType is Guided. Choosing the
        /// target is left to the caller; this only carries an already chosen
        /// target through.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void FirePhysicsProjectileServerRpc(
            Vector3 origin, Vector3 baseDirection, Quaternion rotation,
            PoolableNetworkObjectType poolType,
            float speed, float damageMultiplier,
            ulong ownerMidId, ulong firedByNetObjId,
            ushort configId = 0,
            ushort patternId = 0, byte spreadCount = 1, float spreadDeg = 0f, bool patternIs3D = false,
            ulong targetNetworkObjectId = 0,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsSpawned || _isShuttingDown) return;
            if (!MID_MasterProjectileSystem.HasInstance) return;

            // The NGO client id of whoever called this RPC. It is passed to
            // MID_MasterProjectileSystem.SpawnPhysicsProjectile, whose
            // firingClientId doc explains how it is used for the firing client.
            ulong firingClientId = rpcParams.Receive.SenderClientId;

            // Resolved once, up-front — every direction/spread instance from this
            // single fire call shares the same guided target (matches how a
            // single ClientFireTick shares one origin/rotation too).
            Transform guidedTarget = null;
            if (targetNetworkObjectId != 0 && NetworkManager.Singleton != null
                && NetworkManager.Singleton.SpawnManager != null
                && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                    targetNetworkObjectId, out var targetNetObj)
                && targetNetObj != null)
            {
                guidedTarget = targetNetObj.transform;
            }

            Vector3[] directions;
            if (patternId != 0 || spreadCount > 1)
            {
                var resolved = ProjectileDirectionResolver.Resolve(
                    patternId, origin, baseDirection, spreadCount, spreadDeg, speed, patternIs3D);
                directions = new Vector3[resolved.Length];
                for (int i = 0; i < resolved.Length; i++) directions[i] = resolved[i].Direction;
            }
            else
            {
                directions = new[] { baseDirection.sqrMagnitude > 0.001f ? baseDirection.normalized : Vector3.forward };
            }

            for (int i = 0; i < directions.Length; i++)
            {
                Quaternion rot = directions.Length == 1 ? rotation : DirectionToRotation(directions[i], patternIs3D);

                var netObj = MID_MasterProjectileSystem.Instance
                    .SpawnPhysicsProjectile(poolType, origin, rot, configId, firingClientId);

                if (netObj == null)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"FirePhysicsProjectileServerRpc: pool null for {poolType}.",
                        nameof(MID_ProjectileNetworkBridge));
                    continue;
                }

                var proj = netObj.GetComponent<PhysicsProjectileBase>();
                if (proj != null)
                {
                    proj.SetOwnerContext(ownerMidId, firedByNetObjId, false, 1, damageMultiplier);
                    proj.InitialiseProjectile(ownerMidId, firedByNetObjId, speed, false, 1);

                    // Must run AFTER InitialiseProjectile — that call chain is what
                    // resolves SetupMovementType(), which resets _hasGuidedTarget to
                    // false for every fresh launch (see its doc comment). Setting the
                    // target before that would just get silently wiped.
                    if (guidedTarget != null)
                        proj.SetGuidedTarget(guidedTarget);
                }
            }
        }

        private static Quaternion DirectionToRotation(Vector3 dir, bool is3D)
        {
            if (dir.sqrMagnitude < 0.001f) return Quaternion.identity;
            return is3D
                ? Quaternion.LookRotation(dir.normalized)
                : Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        }

        // ── Server → Clients ──────────────────────────────────────────────────

        /// <summary>
        /// Sent to other clients only, not to the firing client.
        /// Spawns fresh Rust buffer entries for projectiles fired by remote players.
        /// Position is advanced by the elapsed server time (clamped to 0.5 s) to
        /// account for RPC travel time.
        ///
        /// Delivery is unreliable on purpose: this is a one-shot cosmetic spawn
        /// for a client that is neither the shooter nor the authority. If the
        /// packet is dropped, that one client never renders that one shot. No
        /// other state depends on it and nothing desyncs, but nothing retries or
        /// supersedes it either (unlike the periodic snapshot), so a drop is an
        /// accepted occasional miss rather than paying for guaranteed delivery on
        /// every shot in a large lobby.
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
        /// Sent only to the firing client (reliable delivery).
        /// Swaps the temporary ids it spawned locally for the real server-assigned
        /// ids. No new entries are spawned: its projectiles are already running.
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
        /// Periodic authoritative state for the Rust sim projectiles. The current
        /// server tick and the tick interval are passed to ReconcileSnapshots2D/3D
        /// so the stale snapshot position can be extrapolated forward before it is
        /// compared with the local position. See those methods for the math.
        ///
        /// Delivery is unreliable on purpose: each snapshot is superseded by the
        /// next one a few ticks later, and the staleness extrapolation in
        /// ReconcileSnapshots2D/3D already smooths over a gap. A dropped snapshot
        /// is caught up by the next, so reliable retransmission of a value about
        /// to be replaced would be waste. It is the highest-frequency RPC in the
        /// system.
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

        // Pellet directions are never taken from the client. FireServerRpc calls
        // ProjectileDirectionResolver.Resolve(...), which regenerates them from
        // the registered pattern (or the spread scalars) instead of anything the
        // client claims about individual pellets.
    }
}
