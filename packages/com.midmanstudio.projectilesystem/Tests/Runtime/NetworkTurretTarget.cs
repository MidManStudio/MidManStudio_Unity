using System.Collections;
using UnityEngine;
using Unity.Netcode;
using TMPro;
using MidManStudio.Core.Audio;
using MidManStudio.Core.FX;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Config;

namespace TestGame
{
    /// <summary>
    /// Scene-placed 3D turret. The "can be shot" half is TestTarget.cs's exact
    /// pattern (server-owned health, hit flash, death/respawn coroutine) — kept
    /// as close to verbatim as the new pieces allow, since that pattern is
    /// already proven. New on top of it:
    ///   - Detects the nearest living player within _detectionRadius and fires
    ///     real physics projectiles at them via MID_MasterProjectileSystem,
    ///     using a WeaponDefinitionSO for pool type/speed/damage/muzzle FX —
    ///     the exact same asset type player weapons use, so a turret can
    ///     literally share a weapon definition with a player gun if you want.
    ///   - _healthBarRoot is shown/hidden around death/respawn, not just the
    ///     fill amount like TestTarget's _healthBarFill alone does.
    ///
    /// Routed to via TestSceneBootstrapper.ApplyHit()'s IDamageable branch, same
    /// as PlayerHealth — no bootstrapper-side registration needed.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    [DisallowMultipleComponent]
    public class NetworkTurretTarget : NetworkBehaviour, IDamageable
    {
        #region Inspector

        [Header("Health")]
        [SerializeField] private float _maxHealth   = 150f;
        [SerializeField] private float _respawnDelay = 5f;
        [SerializeField] private bool  _respawns     = true;

        [Header("Visuals")]
        [SerializeField] private Renderer[] _bodyRenderers;
        [SerializeField] private TMP_Text   _healthText;
        [SerializeField] private UnityEngine.UI.Image _healthBarFill;
        [Tooltip("Whole health bar UI root — hidden while dead, shown again once respawned/healed.")]
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

        [Header("Collision Radius")]
        [Tooltip("Radius applied to SphereCollider. Must match what you register in TestSceneBootstrapper.")]
        [SerializeField] private float _collisionRadius = 0.6f;

        [Header("Attack")]
        [Tooltip("Reuses the same WeaponDefinitionSO type player weapons use — " +
                 "pool type, projectile speed, damage multiplier and muzzle FX all " +
                 "come from here. A turret can share a weapon asset with a player gun.")]
        [SerializeField] private WeaponDefinitionSO _weapon;
        [SerializeField] private Transform _muzzle;
        [SerializeField] private float _detectionRadius = 12f;
        [SerializeField] private float _fireInterval     = 1.5f;

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

        public uint RegistrationId { get; set; }

        private Vector3    _spawnPosition;
        private Quaternion _spawnRotation;
        private Material[] _bodyMaterials;
        private Coroutine  _flashCoroutine;
        private float      _nextFireTime;

        public bool IsAlive => !_isDead.Value;
        private Vector3 MuzzlePosition => _muzzle != null ? _muzzle.position : transform.position;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            var sc = GetComponent<SphereCollider>();
            if (sc != null) sc.radius = _collisionRadius;
        }

        private void OnValidate()
        {
            var sc = GetComponent<SphereCollider>();
            if (sc != null) sc.radius = _collisionRadius;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || _isDead.Value) return;
            if (Time.time < _nextFireTime) return;

            var target = FindNearestLivingPlayer();
            if (target == null) return;

