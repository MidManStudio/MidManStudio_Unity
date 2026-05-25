// TestSceneBootstrapper.cs
// Initialises all required systems for the projectile test scene.
// Attach to a persistent GameObject alongside LocalLobbyManager.
//
// CHANGES:
//   + SpawnTestTargets now spawns TestTarget components, registers each with
//     the projectile collision system, and stores refs for damage routing.
//   + MID_MasterProjectileSystem.Instance.Adapter OnProjectileHit subscribed
//     so hits automatically call TakeDamage on the matching TestTarget.
//   + BobTargets syncs both 2D and 3D collision positions each frame.
//   + PlayerSpawn correctly parents ShotPoint3D to headPivot (handled by player).

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.LocalMultiplayer;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;

namespace TestGame
{
    public class TestSceneBootstrapper : MonoBehaviour
    {
        #region Inspector

        [Header("Required References")]
        [SerializeField] private LocalLobbyManager              _lobbyManager;
        [SerializeField] private LocalObjectPool                _objectPool;
        [SerializeField] private LocalParticlePool              _particlePool;
        [SerializeField] private ProjectileRegistry             _registry;
        [SerializeField] private MID_MasterProjectileSystem     _projectileSystem;
        [SerializeField] private NetworkManager                 _networkManager;

        [Header("Configs to Register on Start")]
        [Tooltip("Registered in order — first = configId 0, second = 1, etc.")]
        [SerializeField] private ProjectileConfigSO[] _configs;

        [Header("Test Targets")]
        [Tooltip("Prefab must have TestTarget + NetworkObject components.")]
        [SerializeField] private GameObject _targetPrefab;
        [SerializeField] private int        _targetCount       = 8;
        [SerializeField] private float      _targetSpawnRadius = 8f;
        [SerializeField] private float      _targetBobAmplitude = 0.4f;
        [SerializeField] private float      _targetBobSpeed     = 1.2f;

        [Header("UI Roots")]
        [SerializeField] private Canvas _lobbyCanvas;
        [SerializeField] private Canvas _gameHUDCanvas;

        [Header("Player Prefab")]
        [SerializeField] private GameObject _playerPrefab;

