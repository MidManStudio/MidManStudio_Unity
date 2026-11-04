// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "MID_MasterProjectileSystem.cs"
// ============================================================================
// Scene-level entry point that game code calls to fire projectiles, register
// collision targets and shapes, steer guided shots and read live positions. It
// holds no simulation state of its own. Each call is routed to the backend that
// fits the current mode:
//   - Offline (no running NetworkManager, or _forceOfflineMode): the local
//     manager, LocalProjectileManager.
//   - Server or host: ServerProjectileAuthority, which owns the authoritative
//     simulation and all collision.
//   - Pure client: FireNetworkedSim first spawns a predicted copy straight into
//     LocalProjectileManager (SpawnFiringClientBatch2D/3D) so the shooter sees the
//     shot at once, then sends the fire request with FireServerRpc through
//     MID_ProjectileNetworkBridge. The host skips the predicted copy because it
//     renders from the authority's own buffer.
//
// Target and shape registration goes to the authority when this instance is the
// server, and to LocalProjectileManager only when the session is not networked.
// In a networked session LocalProjectileManager's target buffers therefore stay
// empty, and its FixedUpdate skips the hit pass when both the target count and
// the shape count are 0, so only the server decides hits.
//
// SetLocalPlayerMidId forwards the id to ClientPredictionManager and to the
// bridge. Neither class reads the stored id back (see their notes).

using System;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.Pools;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Visuals;
using MidManStudio.Projectiles.Network;
using SimulationMode = MidManStudio.Projectiles.Core.SimulationMode;

namespace MidManStudio.Projectiles.Managers
{
    /// <summary>
    /// Payload of MID_MasterProjectileSystem.OnRustSimHit (see that event for the
    /// full picture). The fields come from one of two sources depending on the
    /// mode, so TargetNetworkObjectId and RawTargetId are never both filled:
    ///   LocalOnly (offline):  RawTargetId populated, TargetNetworkObjectId = 0
    ///   Networked:            TargetNetworkObjectId populated, RawTargetId = 0
    /// The networked HitConfirmation does not carry the raw RustSim numeric
    /// target id to clients, only a resolvable NetworkObjectId.
    /// </summary>
    public struct RustSimHitPayload
    {
        public uint    ProjId;
        public ushort  ConfigId;
        public uint    RawTargetId;
        public ulong   TargetNetworkObjectId;
        public float   Damage;
        public Vector3 HitPosition;
        public bool    IsHeadshot;
        public bool    IsCrit;
    }

    public sealed class MID_MasterProjectileSystem : Singleton<MID_MasterProjectileSystem>
    {
        #region Serialized References

        [Header("Core Systems")]
        [SerializeField] private ProjectileRegistry          _registry;
        [SerializeField] private ServerProjectileAuthority   _authority;
        [SerializeField] private LocalProjectileManager      _localManager;
        [SerializeField] private MID_ProjectileNetworkBridge _networkBridge;
        [SerializeField] private ClientPredictionManager     _predictionManager;
        [SerializeField] private RaycastProjectileHandler    _raycastHandler;

        [Header("Visual Systems")]
        [SerializeField] private ProjectileRenderer2D        _renderer2D;
        [SerializeField] private ProjectileRenderer3D        _renderer3D;
        [SerializeField] private TrailObjectPool             _trailPool;
        [SerializeField] private ProjectileImpactHandler     _impactHandler;

        [Header("Network Object Pool (Physics Projectiles)")]
        [SerializeField] private MID_NetworkObjectPool       _networkObjectPool;

        [Header("Mode")]
        [SerializeField] private bool _forceOfflineMode = false;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        private bool _initialised = false;

        /// <summary>
        /// Raised once, at the end of Initialise(). It is static rather than per
        /// instance on purpose, so a scene object can subscribe before the
        /// singleton exists and still hear it.
        ///
        /// A subscriber that attaches after Initialise() has already run would
        /// never see the event, which is why <see cref="IsReady"/> sits next to
        /// it. A consumer checks first and subscribes only when not ready:
        ///   if (IsReady) { RegisterNow(); } else { OnSystemReady += Handler; }
        /// RustSimTargetRegistrar and RustSimCustomShapeAuthoring do exactly this
        /// in their RegisterNow(). The one code path then covers both a scene
        /// object that races this system's Awake() and an object instantiated
        /// minutes into a session, networked or not.
        ///
        /// Those two components also keep a per-tick retry in Update/FixedUpdate
        /// as a second, independent safety net.
        /// </summary>
        public static event Action OnSystemReady;

        public static bool IsReady => HasInstance && Instance._initialised;

        #endregion

        #region Properties

