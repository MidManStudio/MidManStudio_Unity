// TestSceneBootstrapper.cs
//
// FIXES:
//   + Targets spawn in OFFLINE mode too (no NetworkManager / not server).
//   + Subscribes BOTH ServerProjectileAuthority.Adapter.OnProjectileHit (networked)
//     AND LocalProjectileManager.OnHit (offline) for damage routing.
//   + Hit events → GlobalFXManager.TriggerImpact + MID_NativeAudioBridge.PlayClip.
//   + _autoSpawnOffline flag: when true and no LocalLobbyManager is found,
//     targets and player spawn immediately in Start().
//   + All 3D collision targets registered (CollisionTarget3D) alongside 2D.
//   + BobTargets syncs both 2D and 3D collision positions every frame.
//
// FIX (layer registration):
//   RegisterTargetCollision now passes go.layer to RegisterTarget2D/3D so the
//   projectile system's HitLayers mask can filter per-target layer correctly.
//   Targets will only be hit by projectiles whose HitLayers includes the target's layer.
//
// UPDATED (GlobalFXManager API):
//   TriggerImpact and TriggerMuzzleFlash calls now use the backward-compatible
//   no-EffectType overloads added in GlobalFXManager.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Audio;
using MidManStudio.Core.Effects;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.LocalMultiplayer;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;

namespace TestGame
{
    [DefaultExecutionOrder(100)]
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

        [Header("Configs to Register")]
        [SerializeField] private ProjectileConfigSO[] _configs;

        [Header("Test Targets")]
        [Tooltip("Prefab: TestTarget + MeshFilter + MeshRenderer + SphereCollider + NetworkObject (optional)")]
        [SerializeField] private GameObject _targetPrefab;
        [SerializeField] private int        _targetCount        = 8;
        [SerializeField] private float      _targetSpawnRadius  = 8f;
        [SerializeField] private float      _targetCollisionRadius = 0.6f;
        [SerializeField] private float      _targetBobAmplitude = 0.4f;
        [SerializeField] private float      _targetBobSpeed     = 1.2f;

        [Header("Offline / Auto Spawn")]
        [Tooltip("When true and no lobby session started, spawn targets+player immediately.\n" +
                 "Use this for solo projectile testing without a multiplayer lobby.")]
        [SerializeField] private bool _autoSpawnOffline = true;

        [Header("Player Prefab")]
        [SerializeField] private GameObject _playerPrefab;
        [SerializeField] private Transform[] _playerSpawnPoints;

        [Header("UI Roots")]
        [SerializeField] private Canvas _lobbyCanvas;
        [SerializeField] private Canvas _gameHUDCanvas;

        [Header("Audio — NativeAudioBridge clip indices")]
        [Tooltip("Clip index for projectile impact sound.")]
        [SerializeField] private int   _hitSoundClipIndex = 1;
        [SerializeField, Range(0f,1f)] private float _hitSoundVolume = 0.5f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        private readonly Dictionary<uint, TestTarget> _targetMap = new(16);
        private readonly List<TestTarget>             _targets   = new(16);
        private bool _sessionStarted;

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
            // ── Pool init ─────────────────────────────────────────────────────
            if (_objectPool != null && !_objectPool.HasBeenInitialized())
                _objectPool.CallInitializePool();
            if (_particlePool != null && !_particlePool.HasBeenInitialized())
                _particlePool.CallInitializePool();

            // ── Register projectile configs ───────────────────────────────────
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

            // ── Lobby event ───────────────────────────────────────────────────
            if (_lobbyManager != null)
                _lobbyManager.OnGameStartReceived += HandleGameStart;

            SetLobbyUIActive(true);

            // ── Offline auto-spawn ────────────────────────────────────────────
            if (_autoSpawnOffline && _lobbyManager == null)
            {
                yield return null; // one frame for all Awake()s to finish
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
            MID_Logger.LogInfo(_logLevel,
                $"Lobby game start — {snapshot.Players.Count} players.", nameof(TestSceneBootstrapper));
            SetLobbyUIActive(false);
            StartCoroutine(NetworkedSessionCoroutine(snapshot));
        }

        private void StartOfflineSession()
        {
            if (_sessionStarted) return;
            _sessionStarted = true;

            MID_Logger.LogInfo(_logLevel, "Starting offline test session.", nameof(TestSceneBootstrapper));
            SetLobbyUIActive(false);

            SpawnTestTargets(networked: false);
            SubscribeHitEvents();
            StartCoroutine(BobTargets());

            // Spawn local player (no NetworkObject needed)
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
                SpawnTestTargets(networked: true);
                SubscribeHitEvents();
                StartCoroutine(BobTargets());

                // Spawn player NetworkObjects
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

        #region Target Spawning

        private void SpawnTestTargets(bool networked)
        {
            if (_targetPrefab == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "Target prefab not assigned!", nameof(TestSceneBootstrapper));
                return;
            }

            for (int i = 0; i < _targetCount; i++)
            {
                float angle = i / (float)_targetCount * 360f * Mathf.Deg2Rad;
                var   pos   = new Vector3(
                    Mathf.Cos(angle) * _targetSpawnRadius,
                    0f,
                    Mathf.Sin(angle) * _targetSpawnRadius);

                var go = Instantiate(_targetPrefab, pos, Quaternion.identity);

                if (networked)
                    go.GetComponent<NetworkObject>()?.Spawn();

                var target = go.GetComponent<TestTarget>();
                if (target == null)
                {
                    Debug.LogError("[Bootstrapper] Target prefab is missing TestTarget component!");
                    continue;
                }

                uint regId          = (uint)(100 + i);
                target.RegistrationId = regId;
                _targets.Add(target);
                _targetMap[regId] = target;

                target.OnDestroyedServer += OnTargetDestroyed;

                // Determine collision radius: prefer SphereCollider, else inspector value
                float radius = _targetCollisionRadius;
                var sc = go.GetComponent<SphereCollider>();
                if (sc != null) radius = sc.radius * Mathf.Max(go.transform.lossyScale.x, 0.01f);

                // FIX: pass the target's Unity layer so HitLayers mask filtering works.
                // Set the "Target" layer on the prefab and exclude it from your player's
                // projectile HitLayers to prevent friendly-fire between pattern shots.
                int targetLayer = go.layer;
                RegisterTargetCollision(pos, regId, radius, targetLayer);

                MID_Logger.LogDebug(_logLevel,
                    $"Spawned target id={regId} pos={pos} r={radius:F2} layer={targetLayer}",
                    nameof(TestSceneBootstrapper));
            }

            MID_Logger.LogInfo(_logLevel,
                $"Spawned {_targets.Count} test targets (networked={networked}).",
                nameof(TestSceneBootstrapper));
        }

