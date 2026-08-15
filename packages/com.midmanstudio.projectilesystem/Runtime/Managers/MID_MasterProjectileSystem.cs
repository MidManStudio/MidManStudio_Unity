//   FireNetworkedSim spawns immediately into LocalProjectileManager for the
//   firing client (non-server). This gives zero-latency visual feedback using the
//   same Rust sim + GPU instanced rendering path as the host — no pool objects.
//
//   RegisterTarget2D/3D and Deactivate variants no longer register targets in
//   LocalProjectileManager when networked. This keeps LocalProjectileManager's
//   target buffers empty in networked mode so collision detection is automatically
//   skipped (FixedUpdate already guards on _targetCountXD > 0). The server's
//   ServerProjectileAuthority handles all authoritative collision.
//
//   SetLocalPlayerMidId now also sets the ID on MID_ProjectileNetworkBridge so
//   SpawnConfirmedClientRpc can distinguish firing client from other clients.

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
    /// Unified payload for MID_MasterProjectileSystem.OnRustSimHit — see that
    /// event's own doc comment for the full picture. Fields come from one of
    /// two different underlying sources depending on mode (LocalOnly vs
    /// Networked); TargetNetworkObjectId and RawTargetId are mutually
    /// exclusive in practice, not a bug:
    ///   LocalOnly (offline):  RawTargetId populated, TargetNetworkObjectId = 0
    ///   Networked:            TargetNetworkObjectId populated, RawTargetId = 0
    /// (Networked mode's underlying HitConfirmation genuinely doesn't carry
    /// the raw RustSim numeric target_id to clients, only a resolvable
    /// NetworkObjectId — arguably the more directly useful of the two anyway.)
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
        /// READINESS EVENT ("timing issue... maybe make an event fired once
        /// mid master projectile system ready"): static, not instance-scoped —
        /// deliberately, so anything can subscribe via
        /// MID_MasterProjectileSystem.OnSystemReady += Handler BEFORE the
        /// instance even exists yet, which is exactly the situation a
        /// scene-present object racing this system's own Awake() is in.
        ///
        /// DYNAMICALLY SPAWNED OBJECTS, BOTH NET AND NON-NET: an event alone
        /// only helps subscribers that attach BEFORE it fires — anything
        /// spawned later (after this system is already initialised) would
        /// never see it and hang forever waiting. That's why IsReady exists
        /// as a static property alongside the event: a consumer should always
        /// check `if (IsReady) { RegisterNow(); } else { OnSystemReady +=
        /// Handler; }` — RustSimTargetRegistrar/RustSimCustomShapeAuthoring's
        /// own RegisterNow() do exactly this. Checked-then-subscribed, not
        /// subscribed-only, is what makes both a scene-present object racing
        /// startup AND a dynamically Instantiate()'d one spawned five minutes
        /// into a session work correctly through the same code path.
        ///
        /// Kept alongside — not replacing — the existing per-tick retry in
        /// RustSimTargetRegistrar/RustSimCustomShapeAuthoring's Update/
        /// FixedUpdate as a second, independent safety net.
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

        public ServerProjectileAuthority      GetAuthority()         => _authority;
        public MID_ProjectileNetworkBridge    GetBridge()            => _networkBridge;
        public RaycastProjectileHandler       GetRaycastHandler()    => _raycastHandler;

        /// <summary>
        /// For physics/raycast pool visuals that still need manual prediction management.
        /// The standard Rust sim path no longer requires this — it's automatic.
        /// </summary>
        public ClientPredictionManager        GetPredictionManager() => _predictionManager;

        public int GetBridgeTick() => _networkBridge?.GetServerTick() ?? 0;

        /// <summary>
        /// BUG FIX (physics projectiles not damaging anything): PhysicsProjectileBase
        /// fires OnHitServerConfirmed correctly on every real collision — that part
        /// always worked. But that event lives on each individual spawned instance,
        /// not on a session-wide singleton the way RustSimAdapter.OnProjectileHit and
        /// RaycastProjectileHandler.OnServerHitConfirmed do, so there was nothing to
        /// subscribe to once at session start and nobody ever heard it. This
        /// re-raises every spawned instance's hit through one session-wide event —
        /// SpawnPhysicsProjectile wires each new/reused pooled instance into it
        /// automatically. Subscribe here exactly like the other two hit sources.
        /// </summary>
        public event Action<ProjectileHitPayload> OnPhysicsHit;

        private void RelayPhysicsHit(ProjectileHitPayload payload) => OnPhysicsHit?.Invoke(payload);

        /// <summary>
        /// UNIFIED RUSTSIM HIT EVENT ("how do I make use of when a projectile
        /// collides, for RustSim?"): before this, OnPhysicsHit existed for
        /// physics projectiles but nothing equivalent existed for RustSim ones —
        /// the actual hit signals were scattered across three different places
        /// (LocalProjectileManager.OnHit for offline, MID_ProjectileNetworkBridge.
        /// OnHitConfirmedLocal for networked clients, ServerProjectileAuthority.
        /// Adapter.OnProjectileHit for the server's own authoritative copy) with
        /// no single place to subscribe. This relays the first two into one
        /// event, the same "subscribe once at session start" shape OnPhysicsHit
        /// already has.
        ///
        /// WHY NOT THE THIRD ONE TOO: Adapter.OnProjectileHit is where real
        /// damage should be authoritatively applied server-side — it fires
        /// BEFORE HitConfirmedClientRpc is even sent, is server-only, and (on
        /// a host) would double-fire alongside this event for the same hit
        /// (the host is both server and client — it gets Adapter.OnProjectileHit
        /// AND OnHitConfirmedLocal for its own hits). Keeping them separate
        /// means: use OnRustSimHit for "something visibly happened, react to
        /// it" (VFX, sound, UI feedback — same role PlayImpact already plays,
        /// which is invoked from the exact same OnHitConfirmedLocal source
        /// this relays), and Adapter.OnProjectileHit specifically for
        /// server-authoritative damage/game-state logic.
        ///
        /// TargetNetworkObjectId vs RawTargetId — only one is populated
        /// depending on mode, see RustSimHitPayload's own field docs; this
        /// isn't a bug, the two underlying sources genuinely carry different
        /// target references.
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

            // NOTE: ProjectileLib.ValidateStructSizes() now throws
            // InvalidOperationException both for the original "struct size
            // mismatch" case AND for "native library failed to load on this
            // platform/architecture" (previously that second case threw an
            // uncaught DllNotFoundException here, which meant _initialised
            // never got set to true and everything below — BatchSpawnHelper
            // init, transport config, _networkBridge wiring — silently never
            // ran, with no error surfaced at this call site at all).
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
        /// Sets the local player MID ID on both ClientPredictionManager (for physics
        /// visuals) and MID_ProjectileNetworkBridge (to identify firing client in RPCs).
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
        /// RUSTSIM GUIDED FIX ("guided doesn't work — dead wire"): see
        /// LocalProjectileManager.Spawn2D's matching parameter doc for the full
        /// story. Threaded through both branches below — LocalProjectileManager.
        /// Spawn2D/3D directly for LocalOnly, and as a NetworkObjectId over the
        /// wire (resolved back to a Transform server-side) for RustSim. Null
        /// (default) is a complete no-op, identical to calling Fire() before this
        /// fix existed.
        /// </param>
        public void Fire(
            ushort configId, SpawnPoint[] spawnPoints, int count, WeaponFireContext context,
            ushort patternId = 0, float spreadDeg = 0f,
            Vector3 baseDirection = default, bool patternIs3D = false,
            Transform guidedTarget = null)
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
                        patternId, spreadDeg, baseDirection, patternIs3D, guidedTarget);
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
            Transform guidedTarget = null)
        {
            if (_networkBridge == null) return;

            float resolvedSpeed = count > 0 && spawnPoints[0].Speed > 0f
                ? spawnPoints[0].Speed
                : cfg.ResolveSpeed();

            // BUG FIX: this used to be spawnPoints[0].Direction, which for any
            // pattern/spread with more than one pellet is already offset by that
            // pellet's own angle within the pattern (pellet 0 sits at the start of
            // the arc, not the center). Feeding that back in as the regeneration
            // base made every recipient re-apply the full pattern on top of an
            // already-rotated direction — the whole shot skews off at an angle.
            // baseDirection is the actual raw aim direction, pre-pattern.
            Vector3 resolvedBaseDir = baseDirection.sqrMagnitude > 0.0001f
                ? baseDirection.normalized
                : (count > 0 ? spawnPoints[0].Direction : Vector3.forward);

            // No more ExtraDirections/RngSeed packing here — patternId + spreadDeg
            // are enough for every recipient to regenerate the identical pellet set
            // via ProjectileDirectionResolver.Resolve(). spawnPoints itself is only
            // used below, locally, for this client's own instant predicted visual.
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
                OwnerMidId             = context.OwnerMidId,
                FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                IsBotOwner             = context.IsBotOwner,
                WeaponLevel            = context.WeaponLevel,
                DamageMultiplier       = context.DamageMultiplier,
                ClientFireTick         = _networkBridge.GetServerTick(),
                // RUSTSIM GUIDED FIX: resolved here, not left as a raw Transform —
                // a Transform reference means nothing across the wire. 0 = no
                // guided target, same as every fire request before this fix.
                TargetNetworkObjectId  = guidedTarget != null
                    ? (guidedTarget.GetComponentInParent<NetworkObject>()?.NetworkObjectId ?? 0UL)
                    : 0UL
            };

            // ARCHITECTURE: Firing client immediately spawns into their own Rust sim buffer.
            // Renders via ProjectileRenderer2D/3D — same GPU instanced path as the host.
            // No pool objects, no C# prediction math. The temp IDs are linked to real server
            // IDs when SpawnConfirmedClientRpc arrives via LinkNetworkProjectileBatch.
            // Host is excluded — it renders from ServerProjectileAuthority's buffer.
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

        /// <param name="configId">
        /// The ProjectileConfigSO this spawn should visually represent — gets
        /// pushed into the spawned instance's PhysicsProjectileBase.SetVisualConfigId
        /// immediately after spawn. Previously there was no way to pass this at
        /// all; every instance used whatever was hardcoded in the prefab's
        /// Inspector (defaulting to 0), which is why the real sprite never showed
        /// up regardless of which config was actually fired. Pass 0 to
        /// deliberately keep the prefab's own Inspector default (back-compat).
        /// </param>
        /// <param name="firingClientId">
        /// REVISED FIX ("physics projectile fires twice on client — a split
        /// second after the firing client's own shot, another batch shows up"):
        /// the previous fix (SpawnWithOwnership + PhysicsProjectileBase.
        /// OnNetworkSpawn calling ClientPredictionManager.
        /// OnRealPhysicsProjectileSpawned when IsOwner) was solving the wrong
        /// problem. The firing client was never supposed to see this real,
        /// server-simulated object at all — it already has its own local
        /// prediction ghost (NetworkedDimensionPlayer.FirePhysics's
        /// SpawnLocalPhysicsVisual), which lives out its own MaxLifetime and
        /// glides to the confirmed hit point on its own via
        /// HitConfirmedClientRpc -> ClientPredictionManager.OnHitConfirmed —
        /// that ClientRpc is a plain broadcast with no target list, so it
        /// already reaches the firer. The ghost never needed the real object
        /// to arrive; reconciling against it was actively the bug, since
        /// under any latency/ordering variance both ended up visible at once.
        ///
        /// Fix: this object is server-owned as always, but gets NetworkHidden
        /// from the firing client specifically, so it's never replicated to
        /// them at all — only to every OTHER connected client, which is who
        /// actually needs to see the real simulated projectile. The host is
        /// exempt: FirePhysics's `if (!IsServer)` guard means the host never
        /// spawns a prediction ghost for its own shot in the first place, so
        /// it still needs to see this real object normally.
        ///
        /// Default (ulong.MaxValue) hides from nobody — for the fully
        /// offline/non-networked local-fire path, or any caller with no
        /// specific firing client to exclude.
        /// </param>
        /// <summary>
        /// GUIDED TARGETING ("works but only for the test targets in the test
        /// game — why?"): this method itself never touches Guided targeting at
        /// all — it doesn't even call InitialiseProjectile (see below). Guided
        /// isn't test-scene-specific or hardcoded to anything; it works from
        /// ANY caller, the same way for everyone, but requires two calls in a
        /// specific order:
        ///
        ///   var netObj = system.SpawnPhysicsProjectile(...);
        ///   var proj = netObj.GetComponent&lt;PhysicsProjectileBase&gt;();
        ///   proj.SetOwnerContext(...);
        ///   proj.InitialiseProjectile(...);      // MUST come before SetGuidedTarget
        ///   proj.SetGuidedTarget(yourRealTarget); // MUST come after InitialiseProjectile
        ///
        /// InitialiseProjectile resets the guided-target state on every fresh
        /// launch (see its own doc comment) — call SetGuidedTarget before that
        /// and it gets silently wiped a moment later. That's also why this
        /// method doesn't accept a guidedTarget parameter itself: it happens
        /// BEFORE InitialiseProjectile is even called (the caller calls that
        /// afterward), so accepting one here would just be quietly wrong.
        ///
        /// Target SELECTION (who to lock onto — nearest enemy, current
        /// reticle target, whatever) is deliberately left entirely to the
        /// caller; only target ACQUISITION mechanics (the SetGuidedTarget/
        /// RegisterGuidedTarget2D/3D call itself) are this package's job.
        /// NetworkedDimensionPlayer.SpawnPhysicsProjectileLocal and
        /// MID_ProjectileNetworkBridge.FirePhysicsProjectileServerRpc are
        /// working reference implementations of exactly this pattern — the
        /// ONLY reason it currently "only works for test targets" is that
        /// those two are the only call sites anyone has wired a real target
        /// into so far, not because anything is hardcoded to them.
        /// </summary>
        public NetworkObject SpawnPhysicsProjectile(
            PoolableNetworkObjectType type, Vector3 position, Quaternion rotation,
            ushort configId = 0, ulong firingClientId = ulong.MaxValue)
        {
            if (!IsServer) return null;
            if (_networkObjectPool == null) return null;
            var netObj = _networkObjectPool.GetNetworkObject(type, position, rotation);
            if (netObj == null) return null;

            var physicsBase = netObj.GetComponent<PhysicsProjectileBase>();

        

            // Always server-owned now — see firingClientId doc above. Ownership
            // is no longer how the firing client is told apart; NetworkHide below
            // does that instead (and NetworkHide cannot target an object's own
            // owner anyway, which is exactly why SpawnWithOwnership had to go).
            netObj.Spawn();

            if (physicsBase != null)
                physicsBase.SetVisualConfigId(configId);

            bool firerIsHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost
                && firingClientId == NetworkManager.Singleton.LocalClientId;

            if (firingClientId != ulong.MaxValue && !firerIsHost)
                netObj.NetworkHide(firingClientId);

            if (physicsBase != null)
            {
                // Safe against pooled reuse: this instance may have already been
                // subscribed from a previous life in the pool. Unsubscribe first
                // so we never end up with more than one subscription on it.
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
        /// PATTERN SUPPORT for raycast fire (networked path only — offline/local
        /// multi-pellet raycasts are simpler to just loop through the existing
        /// single-shot RegisterRaycastFire from the caller, which is exactly what
        /// NetworkedDimensionPlayer.FireRaycast does; no server round-trip exists
        /// to optimize away in that case, so there's nothing this method would
        /// add). Routes to RaycastProjectileHandler.ServerHandleFirePattern
        /// directly when this instance IS the server/host, or to
        /// RaycastPatternFireServerRpc when it's a remote client — same
        /// IsServer branch RegisterRaycastFire already uses.
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

            // Same defensive shape as RegisterRaycastFire: wrap the RPC send so a
            // throw in it can't skip the local fallback below, and log if it does
            // (that would mean the server never got this fire event at all, which
            // is bigger than a missing visual).
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

            // FIX: this was the missing counterpart to RegisterRaycastFire's
            // defensive OfflineHandleFire call — ServerHandleFirePattern's
            // ClientRpc deliberately excludes the firing client
            // (BuildTargetList(senderClientId)) on the assumption a local
            // prediction already exists for it, which was true for the
            // single-ray path and never true here. Without this, a client
            // firing a pattern shot was excluded from the RPC and had nothing
            // local to fall back on — nothing rendered for their own shot.
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
                // TEMP DIAG / DEFENSIVE FIX: this RPC send is wrapped in its own
                // try/catch now. If ProjectileFireRequest's NetworkSerialize (or
                // anything else in the send path) throws on this client, an
                // uncaught exception here would abort this whole else-branch and
                // skip OfflineHandleFire below it — which would explain a client
                // never showing their own raycast visual while everything else
                // (relayed shots from other players) keeps working fine, since
                // those go through a completely separate receive path. Logging
                // whatever exception (if any) shows up here, and moving
                // OfflineHandleFire outside the try so the client's own visual
                // shows regardless of whether the RPC send succeeds.
                //
                // IMPORTANT: if this DOES catch something, that's bigger than a
                // missing visual — it means the server never received this fire
                // event at all, so hit registration/damage for this client's
                // shots is likely broken too, and the actual exception needs a
                // real fix (not just this safety net). Remove this try/catch
                // once that's confirmed either way.
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
            // FIX: Do NOT register in localManager when networked.
            // Keeping localManager target buffer empty in networked mode means
            // FixedUpdate's collision check is skipped automatically (_targetCount2D == 0).
            // The server handles all authoritative collision via ServerProjectileAuthority.
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
        // FIX: SetHomingDirection2D/3D used to write to _authority only, via
        // "if (IsServer || !IsNetworked)" — a single combined condition. But
        // every OTHER per-projectile call in this class (see RegisterTarget2D
        // just above) treats IsServer and !IsNetworked as two SEPARATE targets
        // to route to, because they're two different buffers: _authority holds
        // server-authoritative/networked projectiles, _localManager holds
        // non-networked ones. A non-networked game (!IsNetworked, IsServer
        // always false with no NetworkManager) has all of its projectiles in
        // _localManager — so this always wrote into the wrong, empty buffer,
        // meaning SetHomingDirection2D/3D was a no-op offline. Split to match
        // RegisterTarget2D/3D's own pattern below.

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
        /// Live position lookup by ProjId. Added for ProjectileGuidanceTracker,
        /// which needs a projectile's current position every frame to compute
        /// a fresh direction-to-target before calling SetHomingDirection2D/3D.
        /// Same IsServer/!IsNetworked routing as everything else here.
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
                $"LocalManager: {(_localManager != null ? "OK" : "NULL")}",
                nameof(MID_MasterProjectileSystem));
        }

        #endregion
    }
}
