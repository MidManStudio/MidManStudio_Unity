using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Data;
using MidManStudio.Projectiles.Visuals;
using MidManStudio.Projectiles.Network;
using MidManStudio.Netcode.LagCompensation;

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
        [Tooltip("Max world-unit discrepancy between client and server hit positions.\n" +
                 "Increase if server-side re-validation misses due to target desync.")]
        [SerializeField] private float _hitValidationTolerance = 2f;

        [Tooltip("Layers the SERVER raycast tests against for hit validation.\n" +
                 "Default -1 = Everything. Must include the layer(s) your targets are on.")]
        [SerializeField] private LayerMask _serverRaycastLayers = -1;

        [Tooltip("When the server re-validation raycast misses (e.g. desynced target\n" +
                 "positions), fall back to trusting the client's reported hit if the\n" +
                 "hit distance is plausible. Suitable for cooperative / trusted-client games.\n" +
                 "Disable for competitive anti-cheat scenarios.")]
        [SerializeField] private bool _trustClientOnValidationMiss = true;

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

        // Cached ContactFilter for 2D server raycasts — avoids per-call allocation
        private ContactFilter2D _serverContactFilter2D;
        private bool            _contactFilterInitialised;

        #endregion

        #region Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            InitContactFilter2D();
        }

        private void InitContactFilter2D()
        {
            if (_contactFilterInitialised) return;
            _serverContactFilter2D = new ContactFilter2D();
            _serverContactFilter2D.SetLayerMask(_serverRaycastLayers);
            _serverContactFilter2D.useTriggers = true;  // explicitly include triggers
            _contactFilterInitialised = true;
        }

        #endregion

        #region Server — Handle Fire

        /// <summary>
        /// Called by the server to process a raycast fire event.
        /// <paramref name="senderClientId"/>: the NGO client who fired. SpawnVisualClientRpc
        /// will exclude this client because they already spawned their own local visual.
        /// Pass ulong.MaxValue (default) when the server itself fires (host path).
        /// <paramref name="clientFireTick"/>: the shooter's fire tick (see
        /// ProjectileFireRequest.ClientFireTick) — 0 means "no lag compensation"
        /// (e.g. an offline path), and is passed straight through to
        /// MID_LagCompensator.BeginRewind, which itself treats &lt;= 0 as a no-op.
        /// </summary>
        public void ServerHandleFire(
            RaycastFireResult clientResult,
            WeaponFireContext  context,
            ushort             configId,
            ulong              senderClientId = ulong.MaxValue,
            int                clientFireTick = 0)
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
                    clientResult, clientResult.Is3D, clientFireTick,
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

            var targets = BuildTargetList(senderClientId);
            if (targets.Count == 0) return;

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

        #region Server — Handle Pattern Fire

        /// <summary>
        /// PATTERN SUPPORT: resolves patternId (or pelletCount/spreadDeg for the
        /// no-pattern case) into N directions via the same
        /// ProjectileDirectionResolver every other fire path uses, then has the
        /// server cast all N rays itself — fully authoritative, no client hit
        /// claims involved (see the type doc on RaycastPatternFireServerRpc for
        /// why that's the right trust model here). One MID_ProjectileHitPayload
        /// fires per confirmed hit, same as the single-ray path. Visuals for all
        /// N pellets (hit or miss) go out in one batched RPC rather than N
        /// separate calls.
        /// </summary>
        public void ServerHandleFirePattern(
            ushort patternId, Vector3 origin, Vector3 baseDirection,
            byte pelletCount, float spreadDeg, bool is3D,
            WeaponFireContext context, ushort configId,
            ulong senderClientId = ulong.MaxValue,
            int clientFireTick = 0)
        {
            if (!IsServer) return;

            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null)
            {
                MID_Logger.LogError(_logLevel,
                    $"ServerHandleFirePattern: configId {configId} not registered.",
                    nameof(RaycastProjectileHandler));
                return;
            }

            float maxRange = cfg.MaxRange > 0f ? cfg.MaxRange : 1000f;
            var resolved = ProjectileDirectionResolver.Resolve(
                patternId, origin, baseDirection, pelletCount, spreadDeg, 1f, is3D);

            int n = resolved.Length;
            var hitPoints = new Vector3[n];

            // All N pellets in one shot share the same fire tick, so they all
            // need to see the SAME historical world state — one rewind for the
            // whole loop, not one per pellet.
            using (MID_LagCompensator.BeginRewind(clientFireTick))
            {
                for (int i = 0; i < n; i++)
                {
                    bool didHit = CastServerRay(
                        origin, resolved[i].Direction, is3D, maxRange,
                        out Vector3 hitPoint, out ulong targetId);

                    hitPoints[i] = hitPoint;

                    if (didHit && targetId != 0)
                    {
                        bool  isCrit = UnityEngine.Random.value < cfg.CritChance;
                        float damage = cfg.EvaluateDamage(0f) * (isCrit ? cfg.CritMultiplier : 1f)
                                       * context.DamageMultiplier;

                        OnServerHitConfirmed?.Invoke(new ProjectileHitPayload
                        {
                            ProjId                 = 0,
                            ConfigId               = configId,
                            Is3D                   = is3D,
                            TargetId               = (uint)targetId,
                            Damage                 = damage,
                            IsHeadshot             = false,
                            IsCrit                 = isCrit,
                            HitPosition            = hitPoint,
                            OwnerMidId             = context.OwnerMidId,
                            FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                            IsBotOwner             = context.IsBotOwner,
                            WeaponLevel            = context.WeaponLevel,
                            GameData               = BuildRaycastGameData(context, configId, cfg)
                        });
                    }
                }
            }

            var targets = BuildTargetList(senderClientId);
            if (targets.Count == 0) return;

            SpawnPatternVisualClientRpc(origin, hitPoints, configId, _nextVisualId, is3D,
                new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = targets }
                });
            _nextVisualId += n;
        }

        /// <summary>
        /// Single authoritative raycast, 2D or 3D, no client comparison — used
        /// only by the pattern path. The single-ray path keeps its own inline
        /// casts in ValidateHitServer since those need to also carry the
        /// server-vs-client comparison; this is the simpler standalone version.
        /// </summary>
        private bool CastServerRay(
            Vector3 origin, Vector3 direction, bool is3D, float maxDistance,
            out Vector3 hitPoint, out ulong targetNetworkId)
        {
            hitPoint       = origin + direction * maxDistance;
            targetNetworkId = 0;

            if (is3D)
            {
                if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance,
                        _serverRaycastLayers, QueryTriggerInteraction.Collide))
                    return false;

                hitPoint = hit.point;
                var no = hit.collider.GetComponentInParent<NetworkObject>();
                if (no != null) targetNetworkId = no.NetworkObjectId;
                return true;
            }

            if (!_contactFilterInitialised) InitContactFilter2D();
            var results = new RaycastHit2D[1];
            int count = Physics2D.Raycast(origin, direction, _serverContactFilter2D, results, maxDistance);
            if (count == 0) return false;

            hitPoint = results[0].point;
            var no2D = results[0].collider.GetComponentInParent<NetworkObject>();
            if (no2D != null) targetNetworkId = no2D.NetworkObjectId;
            return true;
        }

        #endregion

        #region Server Validation

        private bool ValidateHitServer(
            RaycastFireResult clientResult,
            bool              is3D,
            int               clientFireTick,
            out Vector3       serverHitPoint,
            out ulong         serverTargetId,
            out bool          serverHeadshot)
        {
            serverHitPoint = clientResult.HitPoint;
            serverTargetId = 0;
            serverHeadshot = false;

            if (!clientResult.DidHit) return false;

            // Whole method body wrapped in one scope — both branches below have
            // several early `return`s (raycast hit, tolerance fallback, miss),
            // and `using` guarantees every registered target gets restored to
            // its real current pose on every one of those paths, not just the
            // happy one.
            using (MID_LagCompensator.BeginRewind(clientFireTick))
            {
            if (is3D)
            {
                // ── 3D: server does its own raycast ────────────────────────────
                if (Physics.Raycast(
                    clientResult.Origin, clientResult.Direction,
                    out RaycastHit hit3D, 1000f, _serverRaycastLayers,
                    QueryTriggerInteraction.Collide))
                {
                    serverHitPoint = hit3D.point;

                    if (Vector3.Distance(serverHitPoint, clientResult.HitPoint)
                        <= _hitValidationTolerance)
                    {
                        var no3D = hit3D.collider.GetComponentInParent<NetworkObject>();
                        if (no3D != null)
                        {
                            serverTargetId = no3D.NetworkObjectId;
                        }
                        else if (_trustClientOnValidationMiss
                                 && clientResult.HitTargetNetworkId != 0)
                        {
                            // Server hit something but it has no NetworkObject (e.g. static
                            // geometry). If the client reported a specific target, use it.
                            serverTargetId = clientResult.HitTargetNetworkId;
                            MID_Logger.LogDebug(_logLevel,
                                "3D server raycast hit geometry with no NetworkObject; " +
                                "using client-reported target ID.",
                                nameof(RaycastProjectileHandler));
                        }
                        serverHeadshot = clientResult.IsHeadshot;
                        return true;
                    }
                }

                // Server raycast missed or exceeded tolerance.
                // If the client reports a plausible hit (valid target + reasonable distance),
                // trust it. This handles desynced target positions (e.g. server-side animation
                // not replicated via NetworkTransform).
                if (_trustClientOnValidationMiss
                    && clientResult.HitTargetNetworkId != 0
                    && Vector3.Distance(clientResult.Origin, clientResult.HitPoint) <= 1000f)
                {
                    serverHitPoint = clientResult.HitPoint;
                    serverTargetId = clientResult.HitTargetNetworkId;
                    serverHeadshot = clientResult.IsHeadshot;
                    MID_Logger.LogDebug(_logLevel,
                        "3D server validation raycast missed; falling back to client report.",
                        nameof(RaycastProjectileHandler));
                    return true;
                }
                return false;
            }
            else
            {
                // ── 2D: use ContactFilter2D so trigger colliders are always included ──
                // The default Physics2D.Raycast(origin, dir, distance, layerMask) overload
                // respects Physics2D.queriesHitTriggers project setting, which may be false.
                // Using the ContactFilter2D overload with useTriggers = true is explicit and
                // reliable regardless of project settings.
                if (!_contactFilterInitialised) InitContactFilter2D();

                // Refresh layer mask in case _serverRaycastLayers was changed at runtime
                _serverContactFilter2D.SetLayerMask(_serverRaycastLayers);

                var results2D = new RaycastHit2D[1];
                int hitCount = Physics2D.Raycast(
                    (Vector2)clientResult.Origin,
                    (Vector2)clientResult.Direction,
                    _serverContactFilter2D,
                    results2D,
                    1000f);

                if (hitCount > 0)
                {
                    serverHitPoint = results2D[0].point;

                    if (Vector3.Distance(serverHitPoint, clientResult.HitPoint)
                        <= _hitValidationTolerance)
                    {
                        var no2D = results2D[0].collider.GetComponentInParent<NetworkObject>();
                        if (no2D != null)
                        {
                            serverTargetId = no2D.NetworkObjectId;
                        }
                        else if (_trustClientOnValidationMiss
                                 && clientResult.HitTargetNetworkId != 0)
                        {
                            serverTargetId = clientResult.HitTargetNetworkId;
                            MID_Logger.LogDebug(_logLevel,
                                "2D server raycast hit geometry with no NetworkObject; " +
                                "using client-reported target ID.",
                                nameof(RaycastProjectileHandler));
                        }
                        serverHeadshot = clientResult.IsHeadshot;
                        return true;
                    }
                }

                // 2D server raycast missed or exceeded tolerance. Fall back to client report.
                // Common cause: server-side bob/animation not replicated to client, so the
                // client aimed at the client-side position while the server has it elsewhere.
                if (_trustClientOnValidationMiss
                    && clientResult.HitTargetNetworkId != 0
                    && Vector3.Distance(clientResult.Origin, clientResult.HitPoint) <= 1000f)
                {
                    serverHitPoint = clientResult.HitPoint;
                    serverTargetId = clientResult.HitTargetNetworkId;
                    serverHeadshot = clientResult.IsHeadshot;
                    MID_Logger.LogDebug(_logLevel,
                        "2D server validation raycast missed; falling back to client report.",
                        nameof(RaycastProjectileHandler));
                    return true;
                }
                return false;
            }
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

        /// <summary>
        /// Batched version for pattern fire — one RPC call for all N pellets'
        /// visuals instead of N separate calls. hitPoints.Length is the pellet
        /// count; every entry is treated as a confirmed hit position (misses
        /// already resolve to a point at max range from CastServerRay, same
        /// convention SpawnVisualLocal already expects for a miss).
        /// </summary>
        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        private void SpawnPatternVisualClientRpc(
            Vector3 origin, Vector3[] hitPoints, ushort configId,
            int firstVisualId, bool is3D,
            ClientRpcParams rpcParams = default)
        {
            if (hitPoints == null) return;
            for (int i = 0; i < hitPoints.Length; i++)
                SpawnVisualLocal(origin, hitPoints[i], configId, firstVisualId + i, true, is3D);
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
                    ProjId       = 0,
                    ConfigId     = configId,
                    Is3D         = result.Is3D,
                    Damage       = damage,
                    IsHeadshot   = result.IsHeadshot,
                    IsCrit       = isCrit,
                    HitPosition  = result.HitPoint,
                    OwnerLocalId = ownerLocalId,
                    RawTargetId  = (uint)result.HitTargetNetworkId
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