        private void RegisterTargetCollision(Vector3 pos, uint regId, float radius, int unityLayer)
        {
            if (_projectileSystem == null) return;

            _projectileSystem.RegisterTarget2D(new CollisionTarget
            {
                X = pos.x, Y = pos.y,
                Radius   = radius,
                TargetId = regId,
                Active   = 1
            }, unityLayer);

            _projectileSystem.RegisterTarget3D(new CollisionTarget3D
            {
                X = pos.x, Y = pos.y, Z = pos.z,
                Radius   = radius,
                TargetId = regId,
                Active   = 1
            }, unityLayer);
        }

        private void OnTargetDestroyed(TestTarget target)
        {
            _projectileSystem?.DeactivateTarget2D(target.RegistrationId);
            _projectileSystem?.DeactivateTarget3D(target.RegistrationId);
        }

        #endregion

        #region Hit Event Subscription + Routing

        private void SubscribeHitEvents()
        {
            // Networked: server authority adapter
            if (_projectileSystem?.GetAuthority()?.Adapter != null)
                _projectileSystem.GetAuthority().Adapter.OnProjectileHit += OnProjectileHit;

            // Offline: local manager
            if (LocalProjectileManager.HasInstance)
                LocalProjectileManager.Instance.OnHit += OnLocalHit;
        }

        private void UnsubscribeHitEvents()
        {
            if (_projectileSystem?.GetAuthority()?.Adapter != null)
                _projectileSystem.GetAuthority().Adapter.OnProjectileHit -= OnProjectileHit;

            if (LocalProjectileManager.HasInstance)
                LocalProjectileManager.Instance.OnHit -= OnLocalHit;
        }

        // Networked hit
        private void OnProjectileHit(ProjectileHitPayload payload)
        {
            ApplyHit(payload.TargetId, payload.Damage, payload.HitPosition);
        }

        // LocalOnly / offline hit
        private void OnLocalHit(LocalHitPayload payload)
        {
            ApplyHit(payload.RawTargetId, payload.Damage, payload.HitPosition);
        }

        private void ApplyHit(uint targetId, float damage, Vector3 hitPos)
        {
            // Damage the target
            if (_targetMap.TryGetValue(targetId, out var target))
                target.TakeDamage(damage);

            // Impact FX via GlobalFXManager — uses the no-EffectType overload (defaults to Generic)
            GlobalFXManager.Instance?.TriggerImpact(
                hitPos, Vector3.up,
                particleCount: 6,
                volumeOverride: _hitSoundVolume);

            // Impact sound fallback when GlobalFXManager is absent or has no audio configured
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
                for (int i = 0; i < _targets.Count; i++)
                {
                    var tgt = _targets[i];
                    if (tgt == null || !tgt.gameObject.activeSelf) continue;

                    float phase = i / (float)Mathf.Max(_targets.Count, 1) * Mathf.PI * 2f;
                    float newY  = Mathf.Sin(t + phase) * _targetBobAmplitude;
                    var   pos   = tgt.transform.position;
                    tgt.transform.position = new Vector3(pos.x, newY, pos.z);

                    if (_projectileSystem == null) continue;

                    uint   regId  = tgt.RegistrationId;
                    float  radius = _targetCollisionRadius;
                    var    sc     = tgt.GetComponent<SphereCollider>();
                    if (sc != null)
                        radius = sc.radius * Mathf.Max(tgt.transform.lossyScale.x, 0.01f);

                    byte active = (byte)(tgt.gameObject.activeSelf ? 1 : 0);
                    int  layer  = tgt.gameObject.layer;

                    _projectileSystem.RegisterTarget2D(new CollisionTarget
                    {
                        X = pos.x, Y = newY,
                        Radius = radius, TargetId = regId, Active = active
                    }, layer);

                    _projectileSystem.RegisterTarget3D(new CollisionTarget3D
                    {
                        X = pos.x, Y = newY, Z = pos.z,
                        Radius = radius, TargetId = regId, Active = active
                    }, layer);
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
            return new Vector3(Mathf.Cos(a) * 3f, 0.5f, Mathf.Sin(a) * 3f);
        }

        #endregion
    }
}