        public bool IsNetworked =>
            !_forceOfflineMode
            && NetworkManager.Singleton != null
            && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient);

        public bool IsServer =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        public bool IsHostMode =>
            NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsServer
            && NetworkManager.Singleton.IsClient;

        /// <summary>
        /// True when a RegisterTarget/RegisterShape call made right now would
        /// actually land in a backend: the server authority when this is a
        /// server, or the local manager when offline. A pure client, or a
        /// missing backend, would silently drop the call.
        /// </summary>
        public bool CanAcceptTargets
            => (IsServer && _authority != null) || (!IsNetworked && _localManager != null);

        public ServerProjectileAuthority      GetAuthority()         => _authority;
        public MID_ProjectileNetworkBridge    GetBridge()            => _networkBridge;
        public RaycastProjectileHandler       GetRaycastHandler()    => _raycastHandler;

        /// <summary>
        /// For physics and raycast pool visuals that manage their own prediction
        /// (see ClientPredictionManager). The RustSim path does not need it.
        /// </summary>
        public ClientPredictionManager        GetPredictionManager() => _predictionManager;

        public int GetBridgeTick() => _networkBridge?.GetServerTick() ?? 0;

        /// <summary>
        /// Session-wide re-raise of every spawned physics projectile's
        /// OnHitServerConfirmed. That event lives on each PhysicsProjectileBase
        /// instance, so on its own there is nothing to subscribe to once at session
        /// start, unlike RustSimAdapter.OnProjectileHit and
        /// RaycastProjectileHandler.OnServerHitConfirmed, which are session-wide.
        /// SpawnPhysicsProjectile wires each new or reused pooled instance into this
        /// relay, so it is raised on the server only. Subscribe here exactly like
        /// the other two hit sources.
        /// </summary>
        public event Action<ProjectileHitPayload> OnPhysicsHit;

        private void RelayPhysicsHit(ProjectileHitPayload payload) => OnPhysicsHit?.Invoke(payload);

        /// <summary>
        /// One event for RustSim hits that are visible on this machine. The
        /// underlying signals live in three places: LocalProjectileManager.OnHit
        /// (offline), MID_ProjectileNetworkBridge.OnHitConfirmedLocal (networked
        /// clients, and the host for its own hits) and, on the server,
        /// ServerProjectileAuthority.Adapter.OnProjectileHit. This event relays the
        /// first two, in the same subscribe-once-at-session-start shape as
        /// <see cref="OnPhysicsHit"/>.
        ///
        /// The third one is deliberately not relayed. Adapter.OnProjectileHit is
        /// where damage should be applied authoritatively. It is raised only on the
        /// server, it is the event the bridge itself listens to in order to send
        /// HitConfirmedClientRpc, and on a host it would fire next to this event
        /// for the same hit (the host is both server and client, so it gets
        /// Adapter.OnProjectileHit and OnHitConfirmedLocal).
        /// Use OnRustSimHit to react to something that visibly happened (VFX,
        /// sound, UI feedback; PlayImpact is driven from the same
        /// OnHitConfirmedLocal source) and Adapter.OnProjectileHit for
        /// server-authoritative damage and game state.
        ///
        /// Only one of TargetNetworkObjectId and RawTargetId is filled, depending
        /// on the source; see <see cref="RustSimHitPayload"/>.
        /// </summary>
        public event Action<RustSimHitPayload> OnRustSimHit;

        private void RelayLocalRustSimHit(LocalHitPayload payload)
        {
            OnRustSimHit?.Invoke(new RustSimHitPayload
            {
                ProjId       = payload.ProjId,
                ConfigId     = payload.ConfigId,
                RawTargetId  = payload.RawTargetId,
                Damage       = payload.Damage,
                HitPosition  = payload.HitPosition,
                IsHeadshot   = payload.IsHeadshot,
                IsCrit       = payload.IsCrit,
            });
        }

        private void RelayNetworkedRustSimHit(HitConfirmation confirmation)
        {
            OnRustSimHit?.Invoke(new RustSimHitPayload
            {
                ProjId                = confirmation.ProjId,
                ConfigId              = confirmation.ConfigId,
                TargetNetworkObjectId = confirmation.TargetNetworkId,
                Damage                = confirmation.Damage,
                HitPosition           = confirmation.HitPosition,
                IsHeadshot            = confirmation.IsHeadshot,
                IsCrit                = confirmation.IsCrit,
            });
        }

        #endregion

        #region Initialisation

        protected override void Awake()
        {
            base.Awake();
            Initialise();
        }

