// DimensionCameraController.cs
// KEY CHANGES:
//   + Priority-based vcam management — vcam GameObjects stay active always.
//     High priority (20) = visible, low priority (0) = hidden.
//     This avoids the SetActive flicker and Cinemachine brain losing track.
//   + Removed internal key handling — key is owned by DimensionManager.

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

        [SerializeField] private CinemachineVirtualCamera _vcam2D;
        [SerializeField] private CinemachineVirtualCamera _vcam3D;

        private GameObject _fpsCamLookTarget;
        private Dimension  _currentDimension = Dimension.TwoD;
        private float      _targetOrthoSize;
        private bool       _lerpingOrtho;

        #region Unity Lifecycle

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

        #endregion

        #region Public API

        /// <summary>
        /// Called by NetworkedDimensionPlayer.OnNetworkSpawn() for the local owner.
        /// Wires and configures both virtual cameras.
        /// Both vcam GameObjects must already be in the scene and assigned in the inspector.
        /// </summary>
        public void RegisterPlayerCams(Transform followTarget2D, Transform followTarget3D)
        {
            if (_vcam2D != null && followTarget2D != null)
                ConfigureVcam2D(followTarget2D);

            if (_vcam3D != null && followTarget3D != null)
                ConfigureVcam3D(followTarget3D);

            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Registered — vcam2D={_vcam2D?.name} vcam3D={_vcam3D?.name}",
                nameof(DimensionCameraController));
        }

        /// <summary>Called by NetworkedDimensionPlayer.OnNetworkDespawn().</summary>
        public void UnregisterPlayerCams()
        {
            // Set both to low priority — vcam GOs remain active
            SetVcamPriority(_vcam2D, false);
            SetVcamPriority(_vcam3D, false);
            _vcam2D = null;
            _vcam3D = null;

            if (_fpsCamLookTarget != null)
            {
                Destroy(_fpsCamLookTarget);
                _fpsCamLookTarget = null;
            }
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

            MID_Logger.LogDebug(_logLevel,
                "vcam2D: CinemachineFramingTransposer configured.",
                nameof(DimensionCameraController));
        }

        private void ConfigureVcam3D(Transform headPivot)
        {
            if (_fpsCamLookTarget != null) Destroy(_fpsCamLookTarget);

            _fpsCamLookTarget = new GameObject("[FPSCam_LookTarget]");
            _fpsCamLookTarget.transform.SetParent(headPivot);
            _fpsCamLookTarget.transform.localPosition = new Vector3(0f, 0f, 20f);
            _fpsCamLookTarget.transform.localRotation = Quaternion.identity;

            _vcam3D.Follow = headPivot;
            _vcam3D.LookAt = _fpsCamLookTarget.transform;

            var body = _vcam3D.AddCinemachineComponent<CinemachineHardLockToTarget>();
            body.m_Damping = 0f;
            _vcam3D.AddCinemachineComponent<CinemachineHardLookAt>();

            MID_Logger.LogDebug(_logLevel,
                "vcam3D: HardLockToTarget + HardLookAt configured.",
                nameof(DimensionCameraController));
        }

        #endregion

        #region Dimension Handling

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
            // Use priority — both vcam GameObjects stay active at all times.
            // High priority = Cinemachine picks this camera.
            // Low priority  = other camera wins.
            SetVcamPriority(_vcam2D,  is2D);
            SetVcamPriority(_vcam3D, !is2D);
        }

        /// <summary>
        /// Priority-based vcam activation — never calls SetActive on the vcam GameObject.
        /// Avoids flicker and Cinemachine brain losing its active camera reference.
        /// </summary>
        private static void SetVcamPriority(CinemachineVirtualCamera vcam, bool active)
        {
            if (vcam != null)
                vcam.Priority = active ? PRIORITY_ACTIVE : PRIORITY_INACTIVE;
        }

        #endregion
    }
}
