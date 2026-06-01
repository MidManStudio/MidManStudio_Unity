// RaycastProjectileHandler.cs
// FIX (double visual on firing client): ServerHandleFire now accepts an optional
// senderClientId. SpawnVisualClientRpc is sent to ALL clients EXCEPT the sender,
// because the sender already spawned their own local visual via OfflineHandleFire
// in MID_MasterProjectileSystem.RegisterRaycastFire. Previously the server sent
// the visual RPC to everyone, so the firing client ended up with two travelling
// bullet visuals for every raycast shot.
//
// When the host fires (ServerHandleFire called directly, no sender ID), the default
// of ulong.MaxValue means no client is excluded and the host receives the RPC
// normally — giving them exactly one visual.

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
                 "Default -1 = Everything. Must include the layer(s) your targets are on.")]
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
            public int                VisualId;
            public GameObject         Obj;
            public Vector3            Origin;
            public Vector3            HitPoint;
            public float              Speed;
            public ushort             ConfigId;
            public PoolableObjectType PoolType;
            public bool               PlayImpactOnArrival;
        }

        private readonly List<ActiveVisual> _activeVisuals = new(64);
        private int _nextVisualId = 1;

        #endregion

        #region Server — Handle Fire

        /// <summary>
        /// Called by the server to process a raycast fire event.
        /// <paramref name="senderClientId"/>: the NGO client who fired. SpawnVisualClientRpc
        /// will exclude this client because they already spawned their own local visual via
        /// OfflineHandleFire. Pass ulong.MaxValue (default) when the server itself fires
        /// (host fire path) — in that case no client is excluded.
        /// </summary>
        public void ServerHandleFire(
            RaycastFireResult clientResult,
            WeaponFireContext  context,
            ushort             configId,
            ulong              senderClientId = ulong.MaxValue)
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
                    clientResult, clientResult.Is3D,
                    out serverHitPoint, out serverTargetId, out serverHeadshot);
            }

            if (serverConfirmed && serverTargetId != 0)
            {
                float damage = cfg.EvaluateDamage(0f);
                if (serverHeadshot) damage *= cfg.HeadshotMultiplier;
                bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                if (isCrit) damage *= cfg.CritMultiplier;
                damage *= context.DamageMultiplier;

                OnServerHitConfirmed?.Invoke(new ProjectileHitPayload
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
                    GameData               = BuildRaycastGameData(context, configId, cfg)
                });
            }

            // Build the recipient list. Exclude the sender (they spawned their own local
            // visual already). If senderClientId == ulong.MaxValue, send to everyone.
            var targets = BuildTargetList(senderClientId);
            if (targets.Count == 0)
            {
                // No one to send to (e.g. only one player in the session).
                return;
            }

            SpawnVisualClientRpc(
                clientResult.Origin,
                serverConfirmed ? serverHitPoint : clientResult.HitPoint,
                configId,
                confirmedHit:  serverConfirmed && serverTargetId != 0,
                visualId:      _nextVisualId++,
                is3D:          clientResult.Is3D,
                new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = targets }
                });
        }

        #endregion

        #region Server Validation

        private bool ValidateHitServer(
            RaycastFireResult clientResult, bool is3D,
            out Vector3 serverHitPoint, out ulong serverTargetId, out bool serverHeadshot)
        {
            serverHitPoint = clientResult.HitPoint;
            serverTargetId = 0;
            serverHeadshot = false;

            if (is3D)
            {
                if (!Physics.Raycast(clientResult.Origin, clientResult.Direction,
                    out RaycastHit hit3D, 1000f, _serverRaycastLayers,
                    QueryTriggerInteraction.Collide))
                    return false;

                serverHitPoint = hit3D.point;
                if (Vector3.Distance(serverHitPoint, clientResult.HitPoint) > _hitValidationTolerance)
                    return false;

                var no = hit3D.collider.GetComponentInParent<NetworkObject>();
                if (no != null) serverTargetId = no.NetworkObjectId;
                serverHeadshot = clientResult.IsHeadshot;
                return true;
            }
            else
            {
                RaycastHit2D serverHit = Physics2D.Raycast(
                    clientResult.Origin, clientResult.Direction, 1000f, _serverRaycastLayers);
                if (!serverHit.collider) return false;

                serverHitPoint = serverHit.point;
                if (Vector3.Distance(serverHitPoint, clientResult.HitPoint) > _hitValidationTolerance)
                    return false;

                var no = serverHit.collider.GetComponentInParent<NetworkObject>();
                if (no != null) serverTargetId = no.NetworkObjectId;
                serverHeadshot = clientResult.IsHeadshot;
                return true;
            }
        }

        #endregion

        #region Client — Visual RPC

        [ClientRpc]
        private void SpawnVisualClientRpc(
            Vector3 origin, Vector3 hitPoint, ushort configId,
            bool confirmedHit, int visualId, bool is3D,
            ClientRpcParams rpcParams = default)
        {
            SpawnVisualLocal(origin, hitPoint, configId, visualId, confirmedHit, is3D);
        }

        private void SpawnVisualLocal(
            Vector3 origin, Vector3 hitPoint, ushort configId,
            int visualId, bool playImpactOnArrival, bool is3D)
        {
            if (LocalObjectPool.Instance == null) return;

            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            Vector3    dir      = (hitPoint - origin).normalized;
            Quaternion rot      = ClientPredictionManager.GetDirectionRotation(dir);
            PoolableObjectType poolType = is3D ? _visualPoolType3D : _visualPoolType2D;

            var obj = LocalObjectPool.Instance.GetObject(poolType, origin, rot);
            if (obj == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"SpawnVisualLocal: pool returned null for {poolType}.",
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
            var toRemove = new List<int>(4);

            foreach (var v in _activeVisuals)
            {
                if (v.Obj == null) { toRemove.Add(v.VisualId); continue; }

                v.Obj.transform.position = Vector3.MoveTowards(
                    v.Obj.transform.position, v.HitPoint, v.Speed * Time.deltaTime);

                Vector3 travelDir = v.HitPoint - v.Obj.transform.position;
                if (travelDir.sqrMagnitude > 0.001f)
                    ClientPredictionManager.ApplyDirectionRotation(
                        v.Obj.transform, travelDir.normalized);

                if (Vector3.Distance(v.Obj.transform.position, v.HitPoint) < 0.05f)
                {
                    if (v.PlayImpactOnArrival) PlayImpactEffect(v);
                    ReturnVisual(v);
                    toRemove.Add(v.VisualId);
                }
            }

            _activeVisuals.RemoveAll(v => toRemove.Contains(v.VisualId));
        }

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
            RaycastFireResult result, ushort configId,
            uint ownerLocalId, float damageMultiplier)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            if (result.DidHit && LocalProjectileManager.HasInstance)
            {
                float damage = cfg.EvaluateDamage(0f);
                if (result.IsHeadshot) damage *= cfg.HeadshotMultiplier;
                bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                if (isCrit) damage *= cfg.CritMultiplier;
                damage *= damageMultiplier;

                LocalProjectileManager.Instance.FireHitEvent(new LocalHitPayload
                {
                    ProjId = 0, ConfigId = configId, Is3D = result.Is3D,
                    Damage = damage, IsHeadshot = result.IsHeadshot, IsCrit = isCrit,
                    HitPosition = result.HitPoint, OwnerLocalId = ownerLocalId,
                    RawTargetId = (uint)result.HitTargetNetworkId
                });
            }

            SpawnVisualLocal(result.Origin, result.HitPoint, configId,
                _nextVisualId++, playImpactOnArrival: result.DidHit, is3D: result.Is3D);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Returns all connected client IDs, optionally excluding one (the sender).
        /// </summary>
        private List<ulong> BuildTargetList(ulong excludeId)
        {
            var list = new List<ulong>(NetworkManager.Singleton.ConnectedClientsIds.Count);
            foreach (ulong id in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (id != excludeId) list.Add(id);
            }
            return list;
        }

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
