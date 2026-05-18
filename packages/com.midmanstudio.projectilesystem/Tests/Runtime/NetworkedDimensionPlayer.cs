// packages/com.midmanstudio.projectilesystem/Tests/Runtime/NetworkedDimensionPlayer.cs
// REWRITTEN:
//   - 3D mode is now true first-person (mouse look, cursor locked, vcam3D on HeadPivot)
//   - vcam2D / vcam3D registered with DimensionCameraController AFTER network spawn
//   - Shooting uses MID_MasterProjectileSystem.Instance.IsNetworked for correct routing
//     (respects Force Offline flag — no more "only shoots in force offline" bug)
//   - In 3D, fire direction = HeadPivot.forward (where camera looks)
//   - Movement in 3D is relative to camera yaw, not world forward

using UnityEngine;
using Unity.Netcode;
using Cinemachine;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Config;

namespace TestGame
{
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class NetworkedDimensionPlayer : NetworkBehaviour
    {
        #region Inspector

        [Header("Transforms")]
        [Tooltip("Empty child at the barrel tip. Fire origin + direction source.")]
        [SerializeField] private Transform _shotPoint;
        [Tooltip("Empty child at eye/head height (~0.8 local Y). Rotated by mouse look in 3D.")]
        [SerializeField] private Transform _headPivot;

        [Header("Cinemachine Cameras (children of this prefab)")]
        [Tooltip("2D overhead / side-scroll vcam. Body: Framing Transposer. Aim: Composer.")]
        [SerializeField] private CinemachineVirtualCamera _vcam2D;
        [Tooltip("3D first-person vcam on HeadPivot. Body: Do Nothing. Aim: Do Nothing.")]
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        [Header("Visuals")]
        [Tooltip("Renderer(s) to tint by owner / remote colour.")]
        [SerializeField] private Renderer[] _meshRenderers;
        [SerializeField] private Color _ownerColor  = new Color(0.20f, 0.80f, 1.00f);
        [SerializeField] private Color _remoteColor = new Color(1.00f, 0.40f, 0.30f);

        [Header("Movement")]
        [SerializeField] private float _moveSpeed2D = 6f;
        [SerializeField] private float _moveSpeed3D = 5f;
        [SerializeField] private float _jumpForce   = 7f;

        [Header("3D Mouse Look")]
        [SerializeField] private float _mouseSensitivity = 2f;
        [SerializeField, Range(  -80f, 0f)]   private float _pitchMin = -80f;
        [SerializeField, Range(    0f, 80f)]  private float _pitchMax =  80f;

        [Header("Projectile Config IDs")]
        [Tooltip("ushort config ID as registered in ProjectileRegistry (0 = first registered).")]
        [SerializeField] private ushort _configId2D = 0;
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire")]
        [SerializeField] private float _fireRate         = 5f;
        [SerializeField, Range(1, 16)]
                         private int   _pelletsPerShot   = 1;
        [SerializeField, Range(0f, 45f)]
                         private float _spreadDeg        = 0f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Private State

        private Rigidbody _rb;
        private Dimension _currentDimension = Dimension.TwoD;
        private bool      _grounded;
        private float     _nextFireTime;

        // FPS mouse look
        private float _yaw;    // horizontal — applied to player body
        private float _pitch;  // vertical   — applied to HeadPivot

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ApplyRigidbodyConstraints(Dimension.TwoD);

            // Create HeadPivot if not assigned (fallback)
            if (_headPivot == null)
            {
                var go = new GameObject("HeadPivot");
                go.transform.SetParent(transform);
                go.transform.localPosition = new Vector3(0f, 0.8f, 0f);
                _headPivot = go.transform;
            }
        }

        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Both vcams start hidden; DimensionCameraController activates the right one
            SetVcamActive(_vcam2D, false);
            SetVcamActive(_vcam3D, false);

