// MID_MasterProjectileSystem.cs
// FIXES:
//   + RegisterTarget2D/3D — also route to _localManager when !IsNetworked.
//     Previously only went to _authority (ServerProjectileAuthority) which
//     is null/inactive in offline mode, so LocalProjectileManager._targets2D
//     was always empty → no collisions in offline/LocalOnly mode.
//   + DeactivateTarget2D/3D — same offline routing fix.
//   + ClearAllTargets — same offline routing fix.
//   + Fire() — correctly handles Is3D configs: LocalOnly routes to FireLocal
//     which calls Spawn3D; Networked routes to FireNetworkedSim regardless of
//     config's Is3D (the server-side FireServerRpc routes the buffer correctly).
//   + Added IsHostMode helper — distinguishes dedicated server from host.

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
        [Tooltip("Force offline regardless of NetworkManager state.")]
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

        /// <summary>
        /// True when this machine is both server and client (host mode).
        /// Host machines run server logic AND need client-side visuals.
        /// </summary>
        public bool IsHostMode =>
            NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsServer
            && NetworkManager.Singleton.IsClient;

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

            try
            {
                ProjectileLib.ValidateStructSizes();
            }
            catch (InvalidOperationException ex)
            {
                MID_Logger.LogError(_logLevel,
                    $"Fatal struct size mismatch: {ex.Message}",
                    nameof(MID_MasterProjectileSystem));
                enabled = false;
                return;
            }

            BatchSpawnHelper.Initialise();

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
                $"Initialised. Mode: {(IsNetworked ? "Networked" : "Offline")}",
                nameof(MID_MasterProjectileSystem));
        }

        private void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            ProjectileLib.clear_movement_params();
        }

        #endregion

        #region Public API — Identity

        public void SetLocalPlayerMidId(ulong midId)
        {
            _predictionManager?.SetLocalPlayerMidId(midId);
        }

        #endregion

        #region Public API — Fire

        /// <summary>
        /// Primary entry point for all projectile fire events.
        /// Routes to the correct sub-system based on SimulationMode.
        ///
        /// NOTE on Is3D configs in LocalOnly mode:
        ///   If cfg.Is3D is true, FireLocal calls _localManager.Spawn3D().
        ///   Ensure ProjectileRenderer3D is assigned to LocalProjectileManager
        ///   in the inspector or 3D projectiles will tick but not render.
        ///
        /// PhysicsObject mode: call SpawnPhysicsProjectile() directly from weapon.
        /// </summary>
        public void Fire(
            ushort           configId,
            SpawnPoint[]     spawnPoints,
            int              count,
            WeaponFireContext context)
        {
            if (!_initialised)
            {
                MID_Logger.LogWarning(_logLevel,
                    "Fire() called before initialisation.",
                    nameof(MID_MasterProjectileSystem));
                return;
            }

            var cfg = _registry != null ? _registry.Get(configId) : null;
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Fire(): configId {configId} not registered.",
                    nameof(MID_MasterProjectileSystem));
                return;
            }

            var routing = ProjectileTypeRouter.Route(cfg, context);

            MID_Logger.LogDebug(_logLevel,
                $"Fire: configId={configId} mode={routing.Mode} is3D={cfg.Is3D} count={count}",
                nameof(MID_MasterProjectileSystem));

            switch (routing.Mode)
            {
                case SimulationMode.LocalOnly:
                    FireLocal(configId, spawnPoints, count, context, cfg);
                    break;

                case SimulationMode.RustSim2D:
                case SimulationMode.RustSim3D:
                    FireNetworkedSim(configId, spawnPoints, count, context, cfg, routing);
                    break;

                case SimulationMode.Raycast:
                    MID_Logger.LogWarning(_logLevel,
                        "Fire() called with Raycast mode — use RegisterRaycastFire() instead.",
                        nameof(MID_MasterProjectileSystem));
                    break;

                case SimulationMode.PhysicsObject:
                    MID_Logger.LogWarning(_logLevel,
                        "PhysicsObject mode — call SpawnPhysicsProjectile() from your weapon script.",
                        nameof(MID_MasterProjectileSystem));
                    break;
            }
        }

        // ── Offline ────────────────────────────────────────────────────────────

        private void FireLocal(
            ushort configId, SpawnPoint[] spawnPoints, int count,
            WeaponFireContext context, ProjectileConfigSO cfg)
        {
            if (_localManager == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "FireLocal: LocalProjectileManager not assigned.",
                    nameof(MID_MasterProjectileSystem));
                return;
            }

            // Route based on the config's own Is3D flag — NOT the player's mode.
            // The player mode determines which configId was selected; the config
            // itself determines which Rust buffer and renderer to use.
            if (cfg.Is3D)
                _localManager.Spawn3D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier);
            else
                _localManager.Spawn2D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier);
        }

        // ── Networked Rust sim ─────────────────────────────────────────────────

        private void FireNetworkedSim(
            ushort configId, SpawnPoint[] spawnPoints, int count,
            WeaponFireContext context, ProjectileConfigSO cfg, RoutingResult routing)
        {
            if (_networkBridge == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "FireNetworkedSim: NetworkBridge not assigned.",
                    nameof(MID_MasterProjectileSystem));
                return;
            }

            Vector3 origin    = count > 0 ? spawnPoints[0].Origin    : Vector3.zero;
            Vector3 direction = count > 0 ? spawnPoints[0].Direction : Vector3.forward;
            float   speed     = count > 0 ? spawnPoints[0].Speed     : cfg.ResolveSpeed();

            var request = new ProjectileFireRequest
            {
                ConfigId               = configId,
                Origin                 = origin,
                Direction              = direction,
                Speed                  = speed,
                RngSeed                = (uint)UnityEngine.Random.Range(0, int.MaxValue),
                ProjectileCount        = (byte)Mathf.Min(count, 255),
                OwnerMidId             = context.OwnerMidId,
                FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                IsBotOwner             = context.IsBotOwner,
                WeaponLevel            = context.WeaponLevel,
                DamageMultiplier       = context.DamageMultiplier,
                ClientFireTick         = _networkBridge.GetServerTick()
            };

            _networkBridge.FireServerRpc(request);
        }

        #endregion

        #region Public API — Physics Network Object Pool

        public NetworkObject SpawnPhysicsProjectile(
            PoolableNetworkObjectType type,
            Vector3                   position,
            Quaternion                rotation)
        {
            if (!IsServer)
            {
                MID_Logger.LogWarning(_logLevel,
                    "SpawnPhysicsProjectile must be called on the server.",
                    nameof(MID_MasterProjectileSystem));
                return null;
            }

            if (_networkObjectPool == null)
            {
                MID_Logger.LogError(_logLevel,
                    "SpawnPhysicsProjectile: MID_NetworkObjectPool not assigned.",
                    nameof(MID_MasterProjectileSystem));
                return null;
            }

            var netObj = _networkObjectPool.GetNetworkObject(type, position, rotation);
            if (netObj == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"SpawnPhysicsProjectile: pool returned null for type {type}.",
                    nameof(MID_MasterProjectileSystem));
                return null;
            }

            netObj.Spawn();
            return netObj;
        }

        public void ReturnPhysicsProjectile(
            NetworkObject             netObj,
            PoolableNetworkObjectType type)
        {
            if (_networkObjectPool == null || netObj == null) return;
            _networkObjectPool.ReturnNetworkObject(netObj, type);
        }

        #endregion

        #region Public API — Raycast

        public void RegisterRaycastFire(
            RaycastFireResult result,
            ushort            configId,
            WeaponFireContext  context)
        {
            if (!_initialised) return;

            var cfg = _registry?.Get(configId);
            if (cfg == null) return;

            if (!IsNetworked)
            {
                _raycastHandler?.OfflineHandleFire(
                    result, configId,
                    (uint)context.OwnerMidId,
                    context.DamageMultiplier);
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
                    result.HitPoint,
                    result.DidHit,
                    result.IsHeadshot,
                    result.HitTargetNetworkId);

                // Immediate local cosmetic for shooter
                _raycastHandler?.OfflineHandleFire(
                    result, configId, (uint)context.OwnerMidId, 1f);
            }
        }

        #endregion

        #region Public API — Targets
        // FIX: each method now also routes to _localManager when !IsNetworked.
        // Previously only _authority was called; offline targets were never
        // registered in LocalProjectileManager, so local collision never worked.

        public void RegisterTarget2D(in CollisionTarget target)
        {
            if (IsServer)     _authority?.RegisterTarget2D(target);
            if (!IsNetworked) _localManager?.RegisterTarget2D(target);
        }

        public void RegisterTarget3D(in CollisionTarget3D target)
        {
            if (IsServer)     _authority?.RegisterTarget3D(target);
            if (!IsNetworked) _localManager?.RegisterTarget3D(target);
        }

        public void DeactivateTarget2D(uint targetId)
        {
            if (IsServer)     _authority?.DeactivateTarget2D(targetId);
            if (!IsNetworked) _localManager?.DeactivateTarget2D(targetId);
        }

        public void DeactivateTarget3D(uint targetId)
        {
            if (IsServer)     _authority?.DeactivateTarget3D(targetId);
            if (!IsNetworked) _localManager?.DeactivateTarget3D(targetId);
        }

        public void ClearAllTargets()
        {
            if (IsServer)     _authority?.ClearAllTargets();
            if (!IsNetworked) _localManager?.ClearAllTargets();
        }

        #endregion

        #region Public API — State (reconciliation)

        public int SaveState2D(byte[] buf)    => _authority?.SaveState2D(buf) ?? 0;
        public int RestoreState2D(byte[] buf, int byteCount)
            => _authority?.RestoreState2D(buf, byteCount) ?? 0;

        #endregion

        #region Public API — Guided Homing

        public void SetHomingDirection2D(uint projId, Vector2 worldDir)
        {
            if (IsServer || !IsNetworked)
                _authority?.SetAcceleration2D(projId, worldDir);
        }

        public void SetHomingDirection3D(uint projId, Vector3 worldDir)
        {
            if (IsServer || !IsNetworked)
                _authority?.SetAcceleration3D(projId, worldDir);
        }

        #endregion

        #region Debug

        [ContextMenu("Log System Status")]
        private void LogStatus()
        {
            MID_Logger.LogInfo(_logLevel,
                $"=== MID_MasterProjectileSystem ===\n" +
                $"Initialised:   {_initialised}\n" +
                $"Networked:     {IsNetworked}\n" +
                $"Is Server:     {IsServer}\n" +
                $"Is Host:       {IsHostMode}\n" +
                $"Active 2D:     {_authority?.ActiveCount2D ?? _localManager?.ActiveCount2D ?? 0}\n" +
                $"Active 3D:     {_authority?.ActiveCount3D ?? _localManager?.ActiveCount3D ?? 0}\n" +
                $"Registry:      {_registry?.Count ?? 0} configs\n" +
                $"NetObjPool:    {(_networkObjectPool != null ? "assigned" : "not assigned")}",
                nameof(MID_MasterProjectileSystem));
        }

        #endregion
    }
}
