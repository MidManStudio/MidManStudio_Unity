// TestTarget.cs
// Networked destructible target for the projectile test scene.
// Attach to target prefab alongside NetworkObject.
//
// SETUP:
//   - Prefab needs: NetworkObject, Collider (or Collider2D), MeshRenderer/SpriteRenderer
//   - Server spawns these via TestSceneBootstrapper
//   - Health is a NetworkVariable so all clients see the correct HP bar
//   - When health reaches 0 the server despawns after a short delay (respawn optional)
//
// LISTENING FOR HITS:
//   TestSceneBootstrapper subscribes MID_MasterProjectileSystem.OnHit-style events.
//   Alternatively, wire directly: subscribe to ServerProjectileAuthority's
//   Adapter.OnProjectileHit and check TargetId == our registered ID.

using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using TMPro;

namespace TestGame
{
    public class TestTarget : NetworkBehaviour
    {
        #region Inspector

        [Header("Health")]
        [SerializeField] private float _maxHealth   = 100f;
        [SerializeField] private float _respawnDelay = 3f;
        [Tooltip("If false the object is simply despawned and not respawned.")]
        [SerializeField] private bool  _respawns    = true;

        [Header("Visuals")]
        [SerializeField] private Renderer _bodyRenderer;
        [SerializeField] private TMP_Text _healthText;

        [Tooltip("Optional health-bar fill image (scale X 0-1 by health fraction).")]
        [SerializeField] private UnityEngine.UI.Image _healthBarFill;

        [Header("Death FX")]
        [Tooltip("Particle system played locally on death (not networked — each client plays it).")]
        [SerializeField] private ParticleSystem _deathParticles;

        [Header("Collision")]
        [Tooltip("Radius used for projectile system hit registration. "  +
                 "Should match or slightly exceed the visual collider radius.")]
        [SerializeField] private float _collisionRadius = 0.6f;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region Network State

        private readonly NetworkVariable<float> _currentHealth = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _isDead = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        #endregion

        #region Local State

        // Assigned by TestSceneBootstrapper after spawn so the projectile system
        // can look us up by this ID in its collision results.
        public uint RegistrationId { get; set; }

        // Spawn position — used for respawn
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;

        // Cache original colour for hit-flash tween
        private Color _baseColor;
        private Coroutine _flashCoroutine;

        #endregion

        #region Events

        /// <summary>Fired on the server when this target reaches 0 HP.</summary>
        public event Action<TestTarget> OnDestroyedServer;

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;

            if (IsServer)
                _currentHealth.Value = _maxHealth;

            // Subscribe to changes so non-server clients update their UI
            _currentHealth.OnValueChanged += OnHealthChanged;
            _isDead.OnValueChanged        += OnDeadChanged;

            // Cache base colour
            if (_bodyRenderer != null)
                _baseColor = _bodyRenderer.material.color;

            RefreshUI(_maxHealth);
        }

        public override void OnNetworkDespawn()
        {
            _currentHealth.OnValueChanged -= OnHealthChanged;
            _isDead.OnValueChanged        -= OnDeadChanged;
            base.OnNetworkDespawn();
        }

        #endregion

        #region Public API — called by TestSceneBootstrapper or damage system

        /// <summary>
        /// Apply damage to this target. Server-only.
        /// </summary>
        public void TakeDamage(float amount)
        {
            if (!IsServer || _isDead.Value) return;

            _currentHealth.Value = Mathf.Max(0f, _currentHealth.Value - amount);

            if (_enableLogs)
                Debug.Log($"[TestTarget] id={RegistrationId} hp={_currentHealth.Value:F1} " +
                          $"damage={amount:F1}");

            if (_currentHealth.Value <= 0f)
                ServerOnDeath();
        }

        /// <summary>
        /// Instantly kill. Server-only.
        /// </summary>
        public void Kill() => TakeDamage(_currentHealth.Value + 1f);

        /// <summary>
        /// Restore full health (server-only). Useful for manual testing.
        /// </summary>
        public void Revive()
        {
            if (!IsServer) return;
            _currentHealth.Value = _maxHealth;
            _isDead.Value        = false;
        }

        #endregion

        #region Server Death / Respawn

        private void ServerOnDeath()
        {
            if (_isDead.Value) return; // guard double-call
            _isDead.Value = true;

            OnDestroyedServer?.Invoke(this);
            DeathClientRpc();

            if (_respawns)
                StartCoroutine(RespawnCoroutine());
            else
                StartCoroutine(DespawnAfterDelay(1.5f));
        }

        private IEnumerator RespawnCoroutine()
        {
            yield return new WaitForSeconds(_respawnDelay);
            if (!IsSpawned) yield break;

            // Reset position and health
            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            _currentHealth.Value = _maxHealth;
            _isDead.Value        = false;

            // Notify clients to show the target again
            RespawnClientRpc(_spawnPosition, _spawnRotation);
        }

        private IEnumerator DespawnAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (IsSpawned) NetworkObject.Despawn();
        }

        #endregion

        #region Client RPCs

        [ClientRpc]
        private void DeathClientRpc()
        {
            // Play death FX locally on every client
            if (_deathParticles != null)
                _deathParticles.Play();

            // Hide body
            if (_bodyRenderer != null)
                _bodyRenderer.enabled = false;
        }

        [ClientRpc]
        private void RespawnClientRpc(Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);
            if (_bodyRenderer != null)
                _bodyRenderer.enabled = true;
        }

        #endregion

        #region NetworkVariable Callbacks

        private void OnHealthChanged(float oldHp, float newHp)
        {
            RefreshUI(newHp);

            // Hit flash on damage
            if (newHp < oldHp && newHp > 0f)
                TriggerHitFlash();
        }

        private void OnDeadChanged(bool wasAlive, bool nowDead)
        {
            // Server-side RPC already handles client cosmetics; this handles
            // late-joining clients who receive the initial value.
            if (nowDead && _bodyRenderer != null)
                _bodyRenderer.enabled = false;
            else if (!nowDead && _bodyRenderer != null)
                _bodyRenderer.enabled = true;
        }

        #endregion

        #region UI Helpers

        private void RefreshUI(float hp)
        {
            float fraction = _maxHealth > 0f ? Mathf.Clamp01(hp / _maxHealth) : 0f;

            if (_healthText != null)
                _healthText.text = $"{Mathf.CeilToInt(hp)}";

            if (_healthBarFill != null)
                _healthBarFill.fillAmount = fraction;

            if (_bodyRenderer != null)
            {
                // Tint body from green (full) → red (empty)
                Color healthy  = new Color(0.2f, 0.9f, 0.3f);
                Color damaged  = new Color(0.9f, 0.2f, 0.1f);
                _bodyRenderer.material.color = Color.Lerp(damaged, healthy, fraction);
            }
        }

        private void TriggerHitFlash()
        {
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            _flashCoroutine = StartCoroutine(HitFlash());
        }

        private IEnumerator HitFlash()
        {
            if (_bodyRenderer == null) yield break;
            _bodyRenderer.material.color = Color.white;
            yield return new WaitForSeconds(0.07f);
            // RefreshUI will correct the colour on next NetworkVariable callback,
            // but we can do it eagerly here.
            RefreshUI(_currentHealth.Value);
            _flashCoroutine = null;
        }

        #endregion

        #region Gizmo (editor)
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, _collisionRadius);
        }
#endif
        #endregion
    }
}
