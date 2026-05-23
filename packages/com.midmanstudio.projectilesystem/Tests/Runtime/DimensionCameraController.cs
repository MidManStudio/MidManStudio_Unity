// DimensionCameraController.cs
//
// ROOT CAUSE FIX (priority not changing after first switch):
//   UnregisterPlayerCams was setting _vcam2D = null / _vcam3D = null.
//   These are inspector-serialized scene references — nulling them at runtime
//   meant every subsequent RefreshVcamState() call was a no-op.
//   Fix: NEVER null the vcam refs. UnregisterPlayerCams now only clears
//   the Follow/LookAt targets and lowers priorities.
//
// SUBSCRIPTION FIX:
//   Moved subscription to Start() (guaranteed after all Awake()s) +
//   kept OnEnable/OnDisable for enable/disable cycles. Prevents missing
//   the event when DimensionManager.Awake runs after DCC.OnEnable.
//
// PRIORITY:
//   SetVcamPriority() raises/lowers CinemachineVirtualCamera.Priority.
//   Both vcam GameObjects stay active at all times — Cinemachine brain
//   picks the highest-priority one. No SetActive calls on vcams.

using UnityEngine;
using Cinemachine;
using MidManStudio.Core.Logging;

namespace TestGame
{
    [RequireComponent(typeof(Camera))]
    public class DimensionCameraController : MonoBehaviour
    {
        public static DimensionCameraController Instance { get; private set; }

        private const int PRIORITY_ACTIVE   = 20;
        private const int PRIORITY_INACTIVE = 0;

        #region Inspector

        [Header("Camera References (auto-found if null)")]
        [SerializeField] private Camera           _mainCamera;
        [SerializeField] private CinemachineBrain _brain;

        [Header("Scene Virtual Cameras — assign in inspector, NEVER reassigned at runtime")]
        [Tooltip("Drag the 2D vcam scene object here. It is never destroyed or nulled.")]
        [SerializeField] private CinemachineVirtualCamera _vcam2D;
        [Tooltip("Drag the 3D vcam scene object here. It is never destroyed or nulled.")]
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        [Header("2D Camera (CinemachineFramingTransposer — Platformer)")]
        [SerializeField] private float _orthoSize       = 8f;
        [SerializeField] private float _orthoLerpSpeed  = 6f;
        [SerializeField] private float _blendDuration2D = 0.45f;
        [SerializeField, Range(0f, 1f)] private float _screenY2D = 0.35f;
        [SerializeField] private float _damping2D       = 0.5f;
        [SerializeField] private float _lookahead2D     = 0.15f;

        [Header("3D Camera (HardLockToTarget + HardLookAt — FPS)")]
        [SerializeField] private float _blendDuration3D = 0.45f;
        [SerializeField] private float _fieldOfView     = 70f;

        [Header("Blend")]
        [SerializeField] private CinemachineBlendDefinition.Style _blendStyle
            = CinemachineBlendDefinition.Style.EaseInOut;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        // ── Runtime-only state — never the vcam refs themselves ───────────────
        private GameObject _fpsCamLookTarget;
        private Dimension  _currentDimension = Dimension.TwoD;
        private float      _targetOrthoSize;
        private bool       _lerpingOrtho;
        private bool       _subscribedToDimensionManager;

        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            if (_mainCamera == null) _mainCamera = GetComponent<Camera>();
            if (_brain      == null) _brain       = GetComponent<CinemachineBrain>();
            _targetOrthoSize = _orthoSize;

            // Ensure both vcams start active so Cinemachine can manage them via priority
            if (_vcam2D != null) _vcam2D.gameObject.SetActive(true);
            if (_vcam3D != null) _vcam3D.gameObject.SetActive(true);
        }

        private void OnEnable()
        {
            TrySubscribeToDimensionManager();
        }

        private void Start()
        {
            // Start() runs after all Awake()s — DimensionManager is guaranteed alive here
            TrySubscribeToDimensionManager();

            Dimension start = DimensionManager.HasInstance
                ? DimensionManager.Instance.Current
                : Dimension.TwoD;
            ApplyProjectionImmediate(start);

            MID_Logger.LogInfo(_logLevel,
                $"DimensionCameraController ready. vcam2D={_vcam2D?.name ?? "NULL"} " +
                $"vcam3D={_vcam3D?.name ?? "NULL"}",
                nameof(DimensionCameraController));
        }

        private void OnDisable()
        {
            if (DimensionManager.HasInstance)
                DimensionManager.Instance.OnDimensionChanged -= HandleDimensionChanged;
            _subscribedToDimensionManager = false;
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

        #endregion

        #region Public API — Player Registration

        /// <summary>
        /// Called by NetworkedDimensionPlayer.OnNetworkSpawn() for the local owner.
        /// Provides follow targets — the vcams themselves live in the scene and are
        /// never created or destroyed by this call.
        /// </summary>
        public void RegisterPlayerCams(Transform followTarget2D, Transform followTarget3D)
        {
            if (_vcam2D == null || _vcam3D == null)
            {
                Debug.LogError(
                    "[DimensionCameraController] _vcam2D or _vcam3D is null! " +
                    "Assign both virtual camera scene objects in the inspector.",
                    this);
                return;
            }

            if (followTarget2D != null)
                ConfigureVcam2D(followTarget2D);

            if (followTarget3D != null)
                ConfigureVcam3D(followTarget3D);

            // Apply current dimension's priority immediately
            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Player cams registered. dim={_currentDimension} " +
                $"follow2D={followTarget2D?.name} follow3D={followTarget3D?.name}",
                nameof(DimensionCameraController));
        }

