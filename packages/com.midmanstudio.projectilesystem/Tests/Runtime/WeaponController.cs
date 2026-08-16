using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TMPro;
using MidManStudio.Core.Audio;
using MidManStudio.Core.FX;
using MidManStudio.Core.Logging;
using MidManStudio.Netcode.Pools;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Config;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Network;

namespace TestGame
{
    /// <summary>
    /// Everything fire/weapon-specific that used to live on NetworkedDimensionPlayer:
    /// shoot-mode dispatch (raycast/rustsim/physics test paths), shot pattern /
    /// spread, config-id resolution, and the actual Fire/Raycast/Physics calls
    /// into MID_MasterProjectileSystem — unchanged behaviour, just reading from
    /// whichever WeaponDefinitionSO is currently equipped instead of local
    /// inspector fields. Plus new: an inventory of owned weapons, pickup, and
    /// switching (with an Animator trigger).
    ///
    /// Sits as a sibling NetworkBehaviour on the same GameObject/NetworkObject
    /// as NetworkedDimensionPlayer — OwnerClientId/IsOwner/IsServer/IsSpawned/
    /// NetworkObjectId are therefore identical between the two components with
    /// no extra wiring needed. _player is used for the things that stayed on
    /// the player (rig geometry: ResolveFireDir/ResolveShotPoint, and the
    /// combined 2D/3D "control convention" via Use3DConvention()).
    /// </summary>
    [DisallowMultipleComponent]
    public class WeaponController : NetworkBehaviour
    {
        #region Inspector

        [Header("Player Link (auto-filled if left null)")]
        [SerializeField] private NetworkedDimensionPlayer _player;

        [Header("Inventory")]
        [Tooltip("Every weapon this player could ever hold in this scene. Index 0 " +
                 "is granted automatically on spawn. WeaponPickup instances each " +
                 "reference one of these directly — this array is only used here " +
                 "to resolve a synced WeaponId back into the actual asset, and to " +
                 "drive SwitchToNext()'s ordering.")]
        [SerializeField] private WeaponDefinitionSO[] _availableWeapons;

        [Header("Fire Input")]
        [SerializeField] private KeyCode _fireKey = KeyCode.Mouse0;
        [Tooltip("Assign to enable an on-screen shoot button. Desktop mouse/key firing keeps working either way.")]
        [SerializeField] private MID_TouchShootButton _touchShootButton;

        [Header("Switch Input")]
        [SerializeField] private KeyCode _switchNextKey = KeyCode.Tab;
        [SerializeField] private UnityEngine.UI.Button _switchWeaponButton;

        [Header("Switch Animation")]
        [Tooltip("Animator that plays the weapon-switch animation. Optional — " +
                 "switching still fully functions (inventory + fire logic) with " +
                 "no Animator assigned, it just skips the SetTrigger call.")]
        [SerializeField] private Animator _animator;
        [Tooltip("Used when the equipped WeaponDefinitionSO doesn't specify its own SwitchAnimTrigger.")]
        [SerializeField] private string _defaultSwitchAnimTrigger = "SwitchWeapon";
        [Tooltip("Fire is locked out for this long after a switch, so the draw " +
                 "animation has time to read before the new weapon can shoot. " +
                 "Set to 0 to disable the lockout entirely.")]
        [SerializeField] private float _switchLockDuration = 0.35f;

        [Header("Weapon Model Socket (optional)")]
        [Tooltip("Where WeaponDefinitionSO.WeaponModelPrefab gets instantiated on " +
                 "equip. Leave null to skip visual model swapping entirely.")]
        [SerializeField] private Transform _weaponSocket;

        [Header("Shoot Mode (debug/test)")]
        [SerializeField] private PlayerShootMode _defaultShootMode = PlayerShootMode.LocalOnly;
        [SerializeField] private TMP_Text        _modeText;
        [SerializeField] private UnityEngine.UI.Button _modeCycleButton;

