// packages/com.midmanstudio.projectilesystem/Tests/Runtime/NetworkedDimensionPlayer.cs
//
// CHANGES vs original:
//   + PlayerShootMode enum — keys 1-4 switch at runtime:
//       1 = LocalOnly  (Rust sim, offline — the "Managed" path. There is NO
//                       per-projectile MonoBehaviour. NativeProjectile structs
//                       live in a GC-pinned array; Rust FFI ticks them each
//                       FixedUpdate. ProjectileRenderer2D/3D draws from the
//                       buffer. ProjectileVisual_ is only for client prediction.)
//       2 = RustSim2D  (server-auth 2D Rust sim, uses _configId2D)
//       3 = RustSim3D  (server-auth 3D Rust sim, uses _configId3D)
//       4 = Raycast    (hitscan; server re-validates hit point via RPC)
//   + _shotPoint2D: auto-created at (0.55, 0, 0) local — 2D muzzle (right side).
//   + _shotPoint3D: auto-created at (0.25, -0.05, 0.5) from headPivot — FPS muzzle.
//   + _modeDisplayText: optional TMP_Text shows current mode (wire in inspector).
//   + RegisterPlayerCams now passes headPivot as followTarget3D for FPS cam.
//   + FIX: fire key remains Input.GetKey(_fireKey) (default F), not mouse button.

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
    /// <summary>
    /// Which simulation subsystem fires projectiles.
    /// Switch at runtime with keys 1-4 to compare each path.
    /// </summary>
    public enum PlayerShootMode
    {
        /// <summary>
        /// Full Rust sim, offline — LocalProjectileManager handles it.
        /// WeaponFireContext.IsNetworked = false → skips all NGO RPCs.
        /// This IS the "Managed" path. No per-projectile MonoBehaviour exists;
        /// the data lives in a pinned NativeProjectile[] ticked by Rust FFI.
        /// </summary>
        LocalOnly = 0,

        /// <summary>Server-authoritative Rust 2D sim. Always uses _configId2D.</summary>
        RustSim2D = 1,

        /// <summary>Server-authoritative Rust 3D sim. Always uses _configId3D.</summary>
        RustSim3D = 2,

        /// <summary>
        /// Instant hitscan. Physics / Physics2D.Raycast from shot point.
        /// Server re-validates hit point; client sends result via RPC.
        /// </summary>
        Raycast = 3,
    }

    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class NetworkedDimensionPlayer : NetworkBehaviour
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Inspector
        // ─────────────────────────────────────────────────────────────────────

        #region Inspector — Transforms

        [Header("Transforms")]
        [Tooltip("Eye-level pivot that rotates with mouse look in 3D.\nAuto-created at (0, 0.85, 0) local if null.")]
        [SerializeField] private Transform _headPivot;

        [Tooltip("2D muzzle point. Auto-created at (0.55, 0, 0) local if null.")]
        [SerializeField] private Transform _shotPoint2D;

        [Tooltip("3D muzzle point. Auto-created at (0.25, -0.05, 0.5) from headPivot if null.")]
        [SerializeField] private Transform _shotPoint3D;

        #endregion

        #region Inspector — Cameras

        [Header("Cinemachine Virtual Cameras")]
        [Tooltip("2D platformer cam. DimensionCameraController configures it on spawn.")]
        [SerializeField] private CinemachineVirtualCamera _vcam2D;

        [Tooltip("3D FPS cam (HardLockToTarget body). DimensionCameraController configures it on spawn.")]
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        #endregion

        #region Inspector — Visuals

        [Header("Visuals")]
        [SerializeField] private Renderer[] _meshRenderers;
        [SerializeField] private Color _ownerColor  = new Color(0.20f, 0.80f, 1.00f);
        [SerializeField] private Color _remoteColor = new Color(1.00f, 0.40f, 0.30f);

        #endregion

        #region Inspector — Movement

        [Header("Movement")]
        [SerializeField] private float _moveSpeed2D = 6f;
        [SerializeField] private float _moveSpeed3D = 5f;
        [SerializeField] private float _jumpForce   = 7f;

        [Header("3D Mouse Look")]
        [SerializeField] private float _mouseSensitivity = 2f;
        [SerializeField, Range(-80f, 0f)]  private float _pitchMin = -80f;
        [SerializeField, Range(0f,  80f)]  private float _pitchMax =  80f;

        #endregion

        #region Inspector — Firing

        [Header("Projectile Configs")]
        [Tooltip("Used for LocalOnly, RustSim2D, and Raycast when in 2D dimension.")]
        [SerializeField] private ushort _configId2D = 0;

        [Tooltip("Used for RustSim3D and Raycast when in 3D dimension.")]
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire")]
        [SerializeField] private float _fireRate = 5f;
        [SerializeField, Range(1, 16)] private int  _pelletsPerShot = 1;
        [SerializeField, Range(0f, 45f)] private float _spreadDeg  = 0f;
        [Tooltip("Hold this key to fire continuously at _fireRate.")]
        [SerializeField] private KeyCode _fireKey = KeyCode.F;

        [Header("Shoot Mode  (Keys 1-4 at runtime)")]
        [SerializeField] private PlayerShootMode _shootMode = PlayerShootMode.LocalOnly;
        [Tooltip("Optional TMP_Text on the HUD — shows current shoot mode and switch hints.")]
        [SerializeField] private TMP_Text _modeDisplayText;

        [Header("Raycast (mode 4)")]
        [SerializeField] private LayerMask _raycastLayers = -1;
        [SerializeField] private float     _raycastRange  = 200f;

        #endregion

        #region Inspector — Debug

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        //  Private state
        // ─────────────────────────────────────────────────────────────────────

        private Rigidbody _rb;
        private Dimension _currentDimension = Dimension.TwoD;
        private bool      _grounded;
        private float     _nextFireTime;
        private float     _yaw;
        private float     _pitch;

        // ─────────────────────────────────────────────────────────────────────
        //  Unity lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ApplyRigidbodyConstraints(Dimension.TwoD);
            EnsureHeadPivot();
            EnsureShotPoints();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  NGO lifecycle
        // ─────────────────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            SetVcamActive(_vcam2D, false);
            SetVcamActive(_vcam3D, false);

            if (IsOwner)
            {
                // Pass both follow targets: body (2D), headPivot (3D FPS)
                if (DimensionCameraController.Instance != null)
                {
                    DimensionCameraController.Instance.RegisterPlayerCams(
                        _vcam2D, _vcam3D, transform, _headPivot);
                }
                else
                {
                    // No controller — fall back to activating 2D cam directly
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
                UpdateModeDisplay();
            }

            ApplyTint(IsOwner ? _ownerColor : _remoteColor);

            MID_Logger.LogInfo(_logLevel,
                $"Spawned — IsOwner={IsOwner} clientId={OwnerClientId} netId={NetworkObjectId}",
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

        // ─────────────────────────────────────────────────────────────────────
        //  Update / FixedUpdate
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!IsOwner) return;

            // Tab — dimension switch
            if (Input.GetKeyDown(KeyCode.Tab)
                && DimensionManager.HasInstance
                && !DimensionManager.Instance.IsTransitioning)
            {
                DimensionManager.Instance.SwitchDimension();
            }

            // Keys 1-4 — shoot mode switch
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetShootMode(PlayerShootMode.LocalOnly);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SetShootMode(PlayerShootMode.RustSim2D);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SetShootMode(PlayerShootMode.RustSim3D);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SetShootMode(PlayerShootMode.Raycast);

            if (_currentDimension == Dimension.ThreeD)
                HandleMouseLook();

            HandleFire();
        }

        private void FixedUpdate()
        {
            if (!IsOwner) return;
            HandleMovement();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Shoot mode
        // ─────────────────────────────────────────────────────────────────────

        private void SetShootMode(PlayerShootMode mode)
        {
            _shootMode = mode;
            UpdateModeDisplay();
            MID_Logger.LogInfo(_logLevel,
                $"Shoot mode → {mode}", nameof(NetworkedDimensionPlayer));
        }

        private void UpdateModeDisplay()
        {
            if (_modeDisplayText == null) return;
            _modeDisplayText.text = _shootMode switch
            {
                PlayerShootMode.LocalOnly => "[1] LOCAL — Rust sim offline (no network)",
                PlayerShootMode.RustSim2D => "[2] RUST 2D — server-auth sim",
                PlayerShootMode.RustSim3D => "[3] RUST 3D — server-auth sim",
                PlayerShootMode.Raycast   => "[4] RAYCAST — hitscan",
                _                         => $"Mode: {_shootMode}"
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Mouse look (3D FPS)
        // ─────────────────────────────────────────────────────────────────────

        private void HandleMouseLook()
        {
            float mx = Input.GetAxisRaw("Mouse X") * _mouseSensitivity;
            float my = Input.GetAxisRaw("Mouse Y") * _mouseSensitivity;

            _yaw   += mx;
            _pitch -= my;
            _pitch  = Mathf.Clamp(_pitch, _pitchMin, _pitchMax);

            // Body yaw (horizontal look)
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

            // Head pitch (vertical look) — Cinemachine FPS cam tracks headPivot
            if (_headPivot != null)
                _headPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Movement
        // ─────────────────────────────────────────────────────────────────────

        private void HandleMovement()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if (_currentDimension == Dimension.TwoD)
            {
                // 2D platformer: move in XY plane
                _rb.velocity = new Vector3(h * _moveSpeed2D, v * _moveSpeed2D, 0f);
            }
            else
            {
                // 3D FPS: strafe + forward, gravity via Rigidbody
                Vector3 dir = (transform.right * h + transform.forward * v).normalized;
                _rb.velocity = new Vector3(
                    dir.x * _moveSpeed3D,
                    _rb.velocity.y,
                    dir.z * _moveSpeed3D);

                if (_grounded && Input.GetButton("Jump"))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Firing — dispatch
        // ─────────────────────────────────────────────────────────────────────

        private void HandleFire()
        {
            if (!Input.GetKey(_fireKey))    return;
            if (Time.time < _nextFireTime)  return;
            if (!ProjectileRegistry.HasInstance) return;

            _nextFireTime = Time.time + 1f / Mathf.Max(_fireRate, 0.01f);

            if (_shootMode == PlayerShootMode.Raycast)
                FireRaycast();
            else
                FireSimProjectile();
        }

        // ── Sim projectile (LocalOnly / RustSim2D / RustSim3D) ───────────────

        private void FireSimProjectile()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            bool   is3D  = ResolveIs3D();
            ushort cfgId = ResolveConfigId(is3D);

            var cfg = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"ConfigId {cfgId} not registered — check ProjectileRegistry.",
                    nameof(NetworkedDimensionPlayer));
                return;
            }

            Transform shotPoint = is3D ? _shotPoint3D : _shotPoint2D;
            if (shotPoint == null) shotPoint = transform;

            Vector3 fwdDir = ResolveFireDirection(is3D);
            int     n      = Mathf.Max(_pelletsPerShot, 1);
            var     pts    = BuildSpawnPoints(shotPoint.position, fwdDir, n, cfg);

            // LocalOnly forces IsNetworked = false regardless of NetworkManager state.
            // MasterProjectileSystem routes to LocalProjectileManager when IsNetworked=false.
            bool networked = _shootMode != PlayerShootMode.LocalOnly
                          && MID_MasterProjectileSystem.Instance.IsNetworked
                          && IsSpawned;

            var ctx = new WeaponFireContext
            {
                FireRate               = _fireRate,
                ProjectileCount        = n,
                IsNetworked            = networked,
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
                $"Fire [{_shootMode}] cfgId={cfgId} n={n} dir={fwdDir:F2} networked={networked}",
                nameof(NetworkedDimensionPlayer));
        }

        // ── Raycast hitscan ───────────────────────────────────────────────────

        private void FireRaycast()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            bool   is3D     = ResolveIs3D();
            ushort cfgId    = ResolveConfigId(is3D);
            Transform sp    = is3D ? _shotPoint3D : _shotPoint2D;
            Vector3 origin  = sp != null ? sp.position : transform.position;
            Vector3 dir     = ResolveFireDirection(is3D);

            bool    didHit   = false;
            Vector3 hitPoint = origin + dir * _raycastRange;
            ulong   hitNetId = 0;
            bool    headshot = false;

            if (is3D)
            {
                if (Physics.Raycast(origin, dir, out RaycastHit hit3D,
                    _raycastRange, _raycastLayers))
                {
                    didHit   = true;
                    hitPoint = hit3D.point;
                    var no = hit3D.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) hitNetId = no.NetworkObjectId;
                }
            }
            else
            {
                var hit2D = Physics2D.Raycast(origin, dir, _raycastRange, _raycastLayers);
                if (hit2D.collider != null)
                {
                    didHit   = true;
                    hitPoint = hit2D.point;
                    var no = hit2D.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) hitNetId = no.NetworkObjectId;
                }
            }

            var result = new RaycastFireResult
            {
                Origin             = origin,
                Direction          = dir,
                HitPoint           = hitPoint,
                DidHit             = didHit,
                HitTargetNetworkId = hitNetId,
                IsHeadshot         = headshot
            };

            var ctx = new WeaponFireContext
            {
                FireRate               = _fireRate,
                ProjectileCount        = 1,
                IsNetworked            = MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned,
                IsRaycastWeapon        = true,
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f
            };

            MID_MasterProjectileSystem.Instance.RegisterRaycastFire(result, cfgId, ctx);

            MID_Logger.LogDebug(_logLevel,
                $"Raycast [{(didHit ? "HIT" : "MISS")}] origin={origin:F1} pt={hitPoint:F1}",
                nameof(NetworkedDimensionPlayer));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Fire helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve whether to use 3D projectiles based on current mode.
        /// RustSim3D forces 3D regardless of dimension. RustSim2D forces 2D.
        /// All other modes follow current dimension.
        /// </summary>
        private bool ResolveIs3D()
        {
            return _shootMode switch
            {
                PlayerShootMode.RustSim3D => true,
                PlayerShootMode.RustSim2D => false,
                _                         => _currentDimension == Dimension.ThreeD
            };
        }

        private ushort ResolveConfigId(bool is3D) => is3D ? _configId3D : _configId2D;

        private Vector3 ResolveFireDirection(bool is3D)
        {
            if (is3D)
                return _headPivot != null ? _headPivot.forward : transform.forward;
            return transform.right;   // 2D: fire to the right (player faces right by default)
        }

        private SpawnPoint[] BuildSpawnPoints(
            Vector3 origin, Vector3 fwdDir, int n, ProjectileConfigSO cfg)
        {
            bool is3D = ResolveIs3D();
            var  pts  = new SpawnPoint[n];
            for (int i = 0; i < n; i++)
            {
                float fraction   = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                float angle      = fraction * _spreadDeg;

                Vector3 spreadDir = is3D
                    ? Quaternion.Euler(0f, angle, 0f) * fwdDir
                    : Quaternion.Euler(0f, 0f, angle) * fwdDir;

                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = spreadDir.normalized,
                    Speed     = cfg.ResolveSpeed()
                };
            }
            return pts;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Dimension switch
        // ─────────────────────────────────────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────────────
        //  Initialisation helpers
        // ─────────────────────────────────────────────────────────────────────

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
            // 2D muzzle: to the right of the player body (fire direction = transform.right)
            if (_shotPoint2D == null)
            {
                var go = new GameObject("ShotPoint2D");
                go.transform.SetParent(transform);
                go.transform.localPosition = new Vector3(0.55f, 0f, 0f);
                _shotPoint2D = go.transform;
            }

            // 3D muzzle: slightly right and forward of headPivot (FPS gun barrel position)
            if (_shotPoint3D == null)
            {
                Transform parent = _headPivot != null ? _headPivot : transform;
                var go = new GameObject("ShotPoint3D");
                go.transform.SetParent(parent);
                go.transform.localPosition = new Vector3(0.25f, -0.05f, 0.5f);
                _shotPoint3D = go.transform;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Utility helpers
        // ─────────────────────────────────────────────────────────────────────

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

        private void OnCollisionStay(Collision _) => _grounded = true;
        private void OnCollisionExit(Collision _) => _grounded = false;
    }
}
