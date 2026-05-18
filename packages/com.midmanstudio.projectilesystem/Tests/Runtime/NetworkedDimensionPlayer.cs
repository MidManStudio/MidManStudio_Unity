// packages/com.midmanstudio.projectilesystem/Tests/Runtime/NetworkedDimensionPlayer.cs
// Networked player controller for the projectile system test scene.
// Add NetworkTransform component alongside this for automatic position sync.
// Requires: NetworkObject, NetworkTransform, Rigidbody on same GameObject.
// Cinemachine virtual cameras should be children of the player prefab.

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

        [Header("Shot Point")]
        [Tooltip("Empty transform placed at the barrel tip. Fire direction = right (2D) or forward (3D).")]
        [SerializeField] private Transform _shotPoint;

        [Header("Cinemachine Virtual Cameras  (children of this prefab)")]
        [Tooltip("Activated for owner in 2D mode. Must live on this GameObject or a child.")]
        [SerializeField] private CinemachineVirtualCamera _vcam2D;
        [Tooltip("Activated for owner in 3D mode.")]
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        [Header("Visuals")]
        [Tooltip("Renderers to tint — helps tell owner from remote players in test scene.")]
        [SerializeField] private Renderer[] _meshRenderers;
        [SerializeField] private Color _ownerColor    = new Color(0.2f, 0.8f, 1.0f, 1f);
        [SerializeField] private Color _remoteColor   = new Color(1.0f, 0.4f, 0.3f, 1f);

        [Header("Movement")]
        [SerializeField] private float _moveSpeed2D = 6f;
        [SerializeField] private float _moveSpeed3D = 5f;
        [SerializeField] private float _jumpForce   = 7f;

        [Header("Projectile Config IDs")]
        [Tooltip("Registered ushort config ID used in 2D mode. Match your ProjectileRegistry.")]
        [SerializeField] private ushort _configId2D = 0;
        [Tooltip("Registered ushort config ID used in 3D mode.")]
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire")]
        [Tooltip("Shots per second when mouse button held.")]
        [SerializeField] private float _fireRate = 5f;
        [Tooltip("Number of pellets per trigger pull (1 = single, 8 = shotgun).")]
        [SerializeField, Range(1, 16)] private int _pelletsPerShot = 1;
        [Tooltip("Horizontal spread angle in degrees for multiple pellets.")]
        [SerializeField, Range(0f, 45f)] private float _spreadDeg = 5f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Private State

        private Rigidbody _rb;
        private Dimension _currentDimension = Dimension.TwoD;
        private bool      _grounded;
        private float     _nextFireTime;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ApplyRigidbodyConstraints(Dimension.TwoD);
           
        }
      
        #endregion

        #region NGO Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (DimensionCameraController.HasInstance)
            {
                _vcam2D = DimensionCameraController.Instance._2d_Cam;
                _vcam3D = DimensionCameraController.Instance._3d_Cam;
            }
            // Only the owner drives cameras — deactivate on all others
            ActivateVCam(_vcam2D, IsOwner);   // start in 2D
            ActivateVCam(_vcam3D, false);

            // Tint so testers can tell themselves apart
            ApplyTint(IsOwner ? _ownerColor : _remoteColor);

            if (IsOwner)
            {
                // Subscribe to dimension changes from scene-level DimensionManager
                if (DimensionManager.Instance != null)
                    DimensionManager.Instance.OnDimensionChanged += OnDimensionChanged;

                // Let projectile system know who the local player is
                if (MID_MasterProjectileSystem.HasInstance)
                    MID_MasterProjectileSystem.Instance.SetLocalPlayerMidId(OwnerClientId);
            }

            MID_Logger.LogInfo(_logLevel,
                $"NetworkedDimensionPlayer spawned — IsOwner={IsOwner} " +
                $"clientId={OwnerClientId} netObjId={NetworkObjectId}",
                nameof(NetworkedDimensionPlayer));
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner && DimensionManager.Instance != null)
                DimensionManager.Instance.OnDimensionChanged -= OnDimensionChanged;

            base.OnNetworkDespawn();
        }

        #endregion

        #region Update

        private void Update()
        {
            if (!IsOwner) return;

            HandleMovement();
            HandleFire();

            // Tab to toggle dimension (owner only)
            if (Input.GetKeyDown(KeyCode.Tab)
                && DimensionManager.Instance != null
                && !DimensionManager.Instance.IsTransitioning)
            {
                DimensionManager.Instance.SwitchDimension();
            }
        }

        #endregion

        #region Movement

        private void HandleMovement()
        {
            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");

            if (_currentDimension == Dimension.TwoD)
            {
                // Side-scroll / top-down XY plane
                _rb.velocity = new Vector3(h * _moveSpeed2D, v * _moveSpeed2D, 0f);
            }
            else
            {
                // Standard 3D XZ plane with gravity
                _rb.velocity = new Vector3(
                    h * _moveSpeed3D,
                    _rb.velocity.y,
                    v * _moveSpeed3D);

                var moveDir = new Vector3(h, 0f, v);
                if (moveDir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(moveDir.normalized);

                if (_grounded && Input.GetKeyDown(KeyCode.Space))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        #endregion

        #region Firing

        private void HandleFire()
        {
            if (Input.GetKeyDown(KeyCode.F))
            {
                if (Time.time < _nextFireTime) return;
                if (_shotPoint == null) return;

                _nextFireTime = Time.time + 1f / Mathf.Max(_fireRate, 0.01f);
                Fire();
            }
        }

        private void Fire()
        {
            ushort cfgId = _currentDimension == Dimension.ThreeD ? _configId3D : _configId2D;

            if (!ProjectileRegistry.HasInstance)
            {
                MID_Logger.LogWarning(_logLevel,
                    "ProjectileRegistry not available.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            var cfg = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Config ID {cfgId} not registered — cannot fire. " +
                    "Set _configId2D / _configId3D in inspector and ensure registry is initialised.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            if (!MID_MasterProjectileSystem.HasInstance)
            {
                MID_Logger.LogWarning(_logLevel,
                    "MID_MasterProjectileSystem not found.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            // Build spread spawn points
            int n       = Mathf.Max(_pelletsPerShot, 1);
            var pts     = new SpawnPoint[n];
            Vector3 fwd = _currentDimension == Dimension.TwoD
                ? _shotPoint.right
                : _shotPoint.forward;

            for (int i = 0; i < n; i++)
            {
                float spreadFrac = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                float angle      = spreadFrac * _spreadDeg;

                Vector3 dir = _currentDimension == Dimension.TwoD
                    ? Quaternion.Euler(0f, 0f, angle) * fwd
                    : Quaternion.Euler(0f, angle, 0f) * fwd;

                pts[i] = new SpawnPoint
                {
                    Origin    = _shotPoint.position,
                    Direction = dir.normalized,
                    Speed     = cfg.ResolveSpeed()
                };
            }

            var ctx = new WeaponFireContext
            {
                FireRate               = _fireRate,
                ProjectileCount        = n,
                IsNetworked            = IsSpawned,
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
                $"Fired — cfgId={cfgId} pellets={n} dir={fwd:F2} owner={OwnerClientId}",
                nameof(NetworkedDimensionPlayer));
        }

        #endregion

        #region Dimension Change

        /// <summary>
        /// Called by DimensionManager after a transition completes.
        /// Updates Rigidbody constraints and swaps Cinemachine cameras.
        /// </summary>
        public void OnDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyRigidbodyConstraints(dim);

            if (IsOwner)
            {
                ActivateVCam(_vcam2D, dim == Dimension.TwoD);
                ActivateVCam(_vcam3D, dim == Dimension.ThreeD);
            }

            MID_Logger.LogInfo(_logLevel,
                $"Dimension changed → {dim}",
                nameof(NetworkedDimensionPlayer));
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

                // Snap Z to 0 when entering 3D
                var p = transform.position;
                transform.position = new Vector3(p.x, p.y, 0f);
                _rb.velocity       = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
            }
        }

        private static void ActivateVCam(CinemachineVirtualCamera vcam, bool active)
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
