// packages/com.midmanstudio.projectilesystem/Tests/Runtime/TestSceneBootstrapper.cs
//
// CHANGE: Added ProjectileConfigManager and ProjectileConfigMappingSO references.
// In Start(), after any manual _configs registration, calls
// _configManager.RegisterAll(_configMapping) explicitly so enum-based config
// resolution is available before the session starts — even when
// ProjectileConfigManager.Start() execution order is ambiguous.
//
// TWO REGISTRATION PATHS — both are supported simultaneously:
//
//   A) Manual (legacy, _configs array):
//      Drag configs into _configs in inspector.
//      ProjectileRegistry assigns IDs in insertion order (0, 1, 2…).
//      Set NetworkedDimensionPlayer._configTypeId2D = 0 etc.
//
//   B) Enum system (recommended, _configManager + _configMapping):
//      Run Config Type Generator → generates ProjectileConfigType.cs + mapping asset.
//      Assign mapping asset to ProjectileConfigManager._mapping in scene.
//      Assign ProjectileConfigManager component + mapping to this bootstrapper.
//      Set NetworkedDimensionPlayer._configTypeId2D = (int)ProjectileConfigType.Default.
//
//   Both paths call ProjectileRegistry.Register() which is idempotent by name,
//   so running both for the same assets is safe and produces identical IDs.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Audio;
using MidManStudio.Core.FX;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.LocalMultiplayer;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;

namespace TestGame
{
    [DefaultExecutionOrder(-50)]
    public class TestSceneBootstrapper : MonoBehaviour
    {
        #region Inspector

        [Header("Required References")]
        [SerializeField] private LocalLobbyManager          _lobbyManager;
        [SerializeField] private LocalObjectPool            _objectPool;
        [SerializeField] private LocalParticlePool          _particlePool;
        [SerializeField] private ProjectileRegistry         _registry;
        [SerializeField] private MID_MasterProjectileSystem _projectileSystem;
        [SerializeField] private NetworkManager             _networkManager;

        [Header("Enum Config System (recommended)")]
        [Tooltip("Assign the ProjectileConfigManager from the scene.\n" +
                 "If set, RegisterAll() is called explicitly before the session starts\n" +
                 "so enum-based config resolution is ready immediately.")]
        [SerializeField] private ProjectileConfigManager _configManager;

        [Tooltip("Assign the generated ProjectileConfigMapping.asset.\n" +
                 "Created by: MidManStudio > Projectile System > Config Type Generator > Generate Now.\n" +
                 "Default path: Assets/MidManStudio/Generated/Projectiles/ProjectileConfigMapping.asset")]
        [SerializeField] private ProjectileConfigMappingSO _configMapping;

        [Header("Manual Config Registration (legacy / override)")]
        [Tooltip("Configs registered directly into ProjectileRegistry in insertion order.\n" +
                 "Optional if using the enum config system above — leave empty when\n" +
                 "ProjectileConfigManager handles registration via the mapping asset.")]
        [SerializeField] private ProjectileConfigSO[] _configs;

        [Header("3D Target (SphereCollider — for Raycast3D / RustSim3D / Physics3D)")]
        [Tooltip("Prefab must have: TestTarget, MeshRenderer, SphereCollider, NetworkObject.")]
        [SerializeField] private GameObject _targetPrefab3D;
        [SerializeField] private int   _targetCount3D           = 8;
        [SerializeField] private float _targetSpawnRadius3D     = 8f;
        [SerializeField] private float _targetCollisionRadius3D = 0.6f;

        [Header("2D Target (CircleCollider2D — for Raycast2D / RustSim2D / Physics2D)")]
        [Tooltip("Prefab must have: TestTarget2D, SpriteRenderer, CircleCollider2D, NetworkObject.")]
        [SerializeField] private GameObject _targetPrefab2D;
        [SerializeField] private int   _targetCount2D           = 8;
        [SerializeField] private float _targetSpawnRadius2D     = 8f;
        [SerializeField] private float _targetCollisionRadius2D = 0.6f;

        [Header("Bob Animation")]
        [SerializeField] private float _targetBobAmplitude = 0.4f;
        [SerializeField] private float _targetBobSpeed     = 1.2f;

        [Header("Offline / Auto Spawn")]
        [SerializeField] private bool _autoSpawnOffline = true;

