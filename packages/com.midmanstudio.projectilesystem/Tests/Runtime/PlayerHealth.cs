using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Audio;
using MidManStudio.Core.FX;
using MidManStudio.Core.Logging;

namespace TestGame
{
    /// <summary>
    /// PvP health/damage/respawn for players. Mirrors TestTarget.cs's proven
    /// pattern (server-owned NetworkVariable health, hit flash, death/respawn
    /// coroutine, ClientRpc-driven visuals) rather than reinventing one, minus
    /// the offline-mode branch — PvP inherently needs networking, there's no
    /// meaningful single-player case here the way there is for a shootable
    /// scene target.
    ///
    /// Routed to via TestSceneBootstrapper.ApplyHit()'s IDamageable branch —
    /// nothing here registers itself anywhere, ApplyHit finds it generically
    /// through NetworkManager.SpawnManager.SpawnedObjects.
    /// </summary>
    [RequireComponent(typeof(NetworkedDimensionPlayer))]
    [DisallowMultipleComponent]
    public class PlayerHealth : NetworkBehaviour, IDamageable
    {
        #region Inspector

        [Header("Health")]
        [SerializeField] private float _maxHealth   = 100f;
        [SerializeField] private float _respawnDelay = 3f;

        [Header("Visuals (optional)")]
        [Tooltip("Left empty, falls back to NetworkedDimensionPlayer's own _meshRenderers " +
                 "(the same ones it tints on ownership) for the hit flash.")]
        [SerializeField] private Renderer[] _bodyRenderers;
        [SerializeField] private UnityEngine.UI.Image _healthBarFill;
        [Tooltip("Whole health bar UI root — hidden while dead, shown again on respawn. " +
                 "Separate from _healthBarFill so you can hide the frame/backing too, not just the fill.")]
        [SerializeField] private GameObject _healthBarRoot;

        [Header("Hit Flash")]
        [SerializeField] private Color _flashColor    = Color.white;
        [SerializeField] private float _flashDuration = 0.07f;

        [Header("FX")]
        [SerializeField] private int _hitParticleCount   = 6;
        [SerializeField] private int _deathParticleCount = 20;

        [Header("Audio")]
        [SerializeField] private int   _damageSoundClipIndex = 1;
        [SerializeField, Range(0f, 1f)] private float _damageSoundVolume = 0.5f;
        [SerializeField] private int   _deathSoundClipIndex  = 2;
        [SerializeField, Range(0f, 1f)] private float _deathSoundVolume  = 1.0f;

        [Header("PvP")]
        [Tooltip("Damage where attackerClientId == this player's own OwnerClientId is " +
                 "ignored. Turn off if you want self-damage (e.g. rocket splash) to count.")]
        [SerializeField] private bool _ignoreSelfDamage = true;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Network State

        private readonly NetworkVariable<float> _currentHealth = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _isDead = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        #endregion

        #region Local State

        private NetworkedDimensionPlayer _player;
        private Renderer[] _flashRenderers;
        private Material[] _flashMaterials;
        private Coroutine _flashCoroutine;

        #endregion

        #region Events / IDamageable

        /// <summary>(this, attackerClientId) — hook for score-keeping, kill feed, etc.</summary>
        public event Action<PlayerHealth, ulong> OnPlayerDied;

        public bool IsAlive => !_isDead.Value;

        #endregion

        #region Unity / NGO Lifecycle

        private void Awake()
        {
            _player = GetComponent<NetworkedDimensionPlayer>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _flashRenderers = (_bodyRenderers != null && _bodyRenderers.Length > 0)
                ? _bodyRenderers
                : (_player != null ? _player.MeshRenderers : null);
            CacheFlashMaterials();

            if (IsServer) _currentHealth.Value = _maxHealth;

            _currentHealth.OnValueChanged += OnHealthChanged;
            _isDead.OnValueChanged        += OnDeadChanged;

            RefreshHealthBar(_maxHealth);
            SetHealthBarVisible(true);
        }

        public override void OnNetworkDespawn()
        {
            _currentHealth.OnValueChanged -= OnHealthChanged;
            _isDead.OnValueChanged        -= OnDeadChanged;
            base.OnNetworkDespawn();
        }

        #endregion

        #region IDamageable

