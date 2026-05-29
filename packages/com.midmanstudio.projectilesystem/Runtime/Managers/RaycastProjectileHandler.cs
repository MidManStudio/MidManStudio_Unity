// packages/com.midmanstudio.projectilesystem/Runtime/Managers/RaycastProjectileHandler.cs
//
// FIXES:
//   + SpawnVisualLocal now logs a clear warning when LocalObjectPool returns null
//     instead of silently doing nothing. This diagnoses missing pool entries fast.
//   + Impact FX and sound are skipped when the shot did NOT confirm a hit.
//     Previously HitConfirmedClientRpc always called PlayImpact even on miss,
//     playing particles at 200-unit empty space.
//   + OfflineHandleFire only spawns a visual when result.DidHit = true.
//     A miss visual still travels to the miss point but no impact FX plays.
//   + SpawnVisualClientRpc passes confirmedHit bool through to the client so
//     ActiveVisual knows whether to play impact FX on arrival.
//   + ActiveVisual stores PlayImpactOnArrival flag.
//   + ValidateHitServer: 3D path uses Physics.Raycast, 2D uses Physics2D.Raycast
//     (was already correct from previous session — retained as-is).
//   + ServerHandleFire takes explicit bool is3D parameter so the caller controls
//     which physics system to use, independent of config.Is3D.
//     This matches the new Raycast2D / Raycast3D PlayerShootMode split.
//   + All previous fixes retained.

using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Data;
using MidManStudio.Projectiles.Visuals;
using MidManStudio.Projectiles.Network;

namespace MidManStudio.Projectiles.Managers
{
    public struct RaycastFireResult
    {
        public Vector3 Origin;
        public Vector3 Direction;
        public Vector3 HitPoint;
        public bool    DidHit;
        public ulong   HitTargetNetworkId;
        public bool    IsHeadshot;
        /// <summary>
        /// True when the caller used Physics.Raycast (3D colliders).
        /// False when the caller used Physics2D.Raycast (2D colliders).
        /// Set by the weapon script — matches PlayerShootMode.Raycast2D/3D.
        /// </summary>
        public bool    Is3D;
    }

    public sealed class RaycastProjectileHandler : NetworkBehaviour
    {
        #region Configuration

        [Header("Visual")]
        [SerializeField] private float _visualTravelSpeed = 40f;
        [SerializeField] private PoolableObjectType _visualPoolType2D
            = PoolableObjectType.Projectile_Visual2D;
        [SerializeField] private PoolableObjectType _visualPoolType3D
            = PoolableObjectType.Projectile_Visual3D;

        [Header("Server Validation")]
        [Tooltip("Max world-unit discrepancy between client and server hit positions.")]
        [SerializeField] private float _hitValidationTolerance = 2f;
        [Tooltip("Layers the SERVER raycast can hit. Default -1 = Everything.\n" +
                 "Must include the layer(s) your targets are on.\n" +
                 "Used for both 2D and 3D server validation passes.")]
        [SerializeField] private LayerMask _serverRaycastLayers = -1;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        public event Action<ProjectileHitPayload> OnServerHitConfirmed;

        #endregion

        #region Active Visuals

        private sealed class ActiveVisual
        {
            public int               VisualId;
            public GameObject        Obj;
            public Vector3           Origin;
            public Vector3           HitPoint;
            public float             Speed;
            public ushort            ConfigId;
            public PoolableObjectType PoolType;
            /// <summary>True when a confirmed hit occurred — play impact on arrival.</summary>
            public bool              PlayImpactOnArrival;
        }

        private readonly List<ActiveVisual> _activeVisuals = new(64);
        private int _nextVisualId = 1;

        #endregion

        #region Server — Handle Fire

        /// <summary>
        /// Server-side fire handler. Called directly on server/host,
        /// or via RaycastFireServerRpc for clients.
        /// </summary>
        /// <param name="clientResult">Client-reported fire result.</param>
        /// <param name="context">Weapon context (owner, damage multiplier, etc.).</param>
        /// <param name="configId">Registered projectile config ID.</param>
        public void ServerHandleFire(
            RaycastFireResult clientResult,
            WeaponFireContext  context,
            ushort             configId)
        {
            if (!IsServer) return;

            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"ServerHandleFire: configId {configId} not registered.",
                    nameof(RaycastProjectileHandler));
                return;
            }

            bool    serverConfirmed = false;
            Vector3 serverHitPoint  = clientResult.HitPoint;
            ulong   serverTargetId  = 0;
            bool    serverHeadshot  = false;

            if (clientResult.DidHit)
            {
                // Use the Is3D flag from the fire result — set by the weapon script
                // to match whichever physics system it used (Raycast2D vs Raycast3D).
                serverConfirmed = ValidateHitServer(
                    clientResult,
                    clientResult.Is3D,
                    out serverHitPoint,
                    out serverTargetId,
                    out serverHeadshot);
            }

