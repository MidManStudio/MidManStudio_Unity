// packages/com.midmanstudio.projectilesystem/Tests/Runtime/DimensionCameraController.cs
//
// REDESIGN: True scene Singleton — vcam2D and vcam3D are serialized directly
// in the inspector on this component. The player does NOT hold camera references.
// On spawn the local owner just gives us their follow transforms.
//
// ── 2D: CinemachineFramingTransposer (platformer) ─────────────────────────
//   Camera follows the player body. Player sits at _screenY2D (default 0.35)
//   so you see ahead in the direction of travel. Lookahead anticipates movement.
//
// ── 3D: CinemachineHardLockToTarget + CinemachineHardLookAt (FPS) ─────────
//   Body = HardLockToTarget on headPivot (camera sits at eye level, no lag).
//   Aim  = HardLookAt on a child GameObject parented to headPivot at (0,0,20).
//   As mouse-look rotates headPivot, the look target moves with it.
//   Camera always faces exactly where the player looks — no fight with the
//   player script's custom yaw/pitch code. No CinemachinePOV needed.

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
        //  Inspector — Scene Camera References
        //  Assign these in the Inspector. The player NEVER needs to hold vcams.
        // ─────────────────────────────────────────────────────────────────────

        [Header("Scene Virtual Cameras — assign in Inspector")]
        [Tooltip("2D platformer virtual camera. Must exist in the scene before play.")]
        [SerializeField] private CinemachineVirtualCamera _vcam2D;

        [Tooltip("3D FPS virtual camera. Must exist in the scene before play.")]
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        [Header("Brain (auto-found if null)")]
        [SerializeField] private CinemachineBrain _brain;

        // ─────────────────────────────────────────────────────────────────────
        //  Inspector — 2D Platformer Settings
        // ─────────────────────────────────────────────────────────────────────

        [Header("2D Platformer  (CinemachineFramingTransposer)")]
        [Tooltip("Orthographic size in 2D mode.")]
        [SerializeField] private float _orthoSize       = 8f;

        [Tooltip("Speed at which orthoSize lerps to target when calling SetOrthoSize().")]
        [SerializeField] private float _orthoLerpSpeed  = 6f;

        [Tooltip("Blend duration (seconds) when entering 2D mode.")]
        [SerializeField] private float _blendDuration2D = 0.45f;

        [Tooltip("Normalised screen Y for the player (0=bottom, 1=top).\n" +
                 "0.35 = player sits slightly below centre — standard platformer.")]
        [SerializeField, Range(0f, 1f)]
        private float _screenY2D = 0.35f;

        [Tooltip("Horizontal and vertical follow damping.")]
        [SerializeField] private float _damping2D = 0.5f;

        [Tooltip("Seconds of velocity lookahead. Camera anticipates movement direction.")]
        [SerializeField] private float _lookahead2D = 0.15f;

        // ─────────────────────────────────────────────────────────────────────
        //  Inspector — 3D FPS Settings
        // ─────────────────────────────────────────────────────────────────────

        [Header("3D FPS  (HardLockToTarget + HardLookAt)")]
        [Tooltip("Field of view in 3D FPS mode.")]
        [SerializeField] private float _fieldOfView     = 70f;

        [Tooltip("Blend duration (seconds) when entering 3D mode.")]
        [SerializeField] private float _blendDuration3D = 0.45f;

        // ─────────────────────────────────────────────────────────────────────
        //  Inspector — Blend
        // ─────────────────────────────────────────────────────────────────────

        [Header("Blend")]
        [SerializeField] private CinemachineBlendDefinition.Style _blendStyle
            = CinemachineBlendDefinition.Style.EaseInOut;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        // ─────────────────────────────────────────────────────────────────────
        //  Private state
        // ─────────────────────────────────────────────────────────────────────

        private Camera    _mainCamera;
        private Dimension _currentDimension = Dimension.TwoD;
        private float     _targetOrthoSize;
        private bool      _lerpingOrtho;

        // Parented to headPivot; HardLookAt tracks it → camera faces where player looks.
        private GameObject _fpsCamLookTarget;

        // ─────────────────────────────────────────────────────────────────────
        //  Unity lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            _mainCamera = GetComponent<Camera>();
            if (_brain == null) _brain = GetComponent<CinemachineBrain>();

            _targetOrthoSize = _orthoSize;

            ValidateVcamReferences();

            // Start with both vcams off — RefreshVcamState() called in Start
            SetVcamActive(_vcam2D, false);
            SetVcamActive(_vcam3D, false);
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
            // Configure vcam components at start — before any player spawns.
            // Follow/LookAt targets will be set when a player registers.
            ConfigureVcam2DComponents();
            ConfigureVcam3DComponents();

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
        /// Called by the local owner on NetworkSpawn.
        /// Sets Follow / LookAt targets on the existing scene vcams.
        /// No camera references needed on the player prefab.
        /// </summary>
        /// <param name="followBody">Player body transform — 2D cam follows this.</param>
        /// <param name="headPivot">Eye-level pivot — 3D FPS cam locks here.</param>
        public void RegisterPlayerCams(Transform followBody, Transform headPivot)
        {
            if (_vcam2D == null || _vcam3D == null)
            {
                MID_Logger.LogError(_logLevel,
                    "vcam2D or vcam3D is null — assign them in the DimensionCameraController " +
                    "inspector. The player does not need to hold camera references.",
                    nameof(DimensionCameraController));
                return;
            }

            // 2D: follow the player body
            if (_vcam2D != null && followBody != null)
                _vcam2D.Follow = followBody;

            // 3D FPS: hard lock body to headPivot; look target is its child
            if (_vcam3D != null && headPivot != null)
            {
                _vcam3D.Follow = headPivot;
                SetupFpsLookTarget(headPivot);
            }

            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Player cams registered: followBody={followBody?.name} headPivot={headPivot?.name}",
                nameof(DimensionCameraController));
        }

        /// <summary>Called by the local owner on NetworkDespawn.</summary>
        public void UnregisterPlayerCams()
        {
            if (_vcam2D != null)
            {
                _vcam2D.Follow = null;
                SetVcamActive(_vcam2D, false);
            }

            if (_vcam3D != null)
            {
                _vcam3D.Follow = null;
                _vcam3D.LookAt = null;
                SetVcamActive(_vcam3D, false);
            }

            if (_fpsCamLookTarget != null)
            {
                Destroy(_fpsCamLookTarget);
                _fpsCamLookTarget = null;
            }
        }

        /// <summary>Smoothly zoom the 2D orthographic camera to a new size.</summary>
        public void SetOrthoSize(float size)
        {
            _targetOrthoSize = Mathf.Max(0.5f, size);
            _lerpingOrtho    = _mainCamera != null && _mainCamera.orthographic;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Vcam component configuration
        //  Called once in Start — sets the Cinemachine Body/Aim components.
        //  Follow/LookAt targets are set in RegisterPlayerCams.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Configure 2D vcam Body stage: CinemachineFramingTransposer.
        ///
        /// FramingTransposer keeps the player at a fixed normalised screen position
        /// with damping and lookahead — standard 2D/platformer framing.
        /// No Aim component needed for orthographic cameras.
        /// </summary>
        private void ConfigureVcam2DComponents()
        {
            if (_vcam2D == null) return;

            var ft = _vcam2D.AddCinemachineComponent<CinemachineFramingTransposer>();

            ft.m_LookaheadTime      = _lookahead2D;
            ft.m_LookaheadSmoothing = 10f;
            ft.m_LookaheadIgnoreY   = true;   // ignore vertical velocity spikes from jumps
            ft.m_HorizontalDamping  = _damping2D;
            ft.m_VerticalDamping    = _damping2D * 1.5f; // slower Y (smoother over platforms)
            ft.m_ScreenX            = 0.5f;              // centred horizontally
            ft.m_ScreenY            = _screenY2D;        // slightly below centre
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
        /// Configure 3D vcam: HardLockToTarget (Body) + HardLookAt (Aim).
        ///
        /// HardLockToTarget: camera position == Follow target (headPivot). Damping=0.
        /// HardLookAt: camera rotation always faces LookAt target instantly.
        /// LookAt target = child of headPivot at (0,0,20) local — set in
        /// SetupFpsLookTarget() when a player registers.
        ///
        /// This gives true FPS rotation WITHOUT CinemachinePOV fighting the
        /// player script's mouse-look code.
        /// </summary>
        private void ConfigureVcam3DComponents()
        {
            if (_vcam3D == null) return;

            var body = _vcam3D.AddCinemachineComponent<CinemachineHardLockToTarget>();
            body.m_Damping = 0f;

            _vcam3D.AddCinemachineComponent<CinemachineHardLookAt>();

            MID_Logger.LogDebug(_logLevel,
                "vcam3D: HardLockToTarget + HardLookAt configured (FPS).",
                nameof(DimensionCameraController));
        }

        /// <summary>
        /// Create (or re-parent) the FPS look target as a child of headPivot.
        /// Placed 20u forward in local space so HardLookAt always tracks
        /// exactly where the player is looking as headPivot rotates.
        /// </summary>
        private void SetupFpsLookTarget(Transform headPivot)
        {
            if (_fpsCamLookTarget != null)
                Destroy(_fpsCamLookTarget);

            _fpsCamLookTarget = new GameObject("[FPS_LookTarget]");
            _fpsCamLookTarget.transform.SetParent(headPivot);
            _fpsCamLookTarget.transform.localPosition = new Vector3(0f, 0f, 20f);
            _fpsCamLookTarget.transform.localRotation = Quaternion.identity;

            _vcam3D.LookAt = _fpsCamLookTarget.transform;
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
                $"Camera → {(dim == Dimension.TwoD ? $"Ortho size={_orthoSize}" : $"FPS fov={_fieldOfView}")}",
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

        // ─────────────────────────────────────────────────────────────────────
        //  Validation
        // ─────────────────────────────────────────────────────────────────────

        private void ValidateVcamReferences()
        {
            if (_vcam2D == null)
                Debug.LogError(
                    "[DimensionCameraController] _vcam2D is not assigned. " +
                    "Drag the scene vcam2D GameObject into the DimensionCameraController inspector. " +
                    "The player prefab does NOT need camera references.",
                    this);

            if (_vcam3D == null)
                Debug.LogError(
                    "[DimensionCameraController] _vcam3D is not assigned. " +
                    "Drag the scene vcam3D GameObject into the DimensionCameraController inspector.",
                    this);
        }
    }
}