        [Header("Guided Target (Test)")]
        [Tooltip("See original NetworkedDimensionPlayer doc — unchanged. Assign " +
                 "whatever this player should lock onto for a Guided test-fire. " +
                 "Left null, Guided configs just fly straight.")]
        [SerializeField] private Transform _guidedTestTarget;

        [Header("Audio (fallback source)")]
        [Tooltip("Rig audio source used when MID_NativeAudioBridge isn't available. " +
                 "The clip/volume/pitch-variance themselves now live per-weapon on " +
                 "WeaponDefinitionSO.")]
        [SerializeField] private AudioSource _fallbackAudioSource;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Networked State

        private readonly NetworkVariable<int> _netShootMode = new NetworkVariable<int>(
            (int)PlayerShootMode.LocalOnly,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<ushort> _netCurrentWeaponId = new NetworkVariable<ushort>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        #endregion

        #region Local State

        private PlayerShootMode _shootMode;
        private float _nextFireTime;
        private float _switchLockUntil;

        private WeaponDefinitionSO _current;
        private GameObject _currentModelInstance;
        private readonly HashSet<ushort> _ownedWeaponIds = new(8);

        public WeaponDefinitionSO CurrentWeapon => _current;
        public bool IsUsing3DShootMode
            => _shootMode == PlayerShootMode.RustSim3D
            || _shootMode == PlayerShootMode.Raycast3D
            || _shootMode == PlayerShootMode.Physics3D;

        #endregion

        #region Unity / NGO Lifecycle

        private void Awake()
        {
            if (_player == null) _player = GetComponent<NetworkedDimensionPlayer>();
            _shootMode = _defaultShootMode;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _netShootMode.OnValueChanged      += OnShootModeChanged;
            _netCurrentWeaponId.OnValueChanged += OnCurrentWeaponChanged;

            if (IsOwner)
            {
                _shootMode = _defaultShootMode;
                _netShootMode.Value = (int)_defaultShootMode;

                if (_modeCycleButton != null)
                    _modeCycleButton.onClick.AddListener(CycleModeNext);
                if (_switchWeaponButton != null)
                    _switchWeaponButton.onClick.AddListener(SwitchToNext);

                UpdateModeText();
                EquipStartingWeapon();
            }
            else
            {
                _shootMode = (PlayerShootMode)_netShootMode.Value;
                UpdateModeText();

                var weapon = ResolveWeaponById(_netCurrentWeaponId.Value);
                if (weapon != null) EquipLocal(weapon);
            }
        }

        public override void OnNetworkDespawn()
        {
            _netShootMode.OnValueChanged      -= OnShootModeChanged;
            _netCurrentWeaponId.OnValueChanged -= OnCurrentWeaponChanged;

            if (IsOwner)
            {
                if (_modeCycleButton != null)
                    _modeCycleButton.onClick.RemoveListener(CycleModeNext);
                if (_switchWeaponButton != null)
                    _switchWeaponButton.onClick.RemoveListener(SwitchToNext);
            }
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            if (!IsOwner) return;
            if (_player != null && !_player.ControlEnabled) return;

            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeMode(PlayerShootMode.LocalOnly);
            if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeMode(PlayerShootMode.RustSim2D);
            if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeMode(PlayerShootMode.RustSim3D);
            if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeMode(PlayerShootMode.Raycast2D);
            if (Input.GetKeyDown(KeyCode.Alpha5)) ChangeMode(PlayerShootMode.Raycast3D);
            if (Input.GetKeyDown(KeyCode.Alpha6)) ChangeMode(PlayerShootMode.Physics2D);
            if (Input.GetKeyDown(KeyCode.Alpha7)) ChangeMode(PlayerShootMode.Physics3D);

            if (Input.GetKeyDown(_switchNextKey)) SwitchToNext();

            HandleFire();
        }

        #endregion

        #region Weapon Inventory / Pickup / Switching

        private void EquipStartingWeapon()
        {
            if (_availableWeapons == null || _availableWeapons.Length == 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    "No _availableWeapons assigned — player has no weapon to equip. " +
                    "Create a WeaponDefinitionSO asset and assign at least one.",
                    nameof(WeaponController));
                return;
            }

            var starter = _availableWeapons[0];
            if (starter == null) return;
            _ownedWeaponIds.Add(starter.WeaponId);
            _netCurrentWeaponId.Value = starter.WeaponId;
        }

