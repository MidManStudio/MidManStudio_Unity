// NetworkedDimensionPlayer.cs
//
// FIX: Changed fire input from Input.GetMouseButton(0) to Input.GetKey(KeyCode.F).
//      Mouse button 0 was conflicting with editor clicks and was unintuitive for
//      quick sandbox testing.  F fires on hold (continuous fire at _fireRate),
//      matching the original mouse-button behaviour.  Change _fireKey in the
//      inspector if you want a different key.

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
        [SerializeField] private Transform _shotPoint;
        [SerializeField] private Transform _headPivot;

        [Header("Cinemachine Cameras")]
        [SerializeField] private CinemachineVirtualCamera _vcam2D;
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        [Header("Visuals")]
        [SerializeField] private Renderer[] _meshRenderers;
        [SerializeField] private Color _ownerColor  = new Color(0.20f, 0.80f, 1.00f);
        [SerializeField] private Color _remoteColor = new Color(1.00f, 0.40f, 0.30f);

        [Header("Movement")]
        [SerializeField] private float _moveSpeed2D = 6f;
        [SerializeField] private float _moveSpeed3D = 5f;
        [SerializeField] private float _jumpForce   = 7f;

        [Header("3D Mouse Look")]
        [SerializeField] private float _mouseSensitivity = 2f;
        [SerializeField, Range(-80f,  0f)] private float _pitchMin = -80f;
        [SerializeField, Range(  0f, 80f)] private float _pitchMax =  80f;

        [Header("Projectile Config IDs")]
        [SerializeField] private ushort _configId2D = 0;
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire")]
        [SerializeField] private float   _fireRate       = 5f;
        [SerializeField, Range(1, 16)]
                         private int     _pelletsPerShot = 1;
        [SerializeField, Range(0f, 45f)]
                         private float   _spreadDeg      = 0f;
        // FIX: exposed fire key so it can be changed in the inspector without
        //      modifying code.  Defaults to F for easy sandbox testing.
        [SerializeField] private KeyCode _fireKey        = KeyCode.F;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Private State

        private Rigidbody _rb;
        private Dimension _currentDimension = Dimension.TwoD;
        private bool      _grounded;
        private float     _nextFireTime;
        private float     _yaw;
        private float     _pitch;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ApplyRigidbodyConstraints(Dimension.TwoD);

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

            SetVcamActive(_vcam2D, false);
            SetVcamActive(_vcam3D, false);

            if (IsOwner)
            {
                if (DimensionCameraController.Instance != null)
                {
                    DimensionCameraController.Instance.RegisterPlayerCams(
                        _vcam2D, _vcam3D, transform);
                }
                else
                {
                    SetVcamActive(_vcam2D, true);
                }

                if (DimensionManager.HasInstance)
                    DimensionManager.Instance.OnDimensionChanged += HandleDimensionChanged;

                if (MID_MasterProjectileSystem.HasInstance)
                    MID_MasterProjectileSystem.Instance.SetLocalPlayerMidId(OwnerClientId);

                Dimension current = DimensionManager.HasInstance
                    ? DimensionManager.Instance.Current
                    : Dimension.TwoD;

                if (current != Dimension.TwoD)
                    HandleDimensionChanged(current);

                _yaw = transform.eulerAngles.y;
                ApplyCursorState(_currentDimension);
            }

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

                if (DimensionManager.HasInstance)
                    DimensionManager.Instance.OnDimensionChanged -= HandleDimensionChanged;

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }
            base.OnNetworkDespawn();
        }

        #endregion

        #region Update / FixedUpdate

        private void Update()
        {
            if (!IsOwner) return;

            if (Input.GetKeyDown(KeyCode.Tab)
                && DimensionManager.HasInstance
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

        #region Mouse Look (3D)

        private void HandleMouseLook()
        {
            float mouseX = Input.GetAxisRaw("Mouse X") * _mouseSensitivity;
            float mouseY = Input.GetAxisRaw("Mouse Y") * _mouseSensitivity;

            _yaw   += mouseX;
            _pitch -= mouseY;
            _pitch  = Mathf.Clamp(_pitch, _pitchMin, _pitchMax);

            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

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
                _rb.velocity = new Vector3(h * _moveSpeed2D, v * _moveSpeed2D, 0f);
            }
            else
            {
                Vector3 moveDir = (transform.right * h + transform.forward * v).normalized;
                _rb.velocity = new Vector3(
                    moveDir.x * _moveSpeed3D,
                    _rb.velocity.y,
                    moveDir.z * _moveSpeed3D);

                if (_grounded && Input.GetButton("Jump"))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        #endregion

        #region Firing

        private void HandleFire()
        {
            // FIX: use _fireKey (default F) instead of mouse button 0.
            // GetKey = hold to fire continuously at _fireRate.
            // Change _fireKey in the inspector to remap without editing code.
            if (!Input.GetKey(_fireKey))    return;
            if (Time.time < _nextFireTime)  return;
            if (_shotPoint == null)         return;
            if (!ProjectileRegistry.HasInstance) return;

            _nextFireTime = Time.time + 1f / Mathf.Max(_fireRate, 0.01f);
            Fire();
        }

        private void Fire()
        {
            bool   is3D  = _currentDimension == Dimension.ThreeD;
            ushort cfgId = is3D ? _configId3D : _configId2D;

            var cfg = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Config ID {cfgId} not registered. Assign configs to ProjectileRegistry.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            if (!MID_MasterProjectileSystem.HasInstance) return;

            Vector3 forwardDir = is3D
                ? (_headPivot != null ? _headPivot.forward : _shotPoint.forward)
                : transform.right;

            int n   = Mathf.Max(_pelletsPerShot, 1);
            var pts = new SpawnPoint[n];

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
                $"Fire — key={_fireKey} cfgId={cfgId} n={n} dir={forwardDir:F2} networked={ctx.IsNetworked}",
                nameof(NetworkedDimensionPlayer));
        }

        #endregion

        #region Dimension Switch

        private void HandleDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyRigidbodyConstraints(dim);
            ApplyCursorState(dim);

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
                _rb.constraints = RigidbodyConstraints.FreezePositionZ
                                | RigidbodyConstraints.FreezeRotation;
                _rb.useGravity  = false;
            }
            else
            {
                _rb.constraints = RigidbodyConstraints.FreezeRotationX
                                | RigidbodyConstraints.FreezeRotationZ;
                _rb.useGravity  = true;

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
        private void OnCollisionExit(Collision c) => _grounded = false;

        #endregion
    }
}