            if (serverConfirmed && serverTargetId != 0)
            {
                float damage = cfg.EvaluateDamage(0f);
                if (serverHeadshot) damage *= cfg.HeadshotMultiplier;
                bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                if (isCrit) damage *= cfg.CritMultiplier;
                damage *= context.DamageMultiplier;

                var gameData = BuildRaycastGameData(context, configId, cfg);
                var payload  = new ProjectileHitPayload
                {
                    ProjId                 = 0,
                    ConfigId               = configId,
                    Is3D                   = clientResult.Is3D,
                    TargetId               = (uint)serverTargetId,
                    Damage                 = damage,
                    IsHeadshot             = serverHeadshot,
                    IsCrit                 = isCrit,
                    HitPosition            = serverHitPoint,
                    OwnerMidId             = context.OwnerMidId,
                    FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                    IsBotOwner             = context.IsBotOwner,
                    WeaponLevel            = context.WeaponLevel,
                    GameData               = gameData
                };
                OnServerHitConfirmed?.Invoke(payload);

                MID_Logger.LogDebug(_logLevel,
                    $"ServerHandleFire: hit confirmed targetId={serverTargetId} " +
                    $"damage={damage:F1} is3D={clientResult.Is3D}",
                    nameof(RaycastProjectileHandler));
            }
            else if (clientResult.DidHit)
            {
                MID_Logger.LogDebug(_logLevel,
                    "ServerHandleFire: client reported hit but server could not validate.",
                    nameof(RaycastProjectileHandler));
            }