        /// <summary>
        /// Called by WeaponPickup when the LOCAL OWNER's trigger collider overlaps
        /// a pickup. Grants the weapon and immediately switches to it — matches
        /// "can switch weapons if he picks up the weapon". Safe to call on a
        /// non-owner instance too (falls through as a no-op, since only the
        /// owner may write _netCurrentWeaponId), so WeaponPickup doesn't need to
        /// check ownership itself.
        /// </summary>
        public void PickupWeapon(WeaponDefinitionSO weapon)
        {
            if (!IsOwner || weapon == null) return;
            _ownedWeaponIds.Add(weapon.WeaponId);
            _netCurrentWeaponId.Value = weapon.WeaponId;
        }

        public bool OwnsWeapon(WeaponDefinitionSO weapon)
            => weapon != null && _ownedWeaponIds.Contains(weapon.WeaponId);

        /// <summary>Cycles to the next OWNED weapon in _availableWeapons order. No-op if only one is owned.</summary>
        public void SwitchToNext()
        {
            if (!IsOwner || _availableWeapons == null || _availableWeapons.Length == 0) return;

            int startIdx = System.Array.FindIndex(_availableWeapons,
                w => w != null && w.WeaponId == _netCurrentWeaponId.Value);
            if (startIdx < 0) startIdx = 0;

            for (int step = 1; step <= _availableWeapons.Length; step++)
            {
                int idx = (startIdx + step) % _availableWeapons.Length;
                var w = _availableWeapons[idx];
                if (w != null && _ownedWeaponIds.Contains(w.WeaponId))
                {
                    _netCurrentWeaponId.Value = w.WeaponId;
                    return;
                }
            }
        }

        private WeaponDefinitionSO ResolveWeaponById(ushort id)
        {
            if (_availableWeapons == null) return null;
            foreach (var w in _availableWeapons)
                if (w != null && w.WeaponId == id) return w;
            return null;
        }

        private void OnCurrentWeaponChanged(ushort _, ushort newId)
        {
            var weapon = ResolveWeaponById(newId);
            if (weapon != null) EquipLocal(weapon);
        }

        /// <summary>
        /// Runs identically on every instance (owner + remote observers) in
        /// response to _netCurrentWeaponId changing — each client instantiates
        /// its own local (non-networked) copy of the model prefab, since it's
        /// project data every build already has; only the compact WeaponId
        /// needs to cross the wire.
        /// </summary>
        private void EquipLocal(WeaponDefinitionSO weapon)
        {
            bool isSwitch = _current != null && _current != weapon;
            _current = weapon;
            _switchLockUntil = isSwitch ? Time.time + _switchLockDuration : 0f;

            if (_animator != null)
            {
                string trig = !string.IsNullOrEmpty(weapon.SwitchAnimTrigger)
                    ? weapon.SwitchAnimTrigger : _defaultSwitchAnimTrigger;
                if (!string.IsNullOrEmpty(trig)) _animator.SetTrigger(trig);
            }

            if (_weaponSocket != null)
            {
                if (_currentModelInstance != null) Destroy(_currentModelInstance);
                if (weapon.WeaponModelPrefab != null)
                    _currentModelInstance = Instantiate(weapon.WeaponModelPrefab, _weaponSocket);
            }
        }

        #endregion

        #region Fire Dispatch

        private void HandleFire()
        {
            if (_current == null) return;

            bool firing = Input.GetKey(_fireKey) || (_touchShootButton != null && _touchShootButton.IsPressed);
            if (!firing)                          return;
            if (Time.time < _switchLockUntil)     return;
            if (Time.time < _nextFireTime)        return;
            if (!ProjectileRegistry.HasInstance)  return;
            _nextFireTime = Time.time + 1f / Mathf.Max(_current.FireRate, 0.01f);

            switch (_shootMode)
            {
                case PlayerShootMode.Raycast2D: FireRaycast(false); break;
                case PlayerShootMode.Raycast3D: FireRaycast(true);  break;
                case PlayerShootMode.Physics2D: FirePhysics(false); break;
                case PlayerShootMode.Physics3D: FirePhysics(true);  break;
                default:                        FireSim();          break;
            }
            PlayFireFX();
        }