        [Header("Player Prefab")]
        [SerializeField] private GameObject  _playerPrefab;
        [SerializeField] private Transform[] _playerSpawnPoints;

        [Header("UI Roots")]
        [SerializeField] private Canvas _lobbyCanvas;
        [SerializeField] private Canvas _gameHUDCanvas;

        [Header("Audio")]
        [SerializeField] private int   _hitSoundClipIndex = 1;
        [SerializeField, Range(0f,1f)] private float _hitSoundVolume = 0.5f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        // 3D targets — registration IDs start at 100
        private readonly List<TestTarget>             _targets3D = new(16);
        private readonly Dictionary<uint, TestTarget> _map3D     = new(16);
        private const uint BASE_ID_3D = 100;

        // 2D targets — registration IDs start at 200
        private readonly List<TestTarget2D>             _targets2D = new(16);
        private readonly Dictionary<uint, TestTarget2D> _map2D     = new(16);
        private const uint BASE_ID_2D = 200;

        private bool _sessionStarted;
        private RaycastProjectileHandler _cachedRaycastHandler;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_lobbyManager  == null) _lobbyManager  = FindObjectOfType<LocalLobbyManager>();
            if (_objectPool    == null) _objectPool    = FindObjectOfType<LocalObjectPool>();
            if (_particlePool  == null) _particlePool  = FindObjectOfType<LocalParticlePool>();
        }

        private IEnumerator Start()
        {
            // ── Pool initialisation ───────────────────────────────────────────
            if (_objectPool   != null && !_objectPool.HasBeenInitialized())
                _objectPool.CallInitializePool();
            if (_particlePool != null && !_particlePool.HasBeenInitialized())
                _particlePool.CallInitializePool();

            // ── Path A: manual config registration (legacy) ───────────────────
            // Registers configs directly by inserting them into ProjectileRegistry.
            // Leave _configs empty when using the enum system (Path B).
            if (_registry != null && _configs != null && _configs.Length > 0)
            {
                foreach (var cfg in _configs)
                {
                    if (cfg == null) continue;
                    ushort id = _registry.Register(cfg);
                    MID_Logger.LogInfo(_logLevel,
                        $"[Manual] Registered '{cfg.name}' → configId={id}",
                        nameof(TestSceneBootstrapper));
                }
            }

            // ── Path B: enum-based config registration ────────────────────────
            // ProjectileConfigManager.Start() calls RegisterAll() automatically,
            // but because TestSceneBootstrapper has [DefaultExecutionOrder(-50)]
            // it starts before default-order scripts. We call RegisterAll()
            // explicitly here so configs are available within the same frame before
            // any coroutine awaits or session logic runs.
            if (_configManager != null && _configMapping != null)
            {
                _configManager.RegisterAll(_configMapping);
                MID_Logger.LogInfo(_logLevel,
                    $"[Enum] ProjectileConfigManager.RegisterAll() called explicitly " +
                    $"with {_configMapping.Configs.Length}-slot mapping.",
                    nameof(TestSceneBootstrapper));
            }
            else if (_configManager != null && _configMapping == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "ProjectileConfigManager is assigned but _configMapping is null. " +
                    "Assign the generated ProjectileConfigMapping.asset to this bootstrapper " +
                    "and to ProjectileConfigManager._mapping in the scene.",
                    nameof(TestSceneBootstrapper));
            }

            // ── Lobby / session routing ───────────────────────────────────────
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived += HandleGameStart;

            SetLobbyUIActive(true);

            if (_autoSpawnOffline && _lobbyManager == null)
            {
                yield return null;
                StartOfflineSession();
            }

            yield break;
        }

        private void OnDestroy()
        {
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived -= HandleGameStart;
            UnsubscribeHitEvents();
        }

        #endregion

        #region Session Start

        private void HandleGameStart(LocalLobbySnapshot snapshot)
        {
            SetLobbyUIActive(false);
            StartCoroutine(NetworkedSessionCoroutine(snapshot));
        }

        private void StartOfflineSession()
        {
            if (_sessionStarted) return;
            _sessionStarted = true;

            SetLobbyUIActive(false);
            SpawnTestTargets3D(networked: false);
            SpawnTestTargets2D(networked: false);
            SubscribeHitEvents();
            StartCoroutine(BobTargets());

            if (_playerPrefab != null)
                Instantiate(_playerPrefab, GetSpawnPoint(0), Quaternion.identity);
        }

        private IEnumerator NetworkedSessionCoroutine(LocalLobbySnapshot snapshot)
        {
            if (_sessionStarted) yield break;
            _sessionStarted = true;

            yield return null;
            yield return null;

            bool isServer = _networkManager != null && _networkManager.IsServer;

            if (isServer)
            {
                SpawnTestTargets3D(networked: true);
                SpawnTestTargets2D(networked: true);
                SubscribeHitEvents();
                StartCoroutine(BobTargets());

                if (_playerPrefab != null)
                {
                    for (int i = 0; i < snapshot.Players.Count; i++)
                    {
                        var p = snapshot.Players[i];
                        if (p.IsBot) continue;
                        var go = Instantiate(_playerPrefab, GetSpawnPoint(i), Quaternion.identity);
                        go.GetComponent<NetworkObject>()?.SpawnAsPlayerObject(p.ClientId);
                    }
                }
            }
        }

        #endregion

        #region Target Spawning — 3D

        private void SpawnTestTargets3D(bool networked)
        {
            if (_targetPrefab3D == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "No _targetPrefab3D assigned — skipping 3D target spawn.",
                    nameof(TestSceneBootstrapper));
                return;
            }

            for (int i = 0; i < _targetCount3D; i++)
            {
                float   angle = i / (float)_targetCount3D * 360f * Mathf.Deg2Rad;
                Vector3 pos   = new Vector3(
                    Mathf.Cos(angle) * _targetSpawnRadius3D,
                    0f,
                    Mathf.Sin(angle) * _targetSpawnRadius3D);

                var go = Instantiate(_targetPrefab3D, pos, Quaternion.identity);
                if (networked) go.GetComponent<NetworkObject>()?.Spawn();

                var target = go.GetComponent<TestTarget>();
                if (target == null)
                {
                    Debug.LogError("[Bootstrapper] _targetPrefab3D is missing TestTarget component!");
                    Destroy(go); continue;
                }

                uint regId = BASE_ID_3D + (uint)i;
                target.RegistrationId    = regId;
                target.OnDestroyedServer += OnTarget3DDestroyed;
                _targets3D.Add(target);
                _map3D[regId] = target;

                float radius = _targetCollisionRadius3D;
                var sc = go.GetComponent<SphereCollider>();
                if (sc != null) radius = sc.radius * Mathf.Max(go.transform.lossyScale.x, 0.01f);

                _projectileSystem?.RegisterTarget3D(new CollisionTarget3D
                {
                    X = pos.x, Y = pos.y, Z = pos.z,
                    Radius   = radius,
                    TargetId = regId,
                    Active   = 1
                }, go.layer);

                MID_Logger.LogDebug(_logLevel,
                    $"Spawned 3D target id={regId} pos={pos} radius={radius:F2}",
                    nameof(TestSceneBootstrapper));
            }

            MID_Logger.LogInfo(_logLevel,
                $"Spawned {_targets3D.Count} 3D targets (networked={networked}).",
                nameof(TestSceneBootstrapper));
        }

        #endregion

        #region Target Spawning — 2D

        private void SpawnTestTargets2D(bool networked)
        {
            if (_targetPrefab2D == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "No _targetPrefab2D assigned — skipping 2D target spawn.",
                    nameof(TestSceneBootstrapper));
                return;
            }

            for (int i = 0; i < _targetCount2D; i++)
            {
                float   angle = i / (float)_targetCount2D * 360f * Mathf.Deg2Rad;
                Vector3 pos   = new Vector3(
                    Mathf.Cos(angle) * _targetSpawnRadius2D,
                    Mathf.Sin(angle) * _targetSpawnRadius2D,
                    0f);

                var go = Instantiate(_targetPrefab2D, pos, Quaternion.identity);
                if (networked) go.GetComponent<NetworkObject>()?.Spawn();

                var target = go.GetComponent<TestTarget2D>();
                if (target == null)
                {
                    Debug.LogError("[Bootstrapper] _targetPrefab2D is missing TestTarget2D component!");
                    Destroy(go); continue;
                }

                uint regId = BASE_ID_2D + (uint)i;
                target.RegistrationId    = regId;
                target.OnDestroyedServer += OnTarget2DDestroyed;
                _targets2D.Add(target);
                _map2D[regId] = target;

                float radius = _targetCollisionRadius2D;
                var cc = go.GetComponent<CircleCollider2D>();
                if (cc != null) radius = cc.radius * Mathf.Max(go.transform.lossyScale.x, 0.01f);

                _projectileSystem?.RegisterTarget2D(new CollisionTarget
                {
                    X = pos.x, Y = pos.y,
                    Radius   = radius,
                    TargetId = regId,
                    Active   = 1
                }, go.layer);

                MID_Logger.LogDebug(_logLevel,
                    $"Spawned 2D target id={regId} pos={pos} radius={radius:F2}",
                    nameof(TestSceneBootstrapper));
            }

            MID_Logger.LogInfo(_logLevel,
                $"Spawned {_targets2D.Count} 2D targets (networked={networked}).",
                nameof(TestSceneBootstrapper));
        }

        #endregion

        #region Target Destroyed Callbacks

        private void OnTarget3DDestroyed(TestTarget target)
            => _projectileSystem?.DeactivateTarget3D(target.RegistrationId);

        private void OnTarget2DDestroyed(TestTarget2D target)
            => _projectileSystem?.DeactivateTarget2D(target.RegistrationId);

        #endregion

        #region Hit Event Subscription

        private void SubscribeHitEvents()
        {
            if (_projectileSystem?.GetAuthority()?.Adapter != null)
                _projectileSystem.GetAuthority().Adapter.OnProjectileHit += OnProjectileHit;

            if (LocalProjectileManager.HasInstance)
                LocalProjectileManager.Instance.OnHit += OnLocalHit;

            _cachedRaycastHandler = _projectileSystem?.GetRaycastHandler();
            if (_cachedRaycastHandler != null)
                _cachedRaycastHandler.OnServerHitConfirmed += OnRaycastHitServer;
        }

        private void UnsubscribeHitEvents()
        {
            if (_projectileSystem?.GetAuthority()?.Adapter != null)
                _projectileSystem.GetAuthority().Adapter.OnProjectileHit -= OnProjectileHit;

            if (LocalProjectileManager.HasInstance)
                LocalProjectileManager.Instance.OnHit -= OnLocalHit;

            if (_cachedRaycastHandler != null)
            {
                _cachedRaycastHandler.OnServerHitConfirmed -= OnRaycastHitServer;
                _cachedRaycastHandler = null;
            }
        }

        private void OnProjectileHit(ProjectileHitPayload payload)
            => ApplyHit(payload.TargetId, payload.Damage, payload.HitPosition);

        private void OnLocalHit(LocalHitPayload payload)
            => ApplyHit(payload.RawTargetId, payload.Damage, payload.HitPosition);

        private void OnRaycastHitServer(ProjectileHitPayload payload)
            => ApplyHit(payload.TargetId, payload.Damage, payload.HitPosition);

        #endregion

        #region Hit Resolution

        private void ApplyHit(uint targetId, float damage, Vector3 hitPos)
        {
            if (_map3D.TryGetValue(targetId, out var t3) && t3 != null)
            { t3.TakeDamage(damage); PlayImpactFX(hitPos); return; }

            if (_map2D.TryGetValue(targetId, out var t2) && t2 != null)
            { t2.TakeDamage(damage); PlayImpactFX(hitPos); return; }

            foreach (var kv in _map3D)
            {
                if (kv.Value == null) continue;
                var no = kv.Value.GetComponent<NetworkObject>();
                if (no != null && (uint)no.NetworkObjectId == targetId)
                { kv.Value.TakeDamage(damage); PlayImpactFX(hitPos); return; }
            }
            foreach (var kv in _map2D)
            {
                if (kv.Value == null) continue;
                var no = kv.Value.GetComponent<NetworkObject>();
                if (no != null && (uint)no.NetworkObjectId == targetId)
                { kv.Value.TakeDamage(damage); PlayImpactFX(hitPos); return; }
            }

            if (damage > 0f) SnapToNearest(hitPos, damage);
        }

        private void SnapToNearest(Vector3 hitPos, float damage)
        {
            const float snapRadius = 2f;
            TestTarget  best3D = null; float dist3D = snapRadius;
            foreach (var t in _targets3D)
            {
                if (t == null || !t.gameObject.activeSelf) continue;
                float d = Vector3.Distance(hitPos, t.transform.position);
                if (d < dist3D) { dist3D = d; best3D = t; }
            }
            if (best3D != null) { best3D.TakeDamage(damage); PlayImpactFX(hitPos); return; }

            TestTarget2D best2D = null; float dist2D = snapRadius;
            foreach (var t in _targets2D)
            {
                if (t == null || !t.gameObject.activeSelf) continue;
                float d = Vector3.Distance(hitPos, t.transform.position);
                if (d < dist2D) { dist2D = d; best2D = t; }
            }
            if (best2D != null) { best2D.TakeDamage(damage); PlayImpactFX(hitPos); }
        }

        private void PlayImpactFX(Vector3 hitPos)
        {
            GlobalFXManager.Instance?.TriggerImpact(hitPos, Vector3.up, 6, _hitSoundVolume);
            if (GlobalFXManager.Instance == null)
                MID_NativeAudioBridge.Instance?.PlayClip(_hitSoundClipIndex, _hitSoundVolume);
        }

        #endregion

        #region Bob Animation

        private IEnumerator BobTargets()
        {
            while (true)
            {
                float t = Time.time * _targetBobSpeed;

                for (int i = 0; i < _targets3D.Count; i++)
                {
                    var tgt = _targets3D[i];
                    if (tgt == null || !tgt.gameObject.activeSelf) continue;
                    float phase = i / (float)Mathf.Max(_targets3D.Count, 1) * Mathf.PI * 2f;
                    float newY  = Mathf.Sin(t + phase) * _targetBobAmplitude;
                    var   pos   = tgt.transform.position;
                    tgt.transform.position = new Vector3(pos.x, newY, pos.z);
                    if (_projectileSystem == null) continue;
                    float r = _targetCollisionRadius3D;
                    var sc  = tgt.GetComponent<SphereCollider>();
                    if (sc != null) r = sc.radius * Mathf.Max(tgt.transform.lossyScale.x, 0.01f);
                    _projectileSystem.RegisterTarget3D(new CollisionTarget3D
                    {
                        X = pos.x, Y = newY, Z = pos.z, Radius = r,
                        TargetId = tgt.RegistrationId,
                        Active   = (byte)(tgt.gameObject.activeSelf ? 1 : 0)
                    }, tgt.gameObject.layer);
                }

                for (int i = 0; i < _targets2D.Count; i++)
                {
                    var tgt = _targets2D[i];
                    if (tgt == null || !tgt.gameObject.activeSelf) continue;
                    float phase = i / (float)Mathf.Max(_targets2D.Count, 1) * Mathf.PI * 2f;
                    float newY  = Mathf.Sin(t + phase) * _targetBobAmplitude;
                    var   pos   = tgt.transform.position;
                    tgt.transform.position = new Vector3(pos.x, newY, 0f);
                    if (_projectileSystem == null) continue;
                    float r = _targetCollisionRadius2D;
                    var cc  = tgt.GetComponent<CircleCollider2D>();
                    if (cc != null) r = cc.radius * Mathf.Max(tgt.transform.lossyScale.x, 0.01f);
                    _projectileSystem.RegisterTarget2D(new CollisionTarget
                    {
                        X = pos.x, Y = newY, Radius = r,
                        TargetId = tgt.RegistrationId,
                        Active   = (byte)(tgt.gameObject.activeSelf ? 1 : 0)
                    }, tgt.gameObject.layer);
                }

                yield return null;
            }
        }

        #endregion

        #region Helpers

        private void SetLobbyUIActive(bool active)
        {
            if (_lobbyCanvas   != null) _lobbyCanvas.gameObject.SetActive(active);
            if (_gameHUDCanvas != null) _gameHUDCanvas.gameObject.SetActive(!active);
        }

        private Vector3 GetSpawnPoint(int index)
        {
            if (_playerSpawnPoints != null && _playerSpawnPoints.Length > 0)
                return _playerSpawnPoints[index % _playerSpawnPoints.Length].position;
            float a = index / (float)Mathf.Max(_targetCount3D, 1) * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(a) * 3f, 0.5f, Mathf.Sin(a) * 3f);
        }

        #endregion
    }
}
