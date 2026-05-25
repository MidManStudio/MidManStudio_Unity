// RaycastProjectileHandler.cs
// CHANGE: SpawnVisualLocal now uses ProjectileVisualBase instead of ProjectileVisual_
//   so both 2D and 3D pool visuals work correctly.

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
        [SerializeField] private float _hitValidationTolerance = 2f;
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
            public int        VisualId;
            public GameObject Obj;
            public Vector3    Origin;
            public Vector3    HitPoint;
            public float      Speed;
            public ushort     ConfigId;
            public PoolableObjectType PoolType;
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
                    clientResult, out serverHitPoint,
                    out serverTargetId, out serverHeadshot);
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
                    Is3D                   = cfg.Is3D,
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
            }

            SpawnVisualClientRpc(
                clientResult.Origin,
                serverConfirmed ? serverHitPoint : clientResult.HitPoint,
                configId,
                serverConfirmed && serverTargetId != 0,
                _nextVisualId++);
        }

        #endregion

        #region Server Validation

        private bool ValidateHitServer(
            RaycastFireResult clientResult,
            out Vector3       serverHitPoint,
            out ulong         serverTargetId,
            out bool          serverHeadshot)
        {
            serverHitPoint = clientResult.HitPoint;
            serverTargetId = 0;
            serverHeadshot = false;

            RaycastHit2D serverHit = Physics2D.Raycast(
                clientResult.Origin,
                clientResult.Direction,
                1000f,
                _serverRaycastLayers);

            if (!serverHit.collider) return false;
            serverHitPoint = serverHit.point;

            float dist = Vector3.Distance(serverHitPoint, clientResult.HitPoint);
            if (dist > _hitValidationTolerance) return false;

            var netObj = serverHit.collider.GetComponentInParent<NetworkObject>();
            if (netObj != null) serverTargetId = netObj.NetworkObjectId;

            serverHeadshot = clientResult.IsHeadshot;
            return true;
        }

        #endregion

        #region Client — Visual

        [ClientRpc]
        private void SpawnVisualClientRpc(
            Vector3 origin, Vector3 hitPoint,
            ushort configId, bool confirmedHit, int visualId)
        {
            SpawnVisualLocal(origin, hitPoint, configId, visualId);
        }

        private void SpawnVisualLocal(
            Vector3 origin, Vector3 hitPoint,
            ushort configId, int visualId)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            if (LocalObjectPool.Instance == null) return;

            Vector3    dir      = (hitPoint - origin).normalized;
            Quaternion rot      = ClientPredictionManager.GetDirectionRotation(dir);

            // Select correct pool type by cfg.Is3D
            PoolableObjectType poolType = cfg.Is3D ? _visualPoolType3D : _visualPoolType2D;

            var obj = LocalObjectPool.Instance.GetObject(poolType, origin, rot);
            if (obj == null) return;

            // Works for ProjectileVisual_ (2D) AND ProjectileVisual3D
            var vis = obj.GetComponent<ProjectileVisualBase>();
            vis?.InitializeClientVisual(configId, origin, dir, _visualTravelSpeed);

            _activeVisuals.Add(new ActiveVisual
            {
                VisualId  = visualId,
                Obj       = obj,
                Origin    = origin,
                HitPoint  = hitPoint,
                Speed     = _visualTravelSpeed,
                ConfigId  = configId,
                PoolType  = poolType
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
            if (cfg == null) return;

            if (result.DidHit && LocalProjectileManager.Instance != null)
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
                    Is3D         = cfg.Is3D,
                    Damage       = damage,
                    IsHeadshot   = result.IsHeadshot,
                    IsCrit       = isCrit,
                    HitPosition  = result.HitPoint,
                    OwnerLocalId = ownerLocalId
                };
                LocalProjectileManager.Instance.FireHitEvent(payload);
            }

            SpawnVisualLocal(result.Origin, result.HitPoint, configId, _nextVisualId++);
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