            // Only the owner drives cameras and input
            if (IsOwner)
            {
                // Register our vcams with the scene camera controller AFTER we spawn.
                // The controller sets vcam2D.Follow/LookAt and activates the correct vcam.
                if (DimensionCameraController.Instance != null)
                {
                    DimensionCameraController.Instance.RegisterPlayerCams(
                        _vcam2D, _vcam3D, transform);
                }
                else
                {
                    // Fallback: activate vcam2D directly if no controller in scene
                    SetVcamActive(_vcam2D, true);
                }

                // Subscribe to dimension changes
                if (DimensionManager.Instance != null)
                    DimensionManager.Instance.OnDimensionChanged += OnDimensionChanged;

                // Tell the projectile system who the local player is (for prediction)
                if (MID_MasterProjectileSystem.HasInstance)
                    MID_MasterProjectileSystem.Instance.SetLocalPlayerMidId(OwnerClientId);

                // Apply start-dimension cursor state
                ApplyCursorState(Dimension.TwoD);

                // Seed yaw from current rotation so there's no snap on first frame
                _yaw = transform.eulerAngles.y;
            }

            // Tint so testers can tell themselves apart
            ApplyTint(IsOwner ? _ownerColor : _remoteColor);

            MID_Logger.LogInfo(_logLevel,
                $"Spawned — IsOwner={IsOwner} clientId={OwnerClientId} netObjId={NetworkObjectId}",
                nameof(NetworkedDimensionPlayer));
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                if (DimensionCameraController.Instance != null)
                    DimensionCameraController.Instance.UnregisterPlayerCams();

                if (DimensionManager.Instance != null)
                    DimensionManager.Instance.OnDimensionChanged -= OnDimensionChanged;