        [Header("Spawn Points")]
        [SerializeField] private Transform[] _playerSpawnPoints;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        // Maps RegistrationId → TestTarget for damage routing
        private readonly Dictionary<uint, TestTarget> _targetMap = new(16);
        private readonly List<TestTarget>             _targets   = new(16);

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_lobbyManager    == null) _lobbyManager    = FindObjectOfType<LocalLobbyManager>();
            if (_objectPool      == null) _objectPool      = FindObjectOfType<LocalObjectPool>();
            if (_particlePool    == null) _particlePool    = FindObjectOfType<LocalParticlePool>();
        }

        private IEnumerator Start()
        {
            MID_Logger.LogInfo(_logLevel, "Bootstrapper starting…", nameof(TestSceneBootstrapper));

            // ── Pool init ─────────────────────────────────────────────────────
            if (_objectPool != null && !_objectPool.HasBeenInitialized())
                _objectPool.CallInitializePool();

            if (_particlePool != null && !_particlePool.HasBeenInitialized())
                _particlePool.CallInitializePool();

            // ── Register configs ──────────────────────────────────────────────
            if (_registry != null && _configs != null)
            {
                foreach (var cfg in _configs)
                {
                    if (cfg == null) continue;
                    ushort id = _registry.Register(cfg);
                    MID_Logger.LogInfo(_logLevel,
                        $"Registered '{cfg.name}' → configId={id}", nameof(TestSceneBootstrapper));
                }
            }

            // ── Subscribe to lobby game-start ─────────────────────────────────
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived += HandleGameStart;

            if (_networkManager != null)
                _networkManager.OnClientConnectedCallback += id =>
                    MID_Logger.LogInfo(_logLevel, $"Client {id} connected.",
                        nameof(TestSceneBootstrapper));

            SetLobbyUIActive(true);
            MID_Logger.LogInfo(_logLevel, "Bootstrapper ready.", nameof(TestSceneBootstrapper));
            yield break;
        }

        private void OnDestroy()
        {
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived -= HandleGameStart;

            // Unsubscribe hit event
            if (_projectileSystem != null
                && _projectileSystem.GetAuthority()?.Adapter != null)
            {
                _projectileSystem.GetAuthority().Adapter.OnProjectileHit -= OnProjectileHit;
            }
        }

        #endregion

        #region Game Start

        private void HandleGameStart(LocalLobbySnapshot snapshot)
        {
            MID_Logger.LogInfo(_logLevel,
                $"Game start — {snapshot.Players.Count} players.", nameof(TestSceneBootstrapper));

            SetLobbyUIActive(false);
            StartCoroutine(SpawnEntitiesCoroutine(snapshot));
        }

        private IEnumerator SpawnEntitiesCoroutine(LocalLobbySnapshot snapshot)
        {
            yield return null;
            yield return null;

            if (_networkManager != null && _networkManager.IsServer)
            {
                SpawnTestTargets();

                // Subscribe to projectile hit events AFTER targets are registered
                if (_projectileSystem != null
                    && _projectileSystem.GetAuthority()?.Adapter != null)
                {
                    _projectileSystem.GetAuthority().Adapter.OnProjectileHit += OnProjectileHit;
                }

                StartCoroutine(BobTargets());
            }

            if (_networkManager != null && _networkManager.IsServer && _playerPrefab != null)
            {
                for (int i = 0; i < snapshot.Players.Count; i++)
                {
                    var p = snapshot.Players[i];
                    if (p.IsBot) continue;

                    var go  = Instantiate(_playerPrefab, GetSpawnPoint(i), Quaternion.identity);
                    var no  = go.GetComponent<NetworkObject>();
                    if (no != null) no.SpawnAsPlayerObject(p.ClientId);
                }
            }
        }

        #endregion

        #region Target Spawning

        private void SpawnTestTargets()
        {
            if (_targetPrefab == null) return;

            for (int i = 0; i < _targetCount; i++)
            {
                float angle = i / (float)_targetCount * 360f * Mathf.Deg2Rad;
                var   pos   = new Vector3(
                    Mathf.Cos(angle) * _targetSpawnRadius,
                    0f,
                    Mathf.Sin(angle) * _targetSpawnRadius);

                var go = Instantiate(_targetPrefab, pos, Quaternion.identity);
                var no = go.GetComponent<NetworkObject>();
                no?.Spawn();

                var target = go.GetComponent<TestTarget>();
                if (target == null) { Debug.LogError("[Bootstrapper] Target prefab missing TestTarget component!"); continue; }

                // Assign unique ID starting at 100 to avoid collision with owner IDs
                uint regId = (uint)(100 + i);
                target.RegistrationId = regId;
                _targets.Add(target);
                _targetMap[regId] = target;

                // Subscribe to death event so we can unregister from collision system
                target.OnDestroyedServer += OnTargetDestroyed;

                // Register 2D collision target
                if (_projectileSystem != null)
                {
                    _projectileSystem.RegisterTarget2D(new CollisionTarget
                    {
                        X        = pos.x,
                        Y        = pos.y,
                        Radius   = target.GetComponent<SphereCollider>()?.radius ?? 0.6f,
                        TargetId = regId,
                        Active   = 1
                    });

                    // Register 3D collision target as well
                    _projectileSystem.RegisterTarget3D(new CollisionTarget3D
                    {
                        X        = pos.x,
                        Y        = pos.y,
                        Z        = pos.z,
                        Radius   = target.GetComponent<SphereCollider>()?.radius ?? 0.6f,
                        TargetId = regId,
                        Active   = 1
                    });
                }

                MID_Logger.LogInfo(_logLevel,
                    $"Spawned target id={regId} pos={pos}", nameof(TestSceneBootstrapper));
            }
        }

        private void OnTargetDestroyed(TestTarget target)
        {
            if (_projectileSystem == null) return;

            // Deactivate in collision system while dead
            _projectileSystem.DeactivateTarget2D(target.RegistrationId);
            _projectileSystem.DeactivateTarget3D(target.RegistrationId);
        }

        #endregion

        #region Hit Routing

        private void OnProjectileHit(ProjectileHitPayload payload)
        {
            if (!_targetMap.TryGetValue(payload.TargetId, out var target)) return;
            target.TakeDamage(payload.Damage);
        }

        #endregion

        #region Bob Animation

        private IEnumerator BobTargets()
        {
            while (true)
            {
                float t = Time.time * _targetBobSpeed;
                for (int i = 0; i < _targets.Count; i++)
                {
                    var target = _targets[i];
                    if (target == null || !target.IsSpawned) continue;

                    float phase = i / (float)Mathf.Max(_targets.Count, 1) * Mathf.PI * 2f;
                    float bobY  = Mathf.Sin(t + phase) * _targetBobAmplitude;
                    var   pos   = target.transform.position;
                    var   newY  = bobY; // keep original XZ, animate Y

                    target.transform.position = new Vector3(pos.x, newY, pos.z);

                    if (_projectileSystem != null)
                    {
                        uint regId = target.RegistrationId;

                        // Sync 2D collision (uses X and Y as 2D world coords)
                        _projectileSystem.RegisterTarget2D(new CollisionTarget
                        {
                            X        = pos.x,
                            Y        = newY,
                            Radius   = target.GetComponent<SphereCollider>()?.radius ?? 0.6f,
                            TargetId = regId,
                            Active   = (byte)(_targets[i].gameObject.activeSelf ? 1 : 0)
                        });

                        // Sync 3D collision
                        _projectileSystem.RegisterTarget3D(new CollisionTarget3D
                        {
                            X        = pos.x,
                            Y        = newY,
                            Z        = pos.z,
                            Radius   = target.GetComponent<SphereCollider>()?.radius ?? 0.6f,
                            TargetId = regId,
                            Active   = (byte)(_targets[i].gameObject.activeSelf ? 1 : 0)
                        });
                    }
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
            float a = index / (float)Mathf.Max(_targetCount, 1) * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(a) * 3f, 0f, Mathf.Sin(a) * 3f);
        }

        #endregion
    }
}
