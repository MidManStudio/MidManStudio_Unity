// NetworkedDimensionPlayer.cs
//
// FIXES:
//   + Pattern 3D: BuildSpawnPointsFromPattern now uses local coordinate frame
//     (right = cross(dir, up), localUp = cross(right, dir)) so horizontal/vertical
//     spread is always relative to the fire direction, not world axes.
//     Previously Quaternion.Euler(-y, x, 0) * baseDir rotated in world space
//     causing all ring/fan bullets to overlap when camera was pitched.
//   + Fire() in networked mode now packs ExtraDirections into ProjectileFireRequest
//     so the server uses per-projectile directions for patterns.
//   + Dash: LeftShift gives a short speed burst in movement direction.
//   + Mouse look runs whenever Use3DFireConvention() regardless of dimension.
//   + Audio/FX via GlobalFXManager + MID_NativeAudioBridge.

using UnityEngine;
using Unity.Netcode;
using TMPro;
using MidManStudio.Core.Audio;
using MidManStudio.Core.Effects;
using MidManStudio.Core.Logging;
using MidManStudio.Netcode.Pools;
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

        [Header("Dash")]
        [SerializeField] private KeyCode _dashKey        = KeyCode.LeftShift;
        [SerializeField] private float   _dashSpeed      = 16f;
        [SerializeField] private float   _dashDuration   = 0.12f;
        [SerializeField] private float   _dashCooldown   = 0.9f;

        [Header("3D Mouse Look")]
        [SerializeField] private float _mouseSensitivity = 2f;
        [SerializeField, Range(-80f, 0f)]  private float _pitchMin = -80f;
        [SerializeField, Range(0f,  80f)]  private float _pitchMax =  80f;

        [Header("Projectile Config IDs")]
        [SerializeField] private ushort _configId2D = 0;
        [SerializeField] private ushort _configId3D = 0;

        [Header("Fire Settings")]
        [SerializeField] private float _fireRate = 5f;
        [SerializeField, Range(1, 64)] private int   _pelletsPerShot = 1;
        [SerializeField, Range(0f, 45f)] private float _spreadDeg    = 0f;
        [SerializeField] private KeyCode _fireKey = KeyCode.Mouse0;

        [Header("Shot Pattern (optional)")]
        [SerializeField] private ProjectilePatternSO _shotPattern;

        [Header("Shoot Mode")]
        [SerializeField] private PlayerShootMode _shootMode = PlayerShootMode.LocalOnly;
        [SerializeField] private TMP_Text        _modeText;

        [Header("Dimension Toggle Key")]
        [SerializeField] private KeyCode _dimensionKey = KeyCode.BackQuote;

        [Header("Raycast Settings")]
        [SerializeField] private LayerMask _raycastLayers = -1;
        [SerializeField] private float     _raycastRange  = 200f;

        [Header("Physics Projectile Settings")]
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType
            = PoolableNetworkObjectType.BaseProjectileBlueprint;
        [SerializeField] private float _physicsProjectileSpeed  = 20f;
        [SerializeField] private float _physicsDamageMultiplier = 1f;

        [Header("Audio — NativeAudioBridge clip index")]
        [SerializeField] private int   _fireSoundClipIndex   = 0;
        [SerializeField, Range(0f,1f)] private float _fireSoundVolume = 0.7f;
        [SerializeField] private AudioSource _fallbackAudioSource;
        [SerializeField] private AudioClip   _fallbackFireClip;
        [SerializeField, Range(0f,1f)] private float _fallbackVolume = 0.6f;
        [SerializeField, Range(0.01f,0.3f)] private float _fallbackPitchVariance = 0.1f;

        [Header("Muzzle Flash — GlobalFX")]
        [SerializeField] private int   _muzzleFlashParticleCount = 4;
        [SerializeField, Range(0f,1f)] private float _muzzleFlashVolume = 0.8f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Networked State

        private readonly NetworkVariable<float> _netPitch = new NetworkVariable<float>(
            0f,
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

        // Dash state
        private float _nextDashTime;
        private bool  _isDashing;
        private float _dashEndTime;
        private Vector3 _dashDir;

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
            else
            {
                if (_rb != null)
                {
                    _rb.isKinematic   = true;
                    _rb.interpolation = RigidbodyInterpolation.Interpolate;
                }
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
            if (!IsOwner)
            {
                if (_headPivot != null)
                    _headPivot.localRotation = Quaternion.Euler(_netPitch.Value, 0f, 0f);
                return;
            }

            // Dimension toggle
            if (Input.GetKeyDown(_dimensionKey)
                && DimensionManager.HasInstance
                && !DimensionManager.Instance.IsTransitioning)
                DimensionManager.Instance.SwitchDimension();

            // Mode hotkeys
            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeMode(PlayerShootMode.LocalOnly);
            if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeMode(PlayerShootMode.RustSim2D);
            if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeMode(PlayerShootMode.RustSim3D);
            if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeMode(PlayerShootMode.Raycast);
            if (Input.GetKeyDown(KeyCode.Alpha5)) ChangeMode(PlayerShootMode.Physics);

            // Mouse look: active in 3D dimension OR when using 3D fire convention
            if (_currentDimension == Dimension.ThreeD || Use3DFireConvention())
                HandleMouseLook();

            // Dash trigger
            if (Input.GetKeyDown(_dashKey) && Time.time >= _nextDashTime && !_isDashing)
                StartDash();

            HandleFire();
        }

        private void FixedUpdate()
        {
            if (!IsOwner) return;
            HandleMovement();
        }

        #endregion

        #region Dash

        private void StartDash()
        {
            // Compute dash direction from current input (or forward if no input)
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if (Use3DFireConvention() || _currentDimension == Dimension.ThreeD)
            {
                Vector3 inputDir = (transform.right * h + transform.forward * v);
                _dashDir = inputDir.sqrMagnitude > 0.01f
                    ? inputDir.normalized
                    : (_headPivot != null ? _headPivot.forward : transform.forward);
            }
            else
            {
                Vector3 inputDir = new Vector3(h, v, 0f);
                _dashDir = inputDir.sqrMagnitude > 0.01f
                    ? inputDir.normalized
                    : transform.right;
            }

            _isDashing    = true;
            _dashEndTime  = Time.time + _dashDuration;
            _nextDashTime = Time.time + _dashCooldown;
        }

        #endregion

        #region Shoot Mode

        private void ChangeMode(PlayerShootMode m)
        {
            _shootMode = m;
            UpdateModeText();
            if (Use3DFireConvention())
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
            }
            else if (_currentDimension == Dimension.TwoD)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }
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
            {
                _headPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
                _netPitch.Value = _pitch;
            }
        }

        #endregion

        #region Movement

        private void HandleMovement()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if (_isDashing)
            {
                if (Time.time >= _dashEndTime)
                    _isDashing = false;
                else
                {
                    _rb.velocity = _dashDir * _dashSpeed;
                    return;
                }
            }

            bool use3D = _currentDimension == Dimension.ThreeD || Use3DFireConvention();
            if (!use3D)
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

            PlayFireFX();
        }

        private void PlayFireFX()
        {
            Transform sp = ResolveShotPoint(Use3DFireConvention());
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = ResolveFireDir();

            if (MID_NativeAudioBridge.HasInstance)
                MID_NativeAudioBridge.Instance.PlayClip(_fireSoundClipIndex, _fireSoundVolume);
            else if (_fallbackAudioSource != null && _fallbackFireClip != null)
            {
                _fallbackAudioSource.pitch = 1f + Random.Range(-_fallbackPitchVariance, _fallbackPitchVariance);
                _fallbackAudioSource.PlayOneShot(_fallbackFireClip, _fallbackVolume);
            }

            GlobalFXManager.Instance?.TriggerMuzzleFlash(
                origin, dir, _muzzleFlashParticleCount, _muzzleFlashVolume);
        }

        #endregion

        #region Config + Direction Resolution

        private ushort ResolveConfigId()
        {
            bool prefer3D = _shootMode == PlayerShootMode.RustSim3D
                         || (_shootMode != PlayerShootMode.RustSim2D
                             && _currentDimension == Dimension.ThreeD);
            return prefer3D ? _configId3D : _configId2D;
        }

        private bool Use3DFireConvention()
            => _currentDimension == Dimension.ThreeD
            || _shootMode == PlayerShootMode.RustSim3D;

        private Vector3 ResolveFireDir()
            => (Use3DFireConvention() && _headPivot != null)
                ? _headPivot.forward
                : transform.right;

        private Transform ResolveShotPoint(bool use3D)
            => use3D ? _shotPoint3D : _shotPoint2D;

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
                    $"Config {cfgId} not registered.", nameof(NetworkedDimensionPlayer));
                return;
            }

            bool    use3D  = Use3DFireConvention() || cfg.Is3D;
            Transform sp   = ResolveShotPoint(use3D);
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = ResolveFireDir();

            int n = _shotPattern != null
                ? _shotPattern.ProjectileCount
                : Mathf.Max(_pelletsPerShot, 1);

            SpawnPoint[] pts = BuildSpawnPoints(origin, dir, n, cfg);

            bool networked = _shootMode != PlayerShootMode.LocalOnly
                          && MID_MasterProjectileSystem.Instance.IsNetworked
                          && IsSpawned;

            if (!networked)
            {
                if (LocalProjectileManager.HasInstance)
                {
                    if (use3D)
                        LocalProjectileManager.Instance.Spawn3D(pts, pts.Length, cfgId, (uint)OwnerClientId, 1f);
                    else
                        LocalProjectileManager.Instance.Spawn2D(pts, pts.Length, cfgId, (uint)OwnerClientId, 1f);
                }
                return;
            }

            // Networked: pack extra directions for pattern support
            int     extraCount = Mathf.Min(pts.Length - 1, 63);
            Vector3[] extraDirs = null;
            if (extraCount > 0)
            {
                extraDirs = new Vector3[extraCount];
                for (int i = 0; i < extraCount; i++)
                    extraDirs[i] = pts[i + 1].Direction;
            }

            var request = new ProjectileFireRequest
            {
                ConfigId               = cfgId,
                Origin                 = origin,
                Direction              = pts[0].Direction,
                Speed                  = pts[0].Speed,
                RngSeed                = (uint)UnityEngine.Random.Range(0, int.MaxValue),
                ProjectileCount        = (byte)Mathf.Min(pts.Length, 255),
                OwnerMidId             = OwnerClientId,
                FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner             = false,
                WeaponLevel            = 1,
                DamageMultiplier       = 1f,
                ClientFireTick         = MID_MasterProjectileSystem.Instance.GetBridgeTick(),
                ExtraDirectionCount    = (byte)extraCount,
                ExtraDirections        = extraDirs
            };

            // Send directly via bridge (bypasses MID_MasterProjectileSystem.Fire which rebuilds pts)
            var bridge = MID_MasterProjectileSystem.Instance.GetBridge();
            bridge?.FireServerRpc(request);
        }

        #endregion

        #region Raycast Fire

        private void FireRaycast()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            ushort cfgId = ResolveConfigId();
            var    cfg   = ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null) return;

            Vector3 origin = (_shotPoint3D != null ? _shotPoint3D : transform).position;
            Vector3 dir    = ResolveFireDir();

            bool    hit   = false;
            Vector3 hitPt = origin + dir * _raycastRange;
            ulong   netId = 0;

            bool use3D = Use3DFireConvention() || cfg.Is3D;
            if (use3D)
            {
                if (Physics.Raycast(origin, dir, out RaycastHit h, _raycastRange, _raycastLayers))
                {
                    hit = true; hitPt = h.point;
                    var no = h.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) netId = no.NetworkObjectId;
                }
            }
            else
            {
                var h2 = Physics2D.Raycast(origin, dir, _raycastRange, _raycastLayers);
                if (h2.collider != null)
                {
                    hit = true; hitPt = h2.point;
                    var no = h2.collider.GetComponentInParent<NetworkObject>();
                    if (no != null) netId = no.NetworkObjectId;
                }
            }

            var result = new RaycastFireResult
            {
                Origin = origin, Direction = dir, HitPoint = hitPt,
                DidHit = hit, HitTargetNetworkId = netId, IsHeadshot = false
            };

            var ctx = new WeaponFireContext
            {
                FireRate = _fireRate, ProjectileCount = 1,
                IsNetworked = MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned,
                IsRaycastWeapon = true,
                OwnerMidId = OwnerClientId, FiredByNetworkObjectId = NetworkObjectId,
                IsBotOwner = false, WeaponLevel = 1, DamageMultiplier = 1f
            };

            MID_MasterProjectileSystem.Instance.RegisterRaycastFire(result, cfgId, ctx);
        }

        #endregion

        #region Physics Fire

        private void FirePhysics()
        {
            if (!MID_MasterProjectileSystem.HasInstance) return;

            Vector3 origin = (_shotPoint3D != null ? _shotPoint3D : transform).position;
            Vector3 dir    = ResolveFireDir();

            if (MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned)
                FirePhysicsServerRpc(origin, dir);
            else
                SpawnPhysicsProjectileLocal(origin, dir);
        }

        [ServerRpc]
        private void FirePhysicsServerRpc(Vector3 origin, Vector3 direction)
            => SpawnPhysicsProjectileLocal(origin, direction);

        private void SpawnPhysicsProjectileLocal(Vector3 origin, Vector3 direction)
        {
            if (!MID_MasterProjectileSystem.Instance.IsServer
                && MID_MasterProjectileSystem.Instance.IsNetworked) return;

            Quaternion rot = direction.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(direction.normalized) : Quaternion.identity;

            var netObj = MID_MasterProjectileSystem.Instance
                .SpawnPhysicsProjectile(_physicsPoolType, origin, rot);

            if (netObj == null) return;

            var proj = netObj.GetComponent<PhysicsProjectile>();
            if (proj != null)
            {
                proj.SetOwnerContext(OwnerClientId, NetworkObjectId, false, 1, _physicsDamageMultiplier);
                proj.InitialiseProjectile(OwnerClientId, NetworkObjectId,
                    _physicsProjectileSpeed, false, 1);
            }
        }

        #endregion

        #region Spawn Point Builders

        private SpawnPoint[] BuildSpawnPoints(Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
            => _shotPattern != null
                ? BuildSpawnPointsFromPattern(origin, dir, cfg)
                : BuildSpawnPointsSpread(origin, dir, n, cfg);

        private SpawnPoint[] BuildSpawnPointsSpread(
            Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
        {
            bool   use3D = Use3DFireConvention() || cfg.Is3D;
            var    pts   = new SpawnPoint[n];

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
            bool use3D     = Use3DFireConvention() || cfg.Is3D;
            var  angleDirs = _shotPattern.SampleDirections();
            var  pts       = new SpawnPoint[angleDirs.Length];

            // FIX: build a local coordinate frame from baseDir so that
            // horizontal spread rotates around the "local up" of the fire direction,
            // and vertical spread rotates around the "local right".
            // Previously Quaternion.Euler(-y, x, 0) * baseDir rotated in world space,
            // which caused all bullets to cluster when the camera was pitched.
            Vector3 localRight, localUp;
            if (use3D)
            {
                // For 3D: build frame from fire direction
                Vector3 worldUp  = Mathf.Abs(Vector3.Dot(baseDir.normalized, Vector3.up)) > 0.98f
                    ? Vector3.forward : Vector3.up;
                localRight = Vector3.Cross(baseDir, worldUp).normalized;
                localUp    = Vector3.Cross(localRight, baseDir).normalized;
            }
            else
            {
                // For 2D: right is world-right-ish perp to baseDir in XY plane
                localRight = Vector3.Cross(baseDir, Vector3.forward).normalized;
                localUp    = Vector3.forward; // Z-axis for 2D rotation
            }

            for (int i = 0; i < angleDirs.Length; i++)
            {
                var    angles     = angleDirs[i];
                Vector3 sDir;

                if (use3D)
                {
                    // Rotate baseDir: yaw around localUp, pitch around localRight
                    Quaternion yawRot   = Quaternion.AngleAxis( angles.x, localUp);
                    Quaternion pitchRot = Quaternion.AngleAxis(-angles.y, localRight);
                    sDir = pitchRot * yawRot * baseDir;
                }
                else
                {
                    // 2D: rotate around Z in screen space
                    sDir = Quaternion.Euler(0f, 0f, angles.x) * baseDir;
                }

                float speedMult = _shotPattern.GetSpeedMultiplier(i, _shotPattern.RngSeed);
                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = sDir.normalized,
                    Speed     = cfg.ResolveSpeed() * speedMult
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
            if (_rb == null || !IsOwner) return;
            bool use3D = dim == Dimension.ThreeD || Use3DFireConvention();
            if (!use3D)
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
