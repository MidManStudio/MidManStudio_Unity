// NetworkedDimensionPlayer.cs
//
// CHANGES vs previous:
//   + _dimensionKey replaces hard-coded Tab — default BackQuote.
//   + _shotPattern (ProjectilePatternSO) — optional pattern asset.
//     When assigned, SampleDirections() drives spread instead of _spreadDeg.
//     Pattern.ProjectileCount overrides _pelletsPerShot for the pellet count.
//   + BuildSpawnPointsFromPattern() converts pattern angle pairs to world-space
//     SpawnPoints, applying speed variance per-pellet from the pattern.
//   + NOTE: For networked (RustSim2D/3D) mode the server still receives only
//     the first spawn point direction — full pattern replication across the
//     network requires serialising all directions and is a future task.
//     LocalOnly mode correctly uses the full SpawnPoint[] array.

using UnityEngine;
using Unity.Netcode;
using Cinemachine;
using TMPro;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Config;

namespace TestGame
{
    public enum PlayerShootMode
    {
        LocalOnly = 0,
        RustSim2D = 1,
        RustSim3D = 2,
        Raycast   = 3,
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

        [Header("3D Mouse Look")]
        [SerializeField] private float _mouseSensitivity = 2f;
        [SerializeField, Range(-80f, 0f)] private float _pitchMin = -80f;
        [SerializeField, Range(0f, 80f)]  private float _pitchMax =  80f;

        [Header("Projectile Config IDs")]
        [SerializeField] private ushort _configId2D = 0;
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire")]
        [SerializeField] private float   _fireRate      = 5f;
        [SerializeField, Range(1, 64)] private int _pelletsPerShot = 1;
        [Tooltip("Spread used when no pattern SO is assigned.")]
        [SerializeField, Range(0f, 45f)] private float _spreadDeg = 0f;
        [SerializeField] private KeyCode _fireKey = KeyCode.Mouse0;

        [Header("Shot Pattern (optional)")]
        [Tooltip("Assign a ProjectilePatternSO to use spline-defined spread.\n" +
                 "When assigned: pattern.ProjectileCount overrides _pelletsPerShot,\n" +
                 "and pattern angles override _spreadDeg.")]
        [SerializeField] private ProjectilePatternSO _shotPattern;

        [Header("Shoot Mode  (1=LocalOnly  2=RustSim2D  3=RustSim3D  4=Raycast)")]
        [SerializeField] private PlayerShootMode _shootMode = PlayerShootMode.LocalOnly;
        [SerializeField] private TMP_Text _modeText;

        [Header("Dimension Toggle Key")]
        [Tooltip("Key to switch 2D ↔ 3D. Must match DimensionManager._dimensionToggleKey.")]
        [SerializeField] private KeyCode _dimensionKey = KeyCode.BackQuote;

        [Header("Raycast")]
        [SerializeField] private LayerMask _raycastLayers = -1;
        [SerializeField] private float     _raycastRange  = 200f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

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
                if (DimensionCameraController.Instance != null)
                    DimensionCameraController.Instance.RegisterPlayerCams(transform, _headPivot);
                else
                    Debug.LogWarning("[NetworkedDimensionPlayer] DimensionCameraController not found.", this);

                if (DimensionManager.HasInstance)
                    DimensionManager.Instance.OnDimensionChanged += HandleDimensionChanged;

                if (MID_MasterProjectileSystem.HasInstance)
                    MID_MasterProjectileSystem.Instance.SetLocalPlayerMidId(OwnerClientId);

                Dimension current = DimensionManager.HasInstance
                    ? DimensionManager.Instance.Current : Dimension.TwoD;
                if (current != Dimension.TwoD)
                    HandleDimensionChanged(current);

                _yaw = transform.eulerAngles.y;
                ApplyCursorState(_currentDimension);
                UpdateModeText();
            }

            ApplyTint(IsOwner ? _ownerColor : _remoteColor);

            MID_Logger.LogInfo(_logLevel,
                $"Spawned IsOwner={IsOwner} clientId={OwnerClientId}",
                nameof(NetworkedDimensionPlayer));
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                DimensionCameraController.Instance?.UnregisterPlayerCams();

                if (DimensionManager.HasInstance)
                    DimensionManager.Instance.OnDimensionChanged -= HandleDimensionChanged;

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

            // Dimension toggle — uses configurable key (no Tab)
            if (Input.GetKeyDown(_dimensionKey)
                && DimensionManager.HasInstance
                && !DimensionManager.Instance.IsTransitioning)
                DimensionManager.Instance.SwitchDimension();

