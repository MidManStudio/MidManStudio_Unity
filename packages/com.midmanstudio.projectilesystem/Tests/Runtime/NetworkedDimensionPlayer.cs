// NetworkedDimensionPlayer.cs
//
// FIXES / ADDITIONS:
//   + Fire direction now driven by _currentDimension + _shootMode, NOT by cfg.Is3D.
//     Previously if _configId3D pointed to a config with Is3D=false the head pivot
//     was ignored and all 3D shots fired along transform.right (2D axis).
//   + Spread rotation axis now also driven by dimension/mode.
//   + NetworkVariable<float> _netPitch syncs head-pivot pitch to non-owner clients.
//   + Non-owner OnNetworkSpawn: Rigidbody set to kinematic so local physics
//     does not fight NetworkTransform position updates.
//   + AudioSource + AudioClip for fire sound; plays every shot.
//   + Physics (mode 4) ServerRpc → SpawnPhysicsProjectileLocal unchanged.
//
// PREFAB SETUP (required):
//   • Add NetworkTransform component (set to Owner Authoritative or Server Auth as needed)
//   • Add AudioSource component and assign to _audioSource inspector field
//   • Ensure _headPivot child exists — EnsureHeadPivot() auto-creates it if null

using UnityEngine;
using Unity.Netcode;
using Cinemachine;
using TMPro;
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
        [Tooltip("Spread angle (degrees) when no pattern SO assigned.")]
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
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType
            = PoolableNetworkObjectType.BaseProjectileBlueprint;
        [SerializeField] private float _physicsProjectileSpeed = 20f;
        [SerializeField] private float _physicsDamageMultiplier = 1f;

        [Header("Audio")]
        [Tooltip("AudioSource on this GameObject (add it to the prefab).")]
        [SerializeField] private AudioSource _audioSource;
        [Tooltip("Clip played on every shot fired by the local owner.")]
        [SerializeField] private AudioClip   _fireSoundClip;
        [SerializeField, Range(0f, 1f)]       private float _fireSoundVolume       = 0.6f;
        [SerializeField, Range(0.01f, 0.3f)]  private float _fireSoundPitchVariance = 0.1f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Networked State

        // Head-pivot pitch synced so non-owner clients show correct look direction.
        // WritePermission = Owner so only the owner can write.
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
                // Local owner: register camera, subscribe events, etc.
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
            else
            {
                // Non-owner: let NetworkTransform drive position, physics must not fight it.
                if (_rb != null)
                {
                    _rb.isKinematic  = true;
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
            // Non-owner: sync head pivot pitch from the network variable
            if (!IsOwner)
            {
                if (_headPivot != null)
                    _headPivot.localRotation = Quaternion.Euler(_netPitch.Value, 0f, 0f);
                return;
            }

            // Owner-only input
            if (Input.GetKeyDown(_dimensionKey)
                && DimensionManager.HasInstance
                && !DimensionManager.Instance.IsTransitioning)
                DimensionManager.Instance.SwitchDimension();

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
            {
                _headPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
                // Sync pitch to all other clients
                _netPitch.Value = _pitch;
            }
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

            PlayFireSound();
        }

        private void PlayFireSound()
        {
            if (_audioSource == null || _fireSoundClip == null) return;
            _audioSource.pitch  = 1f + Random.Range(-_fireSoundPitchVariance, _fireSoundPitchVariance);
            _audioSource.PlayOneShot(_fireSoundClip, _fireSoundVolume);
        }

        #endregion

        #region Config + Direction Resolution
        // FIX: fire direction now uses _currentDimension and _shootMode.
        // Previously relied solely on cfg.Is3D — if the user assigned a 2D config
        // in _configId3D the head pivot was ignored and all shots fired along
        // transform.right regardless of where the player was looking.

        private ushort ResolveConfigId()
        {
            bool prefer3DSlot = _shootMode == PlayerShootMode.RustSim3D
                             || (_shootMode != PlayerShootMode.RustSim2D
                                 && _currentDimension == Dimension.ThreeD);
            return prefer3DSlot ? _configId3D : _configId2D;
        }

        /// <summary>
        /// Returns true when the current mode/dimension should use 3D firing conventions
        /// (head-pivot forward, Y-axis spread rotation).
        /// This is intentionally DECOUPLED from cfg.Is3D — a 2D config can still be
        /// fired in "3D space" direction if the player is in 3D dimension.
        /// </summary>
        private bool Use3DFireConvention()
            => _currentDimension == Dimension.ThreeD
            || _shootMode == PlayerShootMode.RustSim3D;

        private Vector3 ResolveFireDir()
        {
            if (Use3DFireConvention() && _headPivot != null)
                return _headPivot.forward;
            return transform.right;
        }

        private Transform ResolveShotPoint(bool cfgIs3D)
            => (cfgIs3D || Use3DFireConvention()) ? _shotPoint3D : _shotPoint2D;

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

            Transform sp   = ResolveShotPoint(cfg.Is3D);
            Vector3 origin = sp != null ? sp.position : transform.position;
            Vector3 dir    = ResolveFireDir();

            int n = _shotPattern != null
                ? _shotPattern.ProjectileCount
                : Mathf.Max(_pelletsPerShot, 1);

            var pts = BuildSpawnPoints(origin, dir, n, cfg);

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

            Vector3 origin = (_shotPoint3D != null ? _shotPoint3D : transform).position;
            Vector3 dir    = ResolveFireDir();

            bool    hit   = false;
            Vector3 hitPt = origin + dir * _raycastRange;
            ulong   netId = 0;

            bool use3D = Use3DFireConvention() || cfg.Is3D;
            if (use3D)
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
                IsNetworked            = MID_MasterProjectileSystem.Instance.IsNetworked && IsSpawned,
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
        {
            SpawnPhysicsProjectileLocal(origin, direction);
        }

        private void SpawnPhysicsProjectileLocal(Vector3 origin, Vector3 direction)
        {
            if (!MID_MasterProjectileSystem.Instance.IsServer
                && MID_MasterProjectileSystem.Instance.IsNetworked)
                return;

            Quaternion spawnRot = direction.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(direction.normalized)
                : Quaternion.identity;

            var netObj = MID_MasterProjectileSystem.Instance
                .SpawnPhysicsProjectile(_physicsPoolType, origin, spawnRot);

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
        // FIX: spread rotation axis driven by Use3DFireConvention(), not cfg.Is3D.

        private SpawnPoint[] BuildSpawnPoints(Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
        {
            return _shotPattern != null
                ? BuildSpawnPointsFromPattern(origin, dir, cfg)
                : BuildSpawnPointsSpread(origin, dir, n, cfg);
        }

        private SpawnPoint[] BuildSpawnPointsSpread(
            Vector3 origin, Vector3 dir, int n, ProjectileConfigSO cfg)
        {
            bool use3D = Use3DFireConvention() || cfg.Is3D;
            var pts    = new SpawnPoint[n];

            for (int i = 0; i < n; i++)
            {
                float frac = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);

                // 2D: rotate around Z (in-plane).  3D: rotate around Y (yaw spread).
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

            for (int i = 0; i < angleDirs.Length; i++)
            {
                var angles = angleDirs[i];

                Quaternion rot = use3D
                    ? Quaternion.Euler(-angles.y, angles.x, 0f)
                    : Quaternion.Euler(0f, 0f, angles.x);

                Vector3 sDir      = rot * baseDir;
                float   speedMult = _shotPattern.GetSpeedMultiplier(i, _shotPattern.RngSeed);
                float   speed     = cfg.ResolveSpeed() * speedMult;

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
            if (_rb == null || !IsOwner) return;
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
                var p          = transform.position;
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
                // Parent to head pivot so it rotates with mouse look
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
