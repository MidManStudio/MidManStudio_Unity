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
                _localManager = FindObjectOfType<LocalProjectileManager>();

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
            }

            _initialised = true;

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
        public void Fire(
            ushort configId, SpawnPoint[] spawnPoints, int count, WeaponFireContext context,
            ushort patternId = 0, float spreadDeg = 0f,
            Vector3 baseDirection = default, bool patternIs3D = false)
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
                    FireLocal(configId, spawnPoints, count, context, cfg);
                    break;

                case SimulationMode.RustSim:
                    FireNetworkedSim(configId, spawnPoints, count, context, cfg,
                        patternId, spreadDeg, baseDirection, patternIs3D);
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
            WeaponFireContext context, ProjectileConfigSO cfg)
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
                    (uint)context.OwnerMidId, context.DamageMultiplier);
            else
                _localManager.Spawn2D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier);
        }

        private void FireNetworkedSim(
            ushort configId, SpawnPoint[] spawnPoints, int count,
            WeaponFireContext context, ProjectileConfigSO cfg,
            ushort patternId, float spreadDeg, Vector3 baseDirection, bool patternIs3D)
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
                ClientFireTick         = _networkBridge.GetServerTick()
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
        /// BUG FIX ("physics projectile fires twice on client, once on host"):
        /// this always spawned server-owned (plain NetworkObject.Spawn()),
        /// regardless of who actually fired it. PhysicsProjectileBase.OnNetworkSpawn()
        /// only calls ClientPredictionManager.OnRealPhysicsProjectileSpawned() —
        /// which kills the firing client's local predicted visual spawned by
        /// NetworkedDimensionPlayer.FirePhysics's SpawnLocalPhysicsVisual — when
        /// IsOwner is true on that client's copy of this object. Since ownership
        /// was never transferred, IsOwner was only ever true on the server/host,
        /// so a firing client's predicted visual was never reconciled/killed and
        /// lived out its full Lifetime alongside the real, server-replicated one:
        /// two visible projectiles for one shot, client-side only (the host never
        /// spawns a local prediction to begin with — see FirePhysics's
        /// `if (!IsServer)` guard around SpawnLocalPhysicsVisual — so it was never
        /// affected).
        ///
        /// Pass the actual firing client's NGO id (ServerRpcParams.Receive.SenderClientId
        /// from FirePhysicsProjectileServerRpc) to spawn with SpawnWithOwnership
        /// instead of Spawn. This only fixes the IsOwner check — it does NOT hand
        /// authority over the projectile's transform to the client:
        /// NetworkProjectileBase (the NetworkTransform subclass every physics
        /// projectile uses) never overrides OnIsServerAuthoritative(), so position
        /// sync stays server-authoritative regardless of who owns the object.
        ///
        /// Default (ulong.MaxValue) preserves the old server-owned Spawn() for
        /// any caller with no specific client to own it (e.g. the fully
        /// offline/non-networked local-fire path).
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

            // FIX: set the visual config id BEFORE Spawn() rather than after.
            // _visualConfigId is now a NetworkVariable (see PhysicsProjectileBase) —
            // writing it before Spawn() means the very first replicated state a
            // remote client receives already carries the correct value, instead of
            // spawning with the prefab default and only picking up the real config
            // a tick later via OnValueChanged. Safe either way (OnValueChanged
            // still covers the post-spawn case for any other caller), but this
            // avoids a one-frame flash of the wrong visual.
            if (physicsBase != null && configId != 0)
                physicsBase.SetVisualConfigId(configId);

            // See firingClientId doc above: SpawnWithOwnership makes IsOwner true
            // on the actual firing client so its predicted visual gets reconciled
            // away instead of doubling up with this real one.
            if (firingClientId != ulong.MaxValue)
                netObj.SpawnWithOwnership(firingClientId);
            else
                netObj.Spawn();

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

        public void RegisterRaycastFire(
            RaycastFireResult result, ushort configId, WeaponFireContext context)
        {
            // TEMP DIAG: confirms RegisterRaycastFire is even being reached, and
            // with what state, before anything else has a chance to bail out
            // silently. Remove once the client-can't-see-own-raycast issue is
            // resolved.
            Debug.LogError(
                $"[RAYDIAG] RegisterRaycastFire ENTER cfg={configId} " +
                $"initialised={_initialised} cfgFound={_registry?.Get(configId) != null} " +
                $"isNetworked={IsNetworked} isServer={IsServer}");

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

        public void RegisterTarget3D(in CollisionTarget3D target, int unityLayer = 0)
        {
            if (IsServer)       _authority?.RegisterTarget3D(target, unityLayer);
            if (!IsNetworked)   _localManager?.RegisterTarget3D(target, unityLayer);
        }

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

        public void SetHomingDirection2D(uint projId, Vector2 worldDir)
        {
            if (IsServer || !IsNetworked) _authority?.SetAcceleration2D(projId, worldDir);
        }

        public void SetHomingDirection3D(uint projId, Vector3 worldDir)
        {
            if (IsServer || !IsNetworked) _authority?.SetAcceleration3D(projId, worldDir);
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