            // Shoot mode keys
            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeMode(PlayerShootMode.LocalOnly);
            if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeMode(PlayerShootMode.RustSim2D);
            if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeMode(PlayerShootMode.RustSim3D);
            if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeMode(PlayerShootMode.Raycast);

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

        #region Shoot Mode

        private void ChangeMode(PlayerShootMode m)
        {
            _shootMode = m;
            UpdateModeText();
            MID_Logger.LogInfo(_logLevel, $"Shoot mode → {m}", nameof(NetworkedDimensionPlayer));
        }

        private void UpdateModeText()
        {
            if (_modeText == null) return;
            _modeText.text = _shootMode switch
            {
                PlayerShootMode.LocalOnly => "[1] LOCAL (Rust sim offline)",
                PlayerShootMode.RustSim2D => "[2] RUST 2D (server-auth)",
                PlayerShootMode.RustSim3D => "[3] RUST 3D (server-auth)",
                PlayerShootMode.Raycast   => "[4] RAYCAST (hitscan)",
                _                         => _shootMode.ToString()
            };
        }

        #endregion

        #region Mouse Look (3D FPS)

        private void HandleMouseLook()
        {
            _yaw   += Input.GetAxisRaw("Mouse X") * _mouseSensitivity;
            _pitch -= Input.GetAxisRaw("Mouse Y") * _mouseSensitivity;
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
                Vector3 dir = (transform.right * h + transform.forward * v).normalized;
                _rb.velocity = new Vector3(
                    dir.x * _moveSpeed3D, _rb.velocity.y, dir.z * _moveSpeed3D);

                if (_grounded && Input.GetButton("Jump"))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        #endregion

        #region Fire Dispatch

        private void HandleFire()
        {
            if (!Input.GetKey(_fireKey))   return;
            if (Time.time < _nextFireTime) return;
            if (!ProjectileRegistry.HasInstance) return;

            _nextFireTime = Time.time + 1f / Mathf.Max(_fireRate, 0.01f);

            if (_shootMode == PlayerShootMode.Raycast)
                FireRaycast();
            else
                FireSim();
        }

        // ── Sim (LocalOnly / RustSim2D / RustSim3D) ──────────────────────────

        private void FireSim()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            bool   is3D  = ResolveIs3D();
            ushort cfgId = is3D ? _configId3D : _configId2D;
            var    cfg   = ProjectileRegistry.Instance.Get(cfgId);

            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"ConfigId {cfgId} not registered.", nameof(NetworkedDimensionPlayer));
                return;
            }

            Transform sp     = is3D ? _shotPoint3D : _shotPoint2D;
            Vector3   origin = sp != null ? sp.position : transform.position;
            Vector3   dir    = ResolveFireDir(is3D);

            // Pattern overrides pellet count; fallback to inspector field
            int n = _shotPattern != null
                ? _shotPattern.ProjectileCount
                : Mathf.Max(_pelletsPerShot, 1);

            var pts = BuildSpawnPoints(origin, dir, n, cfg, is3D);

            bool networked = _shootMode != PlayerShootMode.LocalOnly
                          && MID_MasterProjectileSystem.Instance.IsNetworked
                          && IsSpawned;

            var ctx = new WeaponFireContext
            {
                FireRate               = _fireRate,
                ProjectileCount        = pts.Length,
                IsNetworked            = networked,
                IsRaycastWeapon        = false,
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f
            };

            MID_MasterProjectileSystem.Instance.Fire(cfgId, pts, pts.Length, ctx);
        }

        // ── Raycast hitscan ───────────────────────────────────────────────────

        private void FireRaycast()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            bool   is3D  = _currentDimension == Dimension.ThreeD;
            ushort cfgId = is3D ? _configId3D : _configId2D;

            Transform sp     = is3D ? _shotPoint3D : _shotPoint2D;
            Vector3   origin = sp != null ? sp.position : transform.position;
            Vector3   dir    = ResolveFireDir(is3D);

            bool    hit   = false;
            Vector3 hitPt = origin + dir * _raycastRange;
            ulong   netId = 0;

            if (is3D)
            {
                if (Physics.Raycast(origin, dir, out RaycastHit h, _raycastRange, _raycastLayers))
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

            var result = new RaycastFireResult
            {
                Origin = origin, Direction = dir,
                HitPoint = hitPt, DidHit = hit,
                HitTargetNetworkId = netId, IsHeadshot = false
            };

            var ctx = new WeaponFireContext
            {
                FireRate = _fireRate, ProjectileCount = 1,
                IsNetworked = MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned,
                IsRaycastWeapon = true,
                OwnerMidId = OwnerClientId,
                FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner = false, WeaponLevel = 1, DamageMultiplier = 1f
            };

            MID_MasterProjectileSystem.Instance.RegisterRaycastFire(result, cfgId, ctx);
        }

        #endregion

        #region Spawn Point Builders

        /// <summary>
        /// Build spawn points using the assigned pattern SO if available,
        /// otherwise fall back to the uniform spread defined by _spreadDeg.
        /// </summary>
        private SpawnPoint[] BuildSpawnPoints(
            Vector3 origin, Vector3 dir, int n,
            ProjectileConfigSO cfg, bool is3D)
        {
            if (_shotPattern != null)
                return BuildSpawnPointsFromPattern(origin, dir, cfg, is3D);

            return BuildSpawnPointsSpread(origin, dir, n, cfg, is3D);
        }

        /// <summary>
        /// Uniform spread — evenly distributes n pellets across ±_spreadDeg.
        /// Used when no pattern SO is assigned.
        /// </summary>
        private SpawnPoint[] BuildSpawnPointsSpread(
            Vector3 origin, Vector3 dir, int n,
            ProjectileConfigSO cfg, bool is3D)
        {
            var pts = new SpawnPoint[n];
            for (int i = 0; i < n; i++)
            {
                float frac  = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                var   sDir  = is3D
                    ? Quaternion.Euler(0f, frac * _spreadDeg, 0f) * dir
                    : Quaternion.Euler(0f, 0f,  frac * _spreadDeg) * dir;
                pts[i] = new SpawnPoint
                    { Origin = origin, Direction = sDir.normalized, Speed = cfg.ResolveSpeed() };
            }
            return pts;
        }

        /// <summary>
        /// Pattern-based spread — samples directions from the spline SO.
        /// The pattern returns (horizontal_deg, vertical_deg) pairs in local weapon space.
        /// These are applied as rotations around the base fire direction.
        ///
        /// For 2D: only horizontal angle (Z rotation) is used.
        /// For 3D: both horizontal (Y) and vertical (X) angles are applied.
        ///
        /// Speed variance per pellet is applied deterministically from pattern.RngSeed.
        /// </summary>
        private SpawnPoint[] BuildSpawnPointsFromPattern(
            Vector3 origin, Vector3 baseDir,
            ProjectileConfigSO cfg, bool is3D)
        {
            var angleDirs = _shotPattern.SampleDirections(); // uses pattern.ProjectileCount
            var pts       = new SpawnPoint[angleDirs.Length];

            for (int i = 0; i < angleDirs.Length; i++)
            {
                Vector2 angles = angleDirs[i]; // x = horizontal deg, y = vertical deg

                Quaternion rot;
                if (is3D)
                {
                    // Horizontal = yaw (Y axis), Vertical = pitch (X axis, negative = up)
                    rot = Quaternion.Euler(-angles.y, angles.x, 0f);
                }
                else
                {
                    // 2D — rotate around Z axis (horizontal spread only)
                    rot = Quaternion.Euler(0f, 0f, angles.x);
                }

                Vector3 sDir = rot * baseDir;

                float speedMult = _shotPattern.GetSpeedMultiplier(i, _shotPattern.RngSeed);
                float speed     = cfg.ResolveSpeed() * speedMult;

                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = sDir.normalized,
                    Speed     = speed
                };
            }

            MID_Logger.LogDebug(_logLevel,
                $"Pattern spawn: {angleDirs.Length} pellets from '{_shotPattern.name}'",
                nameof(NetworkedDimensionPlayer));

            return pts;
        }

        #endregion

        #region Helpers

        private bool ResolveIs3D() => _shootMode switch
        {
            PlayerShootMode.RustSim3D => true,
            PlayerShootMode.RustSim2D => false,
            _                         => _currentDimension == Dimension.ThreeD
        };

        private Vector3 ResolveFireDir(bool is3D)
            => is3D
               ? (_headPivot != null ? _headPivot.forward : transform.forward)
               : transform.right;

        private void HandleDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyRigidbodyConstraints(dim);
            ApplyCursorState(dim);
            if (dim == Dimension.ThreeD) _yaw = transform.eulerAngles.y;
        }

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
                _rb.velocity = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
            }
        }

        private static void ApplyCursorState(Dimension dim)
        {
            if (dim == Dimension.ThreeD)
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            else
            { Cursor.lockState = CursorLockMode.None;   Cursor.visible = true;  }
        }

        private void ApplyTint(Color col)
        {
            if (_meshRenderers == null) return;
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
