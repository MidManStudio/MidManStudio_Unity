// packages/com.midmanstudio.projectilesystem/Tests/Runtime/DimensionCameraController.cs
//
// CHANGES vs original:
//   + RegisterPlayerCams takes followTarget3D (headPivot) as 4th param.
//   + ConfigureVcam2D: adds CinemachineFramingTransposer for 2D platformer feel.
//       Player sits at _screenY2D (0.35 default) — slightly below centre so you
//       see more of what's ahead. Lookahead anticipates movement direction.
//   + ConfigureVcam3D: FPS (Call of Duty style).
//       Body  = CinemachineHardLockToTarget — camera position IS headPivot position.
//       Aim   = CinemachineHardLookAt at a child "_fpsCamLookTarget" parented to
//               headPivot, positioned 20u forward in local space. As mouse-look
//               rotates headPivot, the look target moves with it — camera always
//               faces where the player looks. No CinemachinePOV needed; no fight
//               with the player script's custom mouse-look code.
//   + UnregisterPlayerCams destroys _fpsCamLookTarget to prevent orphan objects.
//   + ApplyProjectionImmediate uses the actual current dimension (was hardcoded TwoD).
//   + _blendStyle exposed in inspector.

using UnityEngine;
using Cinemachine;
using MidManStudio.Core.Logging;

namespace TestGame
{
    [RequireComponent(typeof(Camera))]
    public class DimensionCameraController : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        public static DimensionCameraController Instance { get; private set; }

        // ─────────────────────────────────────────────────────────────────────
        //  Inspector
        // ─────────────────────────────────────────────────────────────────────

        #region Inspector — Camera References

        [Header("Camera References (auto-found if null)")]
        [SerializeField] private Camera           _mainCamera;
        [SerializeField] private CinemachineBrain _brain;

        #endregion

        #region Inspector — 2D Settings

        [Header("2D Camera  (CinemachineFramingTransposer — Platformer)")]
        [Tooltip("Orthographic size in 2D mode.")]
        [SerializeField] private float _orthoSize       = 8f;
        [Tooltip("Speed at which orthoSize lerps to target.")]
        [SerializeField] private float _orthoLerpSpeed  = 6f;
        [Tooltip("Cinemachine blend duration when entering 2D.")]
        [SerializeField] private float _blendDuration2D = 0.45f;
        [Tooltip("Player's normalized screen Y position. 0.35 = slightly below centre (platformer feel).")]
        [SerializeField, Range(0f, 1f)] private float _screenY2D  = 0.35f;
        [Tooltip("XY damping of the FramingTransposer.")]
        [SerializeField] private float _damping2D       = 0.5f;
        [Tooltip("Lookahead seconds — camera anticipates movement direction.")]
        [SerializeField] private float _lookahead2D     = 0.15f;

        #endregion

        #region Inspector — 3D FPS Settings

        [Header("3D Camera  (HardLockToTarget + HardLookAt — FPS)")]
        [Tooltip("Cinemachine blend duration when entering 3D.")]
        [SerializeField] private float _blendDuration3D = 0.45f;
        [Tooltip("Camera FOV in 3D FPS mode.")]
        [SerializeField] private float _fieldOfView     = 70f;

        #endregion

        #region Inspector — Blend

        [Header("Blend")]
        [SerializeField] private CinemachineBlendDefinition.Style _blendStyle
            = CinemachineBlendDefinition.Style.EaseInOut;

        #endregion

        #region Inspector — Debug

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────────────────────────────

        private CinemachineVirtualCamera _vcam2D;
        private CinemachineVirtualCamera _vcam3D;

        // Child of headPivot, 20u forward — used as HardLookAt target so the
        // FPS camera always faces where the player is looking.
        private GameObject _fpsCamLookTarget;

        private Dimension _currentDimension = Dimension.TwoD;
        private float     _targetOrthoSize;
        private bool      _lerpingOrtho;

