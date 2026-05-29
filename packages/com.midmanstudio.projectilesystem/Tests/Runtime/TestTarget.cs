// TestTarget.cs
// Networked destructible target — works for both 2D and 3D raycast/collision modes.
//
// COLLIDER SETUP (both can coexist on the same GameObject):
//   SphereCollider   — hit by Physics.Raycast (Raycast3D mode) and
//                      PhysicsProjectile3D collision
//   CircleCollider2D — hit by Physics2D.Raycast (Raycast2D mode) and
//                      PhysicsProjectile2D collision
//   Both live on the same prefab. Unity 2D and 3D physics are fully separate
//   and do not interfere with each other.
//
// EnsureColliders() in Awake adds CircleCollider2D automatically if not present,
// matching the SphereCollider radius so both colliders cover the same area.
//
// UPDATED (GlobalFXManager API):
//   TriggerImpact calls use the no-EffectType overload
//   TriggerImpact(Vector3, Vector3, int, float) for backward compatibility.

using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using TMPro;
using MidManStudio.Core.Audio;
using MidManStudio.Core.FX;

namespace TestGame
{
    [RequireComponent(typeof(SphereCollider))]
    public class TestTarget : NetworkBehaviour
    {
        #region Inspector

        [Header("Health")]
        [SerializeField] private float _maxHealth   = 100f;
        [SerializeField] private float _respawnDelay = 3f;
        [SerializeField] private bool  _respawns     = true;

        [Header("Visuals — 3D mesh")]
        [SerializeField] private MeshRenderer _bodyRenderer;
        [SerializeField] private TMP_Text     _healthText;
        [SerializeField] private UnityEngine.UI.Image _healthBarFill;

        [Header("Death FX")]
        [SerializeField] private int   _deathParticleCount  = 20;
        [SerializeField] private float _deathParticleVolume = 1f;

        [Header("Hit FX")]
        [SerializeField] private int   _hitParticleCount = 6;

        [Header("Audio")]
        [SerializeField] private int   _damageSoundClipIndex = 1;
        [SerializeField, Range(0f,1f)] private float _damageSoundVolume = 0.5f;
        [SerializeField] private int   _deathSoundClipIndex  = 2;
        [SerializeField, Range(0f,1f)] private float _deathSoundVolume  = 1.0f;

        [Header("Collision")]
        [Tooltip("Radius used for both SphereCollider (3D) and auto-added CircleCollider2D.\n" +
                 "Both colliders are kept in sync so Raycast2D and Raycast3D both hit.")]
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

        public uint RegistrationId { get; set; }

        private Vector3    _spawnPosition;
        private Quaternion _spawnRotation;
        private Material   _bodyMaterial;
        private Coroutine  _flashCoroutine;

        private float _offlineHp;
        private bool  _offlineDead;

        #endregion

        #region Events

        public event Action<TestTarget> OnDestroyedServer;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            EnsureColliders();
        }

        /// <summary>
        /// Ensures both a SphereCollider (3D) and a CircleCollider2D (2D) are present
        /// on this GameObject so it can be hit by both Raycast3D and Raycast2D modes,
        /// and by both PhysicsProjectile3D and PhysicsProjectile2D.
        ///
        /// SphereCollider is guaranteed by [RequireComponent].
        /// CircleCollider2D is added here automatically if missing, with radius
        /// matching _collisionRadius.
        /// </summary>
        private void EnsureColliders()
        {
            // Sync 3D sphere collider radius
            var sc = GetComponent<SphereCollider>();
            if (sc != null) sc.radius = _collisionRadius;

            // Auto-add 2D circle collider if not present
            var cc = GetComponent<CircleCollider2D>();
            if (cc == null)
            {
                cc = gameObject.AddComponent<CircleCollider2D>();
                if (_enableLogs)
                    Debug.Log(
                        $"[TestTarget] Added CircleCollider2D to '{name}' " +
                        "so Raycast2D mode can hit this target.", this);
            }
            cc.radius = _collisionRadius;
        }

        private void OnValidate()
        {
            // Keep collider radii in sync when _collisionRadius changes in inspector
            var sc = GetComponent<SphereCollider>();
            if (sc != null) sc.radius = _collisionRadius;
            var cc = GetComponent<CircleCollider2D>();
            if (cc != null) cc.radius = _collisionRadius;
        }

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;

            if (_bodyRenderer != null)
            {
                _bodyMaterial          = new Material(_bodyRenderer.sharedMaterial);
                _bodyRenderer.material = _bodyMaterial;
            }

            if (IsServer)
                _currentHealth.Value = _maxHealth;

            _currentHealth.OnValueChanged += OnHealthChanged;
            _isDead.OnValueChanged        += OnDeadChanged;

