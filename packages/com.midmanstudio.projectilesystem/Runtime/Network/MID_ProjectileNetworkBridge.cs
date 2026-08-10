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
        /// RUSTSIM GUIDED FIX ("guided doesn't work — dead wire"): 0 = no guided
        /// target (the default — every fire request before this fix behaves
        /// identically). Non-zero is resolved server-side in FireServerRpc back to
        /// a live NetworkObject's Transform and handed to ProjectileGuidanceTracker
        /// for every proj_id this request ends up spawning — same target for the
        /// whole batch, including every pellet of a pattern/spread shot.
        /// </summary>
        public ulong   TargetNetworkObjectId;

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
            // RUSTSIM GUIDED FIX: plain ulong, not compressed — a NetworkObjectId
            // isn't a spatial/angular value WireCompression's lossy encodings apply
            // to, and 0 (by far the common case: most fire requests aren't Guided)
            // is only 8 bytes either way.
            s.SerializeValue(ref TargetNetworkObjectId);
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

            // FIX: "Receive queue is full, some packets could be dropped" — this
            // is a DIFFERENT setting from MaxSendQueueSize above. That one is the
            // byte-size batching/accumulation buffer; MaxPacketQueueSize is the
            // count of discrete packets UTP can hold per internal send/receive
            // queue, i.e. how many packets can actually be processed in a single
            // frame. Culling's per-client dispatch (SendSnapshotsCulled) fires N
            // separate targeted RPC calls per snapshot tick instead of the
            // previous handful of broadcasts — each individually chunked, so it's
            // now genuinely possible to have more discrete packets land in one
            // frame than the transport's default packet-count queue was sized
            // for, independent of total bytes (which is already bounded by the
            // chunking fix). 2048 gives real headroom at "10s/100s of players"
            // scale — UTP's own practical ceiling is in the 13-14k range before
            // other problems start, so this is nowhere close to that.
            transport.MaxPacketQueueSize = 2048;
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

            // RUSTSIM GUIDED FIX ("guided doesn't work — dead wire"): baseId/written
            // are exactly the proj_id range this request just spawned — the same
            // values used for the confirmation below. ProjectileGuidanceTracker's
            // own Update() loop then drives every one of them from here on; nothing
            // further to do per-tick on this end. Server-only, deliberately — see
            // ProjectileGuidanceTracker's file header for why a pure network client
            // registering here would be a harmless no-op at best (it doesn't own
            // the authoritative sim) and is skipped entirely rather than relying on
            // that no-op.
            if (request.TargetNetworkObjectId != 0 && cfg.MovementType == ProjectileMovementType.Guided
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

            // Send spawn to all other connected clients — distance-filtered if
            // culling is on. This is the actual mechanism that matters: once a
            // client spawns a projectile locally (via this RPC), its own local
            // sim keeps it moving and rendering independently — it does NOT need
            // further snapshot updates to stay alive or visible. Culling only the
            // periodic SendSnapshotClientRpc and not this one meant every shot
            // still reached every client at the moment of firing regardless of
            // distance, and just kept running from there — the snapshot culling
            // from a previous pass never had anything visible left to prevent.
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
        /// PATTERN SUPPORT: raycast fire previously had no pattern concept at all —
        /// only ever a single ray. This resolves request.PatternId (or
        /// ProjectileCount/SpreadDeg for the no-pattern spread case) into N
        /// directions via the same ProjectileDirectionResolver every other fire
        /// path uses, and has the server independently cast all N rays itself.
        /// Unlike RaycastFireServerRpc above, there's no client hit-claim to
        /// validate against here — for a multi-pellet spread weapon, fully
        /// server-authoritative is both simpler and a perfectly reasonable trust
        /// model (a shotgun's pellets don't need the same tight validation a
        /// precision single-ray weapon does). request.Direction is the raw,
        /// un-rotated aim direction — same convention as the Rust-sim path.
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
        /// BUG FIX: configId was never part of this RPC at all — SpawnPhysicsProjectile
        /// had no way to know which config it was representing, so every physics
        /// projectile used whatever was hardcoded in its prefab's Inspector
        /// (_visualConfigId defaulting to 0) regardless of what was actually fired.
        /// That's why the real sprite never showed up.
        ///
        /// PATTERN SUPPORT: patternId != 0 (or spreadCount > 1) resolves multiple
        /// directions via the same ProjectileDirectionResolver every other fire
        /// path already uses, and spawns one physics NetworkObject per direction —
        /// same deterministic regeneration principle as the Rust-sim path, just
        /// producing N discrete physics bodies instead of N entries in a batch
        /// buffer. No raw direction data crosses the wire here either.
        ///
        /// GUIDED FIX ("guided doesn't work for physics projectiles over the
        /// network"): this RPC previously had no way to carry a homing target
        /// across the wire at all — PhysicsProjectileBase.SetGuidedTarget()
        /// existed and worked correctly, but nothing here ever called it, so
        /// every networked physics projectile with MovementType.Guided just
        /// flew straight. targetNetworkObjectId (0 = none) is resolved back to
        /// a live NetworkObject via SpawnManager and handed to SetGuidedTarget
        /// once the projectile exists. Only meaningful when the fired config's
        /// MovementType is actually Guided — harmless no-op otherwise. Target
        /// SELECTION (who to lock onto) is deliberately left to the caller;
        /// this only plumbs an already-chosen target through.
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

            // BUG FIX ("physics projectile fires twice on client"): this is the
            // actual NGO id of whoever called this RPC — see the firingClientId
            // doc on MID_MasterProjectileSystem.SpawnPhysicsProjectile for why it
            // needs to be passed through instead of spawning server-owned.
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
