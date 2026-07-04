// Minimal example wiring: moves a Transform with the joystick and fires through
// ProjectileManager with the shoot button. No player controller existed in this
// repo yet — Stickman_Template in SampleScene is a static test rig for the
// weapon, not a moving player — so this is a starting point, not a finished
// character controller. Swap MoveTarget for your real player root once you have one.

using UnityEngine;
using MidManStudio.Projectiles;

namespace MidManStudio.Projectiles.MobileControls
{
    public class MID_MobileTestPlayerController : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private MID_TouchJoystick _moveJoystick;
        [SerializeField] private MID_TouchShootButton _shootButton;
        [SerializeField] private Transform _moveTarget; // defaults to this.transform if left empty

        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 5f;

        [Header("Shooting")]
        [Tooltip("Must match a ConfigId already registered in ProjectileRegistry.")]
        [SerializeField] private ushort _projectileConfigId = 0;
        [SerializeField] private float _projectileSpeed = 12f;
        [SerializeField] private float _fireCooldown = 0.2f;
        [Tooltip("Facing angle in degrees used when the joystick isn't held (0 = along local +X).")]
        [SerializeField] private float _defaultFacingDeg = 0f;

        private float _fireTimer;
        private float _facingDeg;

        private void Awake()
        {
            if (_moveTarget == null) _moveTarget = transform;
            _facingDeg = _defaultFacingDeg;
        }

        private void OnEnable()
        {
            if (_shootButton != null) _shootButton.Pressed += TryFireOnce;
        }

        private void OnDisable()
        {
            if (_shootButton != null) _shootButton.Pressed -= TryFireOnce;
        }

        private void Update()
        {
            _fireTimer -= Time.deltaTime;

            if (_moveJoystick == null) return;
            Vector2 move = _moveJoystick.Value;
            if (move.sqrMagnitude <= 0f) return;

            _moveTarget.position += (Vector3)(move * _moveSpeed * Time.deltaTime);
            _facingDeg = Mathf.Atan2(move.y, move.x) * Mathf.Rad2Deg;
        }

        private void TryFireOnce()
        {
            if (_fireTimer > 0f) return;
            if (ProjectileManager.Instance == null) return;

            _fireTimer = _fireCooldown;

            ProjectileManager.Instance.Spawn(
                configId: _projectileConfigId,
                origin:   _moveTarget.position,
                angleDeg: _facingDeg,
                speed:    _projectileSpeed,
                latency:  0f,
                ownerId:  0,
                seed:     (uint)Time.frameCount);
        }
    }
}
