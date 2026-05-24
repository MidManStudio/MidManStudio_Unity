// NetworkedDimensionPlayer.cs
//
// FIXES:
//   + ResolveIs3D() removed — was used to pick which configId slot to read,
//     but caused Is3D=true configs to be unfirable when mode didn't match.
//     Replaced with ResolveConfigId() which picks the slot from dimension/mode,
//     then all routing uses cfg.Is3D from the actual config SO.
//
//   + FireSim() now reads cfg.Is3D (not a computed bool) for:
//       - BuildSpawnPoints direction convention (2D uses transform.right, 3D uses head forward)
//       - SpawnPoint spread axis (Z-rotation for 2D, Y-rotation for 3D)
//     Previously the spread Quaternion was always computed from the same is3D
//     that was also wrong when config.Is3D didn't match the dimension.
//
//   + Added PlayerShootMode.Physics (mode 5) with full implementation:
//       - FirePhysics() server-only path via MID_MasterProjectileSystem.SpawnPhysicsProjectile
//       - _physicsPoolType inspector field selects the PoolableNetworkObjectType
//       - _physicsProjectileSpeed sets launch velocity
//       - SetOwnerContext + InitialiseProjectile called immediately after spawn
//
//   + Added _configId2D_alt / _configId3D_alt — separate slots for when the
//     player wants a different config in 3D dimension vs 2D dimension mode.
//     (Kept original _configId2D/_configId3D for backwards compatibility.)
//
//   + Raycast mode now selects 3D vs 2D raycast API from cfg.Is3D,
//     not from _currentDimension, matching the projectile config's intent.

using UnityEngine;
using Unity.Netcode;
using Cinemachine;
using TMPro;
using MidManStudio.Core.Logging;
using MidManStudio.Netcode.Pools;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Adapters;
using MidManStudio.Projectiles.Config;
using MidManStudio.Core.Pools;

namespace TestGame
{
    public enum PlayerShootMode
    {
        LocalOnly = 0,
        RustSim2D = 1,
        RustSim3D = 2,
        Raycast   = 3,
        Physics   = 4,
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
        [SerializeField, Range(-80f, 0f)]  private float _pitchMin = -80f;
        [SerializeField, Range(0f,  80f)]  private float _pitchMax =  80f;

        [Header("Projectile Config IDs")]
        [Tooltip("Config used when dimension=2D or mode=RustSim2D/LocalOnly.")]
        [SerializeField] private ushort _configId2D = 0;
        [Tooltip("Config used when dimension=3D or mode=RustSim3D.")]
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire Settings")]
        [SerializeField] private float _fireRate = 5f;
        [SerializeField, Range(1, 64)] private int   _pelletsPerShot = 1;
        [Tooltip("Spread when no pattern SO assigned.")]
        [SerializeField, Range(0f, 45f)] private float _spreadDeg = 0f;
        [SerializeField] private KeyCode _fireKey = KeyCode.Mouse0;

        [Header("Shot Pattern (optional)")]
        [Tooltip("Assign a ProjectilePatternSO for spline-based spread.")]
        [SerializeField] private ProjectilePatternSO _shotPattern;

        [Header("Shoot Mode")]
        [SerializeField] private PlayerShootMode _shootMode = PlayerShootMode.LocalOnly;
        [SerializeField] private TMP_Text        _modeText;

        [Header("Dimension Toggle Key")]
        [SerializeField] private KeyCode _dimensionKey = KeyCode.BackQuote;

        [Header("Raycast Settings")]
        [SerializeField] private LayerMask _raycastLayers = -1;
        [SerializeField] private float     _raycastRange  = 200f;

        [Header("Physics Projectile Settings (mode = Physics)")]
        [Tooltip("Network object pool type for the physics projectile prefab.")]
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType
            = PoolableNetworkObjectType.BaseProjectileBlueprint;
        [Tooltip("Launch speed for physics projectiles in world units per second.")]
        [SerializeField] private float _physicsProjectileSpeed = 20f;
        [Tooltip("Damage multiplier forwarded to the PhysicsProjectile component.")]
        [SerializeField] private float _physicsDamageMultiplier = 1f;

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
                    DimensionCameraController.Instance.RegisterPlayerCams(
                        transform, _headPivot);
                else
                    Debug.LogWarning(
                        "[NetworkedDimensionPlayer] DimensionCameraController not found.", this);

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

        #region Update / FixedUpdate

