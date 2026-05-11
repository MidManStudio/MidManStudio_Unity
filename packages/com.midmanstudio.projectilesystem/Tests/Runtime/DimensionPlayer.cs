// DimensionPlayer.cs
// Single player controller that adapts to 2D and 3D dimension modes.
// 2D: movement on XY plane, locked Z, orthographic camera follows directly.
// 3D: movement on XZ plane, full gravity, camera follows from behind/above.

using UnityEngine;
using MidManStudio.Core.Logging;

namespace TestGame
{
    [RequireComponent(typeof(Rigidbody))]
    public class DimensionPlayer : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed2D = 6f;
        [SerializeField] private float _moveSpeed3D = 5f;
        [SerializeField] private float _jumpForce   = 7f;

        [Header("Weapon")]
        [SerializeField] private Transform _muzzle;
        [SerializeField] private ushort    _configId2D;   // register in ProjectileRegistry
        [SerializeField] private ushort    _configId3D;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        private Rigidbody  _rb;
        private Dimension  _currentDimension = Dimension.TwoD;
        private bool       _grounded;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ApplyRigidbodyConstraints(Dimension.TwoD);
        }

        private void Update()
        {
            HandleMovement();

            if (Input.GetMouseButtonDown(0))
                FireProjectile();
        }

        // ── Movement ──────────────────────────────────────────────────────────

        private void HandleMovement()
        {
            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");

            if (_currentDimension == Dimension.TwoD)
            {
                // XY plane — side-scrolling or top-down
                var dir = new Vector3(h, v, 0f).normalized;
                _rb.velocity = new Vector3(
                    dir.x * _moveSpeed2D,
                    dir.y * _moveSpeed2D,
                    0f);
            }
            else
            {
                // XZ plane — 3D top-down/third-person
                var dir = new Vector3(h, 0f, v).normalized;
                _rb.velocity = new Vector3(
                    dir.x * _moveSpeed3D,
                    _rb.velocity.y,   // preserve gravity Y
                    dir.z * _moveSpeed3D);

                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(dir);

                // Simple jump
                if (_grounded && Input.GetKeyDown(KeyCode.Space))
                    _rb.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
            }
        }

        // ── Fire ──────────────────────────────────────────────────────────────

        private void FireProjectile()
        {
            if (!MidManStudio.Projectiles.Managers.MID_MasterProjectileSystem.HasInstance) return;
            if (_muzzle == null) return;

            bool is3D = _currentDimension == Dimension.ThreeD;
            ushort cfgId = is3D ? _configId3D : _configId2D;

            Vector3 dir = is3D
                ? _muzzle.forward
                : (_muzzle.right);   // 2D: fire along right (side-scroll)

            var cfg = MidManStudio.Projectiles.Config.ProjectileRegistry.Instance.Get(cfgId);
            if (cfg == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"DimensionPlayer: configId {cfgId} not registered.",
                    nameof(DimensionPlayer));
                return;
            }

            var spawnPts = new MidManStudio.Projectiles.Adapters.SpawnPoint[]
            {
                new()
                {
                    Origin    = _muzzle.position,
                    Direction = dir.normalized,
                    Speed     = cfg.ResolveSpeed()
                }
            };

            var ctx = new MidManStudio.Projectiles.Adapters.WeaponFireContext
            {
                ProjectileCount  = 1,
                IsNetworked      = false,
                OwnerMidId       = 0,
                DamageMultiplier = 1f
            };

            MidManStudio.Projectiles.Managers.MID_MasterProjectileSystem.Instance
                .Fire(cfgId, spawnPts, 1, ctx);
        }

        // ── Dimension Switch ──────────────────────────────────────────────────

        /// <summary>Called by DimensionManager after a switch completes.</summary>
        public void OnDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyRigidbodyConstraints(dim);

            MID_Logger.LogInfo(_logLevel,
                $"Player adapted to {dim} mode.",
                nameof(DimensionPlayer));
        }

        private void ApplyRigidbodyConstraints(Dimension dim)
        {
            if (dim == Dimension.TwoD)
            {
                // Lock Z position and all rotation for flat 2D movement
                _rb.constraints = RigidbodyConstraints.FreezePositionZ
                                | RigidbodyConstraints.FreezeRotation;
                _rb.useGravity = false;
            }
            else
            {
                // 3D: lock only rotation (allow gravity)
                _rb.constraints = RigidbodyConstraints.FreezeRotationX
                                | RigidbodyConstraints.FreezeRotationZ;
                _rb.useGravity = true;

                // Reset any leftover Z velocity
                _rb.velocity = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
                transform.position = new Vector3(
                    transform.position.x, transform.position.y, 0f);
            }
        }

        // ── Ground check ──────────────────────────────────────────────────────

        private void OnCollisionStay(Collision c)  => _grounded = true;
        private void OnCollisionExit(Collision c)  => _grounded = false;
    }
}
