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
using MidManStudio.Netcode.Collections;

namespace TestGame
{
    public enum PlayerShootMode
    {
        LocalOnly  = 0,
        RustSim2D  = 1,
        RustSim3D  = 2,
        Raycast2D  = 3,
        Raycast3D  = 4,
        Physics2D  = 5,
        Physics3D  = 6,
    }

    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class NetworkedDimensionPlayer : NetworkBehaviour
    {
        #region Inspector

        [Header("Transforms (auto-created if null)")]
        [SerializeField] private Transform _headPivot;
        [SerializeField] private Transform _shotPoint2D;
        [SerializeField] private Transform _shotPoint3D;

        [Header("Visuals")]
        [SerializeField] private Renderer[] _meshRenderers;
        [SerializeField] private Color _ownerColor  = new Color(0.20f, 0.80f, 1.00f);
        [SerializeField] private Color _remoteColor = new Color(1.00f, 0.40f, 0.30f);

        [Header("Movement")]
        [SerializeField] private float _moveSpeed2D = 6f;
        [SerializeField] private float _moveSpeed3D = 5f;
        [SerializeField] private float _jumpForce   = 7f;

        [Header("Dash")]
        [SerializeField] private KeyCode _dashKey      = KeyCode.LeftShift;
        [SerializeField] private float   _dashSpeed    = 16f;
        [SerializeField] private float   _dashDuration = 0.12f;
        [SerializeField] private float   _dashCooldown = 0.9f;

        [Header("3D Mouse Look")]
        [SerializeField] private float _mouseSensitivity = 2f;
        [SerializeField, Range(-80f, 0f)] private float _pitchMin = -80f;
        [SerializeField, Range(0f,  80f)] private float _pitchMax =  80f;

        [Header("Projectile Config Type IDs")]
        [Tooltip(
            "ProjectileConfigType enum value cast to int.\n" +
            "After running Config Type Generator set this to (int)ProjectileConfigType.YourConfig.\n" +
            "Default = 0 maps to ProjectileConfigType.Default.\n" +
            "Resolved via ProjectileConfigManager when available, " +
            "falls back to direct ushort cast otherwise.")]
        [SerializeField] private int _configTypeId2D = 0;

        [Tooltip("ProjectileConfigType enum value for 3D projectiles.")]
        [SerializeField] private int _configTypeId3D = 0;

        [Header("Fire Settings")]
        [SerializeField] private float _fireRate = 5f;
        [SerializeField, Range(1, 64)]   private int   _pelletsPerShot = 1;
        [SerializeField, Range(0f, 45f)] private float _spreadDeg     = 0f;
        [SerializeField, Range(1, 32)]   private int   _raycastPelletCount = 1;
        [SerializeField] private KeyCode _fireKey = KeyCode.Mouse0;

        [Header("Mobile Touch Input (optional)")]
        [Tooltip("Assign to enable on-screen joystick movement. Desktop keyboard input keeps working either way.")]
        [SerializeField] private MID_TouchJoystick _touchMoveJoystick;
        [Tooltip("Assign to enable an on-screen shoot button. Desktop mouse/key firing keeps working either way.")]
        [SerializeField] private MID_TouchShootButton _touchShootButton;

        [Header("Shot Pattern (optional)")]
        [SerializeField] private ProjectilePatternSO _shotPattern;

        [Header("Shoot Mode")]
        [SerializeField] private PlayerShootMode _defaultShootMode = PlayerShootMode.LocalOnly;
        [SerializeField] private TMP_Text        _modeText;
        [SerializeField] private UnityEngine.UI.Button _modeCycleButton;

        [Header("Dimension Toggle")]
        [SerializeField] private KeyCode _dimensionKey = KeyCode.BackQuote;
        [Tooltip("On-screen equivalent of _dimensionKey, for touch devices. Same SwitchDimension() call either way.")]
        [SerializeField] private UnityEngine.UI.Button _dimensionSwitchButton;

        [Header("Local Player UI (Multiplayer)")]
        [Tooltip(
            "Root Canvas holding this player's on-screen controls (joystick, shoot button, " +
            "mode/dimension buttons, HUD text). Every spawned player instance carries its own " +
            "copy of this prefab, so without this gating every remote player's UI renders on " +
            "top of your own screen too. Set active for the owner only, in OnNetworkSpawn.")]
        [SerializeField] private Canvas _localPlayerCanvas;

        [Header("Raycast Settings")]
        [SerializeField] private LayerMask _raycastLayers = -1;
        [SerializeField] private float     _raycastRange  = 200f;

        [Header("Physics Projectile Settings")]
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType2D
            = PoolableNetworkObjectType.BaseProjectileBlueprint_2D;
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType3D
            = PoolableNetworkObjectType.BaseProjectileBlueprint_3D;
        [SerializeField] private float _physicsProjectileSpeed  = 20f;
        [SerializeField] private float _physicsDamageMultiplier = 1f;

        [Header("Guided Target (Test)")]
        [Tooltip(
            "GUIDED FIX: whatever fired config's MovementType is Guided needs a target " +
            "to home toward — that was true for both simulation paths and neither one " +
            "had it wired anywhere. PhysicsProjectileBase.SetGuidedTarget() and " +
            "ProjectileGuidanceTracker.RegisterGuidedTarget2D/3D (RustSim) both existed " +
            "and worked correctly, but nothing called either. This one field now feeds " +
            "both: FirePhysics wires it into the physics path, FireSim wires it into " +
            "RustSim. Assign whatever this player should lock onto for a Guided test-fire " +
            "(an enemy dummy, the other player, etc). Left null, Guided configs just fly " +
            "straight — same as before this fix, not a regression.")]
        [SerializeField] private Transform _guidedTestTarget;

        [Header("Audio")]
        [SerializeField] private int   _fireSoundClipIndex  = 0;
        [SerializeField, Range(0f,1f)] private float _fireSoundVolume = 0.7f;
        [SerializeField] private AudioSource _fallbackAudioSource;
        [SerializeField] private AudioClip   _fallbackFireClip;
        [SerializeField, Range(0f,1f)]      private float _fallbackVolume = 0.6f;
        [SerializeField, Range(0.01f,0.3f)] private float _fallbackPitchVariance = 0.1f;

        [Header("Muzzle Flash — GlobalFX")]
        [SerializeField] private int   _muzzleFlashParticleCount = 4;
        [SerializeField, Range(0f,1f)] private float _muzzleFlashVolume = 0.8f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Networked State

        private readonly NetworkVariable<float> _netPitch = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<int> _netShootMode = new NetworkVariable<int>(
            (int)PlayerShootMode.LocalOnly,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        #endregion

        #region Local State

        private Rigidbody _rb;
        private Dimension _currentDimension = Dimension.TwoD;
        private bool      _grounded;
        private float     _nextFireTime;
        private float     _yaw;
        private float     _pitch;

        private float   _nextDashTime;
        private bool    _isDashing;
        private float   _dashEndTime;
        private Vector3 _dashDir;

        private PlayerShootMode _shootMode;

        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            _rb        = GetComponent<Rigidbody>();
            _shootMode = _defaultShootMode;
            EnsureHeadPivot();
            EnsureShotPoints(); 
        }

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _netShootMode.OnValueChanged += OnShootModeChanged;
            if (IsOwner)
            {
                _netShootMode.Value = (int)_defaultShootMode;
                _shootMode          = _defaultShootMode;

                if (_modeCycleButton != null)
                    _modeCycleButton.onClick.AddListener(CycleModeNext);

                if (_dimensionSwitchButton != null)
                    _dimensionSwitchButton.onClick.AddListener(HandleDimensionButtonPressed);

                Dimension startDim = DimensionManager.HasInstance
                    ? DimensionManager.Instance.Current : Dimension.TwoD;
                _currentDimension = startDim;

                ApplyRigidbodyConstraints(startDim);

                if (DimensionManager.HasInstance)
                    DimensionManager.Instance.OnDimensionChanged += HandleDimensionChanged;

                if (DimensionCameraController.Instance != null)
                    DimensionCameraController.Instance.RegisterPlayerCams(transform, _headPivot);

                // Sets the MID ID on MasterProjectileSystem, ClientPredictionManager,
                // AND MID_ProjectileNetworkBridge (for firing-client routing in RPCs).
                if (MID_MasterProjectileSystem.HasInstance)
                    MID_MasterProjectileSystem.Instance.SetLocalPlayerMidId(OwnerClientId);

                _yaw = transform.eulerAngles.y;
                ApplyCursorState(startDim);
                UpdateModeText();
                MID_Logger.LogInfo(_logLevel,
                    $"Local player spawned. OwnerClientId={OwnerClientId} IsServer={IsServer}",
                    nameof(NetworkedDimensionPlayer));
            }
            else
            {
                _shootMode = (PlayerShootMode)_netShootMode.Value;
                UpdateModeText();
                if (_rb != null)
                {
                    _rb.isKinematic   = true;
                    _rb.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }

            // Only the local owner's on-screen controls should ever be visible/interactable.
            // Every spawned player instance (including remote ones on your own screen) carries
            // its own copy of this Canvas — without this, every connected player's joystick,
            // shoot button, and mode/dimension buttons render stacked on top of each other.
            if (_localPlayerCanvas != null)
                _localPlayerCanvas.gameObject.SetActive(IsOwner);

            ApplyTint(IsOwner ? _ownerColor : _remoteColor);
        }

        public override void OnNetworkDespawn()
        {
            _netShootMode.OnValueChanged -= OnShootModeChanged;
            if (IsOwner)
            {
                if (_modeCycleButton != null)
                    _modeCycleButton.onClick.RemoveListener(CycleModeNext);
                if (_dimensionSwitchButton != null)
                    _dimensionSwitchButton.onClick.RemoveListener(HandleDimensionButtonPressed);
                if (DimensionManager.HasInstance)
                    DimensionManager.Instance.OnDimensionChanged -= HandleDimensionChanged;
                DimensionCameraController.Instance?.UnregisterPlayerCams();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }
            base.OnNetworkDespawn();
        }

        #endregion

        #region Update / FixedUpdate

        private void Update()
        {
            if (!IsOwner)
            {
                if (_headPivot != null)
                    _headPivot.localRotation = Quaternion.Euler(_netPitch.Value, 0f, 0f);
                return;
            }
           
            if (Input.GetKeyDown(_dimensionKey)) HandleDimensionButtonPressed();

            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeMode(PlayerShootMode.LocalOnly);
            if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeMode(PlayerShootMode.RustSim2D);
            if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeMode(PlayerShootMode.RustSim3D);
            if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeMode(PlayerShootMode.Raycast2D);
            if (Input.GetKeyDown(KeyCode.Alpha5)) ChangeMode(PlayerShootMode.Raycast3D);
            if (Input.GetKeyDown(KeyCode.Alpha6)) ChangeMode(PlayerShootMode.Physics2D);
            if (Input.GetKeyDown(KeyCode.Alpha7)) ChangeMode(PlayerShootMode.Physics3D);

            if (Use3DConvention()) HandleMouseLook();
            if (Input.GetKeyDown(_dashKey) && Time.time >= _nextDashTime && !_isDashing)
                StartDash();
            HandleFire();
        }

        private void FixedUpdate()
        {
            if (!IsOwner) return;
            HandleMovement();
            if (Use3DConvention())
                _rb.MoveRotation(Quaternion.Euler(0f, _yaw, 0f));
        }

        #endregion

        #region Dimension Switch

        /// <summary>
        /// Shared by the keyboard key (_dimensionKey) and the on-screen button
        /// (_dimensionSwitchButton) — same call, same guard, one place to change it.
        /// </summary>
        private void HandleDimensionButtonPressed()
        {
            if (!IsOwner) return;
            if (!DimensionManager.HasInstance || DimensionManager.Instance.IsTransitioning) return;
            DimensionManager.Instance.SwitchDimension();
        }

        #endregion

        #region Shoot Mode

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
            if (Use3DConvention())
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            else if (_currentDimension == Dimension.TwoD)
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

        private bool Use3DConvention()
            => _currentDimension == Dimension.ThreeD
            || _shootMode == PlayerShootMode.RustSim3D
            || _shootMode == PlayerShootMode.Raycast3D
            || _shootMode == PlayerShootMode.Physics3D;

        private ushort ResolveConfigId()
        {
            bool prefer3D = _shootMode == PlayerShootMode.RustSim3D
                         || _shootMode == PlayerShootMode.Raycast3D
                         || _shootMode == PlayerShootMode.Physics3D
                         || _currentDimension == Dimension.ThreeD;

            int typeId = prefer3D ? _configTypeId3D : _configTypeId2D;

            if (ProjectileConfigManager.HasInstance)
                return ProjectileConfigManager.Instance.GetConfigId(typeId);

            return (ushort)typeId;
        }

        private Vector3   ResolveFireDir()
            => Use3DConvention() && _headPivot != null ? _headPivot.forward : transform.right;

        private Transform ResolveShotPoint()
            => Use3DConvention() ? _shotPoint3D : _shotPoint2D;

        // Touch only kicks in when keyboard axes are neutral — on an actual mobile
        // device keyboard input is always zero, so this activates automatically
        // with no platform check needed. Shared by HandleMovement and StartDash.
        private Vector2 GetMoveAxes()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if (Mathf.Approximately(h, 0f) && Mathf.Approximately(v, 0f) && _touchMoveJoystick != null)
            {
                Vector2 t = _touchMoveJoystick.Value;
                h = t.x;
                v = t.y;
            }

            return new Vector2(h, v);
        }

