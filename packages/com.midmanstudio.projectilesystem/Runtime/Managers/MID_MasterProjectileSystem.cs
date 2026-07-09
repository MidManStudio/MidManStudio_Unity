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
        public void Fire(
            ushort configId, SpawnPoint[] spawnPoints, int count, WeaponFireContext context,
            ushort patternId = 0, float spreadDeg = 0f)
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
                    FireNetworkedSim(configId, spawnPoints, count, context, cfg, patternId, spreadDeg);
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
            ushort patternId, float spreadDeg)
        {
            if (_networkBridge == null) return;

            float resolvedSpeed = count > 0 && spawnPoints[0].Speed > 0f
                ? spawnPoints[0].Speed
                : cfg.ResolveSpeed();

            // No more ExtraDirections/RngSeed packing here — patternId + spreadDeg
            // are enough for every recipient to regenerate the identical pellet set
            // via ProjectileDirectionResolver.Resolve(). spawnPoints itself is only
            // used below, locally, for this client's own instant predicted visual.
            var request = new ProjectileFireRequest
            {
                ConfigId               = configId,
                Origin                 = count > 0 ? spawnPoints[0].Origin    : Vector3.zero,
                Direction              = count > 0 ? spawnPoints[0].Direction : Vector3.forward,
                Speed                  = resolvedSpeed,
                ProjectileCount        = (byte)Mathf.Min(count, 255),
                PatternId              = patternId,
                SpreadDeg              = spreadDeg,
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

        public NetworkObject SpawnPhysicsProjectile(
            PoolableNetworkObjectType type, Vector3 position, Quaternion rotation)
        {
            if (!IsServer) return null;
            if (_networkObjectPool == null) return null;
            var netObj = _networkObjectPool.GetNetworkObject(type, position, rotation);
            if (netObj == null) return null;
            netObj.Spawn();
            return netObj;
        }

        public void ReturnPhysicsProjectile(NetworkObject netObj, PoolableNetworkObjectType type)
        {
            if (_networkObjectPool == null || netObj == null) return;
            _networkObjectPool.ReturnNetworkObject(netObj, type);
        }

        #endregion

        #region Public API — Raycast

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
