using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Managers;

namespace TestGame
{
    /// <summary>
    /// Movement, dimension switching, dash, mouse look, camera registration and
    /// ownership tint. All fire/weapon logic (shoot-mode dispatch, shot pattern,
    /// config-id resolution, raycast/rustsim/physics fire, muzzle FX/audio) has
    /// moved out to the sibling WeaponController component — see that file for
    /// the "net dimension player fire rate, shot pattern etc." extraction.
    ///
    /// Use3DConvention(), ResolveFireDir(), ResolveShotPoint(), CurrentDimension
    /// and MeshRenderers are public specifically so WeaponController (and
    /// PlayerHealth) can read them without duplicating rig/dimension state —
    /// this script stays the single source of truth for "where is the player's
    /// body/head, and which 2D/3D convention is currently active".
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(WeaponController))]
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

        [Header("Mobile Touch Input (optional)")]
        [Tooltip("Assign to enable on-screen joystick movement. Desktop keyboard input keeps working either way.")]
        [SerializeField] private MID_TouchJoystick _touchMoveJoystick;

        [Header("Dimension Toggle")]
        [SerializeField] private KeyCode _dimensionKey = KeyCode.BackQuote;
        [Tooltip("On-screen equivalent of _dimensionKey, for touch devices. Same SwitchDimension() call either way.")]
        [SerializeField] private UnityEngine.UI.Button _dimensionSwitchButton;

        [Header("Local Player UI (Multiplayer)")]
        [Tooltip(
            "Root Canvas holding this player's on-screen controls (joystick, shoot button, " +
            "mode/dimension/switch buttons, HUD text). Every spawned player instance carries " +
            "its own copy of this prefab, so without this gating every remote player's UI " +
            "renders on top of your own screen too. Set active for the owner only, in OnNetworkSpawn.")]
        [SerializeField] private Canvas _localPlayerCanvas;

        [Header("Weapon Link (auto-filled if left null)")]
        [SerializeField] private WeaponController _weapon;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Networked State

        private readonly NetworkVariable<float> _netPitch = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        #endregion

        #region Local State

        private Rigidbody _rb;
        private Dimension _currentDimension = Dimension.TwoD;
        private bool      _grounded;
        private float     _yaw;
        private float     _pitch;

        private float   _nextDashTime;
        private bool    _isDashing;
        private float   _dashEndTime;
        private Vector3 _dashDir;

        /// <summary>False while dead/respawning (see PlayerHealth.SetControlAndVisibilityEnabled).
        /// Gates Update/FixedUpdate input handling and mesh visibility.</summary>
        private bool _controlEnabled = true;

        #endregion

        #region Public API (read by WeaponController / PlayerHealth)

        public Renderer[] MeshRenderers  => _meshRenderers;
        public Transform  HeadPivot      => _headPivot;
        public Dimension  CurrentDimension => _currentDimension;
        public bool       ControlEnabled => _controlEnabled;

        /// <summary>True when the player should use 3D-style controls/aiming —
        /// either because the dimension itself is 3D, or because the equipped
        /// weapon's debug shoot-mode is one of the 3D test paths (RustSim3D/
        /// Raycast3D/Physics3D) even while still in the 2D dimension. That
        /// second case is intentional test-harness behaviour, not a bug — see
        /// HandleMovement's kinematic-check comment for why movement itself
        /// deliberately does NOT key off this for its 2D/3D branch.</summary>
        public bool Use3DConvention()
            => _currentDimension == Dimension.ThreeD
            || (_weapon != null && _weapon.IsUsing3DShootMode);

        public Vector3 ResolveFireDir()
            => Use3DConvention() && _headPivot != null ? _headPivot.forward : transform.right;

        public Transform ResolveShotPoint()
            => Use3DConvention() ? _shotPoint3D : _shotPoint2D;

        /// <summary>Re-applies the owner/remote tint — used by PlayerHealth to
        /// hand color control back after a hit-flash finishes.</summary>
        public void RefreshTint() => ApplyTint(IsOwner ? _ownerColor : _remoteColor);

        /// <summary>Called by PlayerHealth around death/respawn. Disables input
        /// handling (Update/FixedUpdate both check this) and toggles mesh
        /// visibility; zeroes velocity so a dead body doesn't keep sliding.</summary>
        public void SetControlAndVisibilityEnabled(bool enabled)
        {
            _controlEnabled = enabled;

            if (_meshRenderers != null)
                foreach (var r in _meshRenderers)
                    if (r != null) r.enabled = enabled;

            if (!enabled && _rb != null)
                _rb.velocity = Vector3.zero;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_weapon == null) _weapon = GetComponent<WeaponController>();
            EnsureHeadPivot();
            EnsureShotPoints();
        }

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsOwner)
            {
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
                MID_Logger.LogInfo(_logLevel,
                    $"Local player spawned. OwnerClientId={OwnerClientId} IsServer={IsServer}",
                    nameof(NetworkedDimensionPlayer));
            }
            else
            {
                if (_rb != null)
                {
                    _rb.isKinematic   = true;
                    _rb.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }

            // Only the local owner's on-screen controls should ever be visible/interactable.
            // Every spawned player instance (including remote ones on your own screen) carries
            // its own copy of this Canvas — without this, every connected player's joystick,
            // shoot button, and mode/dimension/switch buttons render stacked on top of each other.
            if (_localPlayerCanvas != null)
                _localPlayerCanvas.gameObject.SetActive(IsOwner);

            ApplyTint(IsOwner ? _ownerColor : _remoteColor);
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
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
            if (!_controlEnabled) return;

            if (Input.GetKeyDown(_dimensionKey)) HandleDimensionButtonPressed();

            if (Use3DConvention()) HandleMouseLook();
            if (Input.GetKeyDown(_dashKey) && Time.time >= _nextDashTime && !_isDashing)
                StartDash();
        }

        private void FixedUpdate()
        {
            if (!IsOwner || !_controlEnabled) return;
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

        #region Helpers

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

            // Branches on the Rigidbody's ACTUAL kinematic state (set by
            // ApplyRigidbodyConstraints, tied to _currentDimension) rather than
            // Use3DConvention(), which also reads true for a 3D SHOOT MODE
            // regardless of dimension — pressing a 3D shoot-mode hotkey while
            // still in the 2D dimension must NOT flip this into the velocity
            // branch while the rigidbody is still kinematic from 2D's
            // ApplyRigidbodyConstraints ("Setting linear velocity of a
            // kinematic body is not supported"). Matches the dash branch above.
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