        #endregion

        #region Mouse Look

        private void HandleMouseLook()
        {
            _yaw   += Input.GetAxisRaw("Mouse X") * _mouseSensitivity;
            _pitch -= Input.GetAxisRaw("Mouse Y") * _mouseSensitivity;
            _pitch  = Mathf.Clamp(_pitch, _pitchMin, _pitchMax);
            if (_headPivot != null)
            {
                _headPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
                _netPitch.Value = _pitch;
            }
        }

        #endregion

        #region Movement

        private void HandleMovement()
        {
            Vector2 axes = GetMoveAxes();
            float h = axes.x;
            float v = axes.y;

            if (_isDashing)
            {
                if (Time.time >= _dashEndTime) _isDashing = false;
                else
                {
                    if (_rb.isKinematic)
                        _rb.MovePosition(_rb.position + _dashDir * _dashSpeed * Time.fixedDeltaTime);
                    else
                        _rb.velocity = _dashDir * _dashSpeed;
                    return;
                }
            }

            // FIX: branch on the Rigidbody's ACTUAL kinematic state (set by
            // ApplyRigidbodyConstraints, tied to _currentDimension) instead of
            // Use3DConvention(), which also reads true for a 3D SHOOT MODE
            // (RustSim3D/Raycast3D/Physics3D) regardless of dimension. Pressing
            // a 3D shoot-mode hotkey while still in the 2D dimension left this
            // reading the velocity-based 3D branch while the rigidbody was
            // still kinematic from 2D's ApplyRigidbodyConstraints —
            // "Setting linear velocity of a kinematic body is not supported"
            // every FixedUpdate, and since the exception aborts the rest of
            // the method, movement just stops dead for whoever hit the combo.
            // Matches the dash branch above, which already checks
            // _rb.isKinematic directly instead of trusting a proxy flag.
            if (_rb.isKinematic)
            {
                _rb.MovePosition(_rb.position +
                    new Vector3(h * _moveSpeed2D, v * _moveSpeed2D, 0f) * Time.fixedDeltaTime);
            }
            else
            {
                Vector3 dir = (transform.right * h + transform.forward * v).normalized;
                _rb.velocity = new Vector3(dir.x * _moveSpeed3D, _rb.velocity.y, dir.z * _moveSpeed3D);
                if (_grounded && Input.GetButton("Jump"))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        #endregion

        #region Dash

        private void StartDash()
        {
            Vector2 axes = GetMoveAxes();
            float h = axes.x;
            float v = axes.y;

            if (Use3DConvention())
            {
                Vector3 d = transform.right * h + transform.forward * v;
                _dashDir  = d.sqrMagnitude > 0.01f
                    ? d.normalized
                    : (_headPivot != null ? _headPivot.forward : transform.forward);
            }
            else
            {
                Vector3 d = new Vector3(h, v, 0f);
                _dashDir  = d.sqrMagnitude > 0.01f ? d.normalized : transform.right;
            }
            _isDashing   = true;
            _dashEndTime = Time.time + _dashDuration;
            _nextDashTime = Time.time + _dashCooldown;
        }

        #endregion

        #region Fire Dispatch

        private void HandleFire()
        {
            bool firing = Input.GetKey(_fireKey) || (_touchShootButton != null && _touchShootButton.IsPressed);
            if (!firing)                         return;
            if (Time.time < _nextFireTime)        return;
            if (!ProjectileRegistry.HasInstance)  return;
            _nextFireTime = Time.time + 1f / Mathf.Max(_fireRate, 0.01f);

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
            Transform sp   = ResolveShotPoint();
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = ResolveFireDir();

            if (MID_NativeAudioBridge.HasInstance)
                MID_NativeAudioBridge.Instance.PlayClip(_fireSoundClipIndex, _fireSoundVolume);
            else if (_fallbackAudioSource != null && _fallbackFireClip != null)
            {
                _fallbackAudioSource.pitch = 1f
                    + Random.Range(-_fallbackPitchVariance, _fallbackPitchVariance);
                _fallbackAudioSource.PlayOneShot(_fallbackFireClip, _fallbackVolume);
            }
            GlobalFXManager.Instance?.TriggerMuzzleFlash(
                origin, dir, _muzzleFlashParticleCount, _muzzleFlashVolume);
        }

        #endregion

        #region Sim Fire

        /// <summary>
        /// FIX: Previously bypassed MID_MasterProjectileSystem.Fire() and called
        /// GetBridge().FireServerRpc() directly, skipping the client's local
        /// SpawnFiringClientBatch call. The empty predMgr block was the leftover
        /// placeholder. Now calls Fire() which routes correctly for all cases.
        /// </summary>
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
                    "_configTypeId2D/_configTypeId3D match valid ProjectileConfigType values.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            Transform sp     = ResolveShotPoint();
            Vector3   origin = sp != null ? sp.position : transform.position;
            Vector3   dir    = ResolveFireDir();

            int n = _shotPattern != null
                ? _shotPattern.ProjectileCount
                : Mathf.Max(_pelletsPerShot, 1);

            SpawnPoint[] pts = BuildSpawnPoints(origin, dir, n, cfg);

            bool networked = _shootMode != PlayerShootMode.LocalOnly
                          && MID_MasterProjectileSystem.Instance.IsNetworked
                          && IsSpawned;

            // patternId 0 means "no pattern" on the wire — ProjectilePatternRegistry
            // reserves 0 specifically so this needs no separate bool field. _shotPattern's
            // own PatternId is cached by ProjectilePatternRegistry.Register() at startup
            // (mirrors ProjectileConfigSO.ConfigId), so this is just a field read, not a
            // lookup, and it's zero for the un-registered/no-pattern case for free.
            ushort patternId = _shotPattern != null ? _shotPattern.PatternId : (ushort)0;

            // Same convention BuildSpawnPointsFromPattern/BuildSpawnPointsSpread already
            // use internally for their own rotation basis — computed once here and sent
            // as-is so the server/other clients use the SAME basis rather than re-deriving
            // it from cfg.Is3D alone. This is deliberately an OR, not just cfg.Is3D: a
            // 2D-configured weapon fired while the player's current shoot mode/dimension
            // is 3D still needs to rotate in 3D view space, not collapse to the flat Z-only
            // path — that's existing, intentional behavior, not something to "fix away".
            bool patternIs3D = Use3DConvention() || cfg.Is3D;

            // FIX: Build WeaponFireContext and call Fire().
            //   LocalOnly / offline → FireLocal → LocalProjectileManager.Spawn2D/3D
            //   Networked client    → FireNetworkedSim → SpawnFiringClientBatch + FireServerRpc
            //   Networked host      → FireNetworkedSim → skip local spawn + FireServerRpc
            // The master system now handles the full flow — no manual RPC call needed here.
            //
            // Wire cap removed: FireNetworkedSim no longer transmits raw directions at all
            // for pattern fire, so there's no 64-pellet ceiling to collapse onto the primary
            // direction anymore — patternId is the only thing that crosses the network, and
            // every recipient (server + every other client) regenerates the full pellet set
            // itself via ProjectileDirectionResolver against the same registered pattern asset.
            var context = new WeaponFireContext
            {
                FireRate               = _fireRate,
                IsRaycastWeapon        = false,
                ProjectileCount        = pts.Length,
                IsNetworked            = networked,
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = IsSpawned ? NetworkObjectId : 0UL,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f
            };

            // dir (raw, unrotated aim direction) — NOT pts[0].Direction, which for any
            // pattern/spread with more than one pellet is already offset by that pellet's
            // own angle within the pattern. Sending pts[0] as the regeneration base was
            // the actual cause of every pattern shot skewing off at an angle: the server
            // and other clients would re-apply the FULL pattern rotation set on top of an
            // already-rotated direction instead of the true center.
            MID_MasterProjectileSystem.Instance.Fire(
                cfgId, pts, pts.Length, context, patternId, _spreadDeg, dir, patternIs3D,
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

            Transform sp   = ResolveShotPoint();
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = ResolveFireDir();

            ushort patternId = _shotPattern != null ? _shotPattern.PatternId : (ushort)0;
            bool   networked = MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned;

            // PATTERN SUPPORT: no pattern (and no simple spread) — unchanged,
            // exactly the original single-ray path.
            if (patternId == 0 && _raycastPelletCount <= 1)
            {
                FireSingleRaycast(origin, dir, is3D, cfgId, networked);
                return;
            }

            bool use3D = Use3DConvention() || is3D;

            if (networked)
            {
                MID_MasterProjectileSystem.Instance.RegisterRaycastPatternFire(
                    origin, dir, use3D, cfgId, patternId,
                    patternId == 0 ? (byte)Mathf.Clamp(_raycastPelletCount, 1, 255) : (byte)0,
                    _spreadDeg,
                    new WeaponFireContext
                    {
                        FireRate               = _fireRate,
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

            // Offline/host multi-pellet: no server round-trip to optimize away,
            // so just resolve directions locally and loop the existing
            // single-shot path once per pellet — reuses the already-proven
            // offline raycast + damage flow exactly as-is, N times.
            var resolved = ProjectileDirectionResolver.Resolve(
                patternId, origin, dir,
                patternId == 0 ? Mathf.Clamp(_raycastPelletCount, 1, 255) : 1,
                _spreadDeg, 1f, use3D);

            foreach (var pt in resolved)
                FireSingleRaycast(origin, pt.Direction, is3D, cfgId, networked: false);
        }

        private void FireSingleRaycast(Vector3 origin, Vector3 dir, bool is3D, ushort cfgId, bool networked)
        {
            bool    hit    = false;
            Vector3 hitPt  = origin + dir * _raycastRange;
            ulong   netId  = 0;

            if (is3D)
            {
                if (Physics.Raycast(origin, dir, out RaycastHit h, _raycastRange,
                    _raycastLayers, QueryTriggerInteraction.Collide))
                {
                    hit   = true;
                    hitPt = h.point;
                    var no = h.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) netId = no.NetworkObjectId;
                }
            }
            else
            {
                var h2 = Physics2D.Raycast(origin, dir, _raycastRange, _raycastLayers);
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
                    FireRate               = _fireRate,
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

            Transform sp   = ResolveShotPoint();
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = ResolveFireDir();
            ushort  cfgId  = ResolveConfigId();

            var poolType = is3D ? _physicsPoolType3D : _physicsPoolType2D;
            Quaternion rot = is3D
                ? (dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity)
                : Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);

            // PATTERN SUPPORT: same PatternId/SpreadDeg reference the Rust-sim path
            // sends — patternId 0 means "no pattern," single body, unchanged from
            // before. _shotPattern is the same field FireSim() already reads.
            ushort patternId  = _shotPattern != null ? _shotPattern.PatternId : (ushort)0;
            byte   pelletCount = patternId == 0 ? (byte)1 : (byte)0; // pattern's own count is authoritative when set
            bool   use3D       = Use3DConvention() || is3D;

            if (MID_MasterProjectileSystem.Instance.IsNetworked)
            {
                if (!IsServer)
                {
                    // FIX: predict the SAME number of ghosts the server will actually
                    // spawn. This used to always predict exactly one straight-line
                    // ghost regardless of pattern, while the server (via
                    // FirePhysicsProjectileServerRpc -> SpawnPhysicsProjectile, once
                    // per resolved direction) could spawn N pattern-shaped
                    // projectiles. That mismatch is what read as "fires twice, one
                    // shot without the pattern then the one with it" — reconciliation
                    // only ever had one ghost to kill off against however many real
                    // projectiles actually showed up. Resolving the same directions
                    // here (mirroring what SpawnPhysicsProjectileLocal already does
                    // for the offline path, just below) keeps the ghost count 1:1
                    // with what the server will spawn.
                    Vector3[] predictedDirs;
                    if (patternId != 0)
                    {
                        var resolved = ProjectileDirectionResolver.Resolve(
                            patternId, origin, dir, 1, 0f, _physicsProjectileSpeed, use3D);
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
                            cfgId, origin, pDir, _physicsProjectileSpeed);
                    }
                }

                // GUIDED FIX: resolved fresh per shot rather than cached — _guidedTestTarget
                // can be reassigned at runtime (e.g. swapping which dummy to lock onto)
                // and this always reflects whatever it's currently pointing at.
                ulong guidedTargetNetId = 0UL;
                if (_guidedTestTarget != null)
                {
                    var targetNetObj = _guidedTestTarget.GetComponentInParent<NetworkObject>();
                    if (targetNetObj != null) guidedTargetNetId = targetNetObj.NetworkObjectId;
                }

                MID_MasterProjectileSystem.Instance.GetBridge()?.FirePhysicsProjectileServerRpc(
                    origin, dir, rot, poolType,
                    _physicsProjectileSpeed, _physicsDamageMultiplier,
                    OwnerClientId, IsSpawned ? NetworkObjectId : 0UL,
                    cfgId, patternId, pelletCount, _spreadDeg, use3D,
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
            var poolType = is3D ? _physicsPoolType3D : _physicsPoolType2D;

            Vector3[] directions;
            if (patternId != 0)
            {
                var resolved = ProjectileDirectionResolver.Resolve(
                    patternId, origin, direction, 1, 0f, _physicsProjectileSpeed, patternIs3D);
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
                        $"Pool null for {poolType}.", nameof(NetworkedDimensionPlayer));
                    continue;
                }

                var proj = netObj.GetComponent<PhysicsProjectileBase>();
                if (proj != null)
                {
                    proj.SetOwnerContext(
                        OwnerClientId, IsSpawned ? NetworkObjectId : 0UL,
                        false, 1, _physicsDamageMultiplier);
                    proj.InitialiseProjectile(
                        OwnerClientId, IsSpawned ? NetworkObjectId : 0UL,
                        _physicsProjectileSpeed, false, 1);

                    // GUIDED FIX: must run AFTER InitialiseProjectile — see the matching
                    // comment in MID_ProjectileNetworkBridge.FirePhysicsProjectileServerRpc
                    // for why (SetupMovementType resets the target on every fresh launch).
                    if (_guidedTestTarget != null)
                        proj.SetGuidedTarget(_guidedTestTarget);
                }
            }
        }

        #endregion

        #region Spawn Point Builders

        private SpawnPoint[] BuildSpawnPoints(
            Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
            => _shotPattern != null
                ? BuildSpawnPointsFromPattern(origin, dir, cfg)
                : BuildSpawnPointsSpread(origin, dir, n, cfg);

        private SpawnPoint[] BuildSpawnPointsSpread(
            Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
        {
            bool use3D = Use3DConvention() || cfg.Is3D;
            var  pts   = new SpawnPoint[n];
            for (int i = 0; i < n; i++)
            {
                float frac = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                Vector3 sDir = use3D
                    ? Quaternion.Euler(0f, frac * _spreadDeg, 0f) * dir
                    : Quaternion.Euler(0f, 0f, frac * _spreadDeg) * dir;
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
            bool use3D     = Use3DConvention() || cfg.Is3D;
            var  angleDirs = _shotPattern.SampleDirections();
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

                float mul = _shotPattern.GetSpeedMultiplier(i, _shotPattern.RngSeed);
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

        #region Misc Helpers

        private void HandleDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyRigidbodyConstraints(dim);
            ApplyCursorState(dim);
            if (dim == Dimension.ThreeD) _yaw = transform.eulerAngles.y;
        }

        private void ApplyRigidbodyConstraints(Dimension dim)
        {
            if (_rb == null || !IsOwner) return;
            if (dim == Dimension.TwoD)
            {
                _rb.isKinematic = true;
                _rb.constraints = RigidbodyConstraints.FreezePositionZ
                                | RigidbodyConstraints.FreezeRotation;
                _rb.useGravity  = false;
            }
            else
            {
                _rb.isKinematic = false;
                _rb.constraints = RigidbodyConstraints.FreezeRotationX
                                | RigidbodyConstraints.FreezeRotationY
                                | RigidbodyConstraints.FreezeRotationZ;
                _rb.useGravity  = true;
                var p = transform.position;
                transform.position = new Vector3(p.x, p.y, 0f);
                _rb.velocity = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
            }
        }

        private static void ApplyCursorState(Dimension dim)
        {
            if (dim == Dimension.ThreeD)
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            else
            { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        private void ApplyTint(Color col)
        {
            if (_meshRenderers != null)
                foreach (var r in _meshRenderers)
                    if (r != null) r.material.color = col;
        }

        private void EnsureHeadPivot()
        {
            if (_headPivot != null) return;
            var go = new GameObject("HeadPivot");
            go.transform.SetParent(transform);
            go.transform.localPosition = new Vector3(0f, 0.85f, 0f);
            _headPivot = go.transform;
        }

        private void EnsureShotPoints()
        {
            if (_shotPoint2D == null)
            {
                var go = new GameObject("ShotPoint2D");
                go.transform.SetParent(transform);
                go.transform.localPosition = new Vector3(0.55f, 0f, 0f);
                _shotPoint2D = go.transform;
            }
            if (_shotPoint3D == null)
            {
                Transform parent = _headPivot != null ? _headPivot : transform;
                var go = new GameObject("ShotPoint3D");
                go.transform.SetParent(parent);
                go.transform.localPosition = new Vector3(0.25f, -0.05f, 0.5f);
                _shotPoint3D = go.transform;
            }
        }

        private void OnCollisionStay(Collision _) => _grounded = true;
        private void OnCollisionExit(Collision _) => _grounded = false;

        #endregion
    }
}