        /// <summary>
        /// Called by NetworkedDimensionPlayer.OnNetworkDespawn().
        /// Clears follow targets and lowers priorities — vcam refs are KEPT.
        /// </summary>
        public void UnregisterPlayerCams()
        {
            // Lower priority — vcam GameObjects stay active, refs stay valid
            SetVcamPriority(_vcam2D, false);
            SetVcamPriority(_vcam3D, false);

            // Clear follow targets so vcams don't chase a null/despawned object
            if (_vcam2D != null) { _vcam2D.Follow = null; _vcam2D.LookAt = null; }
            if (_vcam3D != null) { _vcam3D.Follow = null; _vcam3D.LookAt = null; }

            // Destroy the FPS look-ahead helper only — NOT the vcams
            if (_fpsCamLookTarget != null)
            {
                Destroy(_fpsCamLookTarget);
                _fpsCamLookTarget = null;
            }

            MID_Logger.LogInfo(_logLevel, "Player cams unregistered.",
                nameof(DimensionCameraController));
        }

        public void SetOrthoSize(float size)
        {
            _targetOrthoSize = Mathf.Max(0.5f, size);
            _lerpingOrtho    = _mainCamera != null && _mainCamera.orthographic;
        }

        #endregion

        #region Vcam Configuration

        private void ConfigureVcam2D(Transform followTarget)
        {
            _vcam2D.Follow = followTarget;
            _vcam2D.LookAt = null;

            var ft = _vcam2D.AddCinemachineComponent<CinemachineFramingTransposer>();
            ft.m_LookaheadTime      = _lookahead2D;
            ft.m_LookaheadSmoothing = 10f;
            ft.m_LookaheadIgnoreY   = true;
            ft.m_XDamping           = _damping2D;
            ft.m_YDamping           = _damping2D * 1.5f;
            ft.m_ScreenX            = 0.5f;
            ft.m_ScreenY            = _screenY2D;
            ft.m_DeadZoneWidth      = 0.08f;
            ft.m_DeadZoneHeight     = 0.04f;
            ft.m_SoftZoneWidth      = 0.8f;
            ft.m_SoftZoneHeight     = 0.8f;
        }

        private void ConfigureVcam3D(Transform headPivot)
        {
            if (_fpsCamLookTarget != null) Destroy(_fpsCamLookTarget);

            // Look-ahead target: child of headPivot, 20u forward in local space.
            // When headPivot rotates, this moves with it — HardLookAt snaps camera rotation.
            _fpsCamLookTarget = new GameObject("[FPSCam_LookTarget]");
            _fpsCamLookTarget.transform.SetParent(headPivot);
            _fpsCamLookTarget.transform.localPosition = new Vector3(0f, 0f, 20f);
            _fpsCamLookTarget.transform.localRotation = Quaternion.identity;

            _vcam3D.Follow = headPivot;
            _vcam3D.LookAt = _fpsCamLookTarget.transform;

            var body = _vcam3D.AddCinemachineComponent<CinemachineHardLockToTarget>();
            body.m_Damping = 0f;
            _vcam3D.AddCinemachineComponent<CinemachineHardLookAt>();
        }

        #endregion

        #region Dimension Handling

        private void HandleDimensionChanged(Dimension dim)
        {
            _currentDimension = dim;
            ApplyProjectionTransition(dim);

            MID_Logger.LogInfo(_logLevel,
                $"Dimension changed to {dim}. vcam2D priority={_vcam2D?.Priority ?? -1} " +
                $"vcam3D priority={_vcam3D?.Priority ?? -1}",
                nameof(DimensionCameraController));
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

        /// <summary>
        /// Sets vcam priorities to reflect the current dimension.
        /// Safe to call at any time — works as long as _vcam2D/_vcam3D are non-null.
        /// </summary>
        private void RefreshVcamState()
        {
            if (_vcam2D == null || _vcam3D == null)
            {
                Debug.LogWarning(
                    "[DimensionCameraController] RefreshVcamState: vcam refs are null. " +
                    "Assign _vcam2D and _vcam3D in the inspector.",
                    this);
                return;
            }

            bool is2D = _currentDimension == Dimension.TwoD;
            SetVcamPriority(_vcam2D,  is2D);
            SetVcamPriority(_vcam3D, !is2D);
        }

        /// <summary>
        /// Priority-based vcam selection. Active vcam gets high priority;
        /// inactive vcam gets 0. Both GameObjects stay active at all times.
        /// </summary>
        private static void SetVcamPriority(CinemachineVirtualCamera vcam, bool active)
        {
            if (vcam != null)
                vcam.Priority = active ? PRIORITY_ACTIVE : PRIORITY_INACTIVE;
        }

        #endregion

        #region Subscription Helper

        private void TrySubscribeToDimensionManager()
        {
            if (_subscribedToDimensionManager) return;
            if (!DimensionManager.HasInstance)  return;

            DimensionManager.Instance.OnDimensionChanged += HandleDimensionChanged;
            _subscribedToDimensionManager = true;

            MID_Logger.LogDebug(_logLevel,
                "Subscribed to DimensionManager.OnDimensionChanged",
                nameof(DimensionCameraController));
        }

        #endregion
    }
}