        public void TakeDamage(float amount, ulong attackerClientId = ulong.MaxValue)
        {
            if (!IsSpawned || !IsServer) return; // server-authoritative, same as TestTarget
            if (_isDead.Value) return;
            if (_ignoreSelfDamage && attackerClientId == OwnerClientId) return;

            float newHp = Mathf.Max(0f, _currentHealth.Value - amount);
            _currentHealth.Value = newHp;

            MID_Logger.LogDebug(_logLevel,
                $"PlayerHealth: OwnerClientId={OwnerClientId} hp={newHp:F1} (from {attackerClientId})",
                nameof(PlayerHealth));

            if (newHp <= 0f) OnDeath(attackerClientId);
        }

        #endregion

        #region Death + Respawn

        private void OnDeath(ulong attackerClientId)
        {
            if (_isDead.Value) return;
            _isDead.Value = true;
            OnPlayerDied?.Invoke(this, attackerClientId);
            DeathClientRpc(transform.position);
            StartCoroutine(RespawnCoroutine());
        }

        private IEnumerator RespawnCoroutine()
        {
            yield return new WaitForSeconds(_respawnDelay);
            if (!IsSpawned) yield break;

            Vector3 spawnPos = TestSceneBootstrapper.Instance != null
                ? TestSceneBootstrapper.Instance.GetPlayerRespawnPoint()
                : transform.position;

            transform.SetPositionAndRotation(spawnPos, Quaternion.identity);
            _currentHealth.Value = _maxHealth;
            _isDead.Value        = false;
            RespawnClientRpc(spawnPos);
        }

        [ClientRpc]
        private void DeathClientRpc(Vector3 pos)
        {
            PlayDeathFX(pos);
            _player?.SetControlAndVisibilityEnabled(false);
            SetHealthBarVisible(false);
        }

        [ClientRpc]
        private void RespawnClientRpc(Vector3 pos)
        {
            transform.position = pos;
            _player?.SetControlAndVisibilityEnabled(true);
            SetHealthBarVisible(true);
        }

        #endregion

        #region NetworkVariable Callbacks

        private void OnHealthChanged(float oldHp, float newHp)
        {
            RefreshHealthBar(newHp);
            if (newHp < oldHp && newHp > 0f)
            {
                TriggerHitFlash();
                PlayHitFX(transform.position);
            }
        }

        // Visuals for the dead/alive transition are driven by DeathClientRpc /
        // RespawnClientRpc above (they also need to disable/re-enable movement,
        // not just visuals, so it's one RPC doing both rather than splitting
        // "state changed" from "movement changed").
        private void OnDeadChanged(bool _, bool nowDead) { }

        #endregion

        #region FX + Audio

        private void PlayHitFX(Vector3 pos)
        {
            GlobalFXManager.Instance?.TriggerImpact(pos, Vector3.up, _hitParticleCount, _damageSoundVolume);
            if (GlobalFXManager.Instance == null)
                MID_NativeAudioBridge.Instance?.PlayClip(_damageSoundClipIndex, _damageSoundVolume);
        }

        private void PlayDeathFX(Vector3 pos)
        {
            GlobalFXManager.Instance?.TriggerImpact(pos, Vector3.up, _deathParticleCount, _deathSoundVolume);
            if (GlobalFXManager.Instance == null)
                MID_NativeAudioBridge.Instance?.PlayClip(_deathSoundClipIndex, _deathSoundVolume);
        }

        private void TriggerHitFlash()
        {
            if (_flashMaterials == null || _flashMaterials.Length == 0) return;
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            _flashCoroutine = StartCoroutine(HitFlash());
        }

        private IEnumerator HitFlash()
        {
            foreach (var m in _flashMaterials)
                if (m != null) m.color = _flashColor;

            yield return new WaitForSeconds(_flashDuration);

            // Hand color control back to NetworkedDimensionPlayer's own
            // owner/remote tint rather than guessing a "default" color here.
            _player?.RefreshTint();
            _flashCoroutine = null;
        }

        private void CacheFlashMaterials()
        {
            if (_flashRenderers == null) return;
            _flashMaterials = new Material[_flashRenderers.Length];
            for (int i = 0; i < _flashRenderers.Length; i++)
                if (_flashRenderers[i] != null)
                    _flashMaterials[i] = _flashRenderers[i].material; // instances (or reuses NetworkedDimensionPlayer's own instance — .material is idempotent per-renderer)
        }

        #endregion

        #region Visuals

        private void RefreshHealthBar(float hp)
        {
            if (_healthBarFill != null)
                _healthBarFill.fillAmount = _maxHealth > 0f ? Mathf.Clamp01(hp / _maxHealth) : 0f;
        }

        private void SetHealthBarVisible(bool visible)
        {
            if (_healthBarRoot != null) _healthBarRoot.SetActive(visible);
        }

        #endregion
    }
}
