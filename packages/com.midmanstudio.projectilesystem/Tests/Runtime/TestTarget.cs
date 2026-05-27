// TestTarget.cs
// Networked destructible 3D target — sphere mesh, no SpriteRenderer.
//
// UPDATED (GlobalFXManager API):
//   TriggerImpact calls now use the no-EffectType overload
//   TriggerImpact(Vector3, Vector3, int, float) that was added for backward
//   compatibility. No logic changes otherwise.

using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using TMPro;
using MidManStudio.Core.Audio;
using MidManStudio.Core.FX;

namespace TestGame
{
    public class TestTarget : NetworkBehaviour
    {
        #region Inspector

        [Header("Health")]
        [SerializeField] private float _maxHealth    = 100f;
        [SerializeField] private float _respawnDelay  = 3f;
        [SerializeField] private bool  _respawns      = true;

        [Header("Visuals — 3D mesh (no SpriteRenderer)")]
        [Tooltip("MeshRenderer on the body sphere. Colour lerps green→red by health.")]
        [SerializeField] private MeshRenderer _bodyRenderer;
        [SerializeField] private TMP_Text     _healthText;
        [SerializeField] private UnityEngine.UI.Image _healthBarFill;

        [Header("Death FX (GlobalFXManager)")]
        [SerializeField] private int   _deathParticleCount   = 20;
        [SerializeField] private float _deathParticleVolume  = 1f;
        [Header("Hit FX")]
        [SerializeField] private int   _hitParticleCount     = 6;

        [Header("Audio (NativeAudioBridge clip indices)")]
        [SerializeField] private int   _damageSoundClipIndex = 1;
        [SerializeField, Range(0f,1f)] private float _damageSoundVolume = 0.5f;
        [SerializeField] private int   _deathSoundClipIndex  = 2;
        [SerializeField, Range(0f,1f)] private float _deathSoundVolume  = 1.0f;

        [Header("Collision")]
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

        #endregion

        #region Events

        public event Action<TestTarget> OnDestroyedServer;

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;

            if (_bodyRenderer != null)
            {
                _bodyMaterial = new Material(_bodyRenderer.sharedMaterial);
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
                _bodyMaterial = new Material(_bodyRenderer.sharedMaterial);
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

            float currentHp = IsSpawned ? _currentHealth.Value : _offlineHp;

            if (_isDead.Value && IsSpawned) return;
            if (_offlineDead && !IsSpawned) return;

            float newHp = Mathf.Max(0f, currentHp - amount);

            if (IsSpawned)
                _currentHealth.Value = newHp;
            else
            {
                _offlineHp = newHp;
                RefreshVisuals(newHp);
                PlayHitFX(transform.position);
            }

            if (_enableLogs)
                Debug.Log($"[TestTarget] id={RegistrationId} hp={newHp:F1} dmg={amount:F1}");

            if (newHp <= 0f)
                OnDeath();
        }

        public void Kill() => TakeDamage((_offlineHp > 0 ? _offlineHp : _maxHealth) + 1f);

        #endregion

        #region Death + Respawn

        private float _offlineHp;
        private bool  _offlineDead;

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
            // Uses no-EffectType overload — defaults to EffectType.Generic
            GlobalFXManager.Instance?.TriggerImpact(
                pos, Vector3.up, _hitParticleCount, _damageSoundVolume);

            if (GlobalFXManager.Instance == null)
                MID_NativeAudioBridge.Instance?.PlayClip(
                    _damageSoundClipIndex, _damageSoundVolume);
        }

        private void PlayDeathFX(Vector3 pos)
        {
            // Uses no-EffectType overload — defaults to EffectType.Generic
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
            float fraction = _maxHealth > 0f ? Mathf.Clamp01(hp / _maxHealth) : 0f;

            if (_healthText != null)
                _healthText.text = $"{Mathf.CeilToInt(hp)}";

            if (_healthBarFill != null)
                _healthBarFill.fillAmount = fraction;

            if (_bodyMaterial != null)
            {
                _bodyMaterial.color = Color.Lerp(
                    new Color(0.9f, 0.2f, 0.1f),
                    new Color(0.2f, 0.9f, 0.3f),
                    fraction);
            }
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
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            float r = _collisionRadius;
            var sc = GetComponent<SphereCollider>();
            if (sc != null) r = sc.radius * Mathf.Max(transform.lossyScale.x, 0.01f);
            Gizmos.DrawWireSphere(transform.position, r);
        }
#endif
        #endregion
    }
}
