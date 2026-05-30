// RaycastProjectileHandler.cs
// FIX: ValidateHitServer 3D path now passes QueryTriggerInteraction.Collide to Physics.Raycast.
//   Without this, trigger-mode colliders on targets are not detected depending on project physics
//   settings (Physics.queriesHitTriggers), causing server-side validation to silently fail even
//   when the client correctly detected a hit.
//
// The _serverRaycastLayers LayerMask IS and was already being applied — added a more prominent
// tooltip to make it clear this must include the layer(s) your targets are on.
//
// All other fixes from previous session retained.

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

        [Tooltip("Layers the SERVER raycast tests against for hit validation.\n" +
                 "Default -1 = Everything.\n" +
                 "IMPORTANT: This must include whatever layer(s) your targets are on.\n" +
                 "If validation always fails, check that targets are on an included layer.\n" +
                 "Used for BOTH 3D (Physics.Raycast) and 2D (Physics2D.Raycast) server passes.")]
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
            public bool              PlayImpactOnArrival;
        }

        private readonly List<ActiveVisual> _activeVisuals = new(64);
        private int _nextVisualId = 1;

        #endregion

        #region Server — Handle Fire

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
                    "ServerHandleFire: client reported hit but server could not validate. " +
                    $"is3D={clientResult.Is3D} serverLayers={_serverRaycastLayers.value} " +
                    $"tolerance={_hitValidationTolerance}",
                    nameof(RaycastProjectileHandler));
            }

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
        /// Validates the client-reported hit using the matching physics system.
        /// FIX: 3D path now uses QueryTriggerInteraction.Collide so trigger-mode colliders
        ///   are detected regardless of project-level Physics.queriesHitTriggers setting.
        /// The _serverRaycastLayers mask IS applied — if validation always fails, verify
        ///   that your targets are on a layer included in _serverRaycastLayers.
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
                // FIX: added QueryTriggerInteraction.Collide — trigger colliders now detected.
                if (!Physics.Raycast(
                    clientResult.Origin,
                    clientResult.Direction,
                    out RaycastHit hit3D,
                    1000f,
                    _serverRaycastLayers,
                    QueryTriggerInteraction.Collide))
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
                // 2D validation — Physics2D doesn't have a QueryTriggerInteraction overload,
                // but respects Physics2D.queriesHitTriggers project setting.
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