            RefreshVisuals(_maxHealth);
        }

        public override void OnNetworkDespawn()
        {
            _currentHealth.OnValueChanged -= OnHealthChanged;
            _isDead.OnValueChanged        -= OnDeadChanged;
            if (_bodyMaterial != null) Destroy(_bodyMaterial);
            base.OnNetworkDespawn();
        }

        // For offline (no NetworkObject) use
        private void Start()
        {
            if (IsSpawned) return;

            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;

            if (_bodyRenderer != null)
            {
                _bodyMaterial          = new Material(_bodyRenderer.sharedMaterial);
                _bodyRenderer.material = _bodyMaterial;
            }

            _offlineHp = _maxHealth;
            RefreshVisuals(_maxHealth);
        }

        private void OnDestroy()
        {
            if (_bodyMaterial != null) Destroy(_bodyMaterial);
        }

        #endregion

        #region Public API

        public void TakeDamage(float amount)
        {
            bool canAct = !IsSpawned || IsServer;
            if (!canAct) return;

            if (IsSpawned && _isDead.Value) return;
            if (!IsSpawned && _offlineDead)  return;

            float currentHp = IsSpawned ? _currentHealth.Value : _offlineHp;
            float newHp     = Mathf.Max(0f, currentHp - amount);

            if (IsSpawned)
                _currentHealth.Value = newHp;
            else
            {
                _offlineHp = newHp;
                RefreshVisuals(newHp);
                PlayHitFX(transform.position);
            }

            if (_enableLogs)
                Debug.Log(
                    $"[TestTarget] id={RegistrationId} hp={newHp:F1} dmg={amount:F1}");

            if (newHp <= 0f) OnDeath();
        }

        public void Kill() =>
            TakeDamage((_offlineHp > 0 ? _offlineHp : _maxHealth) + 1f);

        #endregion

        #region Death + Respawn

        private void OnDeath()
        {
            if (IsSpawned)
            {
                if (_isDead.Value) return;
                _isDead.Value = true;
                OnDestroyedServer?.Invoke(this);
                DeathClientRpc(transform.position);
                if (_respawns) StartCoroutine(RespawnCoroutine());
                else           StartCoroutine(DespawnAfterDelay(1.5f));
            }
            else
            {
                if (_offlineDead) return;
                _offlineDead = true;
                OnDestroyedServer?.Invoke(this);
                PlayDeathFX(transform.position);
                if (_bodyRenderer != null) _bodyRenderer.enabled = false;
                if (_respawns) StartCoroutine(OfflineRespawnCoroutine());
                else           Destroy(gameObject, 1.5f);
            }
        }

        private IEnumerator RespawnCoroutine()
        {
            yield return new WaitForSeconds(_respawnDelay);
            if (!IsSpawned) yield break;
            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            _currentHealth.Value = _maxHealth;
            _isDead.Value        = false;
            RespawnClientRpc(_spawnPosition, _spawnRotation);
        }

        private IEnumerator OfflineRespawnCoroutine()
        {
            yield return new WaitForSeconds(_respawnDelay);
            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            _offlineHp   = _maxHealth;
            _offlineDead = false;
            if (_bodyRenderer != null) _bodyRenderer.enabled = true;
            RefreshVisuals(_maxHealth);
        }

        private IEnumerator DespawnAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (IsSpawned) NetworkObject.Despawn();
        }

        #endregion

        #region Client RPCs

        [ClientRpc]
        private void DeathClientRpc(Vector3 pos)
        {
            PlayDeathFX(pos);
            if (_bodyRenderer != null) _bodyRenderer.enabled = false;
        }

        [ClientRpc]
        private void RespawnClientRpc(Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);
            if (_bodyRenderer != null) _bodyRenderer.enabled = true;
        }

        #endregion

        #region NetworkVariable Callbacks

        private void OnHealthChanged(float oldHp, float newHp)
        {
            RefreshVisuals(newHp);
            if (newHp < oldHp && newHp > 0f)
            {
                TriggerHitFlash();
                PlayHitFX(transform.position);
            }
        }

        private void OnDeadChanged(bool _, bool nowDead)
        {
            if (_bodyRenderer != null)
                _bodyRenderer.enabled = !nowDead;
        }

        #endregion

        #region FX + Audio

        private void PlayHitFX(Vector3 pos)
        {
            GlobalFXManager.Instance?.TriggerImpact(
                pos, Vector3.up, _hitParticleCount, _damageSoundVolume);
            if (GlobalFXManager.Instance == null)
                MID_NativeAudioBridge.Instance?.PlayClip(
                    _damageSoundClipIndex, _damageSoundVolume);
        }

        private void PlayDeathFX(Vector3 pos)
        {
            GlobalFXManager.Instance?.TriggerImpact(
                pos, Vector3.up, _deathParticleCount, _deathSoundVolume);
            if (GlobalFXManager.Instance == null)
                MID_NativeAudioBridge.Instance?.PlayClip(
                    _deathSoundClipIndex, _deathSoundVolume);
        }

        #endregion

        #region Visuals

        private void RefreshVisuals(float hp)
        {
            float fraction = _maxHealth > 0f
                ? Mathf.Clamp01(hp / _maxHealth) : 0f;

            if (_healthText     != null) _healthText.text       = $"{Mathf.CeilToInt(hp)}";
            if (_healthBarFill  != null) _healthBarFill.fillAmount = fraction;

            if (_bodyMaterial != null)
                _bodyMaterial.color = Color.Lerp(
                    new Color(0.9f, 0.2f, 0.1f),
                    new Color(0.2f, 0.9f, 0.3f),
                    fraction);
        }

        private void TriggerHitFlash()
        {
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            _flashCoroutine = StartCoroutine(HitFlash());
        }

        private IEnumerator HitFlash()
        {
            if (_bodyMaterial == null) yield break;
            _bodyMaterial.color = Color.white;
            yield return new WaitForSeconds(0.07f);
            RefreshVisuals(IsSpawned ? _currentHealth.Value : _offlineHp);
            _flashCoroutine = null;
        }

        #endregion

        #region Gizmo
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // 3D sphere
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, _collisionRadius);
            // 2D circle (drawn as a flat disc at target position)
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.3f);
            Gizmos.DrawWireSphere(
                transform.position, _collisionRadius * 0.98f); // slightly smaller so both visible
        }
#endif
        #endregion
    }
}