        private void Update()
        {
            if (!IsOwner) return;

            if (Input.GetKeyDown(_dimensionKey)
                && DimensionManager.HasInstance
                && !DimensionManager.Instance.IsTransitioning)
                DimensionManager.Instance.SwitchDimension();

            // Shoot mode hotkeys
            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeMode(PlayerShootMode.LocalOnly);
            if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeMode(PlayerShootMode.RustSim2D);
            if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeMode(PlayerShootMode.RustSim3D);
            if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeMode(PlayerShootMode.Raycast);
            if (Input.GetKeyDown(KeyCode.Alpha5)) ChangeMode(PlayerShootMode.Physics);

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
            MID_Logger.LogInfo(_logLevel,
                $"Shoot mode → {m}", nameof(NetworkedDimensionPlayer));
        }

        private void UpdateModeText()
        {
            if (_modeText == null) return;
            _modeText.text = _shootMode switch
            {
                PlayerShootMode.LocalOnly => "[1] LOCAL",
                PlayerShootMode.RustSim2D => "[2] RUST 2D",
                PlayerShootMode.RustSim3D => "[3] RUST 3D",
                PlayerShootMode.Raycast   => "[4] RAYCAST",
                PlayerShootMode.Physics   => "[5] PHYSICS",
                _                         => _shootMode.ToString()
            };
        }

        #endregion

        #region Mouse Look

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

            switch (_shootMode)
            {
                case PlayerShootMode.Raycast: FireRaycast(); break;
                case PlayerShootMode.Physics: FirePhysics(); break;
                default:                      FireSim();     break;
            }
        }

        #endregion

        #region Config Resolution
        // FIX: config selection is now fully independent of routing.
        // ResolveConfigId picks the slot (2D vs 3D) from the player's current
        // state, then cfg.Is3D drives all subsequent buffer/render routing.
        // This means a config with Is3D=true in the _configId2D slot will
        // still fire correctly into the 3D buffer.

        private ushort ResolveConfigId()
        {
            // RustSim3D mode or 3D dimension → prefer the 3D config slot
            bool prefer3DSlot = _shootMode == PlayerShootMode.RustSim3D
                             || (_shootMode != PlayerShootMode.RustSim2D
                                 && _currentDimension == Dimension.ThreeD);
            return prefer3DSlot ? _configId3D : _configId2D;
        }

        private Vector3 ResolveFireDir(bool cfgIs3D)
        {
            // 3D configs use head pivot forward; 2D configs use transform.right
            if (cfgIs3D)
                return _headPivot != null ? _headPivot.forward : transform.forward;
            return transform.right;
        }

        private Transform ResolveShotPoint(bool cfgIs3D)
            => cfgIs3D ? _shotPoint3D : _shotPoint2D;

        #endregion

        #region Sim Fire (LocalOnly / RustSim2D / RustSim3D)