            // Spawn visual on all clients.
            // Pass confirmedHit so clients only play impact FX on actual hits.
            SpawnVisualClientRpc(
                clientResult.Origin,
                serverConfirmed ? serverHitPoint : clientResult.HitPoint,
                configId,
                confirmedHit: serverConfirmed && serverTargetId != 0,
                visualId: _nextVisualId++,
                is3D: clientResult.Is3D);
        }

        #endregion

        #region Server Validation

        /// <summary>
        /// Validates the client-reported hit on the server using the matching
        /// physics system (3D or 2D).
        /// </summary>
        private bool ValidateHitServer(
            RaycastFireResult clientResult,
            bool              is3D,
            out Vector3       serverHitPoint,
            out ulong         serverTargetId,
            out bool          serverHeadshot)
        {
            serverHitPoint = clientResult.HitPoint;
            serverTargetId = 0;
            serverHeadshot = false;

            if (is3D)
            {
                // 3D validation — targets need SphereCollider / CapsuleCollider etc.
                if (!Physics.Raycast(
                    clientResult.Origin,
                    clientResult.Direction,
                    out RaycastHit hit3D,
                    1000f,
                    _serverRaycastLayers))
                    return false;

                serverHitPoint = hit3D.point;

                float dist = Vector3.Distance(serverHitPoint, clientResult.HitPoint);
                if (dist > _hitValidationTolerance) return false;

                var netObj3D = hit3D.collider.GetComponentInParent<NetworkObject>();
                if (netObj3D != null) serverTargetId = netObj3D.NetworkObjectId;

                serverHeadshot = clientResult.IsHeadshot;
                return true;
            }
            else
            {
                // 2D validation — targets need CircleCollider2D / PolygonCollider2D etc.
                RaycastHit2D serverHit = Physics2D.Raycast(
                    clientResult.Origin,
                    clientResult.Direction,
                    1000f,
                    _serverRaycastLayers);

                if (!serverHit.collider) return false;

                serverHitPoint = serverHit.point;

                float dist = Vector3.Distance(serverHitPoint, clientResult.HitPoint);
                if (dist > _hitValidationTolerance) return false;

                var netObj2D = serverHit.collider.GetComponentInParent<NetworkObject>();
                if (netObj2D != null) serverTargetId = netObj2D.NetworkObjectId;

                serverHeadshot = clientResult.IsHeadshot;
                return true;
            }
        }

        #endregion

        #region Client — Visual RPC

        [ClientRpc]
        private void SpawnVisualClientRpc(
            Vector3 origin,
            Vector3 hitPoint,
            ushort  configId,
            bool    confirmedHit,
            int     visualId,
            bool    is3D)
        {
            SpawnVisualLocal(origin, hitPoint, configId, visualId, confirmedHit, is3D);
        }

        private void SpawnVisualLocal(
            Vector3 origin,
            Vector3 hitPoint,
            ushort  configId,
            int     visualId,
            bool    playImpactOnArrival,
            bool    is3D)
        {
            if (LocalObjectPool.Instance == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "SpawnVisualLocal: LocalObjectPool.Instance is null. " +
                    "No raycast visual will appear.",
                    nameof(RaycastProjectileHandler));
                return;
            }

            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SpawnVisualLocal: config {configId} not registered.",
                    nameof(RaycastProjectileHandler));
                return;
            }

            Vector3    dir      = (hitPoint - origin).normalized;
            Quaternion rot      = ClientPredictionManager.GetDirectionRotation(dir);

            // Use is3D from the fire result to pick the correct pool type
            PoolableObjectType poolType = is3D ? _visualPoolType3D : _visualPoolType2D;

            var obj = LocalObjectPool.Instance.GetObject(poolType, origin, rot);
            if (obj == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SpawnVisualLocal: LocalObjectPool returned null for type {poolType}. " +
                    $"Ensure the pool has a prefab assigned for {poolType}. " +
                    "No raycast visual will appear.",
                    nameof(RaycastProjectileHandler));
                return;
            }

            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(configId, origin, dir, _visualTravelSpeed);

            _activeVisuals.Add(new ActiveVisual
            {
                VisualId            = visualId,
                Obj                 = obj,
                Origin              = origin,
                HitPoint            = hitPoint,
                Speed               = _visualTravelSpeed,
                ConfigId            = configId,
                PoolType            = poolType,
                PlayImpactOnArrival = playImpactOnArrival
            });
        }

        #endregion

        #region Update — Visual Movement

        private void Update()
        {
            if (_activeVisuals.Count == 0) return;

            var toRemove = new List<int>();

            foreach (var v in _activeVisuals)
            {
                if (v.Obj == null) { toRemove.Add(v.VisualId); continue; }

                v.Obj.transform.position = Vector3.MoveTowards(
                    v.Obj.transform.position,
                    v.HitPoint,
                    v.Speed * Time.deltaTime);

                Vector3 travelDir = v.HitPoint - v.Obj.transform.position;
                if (travelDir.sqrMagnitude > 0.001f)
                    ClientPredictionManager.ApplyDirectionRotation(
                        v.Obj.transform, travelDir.normalized);

                if (Vector3.Distance(v.Obj.transform.position, v.HitPoint) < 0.05f)
                {
                    // Only play impact FX when this was a confirmed hit
                    if (v.PlayImpactOnArrival)
                        PlayImpactEffect(v);

                    ReturnVisual(v);
                    toRemove.Add(v.VisualId);
                }
            }

            _activeVisuals.RemoveAll(v => toRemove.Contains(v.VisualId));
        }

        #endregion

        #region Visual Cleanup

        private void PlayImpactEffect(ActiveVisual v)
            => ProjectileImpactHandler.Instance?.PlayImpact(v.HitPoint, v.ConfigId);

        private void ReturnVisual(ActiveVisual v)
        {
            if (v.Obj == null) return;
            LocalObjectPool.Instance?.ReturnObject(v.Obj, v.PoolType);
        }

        #endregion

        #region Offline Support

        /// <summary>
        /// Called by MID_MasterProjectileSystem when not in a networked session.
        /// Handles client-side visual and fires the local hit event.
        /// </summary>
        public void OfflineHandleFire(
            RaycastFireResult result,
            ushort            configId,
            uint              ownerLocalId,
            float             damageMultiplier)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"OfflineHandleFire: config {configId} not registered.",
                    nameof(RaycastProjectileHandler));
                return;
            }

            if (result.DidHit && LocalProjectileManager.HasInstance)
            {
                float damage = cfg.EvaluateDamage(0f);
                if (result.IsHeadshot) damage *= cfg.HeadshotMultiplier;
                bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                if (isCrit) damage *= cfg.CritMultiplier;
                damage *= damageMultiplier;

                var payload = new LocalHitPayload
                {
                    ProjId       = 0,
                    ConfigId     = configId,
                    Is3D         = result.Is3D,
                    Damage       = damage,
                    IsHeadshot   = result.IsHeadshot,
                    IsCrit       = isCrit,
                    HitPosition  = result.HitPoint,
                    OwnerLocalId = ownerLocalId,
                    RawTargetId  = (uint)result.HitTargetNetworkId
                };
                LocalProjectileManager.Instance.FireHitEvent(payload);
            }

            // Always spawn the travelling visual — but only play impact on actual hits
            SpawnVisualLocal(
                result.Origin,
                result.HitPoint,
                configId,
                _nextVisualId++,
                playImpactOnArrival: result.DidHit,
                is3D: result.Is3D);
        }

        #endregion

        #region Helpers

        private static ServerProjectileData BuildRaycastGameData(
            WeaponFireContext context, ushort configId, ProjectileConfigSO cfg)
        {
            return new ServerProjectileData(
                ownerMidId:         context.OwnerMidId,
                firedById:          context.FiredByNetworkObjectId,
                isBot:              context.IsBotOwner,
                level:              context.WeaponLevel,
                spawnPos2D:         Vector2.zero,
                damageMultiplierIn: context.DamageMultiplier,
                config:             cfg);
        }

        #endregion
    }
}