                // Restore cursor on despawn
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }
            base.OnNetworkDespawn();
        }

        #endregion

        #region Update

        private void Update()
        {
            if (!IsOwner) return;

            // Tab: toggle dimension (owner drives scene-wide switch)
            if (Input.GetKeyDown(KeyCode.Tab)
                && DimensionManager.Instance != null
                && !DimensionManager.Instance.IsTransitioning)
            {
                DimensionManager.Instance.SwitchDimension();
            }

            if (_currentDimension == Dimension.ThreeD)
                HandleMouseLook();

            HandleFire();
        }

        private void FixedUpdate()
        {
            if (!IsOwner) return;
            HandleMovement();
        }

        #endregion

        #region Mouse Look (3D FPS)

        private void HandleMouseLook()
        {
            float mouseX = Input.GetAxisRaw("Mouse X") * _mouseSensitivity;
            float mouseY = Input.GetAxisRaw("Mouse Y") * _mouseSensitivity;

            _yaw   += mouseX;
            _pitch -= mouseY;                              // invert Y: drag up → look up
            _pitch  = Mathf.Clamp(_pitch, _pitchMin, _pitchMax);

            // Body rotates horizontally
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

            // Head pivot rotates vertically — vcam3D (child of HeadPivot) follows
            if (_headPivot != null)
                _headPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        #endregion

        #region Movement

        private void HandleMovement()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if (_currentDimension == Dimension.TwoD)
            {
                // Side-scroll / top-down XY plane — velocity directly applied
                _rb.velocity = new Vector3(h * _moveSpeed2D, v * _moveSpeed2D, 0f);
            }
            else
            {
                // 3D FPS: move relative to player's current yaw rotation
                Vector3 moveDir = (transform.right * h + transform.forward * v).normalized;
                _rb.velocity = new Vector3(
                    moveDir.x * _moveSpeed3D,
                    _rb.velocity.y,          // preserve gravity
                    moveDir.z * _moveSpeed3D);

                if (_grounded && Input.GetButton("Jump"))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        #endregion

        #region Firing

        private void HandleFire()
        {
            if (!Input.GetMouseButton(0))  return;
            if (Time.time < _nextFireTime) return;
            if (_shotPoint == null)        return;
            if (!ProjectileRegistry.HasInstance) return;

            _nextFireTime = Time.time + 1f / Mathf.Max(_fireRate, 0.01f);
            Fire();
        }

        private void Fire()
        {
            bool is3D   = _currentDimension == Dimension.ThreeD;
            ushort cfgId = is3D ? _configId3D : _configId2D;

            var cfg = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Config ID {cfgId} not registered. Assign configs to ProjectileRegistry first.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            if (!MID_MasterProjectileSystem.HasInstance) return;

            // ── Fire direction ────────────────────────────────────────────────
            Vector3 forwardDir;
            if (is3D)
            {
                // FPS: shoot where the head/camera is looking
                forwardDir = _headPivot != null
                    ? _headPivot.forward
                    : _shotPoint.forward;
            }
            else
            {
                // 2D: shoot right (side-scroll). For top-down, use transform.up instead.
                forwardDir = transform.right;
            }

            // ── Build spread spawn points ─────────────────────────────────────
            int n    = Mathf.Max(_pelletsPerShot, 1);
            var pts  = new SpawnPoint[n];

            for (int i = 0; i < n; i++)
            {
                float fraction   = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                float angleDelta = fraction * _spreadDeg;

                Vector3 spreadDir = is3D
                    ? Quaternion.Euler(0f, angleDelta, 0f) * forwardDir
                    : Quaternion.Euler(0f, 0f, angleDelta) * forwardDir;

                pts[i] = new SpawnPoint
                {
                    Origin    = _shotPoint.position,
                    Direction = spreadDir.normalized,
                    Speed     = cfg.ResolveSpeed()
                };
            }

            // ── Context: use system's IsNetworked so Force Offline Mode is respected ──
            // This fixes the "only shoots in force offline mode" bug:
            // Previously used `IsSpawned` which ignored the Force Offline flag.
            bool systemIsNetworked = MID_MasterProjectileSystem.Instance.IsNetworked;

            var ctx = new WeaponFireContext
            {
                FireRate               = _fireRate,
                ProjectileCount        = n,
                IsNetworked            = systemIsNetworked && IsSpawned,
                IsRaycastWeapon        = false,
                LatencyCompensation    = 0f,
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f
            };

            MID_MasterProjectileSystem.Instance.Fire(cfgId, pts, n, ctx);

            MID_Logger.LogDebug(_logLevel,
                $"Fire — cfgId={cfgId} n={n} dir={forwardDir:F2} networked={ctx.IsNetworked}",
                nameof(NetworkedDimensionPlayer));
        }

        #endregion

        #region Dimension Switch

        /// <summary>Called by DimensionManager after a transition completes.</summary>
        public void OnDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyRigidbodyConstraints(dim);
            ApplyCursorState(dim);

            // Camera controller handles vcam activation —
            // we just need to snap yaw so there's no sudden rotation jump
            if (dim == Dimension.ThreeD)
                _yaw = transform.eulerAngles.y;

            MID_Logger.LogInfo(_logLevel,
                $"Dimension → {dim}", nameof(NetworkedDimensionPlayer));
        }

        #endregion

        #region Helpers

        private void ApplyRigidbodyConstraints(Dimension dim)
        {
            if (_rb == null) return;

            if (dim == Dimension.TwoD)
            {
                // Lock Z and all rotation for flat 2D movement
                _rb.constraints = RigidbodyConstraints.FreezePositionZ
                                | RigidbodyConstraints.FreezeRotation;
                _rb.useGravity  = false;
            }
            else
            {
                // Lock only X/Z rotation — Y rotation is handled by mouse look code
                _rb.constraints = RigidbodyConstraints.FreezeRotationX
                                | RigidbodyConstraints.FreezeRotationZ;
                _rb.useGravity  = true;

                // Snap Z to 0 when entering 3D from 2D
                var p = transform.position;
                transform.position = new Vector3(p.x, p.y, 0f);
                _rb.velocity       = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
            }
        }

        private static void ApplyCursorState(Dimension dim)
        {
            if (dim == Dimension.ThreeD)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }
        }

        private static void SetVcamActive(CinemachineVirtualCamera vcam, bool active)
        {
            if (vcam != null) vcam.gameObject.SetActive(active);
        }

        private void ApplyTint(Color col)
        {
            if (_meshRenderers == null) return;
            foreach (var r in _meshRenderers)
                if (r != null) r.material.color = col;
        }

        private void OnCollisionStay(Collision c) => _grounded = true;
        private void OnCollisionExit(Collision c)  => _grounded = false;

        #endregion
    }
}