        private void FireSim()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            ushort cfgId = ResolveConfigId();
            var    cfg   = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"Config {cfgId} not registered.", nameof(NetworkedDimensionPlayer));
                return;
            }

            // FIX: use cfg.Is3D for ALL routing, not a mode-derived bool.
            bool    cfgIs3D = cfg.Is3D;
            Transform sp    = ResolveShotPoint(cfgIs3D);
            Vector3 origin  = sp != null ? sp.position : transform.position;
            Vector3 dir     = ResolveFireDir(cfgIs3D);

            int n = _shotPattern != null
                ? _shotPattern.ProjectileCount
                : Mathf.Max(_pelletsPerShot, 1);

            var pts = BuildSpawnPoints(origin, dir, n, cfg, cfgIs3D);

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

        #endregion

        #region Raycast Fire

        private void FireRaycast()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            ushort cfgId = ResolveConfigId();
            var    cfg   = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null) return;

            // FIX: use cfg.Is3D to pick the correct raycast API, not dimension.
            bool    cfgIs3D = cfg.Is3D;
            Transform sp    = ResolveShotPoint(cfgIs3D);
            Vector3 origin  = sp != null ? sp.position : transform.position;
            Vector3 dir     = ResolveFireDir(cfgIs3D);

            bool    hit   = false;
            Vector3 hitPt = origin + dir * _raycastRange;
            ulong   netId = 0;

            if (cfgIs3D)
            {
                if (Physics.Raycast(origin, dir, out RaycastHit h,
                    _raycastRange, _raycastLayers))
                {
                    hit   = true;
                    hitPt = h.point;
                    var no = h.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) netId = no.NetworkObjectId;
                }
            }
            else
            {
                var h2 = Physics2D.Raycast(
                    origin, dir, _raycastRange, _raycastLayers);
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
                Origin             = origin,
                Direction          = dir,
                HitPoint           = hitPt,
                DidHit             = hit,
                HitTargetNetworkId = netId,
                IsHeadshot         = false
            };

            var ctx = new WeaponFireContext
            {
                FireRate               = _fireRate,
                ProjectileCount        = 1,
                IsNetworked            = MID_MasterProjectileSystem.Instance.IsNetworked
                                         && IsSpawned,
                IsRaycastWeapon        = true,
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f
            };

            MID_MasterProjectileSystem.Instance.RegisterRaycastFire(result, cfgId, ctx);
        }

        #endregion

        #region Physics Fire

        /// <summary>
        /// Spawns a physics-driven networked projectile (rocket, grenade, etc.)
        /// via MID_MasterProjectileSystem.SpawnPhysicsProjectile().
        ///
        /// Server-only: only the server machine executes the actual spawn.
        /// Clients send an RPC (ServerRpc) to request the server to fire.
        /// The spawned NetworkObject replicates to all clients via NetworkTransform.
        ///
        /// Weapon level and damage multiplier are forwarded to PhysicsProjectile
        /// via SetOwnerContext + InitialiseProjectile.
        /// </summary>
        private void FirePhysics()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            // Determine fire direction from current dimension
            bool    is3DSpace = _currentDimension == Dimension.ThreeD;
            Vector3 origin    = _shotPoint3D != null
                ? _shotPoint3D.position : transform.position;
            Vector3 dir = is3DSpace
                ? (_headPivot != null ? _headPivot.forward : transform.forward)
                : transform.right;

            if (MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned)
            {
                // Client: ask server to do the spawn
                FirePhysicsServerRpc(origin, dir);
            }
            else
            {
                // Offline: spawn directly (no network)
                SpawnPhysicsProjectileLocal(origin, dir);
            }
        }

        [ServerRpc]
        private void FirePhysicsServerRpc(Vector3 origin, Vector3 direction)
        {
            SpawnPhysicsProjectileLocal(origin, direction);
        }

        private void SpawnPhysicsProjectileLocal(Vector3 origin, Vector3 direction)
        {
            if (!MID_MasterProjectileSystem.Instance.IsServer
                && MID_MasterProjectileSystem.Instance.IsNetworked)
                return; // Non-server clients must not spawn network objects

            // Rotate the spawn point so the projectile's forward = fire direction
            Quaternion spawnRot = direction.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(direction.normalized)
                : Quaternion.identity;

            var netObj = MID_MasterProjectileSystem.Instance
                .SpawnPhysicsProjectile(_physicsPoolType, origin, spawnRot);

            if (netObj == null) return;

            // Wire owner context and initialise (sets Rigidbody velocity)
            var proj = netObj.GetComponent<PhysicsProjectile>();
            if (proj != null)
            {
                proj.SetOwnerContext(
                    ownerMidId:             OwnerClientId,
                    firedByNetworkObjectId: NetworkObjectId,
                    isBotOwner:             false,
                    weaponLevel:            1,
                    damageMultiplier:       _physicsDamageMultiplier);

                proj.InitialiseProjectile(
                    ownerMidId:             OwnerClientId,
                    firedByNetworkObjectId: NetworkObjectId,
                    bulletVelocity:         _physicsProjectileSpeed,
                    isBotOwned:             false,
                    weaponLevel:            1);
            }

            MID_Logger.LogInfo(_logLevel,
                $"Physics projectile spawned: type={_physicsPoolType} " +
                $"origin={origin} dir={direction}",
                nameof(NetworkedDimensionPlayer));
        }

        #endregion

        #region Spawn Point Builders

        private SpawnPoint[] BuildSpawnPoints(
            Vector3 origin, Vector3 dir, int n,
            ProjectileConfigSO cfg, bool cfgIs3D)
        {
            return _shotPattern != null
                ? BuildSpawnPointsFromPattern(origin, dir, cfg, cfgIs3D)
                : BuildSpawnPointsSpread(origin, dir, n, cfg, cfgIs3D);
        }

        private SpawnPoint[] BuildSpawnPointsSpread(
            Vector3 origin, Vector3 dir, int n,
            ProjectileConfigSO cfg, bool cfgIs3D)
        {
            var pts = new SpawnPoint[n];
            for (int i = 0; i < n; i++)
            {
                float frac = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);

                // FIX: use cfg.Is3D to pick correct spread rotation axis.
                // 2D: spread around Z (in-plane). 3D: spread around Y (yaw).
                Vector3 sDir = cfgIs3D
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
            Vector3 origin, Vector3 baseDir,
            ProjectileConfigSO cfg, bool cfgIs3D)
        {
            var angleDirs = _shotPattern.SampleDirections();
            var pts       = new SpawnPoint[angleDirs.Length];

            for (int i = 0; i < angleDirs.Length; i++)
            {
                var angles = angleDirs[i];

                Quaternion rot = cfgIs3D
                    ? Quaternion.Euler(-angles.y, angles.x, 0f)
                    : Quaternion.Euler(0f, 0f, angles.x);

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

            return pts;
        }

        #endregion

        #region Helpers

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
            { Cursor.lockState = CursorLockMode.None;   Cursor.visible = true; }
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