        private void PlayFireFX()
        {
            Transform sp   = _player.ResolveShotPoint();
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = _player.ResolveFireDir();

            if (MID_NativeAudioBridge.HasInstance)
                MID_NativeAudioBridge.Instance.PlayClip(_current.FireSoundClipIndex, _current.FireSoundVolume);
            else if (_fallbackAudioSource != null && _current.FallbackFireClip != null)
            {
                _fallbackAudioSource.pitch = 1f
                    + Random.Range(-_current.FallbackPitchVariance, _current.FallbackPitchVariance);
                _fallbackAudioSource.PlayOneShot(_current.FallbackFireClip, _current.FallbackVolume);
            }
            GlobalFXManager.Instance?.TriggerMuzzleFlash(
                origin, dir, _current.MuzzleFlashParticleCount, _current.MuzzleFlashVolume);
        }

        #endregion

        #region Sim Fire

        private void FireSim()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            ushort cfgId = ResolveConfigId();
            var    cfg   = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"FireSim: configId {cfgId} not registered. " +
                    "Ensure ProjectileConfigManager.RegisterAll() has run and " +
                    "the equipped WeaponDefinitionSO's ConfigTypeId2D/3D match valid ProjectileConfigType values.",
                    nameof(WeaponController));
                return;
            }

            Transform sp     = _player.ResolveShotPoint();
            Vector3   origin = sp != null ? sp.position : transform.position;
            Vector3   dir    = _player.ResolveFireDir();

            var pattern = _current.ShotPattern;
            int n = pattern != null ? pattern.ProjectileCount : Mathf.Max(_current.PelletsPerShot, 1);

            SpawnPoint[] pts = BuildSpawnPoints(origin, dir, n, cfg);

            bool networked = _shootMode != PlayerShootMode.LocalOnly
                          && MID_MasterProjectileSystem.Instance.IsNetworked
                          && IsSpawned;

            // patternId 0 == "no pattern" on the wire, same convention as before.
            ushort patternId = pattern != null ? pattern.PatternId : (ushort)0;

            // Same OR-not-just-cfg.Is3D convention BuildSpawnPoints* use for their
            // own rotation basis — a 2D-configured weapon fired while the shoot
            // mode/dimension is 3D still needs to rotate in 3D view space.
            bool patternIs3D = _player.Use3DConvention() || cfg.Is3D;

            var context = new WeaponFireContext
            {
                FireRate               = _current.FireRate,
                IsRaycastWeapon        = false,
                ProjectileCount        = pts.Length,
                IsNetworked            = networked,
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = IsSpawned ? NetworkObjectId : 0UL,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f
            };

            // dir here is the raw unrotated aim direction, NOT pts[0].Direction —
            // sending pts[0] as the regeneration base skews the whole pattern,
            // see BuildSpawnPointsFromPattern's doc for why.
            MID_MasterProjectileSystem.Instance.Fire(
                cfgId, pts, pts.Length, context, patternId, _current.SpreadDeg, dir, patternIs3D,
                _guidedTestTarget);
        }

        #endregion

        #region Raycast Fire

        private void FireRaycast(bool is3D)
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            ushort cfgId = ResolveConfigId();
            var    cfg   = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null) return;

            Transform sp   = _player.ResolveShotPoint();
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = _player.ResolveFireDir();

            var pattern = _current.ShotPattern;
            ushort patternId = pattern != null ? pattern.PatternId : (ushort)0;
            bool   networked = MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned;

            if (patternId == 0 && _current.RaycastPelletCount <= 1)
            {
                FireSingleRaycast(origin, dir, is3D, cfgId, networked);
                return;
            }

            bool use3D = _player.Use3DConvention() || is3D;

            if (networked)
            {
                MID_MasterProjectileSystem.Instance.RegisterRaycastPatternFire(
                    origin, dir, use3D, cfgId, patternId,
                    patternId == 0 ? (byte)Mathf.Clamp(_current.RaycastPelletCount, 1, 255) : (byte)0,
                    _current.SpreadDeg,
                    new WeaponFireContext
                    {
                        FireRate               = _current.FireRate,
                        IsRaycastWeapon        = true,
                        IsNetworked            = true,
                        OwnerMidId             = OwnerClientId,
                        FiredByNetworkObjectId = NetworkObjectId,
                        IsBotOwner             = false,
                        WeaponLevel            = 1,
                        DamageMultiplier       = 1f
                    });
                return;
            }

            // Offline/host multi-pellet: no server round-trip to optimise away,
            // resolve directions locally and loop the single-shot path per pellet.
            var resolved = ProjectileDirectionResolver.Resolve(
                patternId, origin, dir,
                patternId == 0 ? Mathf.Clamp(_current.RaycastPelletCount, 1, 255) : 1,
                _current.SpreadDeg, 1f, use3D);

            foreach (var pt in resolved)
                FireSingleRaycast(origin, pt.Direction, is3D, cfgId, networked: false);
        }

        private void FireSingleRaycast(Vector3 origin, Vector3 dir, bool is3D, ushort cfgId, bool networked)
        {
            bool    hit    = false;
            Vector3 hitPt  = origin + dir * _current.RaycastRange;
            ulong   netId  = 0;

            if (is3D)
            {
                if (Physics.Raycast(origin, dir, out RaycastHit h, _current.RaycastRange,
                    _current.RaycastLayers, QueryTriggerInteraction.Collide))
                {
                    hit   = true;
                    hitPt = h.point;
                    var no = h.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) netId = no.NetworkObjectId;
                }
            }
            else
            {
                var h2 = Physics2D.Raycast(origin, dir, _current.RaycastRange, _current.RaycastLayers);
                if (h2.collider != null)
                {
                    hit   = true;
                    hitPt = h2.point;
                    var no = h2.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) netId = no.NetworkObjectId;
                }
            }

            MID_MasterProjectileSystem.Instance.RegisterRaycastFire(
                new RaycastFireResult
                {
                    Origin             = origin,
                    Direction          = dir,
                    HitPoint           = hitPt,
                    DidHit             = hit,
                    HitTargetNetworkId = netId,
                    IsHeadshot         = false,
                    Is3D               = is3D
                },
                cfgId,
                new WeaponFireContext
                {
                    FireRate               = _current.FireRate,
                    ProjectileCount        = 1,
                    IsNetworked            = networked,
                    IsRaycastWeapon        = true,
                    OwnerMidId             = OwnerClientId,
                    FiredByNetworkObjectId = NetworkObjectId,
                    IsBotOwner             = false,
                    WeaponLevel            = 1,
                    DamageMultiplier       = 1f
                });
        }

        #endregion

        #region Physics Fire

        private void FirePhysics(bool is3D)
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            Transform sp   = _player.ResolveShotPoint();
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = _player.ResolveFireDir();
            ushort  cfgId  = ResolveConfigId();

            var poolType = is3D ? _current.PhysicsPoolType3D : _current.PhysicsPoolType2D;
            Quaternion rot = is3D
                ? (dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity)
                : Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);

            var pattern = _current.ShotPattern;
            ushort patternId   = pattern != null ? pattern.PatternId : (ushort)0;
            byte   pelletCount = patternId == 0 ? (byte)1 : (byte)0; // pattern's own count is authoritative when set
            bool   use3D       = _player.Use3DConvention() || is3D;

            if (MID_MasterProjectileSystem.Instance.IsNetworked)
            {
                if (!IsServer)
                {
                    // Predict the SAME number of ghosts the server will actually
                    // spawn (mirrors SpawnPhysicsProjectileLocal below).
                    Vector3[] predictedDirs;
                    if (patternId != 0)
                    {
                        var resolved = ProjectileDirectionResolver.Resolve(
                            patternId, origin, dir, 1, 0f, _current.PhysicsProjectileSpeed, use3D);
                        predictedDirs = new Vector3[resolved.Length];
                        for (int i = 0; i < resolved.Length; i++) predictedDirs[i] = resolved[i].Direction;
                    }
                    else
                    {
                        predictedDirs = new[] { dir };
                    }

                    var predictionManager = MID_MasterProjectileSystem.Instance.GetPredictionManager();
                    foreach (var pDir in predictedDirs)
                    {
                        predictionManager?.SpawnLocalPhysicsVisual(
                            cfgId, origin, pDir, _current.PhysicsProjectileSpeed);
                    }
                }

                ulong guidedTargetNetId = 0UL;
                if (_guidedTestTarget != null)
                {
                    var targetNetObj = _guidedTestTarget.GetComponentInParent<NetworkObject>();
                    if (targetNetObj != null) guidedTargetNetId = targetNetObj.NetworkObjectId;
                }

                MID_MasterProjectileSystem.Instance.GetBridge()?.FirePhysicsProjectileServerRpc(
                    origin, dir, rot, poolType,
                    _current.PhysicsProjectileSpeed, _current.PhysicsDamageMultiplier,
                    OwnerClientId, IsSpawned ? NetworkObjectId : 0UL,
                    cfgId, patternId, pelletCount, _current.SpreadDeg, use3D,
                    guidedTargetNetId);
            }
            else
            {
                SpawnPhysicsProjectileLocal(origin, dir, is3D, cfgId, patternId, use3D);
            }
        }

        private void SpawnPhysicsProjectileLocal(
            Vector3 origin, Vector3 direction, bool is3D,
            ushort configId, ushort patternId, bool patternIs3D)
        {
            var poolType = is3D ? _current.PhysicsPoolType3D : _current.PhysicsPoolType2D;

            Vector3[] directions;
            if (patternId != 0)
            {
                var resolved = ProjectileDirectionResolver.Resolve(
                    patternId, origin, direction, 1, 0f, _current.PhysicsProjectileSpeed, patternIs3D);
                directions = new Vector3[resolved.Length];
                for (int i = 0; i < resolved.Length; i++) directions[i] = resolved[i].Direction;
            }
            else
            {
                directions = new[] { direction };
            }

            foreach (var dir in directions)
            {
                Quaternion rot = is3D
                    ? (dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity)
                    : Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);

                var netObj = MID_MasterProjectileSystem.Instance?.SpawnPhysicsProjectile(
                    poolType, origin, rot, configId);
                if (netObj == null)
                {
                    MID_Logger.LogWarning(_logLevel,
                        $"Pool null for {poolType}.", nameof(WeaponController));
                    continue;
                }

                var proj = netObj.GetComponent<PhysicsProjectileBase>();
                if (proj != null)
                {
                    proj.SetOwnerContext(
                        OwnerClientId, IsSpawned ? NetworkObjectId : 0UL,
                        false, 1, _current.PhysicsDamageMultiplier);
                    proj.InitialiseProjectile(
                        OwnerClientId, IsSpawned ? NetworkObjectId : 0UL,
                        _current.PhysicsProjectileSpeed, false, 1);

                    if (_guidedTestTarget != null)
                        proj.SetGuidedTarget(_guidedTestTarget);
                }
            }
        }

        #endregion

        #region Spawn Point Builders

        private SpawnPoint[] BuildSpawnPoints(
            Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
            => _current.ShotPattern != null
                ? BuildSpawnPointsFromPattern(origin, dir, cfg)
                : BuildSpawnPointsSpread(origin, dir, n, cfg);

        private SpawnPoint[] BuildSpawnPointsSpread(
            Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
        {
            bool use3D = _player.Use3DConvention() || cfg.Is3D;
            var  pts   = new SpawnPoint[n];
            for (int i = 0; i < n; i++)
            {
                float frac = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                Vector3 sDir = use3D
                    ? Quaternion.Euler(0f, frac * _current.SpreadDeg, 0f) * dir
                    : Quaternion.Euler(0f, 0f, frac * _current.SpreadDeg) * dir;
                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = sDir.normalized,
                    Speed     = cfg.ResolveSpeed()
                };
            }
            return pts;
        }

        private SpawnPoint[] BuildSpawnPointsFromPattern(
            Vector3 origin, Vector3 baseDir, ProjectileConfigSO cfg)
        {
            var pattern = _current.ShotPattern;
            bool use3D     = _player.Use3DConvention() || cfg.Is3D;
            var  angleDirs = pattern.SampleDirections();
            var  pts       = new SpawnPoint[angleDirs.Length];

            Vector3 localRight, localUp;
            if (use3D)
            {
                Vector3 fwd    = baseDir.sqrMagnitude > 0.001f ? baseDir.normalized : Vector3.forward;
                Vector3 worldUp = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.98f
                    ? Vector3.forward : Vector3.up;
                localRight = Vector3.Cross(worldUp, fwd).normalized;
                localUp    = Vector3.Cross(fwd, localRight).normalized;
            }
            else
            {
                localRight = Vector3.Cross(baseDir, Vector3.forward).normalized;
                localUp    = Vector3.forward;
            }

            for (int i = 0; i < angleDirs.Length; i++)
            {
                var a = angleDirs[i];
                Vector3 sDir = use3D
                    ? Quaternion.AngleAxis(-a.y, localRight)
                      * Quaternion.AngleAxis(a.x, localUp) * baseDir
                    : Quaternion.Euler(0f, 0f, a.x) * baseDir;

                if (sDir.sqrMagnitude < 0.001f) sDir = baseDir;

                float mul = pattern.GetSpeedMultiplier(i, pattern.RngSeed);
                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = sDir.normalized,
                    Speed     = cfg.ResolveSpeed() * mul
                };
            }
            return pts;
        }

        #endregion

        #region Shoot Mode (debug/test)

        private void CycleModeNext()
        {
            if (!IsOwner) return;
            int count = System.Enum.GetValues(typeof(PlayerShootMode)).Length;
            ChangeMode((PlayerShootMode)(((int)_shootMode + 1) % count));
        }

        private void ChangeMode(PlayerShootMode m)
        {
            _shootMode = m;
            if (IsSpawned && IsOwner) _netShootMode.Value = (int)m;
            UpdateModeText();

            if (_player == null) return;
            if (_player.Use3DConvention())
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            else if (_player.CurrentDimension == Dimension.TwoD)
            { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        private void OnShootModeChanged(int _, int newVal)
        {
            if (!IsOwner) { _shootMode = (PlayerShootMode)newVal; UpdateModeText(); }
        }

        private void UpdateModeText()
        {
            if (_modeText == null) return;
            _modeText.text = _shootMode switch
            {
                PlayerShootMode.LocalOnly  => "[1] LOCAL",
                PlayerShootMode.RustSim2D  => "[2] RUST 2D",
                PlayerShootMode.RustSim3D  => "[3] RUST 3D",
                PlayerShootMode.Raycast2D  => "[4] RAYCAST 2D",
                PlayerShootMode.Raycast3D  => "[5] RAYCAST 3D",
                PlayerShootMode.Physics2D  => "[6] PHYSICS 2D",
                PlayerShootMode.Physics3D  => "[7] PHYSICS 3D",
                _                          => _shootMode.ToString()
            };
        }

        #endregion

        #region Helpers

        private ushort ResolveConfigId()
        {
            if (_current == null) return ushort.MaxValue;

            int typeId = _player.Use3DConvention() ? _current.ConfigTypeId3D : _current.ConfigTypeId2D;

            if (ProjectileConfigManager.HasInstance)
                return ProjectileConfigManager.Instance.GetConfigId(typeId);

            return (ushort)typeId;
        }

        #endregion
    }
}
