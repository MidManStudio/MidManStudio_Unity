// packages/com.midmanstudio.projectilesystem/Tests/Runtime/TestSceneBootstrapper.cs
// Initialises all required systems for the projectile test scene.
// Attach to a persistent GameObject alongside LocalLobbyManager.
//
// SETUP ORDER (Script Execution Order):
//   -100 : TestSceneBootstrapper  (must run before player spawns)
//     0  : Everything else

using System.Collections;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.LocalMultiplayer;
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
        [Tooltip("Drag all ProjectileConfigSO assets used in this test. " +
                 "Registered in order — first = configId 0, second = 1, etc.")]
        [SerializeField] private ProjectileConfigSO[] _configs;

        [Header("Test Targets  (spawned by server at game start)")]
        [SerializeField] private GameObject _targetPrefab;
        [SerializeField] private int        _targetCount        = 8;
        [SerializeField] private float      _targetRadius       = 0.6f;
        [SerializeField] private float      _targetSpawnRadius  = 8f;
        [SerializeField] private float      _targetBobAmplitude = 0.4f;
        [SerializeField] private float      _targetBobSpeed     = 1.2f;

        [Header("UI Roots")]
        [Tooltip("Canvas holding the lobby UI — disabled once game starts.")]
        [SerializeField] private Canvas _lobbyCanvas;
        [Tooltip("Canvas holding the in-game HUD — enabled once game starts.")]
        [SerializeField] private Canvas _gameHUDCanvas;

        [Header("Player Prefab")]
        [SerializeField] private GameObject _playerPrefab;

        [Header("Spawn Points")]
        [SerializeField] private Transform[] _playerSpawnPoints;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        private readonly System.Collections.Generic.List<GameObject> _spawnedTargets = new(16);
        private int _spawnIndex;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Validate required refs
            if (_lobbyManager == null)
                _lobbyManager = FindObjectOfType<LocalLobbyManager>();
            if (_objectPool == null)
                _objectPool = FindObjectOfType<LocalObjectPool>();
            if (_particlePool == null)
                _particlePool = FindObjectOfType<LocalParticlePool>();
        }

        private IEnumerator Start()
        {
            MID_Logger.LogInfo(_logLevel, "Bootstrapper starting…",
                nameof(TestSceneBootstrapper));

            // ── Pool initialisation ───────────────────────────────────────────
            if (_objectPool != null && !_objectPool.HasBeenInitialized())
            {
                _objectPool.CallInitializePool();
                MID_Logger.LogInfo(_logLevel, "Object pool initialised.",
                    nameof(TestSceneBootstrapper));
            }

            if (_particlePool != null && !_particlePool.HasBeenInitialized())
            {
                _particlePool.CallInitializePool();
                MID_Logger.LogInfo(_logLevel, "Particle pool initialised.",
                    nameof(TestSceneBootstrapper));
            }

            // ── Register projectile configs ───────────────────────────────────
            if (_registry != null && _configs != null)
            {
                foreach (var cfg in _configs)
                {
                    if (cfg == null) continue;
                    ushort id = _registry.Register(cfg);
                    MID_Logger.LogInfo(_logLevel,
                        $"Registered config '{cfg.name}' → id={id}",
                        nameof(TestSceneBootstrapper));
                }
            }

            // ── Subscribe to lobby events ─────────────────────────────────────
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived += HandleGameStart;

            // ── NGO client connect callback ───────────────────────────────────
            if (_networkManager != null)
                _networkManager.OnClientConnectedCallback += HandleClientConnected;

            // Set initial UI state
            SetLobbyUIActive(true);

            MID_Logger.LogInfo(_logLevel, "Bootstrapper ready.",
                nameof(TestSceneBootstrapper));

            yield break;
        }

        private void OnDestroy()
        {
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived -= HandleGameStart;
            if (_networkManager != null)
                _networkManager.OnClientConnectedCallback -= HandleClientConnected;
        }

        #endregion

        #region Game Start

        private void HandleGameStart(LocalLobbySnapshot snapshot)
        {
            MID_Logger.LogInfo(_logLevel,
                $"Game start — {snapshot.Players.Count} players.",
                nameof(TestSceneBootstrapper));

            SetLobbyUIActive(false);
            StartCoroutine(SpawnEntitiesCoroutine(snapshot));
        }

        private IEnumerator SpawnEntitiesCoroutine(LocalLobbySnapshot snapshot)
        {
            // Give NGO one frame to stabilise after game-start RPC
            yield return null;
            yield return null;

            // Server spawns targets and registers them as collision targets
            if (_networkManager != null && _networkManager.IsServer)
            {
                SpawnTestTargets();
                StartCoroutine(BobTargets());
            }

            // Server spawns player prefabs for each real player
            if (_networkManager != null && _networkManager.IsServer && _playerPrefab != null)
            {
                for (int i = 0; i < snapshot.Players.Count; i++)
                {
                    var p = snapshot.Players[i];
                    if (p.IsBot) continue;

                    var spawnPos = GetSpawnPoint(i);
                    var go       = Instantiate(_playerPrefab, spawnPos, Quaternion.identity);
                    var netObj   = go.GetComponent<NetworkObject>();
                    if (netObj != null)
                        netObj.SpawnAsPlayerObject(p.ClientId);
                }
            }
        }

        #endregion

        #region Targets

        private void SpawnTestTargets()
        {
            if (_targetPrefab == null) return;

            for (int i = 0; i < _targetCount; i++)
            {
                float angle = i / (float)_targetCount * 360f * Mathf.Deg2Rad;
                var pos     = new Vector3(
                    Mathf.Cos(angle) * _targetSpawnRadius,
                    0f,
                    Mathf.Sin(angle) * _targetSpawnRadius);

                var go = Instantiate(_targetPrefab, pos, Quaternion.identity);
                _spawnedTargets.Add(go);

                // Register as 2D collision target (XY plane)
                if (_projectileSystem != null)
                {
                    _projectileSystem.RegisterTarget2D(new CollisionTarget
                    {
                        X        = pos.x,
                        Y        = pos.y,
                        Radius   = _targetRadius,
                        TargetId = (uint)(100 + i),
                        Active   = 1
                    });
                }
            }

            MID_Logger.LogInfo(_logLevel,
                $"Spawned {_targetCount} test targets.",
                nameof(TestSceneBootstrapper));
        }

        private IEnumerator BobTargets()
        {
            while (true)
            {
                float t = Time.time * _targetBobSpeed;
                for (int i = 0; i < _spawnedTargets.Count; i++)
                {
                    var go = _spawnedTargets[i];
                    if (go == null) continue;

                    float phase = i / (float)_spawnedTargets.Count * Mathf.PI * 2f;
                    var p       = go.transform.position;
                    go.transform.position = new Vector3(p.x,
                        Mathf.Sin(t + phase) * _targetBobAmplitude, p.z);

                    // Sync updated Y to collision system each frame
                    if (_projectileSystem != null)
                    {
                        _projectileSystem.RegisterTarget2D(new CollisionTarget
                        {
                            X        = p.x,
                            Y        = go.transform.position.y,
                            Radius   = _targetRadius,
                            TargetId = (uint)(100 + i),
                            Active   = 1
                        });
                    }
                }
                yield return null;
            }
        }

        #endregion

        #region Helpers

        private void HandleClientConnected(ulong clientId)
        {
            MID_Logger.LogInfo(_logLevel,
                $"Client {clientId} connected.",
                nameof(TestSceneBootstrapper));
        }

        private void SetLobbyUIActive(bool active)
        {
            if (_lobbyCanvas    != null) _lobbyCanvas.gameObject.SetActive(active);
            if (_gameHUDCanvas  != null) _gameHUDCanvas.gameObject.SetActive(!active);
        }

        private Vector3 GetSpawnPoint(int index)
        {
            if (_playerSpawnPoints != null && _playerSpawnPoints.Length > 0)
                return _playerSpawnPoints[index % _playerSpawnPoints.Length].position;

            // Fallback: evenly spaced around origin
            float a = index / (float)Mathf.Max(_targetCount, 1) * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(a) * 3f, 0f, Mathf.Sin(a) * 3f);
        }

        #endregion
    }
}