        // ─────────────────────────────────────────────────────────────────────
        //  Unity lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            if (_mainCamera == null) _mainCamera = GetComponent<Camera>();
            if (_brain      == null) _brain       = GetComponent<CinemachineBrain>();
            _targetOrthoSize = _orthoSize;
        }

        private void OnEnable()
        {
            if (DimensionManager.HasInstance)
                DimensionManager.Instance.OnDimensionChanged += HandleDimensionChanged;
        }

        private void OnDisable()
        {
            if (DimensionManager.HasInstance)
                DimensionManager.Instance.OnDimensionChanged -= HandleDimensionChanged;
        }

        private void Start()
        {
            Dimension start = DimensionManager.HasInstance
                ? DimensionManager.Instance.Current
                : Dimension.TwoD;
            ApplyProjectionImmediate(start);
        }

        private void Update()
        {
            if (!_lerpingOrtho || _mainCamera == null || !_mainCamera.orthographic) return;
            _mainCamera.orthographicSize = Mathf.Lerp(
                _mainCamera.orthographicSize, _targetOrthoSize,
                Time.deltaTime * _orthoLerpSpeed);

            if (Mathf.Abs(_mainCamera.orthographicSize - _targetOrthoSize) < 0.01f)
            {
                _mainCamera.orthographicSize = _targetOrthoSize;
                _lerpingOrtho = false;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API — player registration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by NetworkedDimensionPlayer.OnNetworkSpawn() for the local owner.
        /// Wires and configures both virtual cameras.
        /// </summary>
        /// <param name="vcam2D">2D platformer virtual camera.</param>
        /// <param name="vcam3D">3D FPS virtual camera.</param>
        /// <param name="followTarget2D">Player body transform — 2D cam follows this.</param>
        /// <param name="followTarget3D">HeadPivot transform — 3D FPS cam locks here.</param>
        public void RegisterPlayerCams(
            CinemachineVirtualCamera vcam2D,
            CinemachineVirtualCamera vcam3D,
            Transform                followTarget2D,
            Transform                followTarget3D)
        {
            _vcam2D = vcam2D;
            _vcam3D = vcam3D;

            if (_vcam2D != null && followTarget2D != null)
                ConfigureVcam2D(followTarget2D);

            if (_vcam3D != null && followTarget3D != null)
                ConfigureVcam3D(followTarget3D);

            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Registered — vcam2D={vcam2D?.name} vcam3D={vcam3D?.name}",
                nameof(DimensionCameraController));
        }

        /// <summary>Called by NetworkedDimensionPlayer.OnNetworkDespawn().</summary>
        public void UnregisterPlayerCams()
        {
            SetVcamActive(_vcam2D, false);
            SetVcamActive(_vcam3D, false);
            _vcam2D = null;
            _vcam3D = null;

            if (_fpsCamLookTarget != null)
            {
                Destroy(_fpsCamLookTarget);
                _fpsCamLookTarget = null;
            }
        }

        /// <summary>Smoothly change the 2D orthographic size.</summary>
        public void SetOrthoSize(float size)
        {
            _targetOrthoSize = Mathf.Max(0.5f, size);
            _lerpingOrtho    = _mainCamera != null && _mainCamera.orthographic;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Virtual camera configuration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Configure the 2D platformer virtual camera.
        ///
        /// Body: CinemachineFramingTransposer
        ///   Follows the player in screen-space with damping and optional lookahead.
        ///   _screenY2D = 0.35 puts the player slightly below centre — you see
        ///   more of the platform ahead (standard platformer convention).
        ///
        /// Aim: none — orthographic cameras don't need a rotation target.
        /// </summary>
        private void ConfigureVcam2D(Transform followTarget)
        {
            _vcam2D.Follow = followTarget;
            _vcam2D.LookAt = null;

            // AddCinemachineComponent replaces any existing Body-stage component.
            var ft = _vcam2D.AddCinemachineComponent<CinemachineFramingTransposer>();

            ft.m_LookaheadTime      = _lookahead2D;
            ft.m_LookaheadSmoothing = 10f;
            ft.m_LookaheadIgnoreY   = true;    // ignore vertical velocity spikes (jumps)
            ft.m_HorizontalDamping  = _damping2D;
            ft.m_VerticalDamping    = _damping2D * 1.5f; // slower Y follow (smoother jumps)
            ft.m_ScreenX            = 0.5f;              // centred horizontally
            ft.m_ScreenY            = _screenY2D;        // below centre (platformer feel)
            ft.m_DeadZoneWidth      = 0.08f;
            ft.m_DeadZoneHeight     = 0.04f;
            ft.m_SoftZoneWidth      = 0.8f;
            ft.m_SoftZoneHeight     = 0.8f;
            ft.m_BiasX              = 0f;
            ft.m_BiasY              = 0f;

            MID_Logger.LogDebug(_logLevel,
                "vcam2D: CinemachineFramingTransposer configured (platformer).",
                nameof(DimensionCameraController));
        }

        /// <summary>
        /// Configure the 3D FPS virtual camera — Call of Duty style.
        ///
        /// Body: CinemachineHardLockToTarget
        ///   Camera position == headPivot position. Damping 0 = instant.
        ///
        /// Aim: CinemachineHardLookAt → _fpsCamLookTarget
        ///   _fpsCamLookTarget is a child of headPivot at (0,0,20) local space.
        ///   When headPivot rotates with mouse look, _fpsCamLookTarget moves with
        ///   it — HardLookAt snaps the camera rotation to always face it.
        ///   Result: camera rotation exactly tracks player gaze. No CinemachinePOV,
        ///   no fighting with NetworkedDimensionPlayer's custom mouse-look code.
        ///
        /// The camera sits exactly at eye level (headPivot) facing forward.
        /// Adjust headPivot localPosition on the player prefab to tune eye height.
        /// </summary>
        private void ConfigureVcam3D(Transform headPivot)
        {
            // ── Create look-ahead target ──────────────────────────────────────
            if (_fpsCamLookTarget != null) Destroy(_fpsCamLookTarget);

            _fpsCamLookTarget = new GameObject("[FPSCam_LookTarget]");
            _fpsCamLookTarget.transform.SetParent(headPivot);
            _fpsCamLookTarget.transform.localPosition = new Vector3(0f, 0f, 20f);
            _fpsCamLookTarget.transform.localRotation = Quaternion.identity;

            _vcam3D.Follow = headPivot;
            _vcam3D.LookAt = _fpsCamLookTarget.transform;

            // ── Body: hard lock position to headPivot ─────────────────────────
            var body = _vcam3D.AddCinemachineComponent<CinemachineHardLockToTarget>();
            body.m_Damping = 0f;  // instant — no interpolation lag on FPS cam

            // ── Aim: hard look at the look-ahead target ───────────────────────
            // CinemachineHardLookAt snaps rotation to face LookAt target each frame.
            _vcam3D.AddCinemachineComponent<CinemachineHardLookAt>();

            MID_Logger.LogDebug(_logLevel,
                "vcam3D: HardLockToTarget + HardLookAt configured (FPS).",
                nameof(DimensionCameraController));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Dimension handling
        // ─────────────────────────────────────────────────────────────────────

        private void HandleDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyProjectionTransition(dim);
        }

        private void ApplyProjectionTransition(Dimension dim)
        {
            if (_mainCamera == null) return;

            if (_brain != null)
            {
                _brain.m_DefaultBlend.m_Style = _blendStyle;
                _brain.m_DefaultBlend.m_Time  = dim == Dimension.TwoD
                    ? _blendDuration2D : _blendDuration3D;
            }

            if (dim == Dimension.TwoD)
            {
                _mainCamera.orthographic = true;
                _targetOrthoSize         = _orthoSize;
                _lerpingOrtho            = true;
            }
            else
            {
                _mainCamera.orthographic = false;
                _mainCamera.fieldOfView  = _fieldOfView;
                _lerpingOrtho            = false;
            }

            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Camera → {(dim == Dimension.TwoD ? $"Ortho {_orthoSize}" : $"Perspective FPS fov={_fieldOfView}")}",
                nameof(DimensionCameraController));
        }

        private void ApplyProjectionImmediate(Dimension dim)
        {
            if (_mainCamera == null) return;

            if (dim == Dimension.TwoD)
            {
                _mainCamera.orthographic     = true;
                _mainCamera.orthographicSize = _orthoSize;
                _targetOrthoSize             = _orthoSize;
            }
            else
            {
                _mainCamera.orthographic = false;
                _mainCamera.fieldOfView  = _fieldOfView;
            }

            _currentDimension = dim;
            RefreshVcamState();
        }

        private void RefreshVcamState()
        {
            bool is2D = _currentDimension == Dimension.TwoD;
            SetVcamActive(_vcam2D,  is2D);
            SetVcamActive(_vcam3D, !is2D);
        }

        private static void SetVcamActive(CinemachineVirtualCamera vcam, bool active)
        {
            if (vcam != null) vcam.gameObject.SetActive(active);
        }
    }
}