        private void Initialise()
        {
            if (_initialised) return;

            // ValidateStructSizes throws InvalidOperationException both for a
            // struct size mismatch and when the native library cannot be loaded
            // on this platform or architecture. Catching it here lets the system
            // disable itself with a logged error. An uncaught exception would
            // leave _initialised false and skip everything below (batch helper,
            // transport setup, bridge wiring) with nothing logged at this call
            // site.
            try { ProjectileLib.ValidateStructSizes(); }
            catch (InvalidOperationException ex)
            {
                MID_Logger.LogError(_logLevel,
                    $"Fatal struct size mismatch: {ex.Message}",
                    nameof(MID_MasterProjectileSystem));
                enabled = false;
                return;
            }

            BatchSpawnHelper.Initialise();
            MID_ProjectileNetworkBridge.ConfigureTransportForHighThroughput();

            if (_localManager == null)
                _localManager = FindAnyObjectByType<LocalProjectileManager>();

            if (_localManager != null)
            {
                // Safe against re-Initialise() calls, same pattern SpawnPhysicsProjectile
                // already uses for OnHitServerConfirmed below.
                _localManager.OnHit -= RelayLocalRustSimHit;
                _localManager.OnHit += RelayLocalRustSimHit;
            }

            if (_authority != null)
            {
                _authority.TrailPool     = _trailPool;
                _authority.NetworkBridge = _networkBridge;
            }

            if (_networkBridge != null)
            {
                _networkBridge.Authority      = _authority;
                _networkBridge.Prediction     = _predictionManager;
                _networkBridge.RaycastHandler = _raycastHandler;
                _networkBridge.ImpactHandler  = _impactHandler;

                _networkBridge.OnHitConfirmedLocal -= RelayNetworkedRustSimHit;
                _networkBridge.OnHitConfirmedLocal += RelayNetworkedRustSimHit;
            }

            _initialised = true;
            OnSystemReady?.Invoke();

            MID_Logger.LogInfo(_logLevel,
                $"Initialised. Mode: {(IsNetworked ? "Networked" : "Offline")} " +
                $"LocalManager: {(_localManager != null ? "OK" : "MISSING")}",
                nameof(MID_MasterProjectileSystem));
        }