            _nextFireTime = Time.time + _fireInterval;
            FireAtPlayer(target);
        }

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;

            CacheBodyMaterials();

            if (IsServer) _currentHealth.Value = _maxHealth;

            _currentHealth.OnValueChanged += OnHealthChanged;
            _isDead.OnValueChanged        += OnDeadChanged;

            RefreshVisuals(_maxHealth);
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
            if (!IsSpawned || !IsServer) return;
            if (_isDead.Value) return;

            float newHp = Mathf.Max(0f, _currentHealth.Value - amount);
            _currentHealth.Value = newHp;

            MID_Logger.LogDebug(_logLevel, $"NetworkTurretTarget: id={RegistrationId} hp={newHp:F1}", nameof(NetworkTurretTarget));

            if (newHp <= 0f) OnDeath();
        }

        #endregion

        #region Attack

        private NetworkObject FindNearestLivingPlayer()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;

            NetworkObject best = null;
            float bestDistSq = _detectionRadius * _detectionRadius;
            Vector3 origin = MuzzlePosition;

            foreach (var clientId in nm.ConnectedClientsIds)
            {
                var playerObj = nm.SpawnManager.GetPlayerNetworkObject(clientId);
                if (playerObj == null) continue;

                var health = playerObj.GetComponent<PlayerHealth>();
                if (health != null && !health.IsAlive) continue;

                float d = (playerObj.transform.position - origin).sqrMagnitude;
                if (d < bestDistSq) { bestDistSq = d; best = playerObj; }
            }
            return best;
        }

        private void FireAtPlayer(NetworkObject target)
        {
            if (_weapon == null || !MID_MasterProjectileSystem.HasInstance) return;

            Vector3 origin = MuzzlePosition;
            Vector3 dir    = (target.transform.position - origin);
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            ushort cfgId = ResolveConfigId();
            Quaternion rot = Quaternion.LookRotation(dir);

            var netObj = MID_MasterProjectileSystem.Instance.SpawnPhysicsProjectile(
                _weapon.PhysicsPoolType3D, origin, rot, cfgId);
            if (netObj == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FireAtPlayer: pool null for {_weapon.PhysicsPoolType3D}.", nameof(NetworkTurretTarget));
                return;
            }

            var proj = netObj.GetComponent<PhysicsProjectileBase>();
            if (proj != null)
            {
                // ulong.MaxValue = no owning player. IDamageable's own default
                // parameter value is the same sentinel, and PlayerHealth's
                // self-damage check (attackerClientId == OwnerClientId) can
                // never accidentally match it, so this reads as "environmental"
                // everywhere it's checked.
                proj.SetOwnerContext(ulong.MaxValue, NetworkObjectId, false, 1, _weapon.PhysicsDamageMultiplier);
                proj.InitialiseProjectile(ulong.MaxValue, NetworkObjectId, _weapon.PhysicsProjectileSpeed, false, 1);
            }

            GlobalFXManager.Instance?.TriggerMuzzleFlash(
                origin, dir, _weapon.MuzzleFlashParticleCount, _weapon.MuzzleFlashVolume);
        }

        private ushort ResolveConfigId()
        {
            if (_weapon == null) return ushort.MaxValue;
            if (ProjectileConfigManager.HasInstance)
                return ProjectileConfigManager.Instance.GetConfigId(_weapon.ConfigTypeId3D);
            return (ushort)_weapon.ConfigTypeId3D;
        }

        #endregion

        #region Death + Respawn

        private void OnDeath()
        {
            if (_isDead.Value) return;
            _isDead.Value = true;
            DeathClientRpc(transform.position);
            if (_respawns) StartCoroutine(RespawnCoroutine());
            else           StartCoroutine(DespawnAfterDelay(1.5f));
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

        private IEnumerator DespawnAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (IsSpawned) NetworkObject.Despawn();
        }

        #endregion

        #region RPCs

        [ClientRpc]
        private void DeathClientRpc(Vector3 pos)
        {
            PlayDeathFX(pos);
            SetBodyVisible(false);
            SetHealthBarVisible(false);
        }

        [ClientRpc]
        private void RespawnClientRpc(Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);
            SetBodyVisible(true);
            SetHealthBarVisible(true);
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

        // Body/health-bar visibility for the dead/alive transition is handled
        // by DeathClientRpc / RespawnClientRpc above, alongside FX — kept together
        // rather than split across this callback too.
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
            if (_bodyMaterials == null || _bodyMaterials.Length == 0) return;
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            _flashCoroutine = StartCoroutine(HitFlash());
        }

        private IEnumerator HitFlash()
        {
            foreach (var m in _bodyMaterials)
                if (m != null) m.color = _flashColor;

            yield return new WaitForSeconds(_flashDuration);

            RefreshVisuals(_currentHealth.Value);
            _flashCoroutine = null;
        }

        private void CacheBodyMaterials()
        {
            if (_bodyRenderers == null) return;
            _bodyMaterials = new Material[_bodyRenderers.Length];
            for (int i = 0; i < _bodyRenderers.Length; i++)
            {
                if (_bodyRenderers[i] == null) continue;
                _bodyMaterials[i] = new Material(_bodyRenderers[i].sharedMaterial);
                _bodyRenderers[i].material = _bodyMaterials[i];
            }
        }

        #endregion

        #region Visuals

        private void RefreshVisuals(float hp)
        {
            float f = _maxHealth > 0f ? Mathf.Clamp01(hp / _maxHealth) : 0f;
            if (_healthText    != null) _healthText.text          = $"{Mathf.CeilToInt(hp)}";
            if (_healthBarFill != null) _healthBarFill.fillAmount = f;
            if (_bodyMaterials != null)
                foreach (var m in _bodyMaterials)
                    if (m != null)
                        m.color = Color.Lerp(new Color(0.9f, 0.2f, 0.1f), new Color(0.2f, 0.9f, 0.3f), f);
        }

        private void SetBodyVisible(bool visible)
        {
            if (_bodyRenderers == null) return;
            foreach (var r in _bodyRenderers)
                if (r != null) r.enabled = visible;
        }

        private void SetHealthBarVisible(bool visible)
        {
            if (_healthBarRoot != null) _healthBarRoot.SetActive(visible);
        }

        #endregion

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.45f);
            Gizmos.DrawWireSphere(transform.position, _collisionRadius);

            Gizmos.color = new Color(1f, 0f, 0f, 0.25f);
            Gizmos.DrawWireSphere(MuzzlePosition, _detectionRadius);
        }
#endif
    }
}