        protected override void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            ProjectileLib.clear_movement_params();
        }

        #endregion

        #region Public API — Identity

        /// <summary>
        /// Stores the local player's id on ClientPredictionManager and on
        /// MID_ProjectileNetworkBridge. Both only keep the value and neither reads
        /// it back, so calling this does not change any routing today.
        /// </summary>
        public void SetLocalPlayerMidId(ulong midId)
        {
            _predictionManager?.SetLocalPlayerMidId(midId);
            _networkBridge?.SetLocalPlayerMidId(midId);
        }

        #endregion

        #region Public API — Fire

        /// <param name="patternId">
        /// 0 = no pattern (default). When non-zero, only PatternId travels over
        /// the wire for RustSim fire — the server re-samples the same registered
        /// ProjectilePatternSO instead of trusting spawnPoints' directions.
        /// spawnPoints is still used in full for this client's own local visual
        /// (predicted spawn / LocalOnly mode never leaves the machine).
        /// </param>
        /// <param name="spreadDeg">
        /// Only relevant when patternId == 0 and count > 1 — the arc used to
        /// regenerate a parametric fan spread server-side.
        /// </param>
        /// <param name="baseDirection">
        /// The RAW, unrotated aim direction (before any pattern/spread offset is
        /// applied) — i.e. whatever was passed as `dir` into whatever built
        /// spawnPoints, NOT spawnPoints[0].Direction. This matters: pellet 0 is
        /// not generally centered on the aim direction (a fan's first pellet sits
        /// at -halfArc, for example), so using spawnPoints[0] here would make
        /// every recipient regenerate the pattern rotated around an
        /// already-rotated base — the whole pattern skews off at an angle.
        /// Falls back to spawnPoints[0].Direction if left default, which is only
        /// correct for single-shot (count == 1) fire.
        /// </param>
        /// <param name="patternIs3D">
        /// The resolved rotation convention the caller used to build spawnPoints
        /// (e.g. Use3DConvention() || cfg.Is3D) — sent as-is rather than
        /// re-derived from cfg.Is3D server-side, since the two can legitimately
        /// diverge (a 2D-configured weapon fired in 3D mode or vice versa).
        /// </param>
        /// <param name="guidedTarget">
        /// Target for Guided movement (see LocalProjectileManager.Spawn2D's matching
        /// parameter). LocalOnly passes it straight to Spawn2D/3D. For RustSim it
        /// is sent as a NetworkObjectId in the fire request and resolved back to a
        /// Transform on the server. Null (default) means no target.
        /// </param>
        /// <param name="fireSeed">
        /// The seed that was passed to ProjectileDirectionResolver.Resolve() when
        /// spawnPoints was built. It travels with the request so the server and
        /// every other client rebuild the same layout, speeds and burst delays.
        /// 0 = fixed layout from the pattern asset.
        /// </param>
        public void Fire(
            ushort configId, SpawnPoint[] spawnPoints, int count, WeaponFireContext context,
            ushort patternId = 0, float spreadDeg = 0f,
            Vector3 baseDirection = default, bool patternIs3D = false,
            Transform guidedTarget = null, ushort fireSeed = 0)
        {
            if (!_initialised) return;

            var cfg = _registry?.Get(configId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Fire(): configId {configId} not registered.",
                    nameof(MID_MasterProjectileSystem));
                return;
            }

            var routing = ProjectileTypeRouter.Route(cfg, context);

            switch (routing.Mode)
            {
                case SimulationMode.LocalOnly:
                    FireLocal(configId, spawnPoints, count, context, cfg, guidedTarget);
                    break;

                case SimulationMode.RustSim:
                    FireNetworkedSim(configId, spawnPoints, count, context, cfg,
                        patternId, spreadDeg, baseDirection, patternIs3D, guidedTarget, fireSeed);
                    break;

                case SimulationMode.Raycast:
                    MID_Logger.LogWarning(_logLevel,
                        "Fire() with Raycast mode — use RegisterRaycastFire() instead.",
                        nameof(MID_MasterProjectileSystem));
                    break;

                case SimulationMode.PhysicsObject:
                    MID_Logger.LogWarning(_logLevel,
                        "PhysicsObject mode — call SpawnPhysicsProjectile() from weapon script.",
                        nameof(MID_MasterProjectileSystem));
                    break;
            }
        }

        private void FireLocal(
            ushort configId, SpawnPoint[] spawnPoints, int count,
            WeaponFireContext context, ProjectileConfigSO cfg, Transform guidedTarget = null)
        {
            if (_localManager == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "FireLocal: LocalProjectileManager not found.",
                    nameof(MID_MasterProjectileSystem));
                return;
            }

            if (cfg.Is3D)
                _localManager.Spawn3D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier, guidedTarget);
            else
                _localManager.Spawn2D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier, guidedTarget);
        }

        private void FireNetworkedSim(
            ushort configId, SpawnPoint[] spawnPoints, int count,
            WeaponFireContext context, ProjectileConfigSO cfg,
            ushort patternId, float spreadDeg, Vector3 baseDirection, bool patternIs3D,
            Transform guidedTarget = null, ushort fireSeed = 0)
        {
            if (_networkBridge == null) return;

            float resolvedSpeed = count > 0 && spawnPoints[0].Speed > 0f
                ? spawnPoints[0].Speed
                : cfg.ResolveSpeed();

            // The direction that travels is the raw aim direction, before any
            // pattern or spread offset. spawnPoints[0].Direction is not that: for a
            // pattern or spread of more than one pellet it already carries pellet
            // 0's own angle (pellet 0 sits at the start of the arc, not the
            // centre). Every recipient re-applies the pattern around the direction
            // it receives, so sending an already offset direction would skew the
            // whole shot. spawnPoints[0].Direction is only the fallback, correct for
            // a single shot.
            Vector3 resolvedBaseDir = baseDirection.sqrMagnitude > 0.0001f
                ? baseDirection.normalized
                : (count > 0 ? spawnPoints[0].Direction : Vector3.forward);

            // Only patternId, spreadDeg, fireSeed and the base direction travel.
            // Every recipient regenerates the same pellet set with
            // ProjectileDirectionResolver.Resolve(). spawnPoints is used below only
            // for this client's own predicted visual.
            var request = new ProjectileFireRequest
            {
                ConfigId               = configId,
                Origin                 = count > 0 ? spawnPoints[0].Origin : Vector3.zero,
                Direction              = resolvedBaseDir,
                Speed                  = resolvedSpeed,
                ProjectileCount        = (byte)Mathf.Min(count, 255),
                PatternId              = patternId,
                SpreadDeg              = spreadDeg,
                PatternIs3D            = patternIs3D,
                FireSeed               = fireSeed,
                OwnerMidId             = context.OwnerMidId,
                FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                IsBotOwner             = context.IsBotOwner,
                WeaponLevel            = context.WeaponLevel,
                DamageMultiplier       = context.DamageMultiplier,
                ClientFireTick         = _networkBridge.GetServerTick(),
                // A Transform means nothing across the wire, so it is sent as the
                // NetworkObjectId of the nearest NetworkObject on it or above it.
                // 0 means no guided target.
                TargetNetworkObjectId  = guidedTarget != null
                    ? (guidedTarget.GetComponentInParent<NetworkObject>()?.NetworkObjectId ?? 0UL)
                    : 0UL
            };

            // A pure client spawns its own copy into LocalProjectileManager now.
            // It renders through ProjectileRenderer2D/3D, the same GPU instanced
            // path as the host, with no pool objects and no C# prediction math.
            // The temporary ids are linked to the server's real ids when
            // SpawnConfirmedClientRpc arrives, through
            // LocalProjectileManager.LinkNetworkProjectileBatch. The host is
            // excluded because it renders from ServerProjectileAuthority's buffer.
            if (!IsServer && _localManager != null)
            {
                if (!cfg.Is3D)
                    _localManager.SpawnFiringClientBatch2D(spawnPoints, count, configId, resolvedSpeed);
                else
                    _localManager.SpawnFiringClientBatch3D(spawnPoints, count, configId, resolvedSpeed);
            }

            _networkBridge.FireServerRpc(request);
        }

        #endregion

        #region Public API — Physics Pool

        /// <summary>
        /// Server only. Takes a projectile from the network object pool, spawns it
        /// (always server-owned), tells it which config it represents and relays
        /// its hits to <see cref="OnPhysicsHit"/>. Returns null on a client, when
        /// there is no pool, or when the pool hands nothing back.
        ///
        /// This method does not call InitialiseProjectile and takes no guided
        /// target. The caller does both afterwards, in this order:
        ///
        ///   var netObj = system.SpawnPhysicsProjectile(...);
        ///   var proj = netObj.GetComponent&lt;PhysicsProjectileBase&gt;();
        ///   proj.SetOwnerContext(...);
        ///   proj.InitialiseProjectile(...);      // must come before SetGuidedTarget
        ///   proj.SetGuidedTarget(yourRealTarget); // must come after InitialiseProjectile
        ///
        /// InitialiseProjectile resets the guided-target state on every fresh
        /// launch, so a target set before it is wiped a moment later. A guided
        /// target parameter here would therefore be set at the wrong time.
        /// Guided targeting is not tied to any particular caller or scene.
        ///
        /// Choosing the target (nearest enemy, current reticle target and so on)
        /// is left to the caller. MID_ProjectileNetworkBridge.
        /// FirePhysicsProjectileServerRpc follows exactly this order and is the
        /// reference call site inside this package.
        /// </summary>
        /// <param name="configId">
        /// The ProjectileConfigSO this spawn represents. It is pushed into the
        /// instance with PhysicsProjectileBase.SetVisualConfigId right after the
        /// spawn. Pass 0 to keep the prefab's own inspector default.
        /// </param>
        /// <param name="firingClientId">
        /// The client that fired the shot. The instance is spawned server-owned
        /// and then hidden from that client with NetworkHide, so it is replicated
        /// only to the other clients. The firing client already has its own local
        /// prediction ghost (SpawnLocalPhysicsVisual), which lives out its own
        /// MaxLifetime and glides to the confirmed hit point through
        /// HitConfirmedClientRpc and ClientPredictionManager.OnHitConfirmed. That
        /// ClientRpc is a plain broadcast with no target list, so it reaches the
        /// firer. Showing the real object to the firer as well would put two
        /// copies of the same shot on their screen.
        ///
        /// A host firer is exempt from the hide: it is expected to have no ghost
        /// of its own, so it needs the real object. The package cannot enforce
        /// that expectation, since the weapon script that spawns ghosts lives in
        /// the game project.
        ///
        /// The default, ulong.MaxValue, hides from nobody (offline use, or no
        /// specific firing client to exclude).
        /// </param>
        public NetworkObject SpawnPhysicsProjectile(
            PoolableNetworkObjectType type, Vector3 position, Quaternion rotation,
            ushort configId = 0, ulong firingClientId = ulong.MaxValue)
        {
            if (!IsServer) return null;
            if (_networkObjectPool == null) return null;
            var netObj = _networkObjectPool.GetNetworkObject(type, position, rotation);
            if (netObj == null) return null;

            var physicsBase = netObj.GetComponent<PhysicsProjectileBase>();

        

            // Always spawned server-owned (see the firingClientId doc above). The
            // firing client is told apart by the NetworkHide call below, not by
            // ownership.
            netObj.Spawn();

            if (physicsBase != null)
                physicsBase.SetVisualConfigId(configId);

            bool firerIsHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost
                && firingClientId == NetworkManager.Singleton.LocalClientId;

            if (firingClientId != ulong.MaxValue && !firerIsHost)
                netObj.NetworkHide(firingClientId);

            if (physicsBase != null)
            {
                // Safe against pooled reuse: this instance may already be
                // subscribed from an earlier life in the pool. Unsubscribe first so
                // it never holds more than one subscription.
                physicsBase.OnHitServerConfirmed -= RelayPhysicsHit;
                physicsBase.OnHitServerConfirmed += RelayPhysicsHit;
            }

            return netObj;
        }

        public void ReturnPhysicsProjectile(NetworkObject netObj, PoolableNetworkObjectType type)
        {
            if (_networkObjectPool == null || netObj == null) return;
            _networkObjectPool.ReturnNetworkObject(netObj, type);
        }

        #endregion

        #region Public API — Raycast

        /// <summary>
        /// Networked pattern fire for raycast weapons (several pellets from one
        /// fire event). On the server or host it calls
        /// RaycastProjectileHandler.ServerHandleFirePattern directly. On a remote
        /// client it sends RaycastPatternFireServerRpc and then runs a local
        /// prediction, with the same IsServer branch RegisterRaycastFire uses.
        ///
        /// It is meant for networked sessions only. An offline multi-pellet raycast
        /// is simpler as a loop of the caller's own single-shot RegisterRaycastFire
        /// calls, since there is no server round trip to save. Called offline, this
        /// method would take the client branch, and the RPC send would throw
        /// (logged with the [RAYDIAG] prefix) before the local prediction runs.
        /// </summary>
        public void RegisterRaycastPatternFire(
            Vector3 origin, Vector3 baseDirection, bool is3D,
            ushort configId, ushort patternId, byte pelletCount, float spreadDeg,
            WeaponFireContext context)
        {
            if (!_initialised) return;
            var cfg = _registry?.Get(configId);
            if (cfg == null) return;

            if (IsServer)
            {
                _raycastHandler?.ServerHandleFirePattern(
                    patternId, origin, baseDirection, pelletCount, spreadDeg, is3D,
                    context, configId, ulong.MaxValue);
                return;
            }

            // Same shape as RegisterRaycastFire: the RPC send is wrapped so a throw
            // in it cannot skip the local prediction below. The exception is
            // logged, since it would also mean the server never got this fire
            // event.
            try
            {
                _networkBridge?.RaycastPatternFireServerRpc(new ProjectileFireRequest
                {
                    ConfigId               = configId,
                    Origin                 = origin,
                    Direction              = baseDirection,
                    PatternId              = patternId,
                    ProjectileCount        = pelletCount,
                    SpreadDeg              = spreadDeg,
                    PatternIs3D            = is3D,
                    OwnerMidId             = context.OwnerMidId,
                    FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                    IsBotOwner             = context.IsBotOwner,
                    WeaponLevel            = context.WeaponLevel,
                    DamageMultiplier       = context.DamageMultiplier,
                    ClientFireTick         = _networkBridge.GetServerTick()
                });
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RAYDIAG] RaycastPatternFireServerRpc THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            // Local prediction for the firing client. ServerHandleFirePattern's
            // ClientRpc leaves the firing client out of its target list
            // (BuildTargetList(senderClientId)) on the assumption that a local
            // prediction already exists. Without this call the shooter would see
            // nothing of their own pattern shot.
            _raycastHandler?.ClientPredictPatternLocal(
                patternId, origin, baseDirection, pelletCount, spreadDeg, is3D,
                configId, (uint)context.OwnerMidId, 1f);
        }

        public void RegisterRaycastFire(
            RaycastFireResult result, ushort configId, WeaponFireContext context)
        {
         
            if (!_initialised) return;
            var cfg = _registry?.Get(configId);
            if (cfg == null) return;

            if (!IsNetworked)
            {
                _raycastHandler?.OfflineHandleFire(
                    result, configId, (uint)context.OwnerMidId, context.DamageMultiplier);
                return;
            }

            if (IsServer)
            {
                _raycastHandler?.ServerHandleFire(result, context, configId);
            }
            else
            {
                // The RPC send is wrapped in its own try/catch so an exception in it
                // (for example from ProjectileFireRequest's NetworkSerialize)
                // cannot abort this branch and skip OfflineHandleFire below, which
                // draws the firing client's own ray. OfflineHandleFire runs outside
                // the try, so the shooter's visual shows whether or not the send
                // succeeded.
                //
                // A caught exception here means more than a missing visual: the
                // server never received the fire event, so hit registration and
                // damage for this client's shots are broken as well. The catch
                // only logs (with the [RAYDIAG] prefix). It does not retry, and it
                // does not report the failure to the caller.
                try
                {
                    _networkBridge?.RaycastFireServerRpc(
                        new ProjectileFireRequest
                        {
                            ConfigId               = configId,
                            Origin                 = result.Origin,
                            Direction              = result.Direction,
                            OwnerMidId             = context.OwnerMidId,
                            FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                            IsBotOwner             = context.IsBotOwner,
                            WeaponLevel            = context.WeaponLevel,
                            DamageMultiplier       = context.DamageMultiplier,
                            ClientFireTick         = _networkBridge.GetServerTick()
                        },
                        result.HitPoint, result.DidHit,
                        result.IsHeadshot, result.HitTargetNetworkId,
                        result.Is3D);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[RAYDIAG] RaycastFireServerRpc THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }

                _raycastHandler?.OfflineHandleFire(
                    result, configId, (uint)context.OwnerMidId, 1f);
            }
        }

        #endregion

        #region Public API — Targets

        public void RegisterTarget2D(in CollisionTarget target, int unityLayer = 0)
        {
            if (IsServer)       _authority?.RegisterTarget2D(target, unityLayer);
            // Not registered in the local manager when networked. Its target buffer
            // then stays empty and its FixedUpdate skips the hit pass. The server
            // decides every hit through ServerProjectileAuthority.
            if (!IsNetworked)   _localManager?.RegisterTarget2D(target, unityLayer);
        }

        /// <summary>
        /// Same as RegisterTarget2D(target, int), but reads the Unity layer
        /// straight off <paramref name="sourceObject"/> so there's no
        /// unityLayer argument to forget — the exact mistake that silently
        /// defaults every unregistered target to layer 0 when using the
        /// int-argument overload directly (RegisterTarget2D(target) with the
        /// default 0 falls into the same trap, so prefer this one whenever
        /// you have the target's GameObject at the call site).
        /// </summary>
        public void RegisterTarget2D(in CollisionTarget target, GameObject sourceObject)
            => RegisterTarget2D(in target, sourceObject != null ? sourceObject.layer : 0);

        public void RegisterTarget2D(in CollisionTarget target, Component sourceComponent)
            => RegisterTarget2D(in target, sourceComponent != null ? sourceComponent.gameObject.layer : 0);

        public void RegisterTarget3D(in CollisionTarget3D target, int unityLayer = 0)
        {
            if (IsServer)       _authority?.RegisterTarget3D(target, unityLayer);
            if (!IsNetworked)   _localManager?.RegisterTarget3D(target, unityLayer);
        }

        public void RegisterTarget3D(in CollisionTarget3D target, GameObject sourceObject)
            => RegisterTarget3D(in target, sourceObject != null ? sourceObject.layer : 0);

        public void RegisterTarget3D(in CollisionTarget3D target, Component sourceComponent)
            => RegisterTarget3D(in target, sourceComponent != null ? sourceComponent.gameObject.layer : 0);

        public void DeactivateTarget2D(uint targetId)
        {
            if (IsServer)       _authority?.DeactivateTarget2D(targetId);
            if (!IsNetworked)   _localManager?.DeactivateTarget2D(targetId);
        }

        public void DeactivateTarget3D(uint targetId)
        {
            if (IsServer)       _authority?.DeactivateTarget3D(targetId);
            if (!IsNetworked)   _localManager?.DeactivateTarget3D(targetId);
        }

        #endregion

        #region Public API — Shape Colliders (Box/Capsule/Edge/Polygon)
        //
        // Mirrors the Target API above exactly — same IsServer/IsNetworked
        // routing, same GameObject/Component convenience overloads.

        public void RegisterShape2D(in ShapeCollider2D shape, int unityLayer = 0)
        {
            if (IsServer)       _authority?.RegisterShape2D(shape, unityLayer);
            if (!IsNetworked)   _localManager?.RegisterShape2D(shape, unityLayer);
        }

        public void RegisterShape2D(in ShapeCollider2D shape, GameObject sourceObject)
            => RegisterShape2D(in shape, sourceObject != null ? sourceObject.layer : 0);

        public void RegisterShape2D(in ShapeCollider2D shape, Component sourceComponent)
            => RegisterShape2D(in shape, sourceComponent != null ? sourceComponent.gameObject.layer : 0);

        public void RegisterShape3D(in ShapeCollider3D shape, int unityLayer = 0)
        {
            if (IsServer)       _authority?.RegisterShape3D(shape, unityLayer);
            if (!IsNetworked)   _localManager?.RegisterShape3D(shape, unityLayer);
        }

        public void RegisterShape3D(in ShapeCollider3D shape, GameObject sourceObject)
            => RegisterShape3D(in shape, sourceObject != null ? sourceObject.layer : 0);

        public void RegisterShape3D(in ShapeCollider3D shape, Component sourceComponent)
            => RegisterShape3D(in shape, sourceComponent != null ? sourceComponent.gameObject.layer : 0);

        public void DeactivateShape2D(uint targetId)
        {
            if (IsServer)       _authority?.DeactivateShape2D(targetId);
            if (!IsNetworked)   _localManager?.DeactivateShape2D(targetId);
        }

        public void DeactivateShape3D(uint targetId)
        {
            if (IsServer)       _authority?.DeactivateShape3D(targetId);
            if (!IsNetworked)   _localManager?.DeactivateShape3D(targetId);
        }

        #endregion

        #region API - Clearing Targets
        public void ClearAllTargets()
        {
            if (IsServer)       _authority?.ClearAllTargets();
            if (!IsNetworked)   _localManager?.ClearAllTargets();
        }

        #endregion

        #region Public API — State

        public int SaveState2D(byte[] buf)           => _authority?.SaveState2D(buf) ?? 0;
        public int RestoreState2D(byte[] buf, int n) => _authority?.RestoreState2D(buf, n) ?? 0;

        #endregion

        #region Public API — Guided
        //
        // SetHomingDirection2D/3D route like RegisterTarget2D/3D: to the authority
        // when this instance is the server, and to the local manager when the
        // session is not networked. They are two separate buffers (the authority
        // holds networked projectiles, the local manager holds non-networked
        // ones), and a non-networked game has IsServer false, so one combined
        // condition would write into the wrong, empty buffer.

        public void SetHomingDirection2D(uint projId, Vector2 worldDir)
        {
            if (IsServer)     _authority?.SetAcceleration2D(projId, worldDir);
            if (!IsNetworked) _localManager?.SetAcceleration2D(projId, worldDir);
        }

        public void SetHomingDirection3D(uint projId, Vector3 worldDir)
        {
            if (IsServer)     _authority?.SetAcceleration3D(projId, worldDir);
            if (!IsNetworked) _localManager?.SetAcceleration3D(projId, worldDir);
        }

        /// <summary>
        /// Live position lookup by ProjId, with the same IsServer / !IsNetworked
        /// routing as the rest of this class. ProjectileGuidanceTracker calls it
        /// every frame to compute a fresh direction to its target before calling
        /// SetHomingDirection2D/3D.
        /// </summary>
        public bool TryGetProjectilePosition2D(uint projId, out Vector2 pos)
        {
            if (IsServer && _authority != null && _authority.TryGetPosition2D(projId, out pos))
                return true;
            if (!IsNetworked && _localManager != null && _localManager.TryGetPosition2D(projId, out pos))
                return true;
            pos = default;
            return false;
        }

        public bool TryGetProjectilePosition3D(uint projId, out Vector3 pos)
        {
            if (IsServer && _authority != null && _authority.TryGetPosition3D(projId, out pos))
                return true;
            if (!IsNetworked && _localManager != null && _localManager.TryGetPosition3D(projId, out pos))
                return true;
            pos = default;
            return false;
        }

        #endregion

        #region Debug

        [ContextMenu("Log System Status")]
        private void LogStatus()
        {
            MID_Logger.LogInfo(_logLevel,
                $"=== MID_MasterProjectileSystem ===\n" +
                $"Initialised:  {_initialised}\n" +
                $"Networked:    {IsNetworked}\n" +
                $"IsServer:     {IsServer}\n" +
                $"IsHost:       {IsHostMode}\n" +
                $"Active2D:     {_authority?.ActiveCount2D ?? _localManager?.ActiveCount2D ?? 0}\n" +
                $"Active3D:     {_authority?.ActiveCount3D ?? _localManager?.ActiveCount3D ?? 0}\n" +
                $"Registry:     {_registry?.Count ?? 0} configs\n" +
                $"CanAcceptTargets: {CanAcceptTargets}\n" +
                $"Authority targets  2D/3D circle {_authority?.TargetCount2D ?? -1}/{_authority?.TargetCount3D ?? -1}, shape {_authority?.ShapeCount2D ?? -1}/{_authority?.ShapeCount3D ?? -1}\n" +
                $"Local targets      2D/3D circle {_localManager?.TargetCount2D ?? -1}/{_localManager?.TargetCount3D ?? -1}, shape {_localManager?.ShapeCount2D ?? -1}/{_localManager?.ShapeCount3D ?? -1}\n" +
                $"LocalManager: {(_localManager != null ? "OK" : "NULL")}",
                nameof(MID_MasterProjectileSystem));
        }

        #endregion
    }
}
