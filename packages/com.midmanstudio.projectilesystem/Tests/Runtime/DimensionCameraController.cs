// DimensionCameraController.cs
//
// FIXES vs original:
//   + Added static Instance property so NetworkedDimensionPlayer.OnNetworkSpawn
//     can call DimensionCameraController.Instance.RegisterPlayerCams() without a
//     compile error (original had no Instance — was not a Singleton).
//   + Subscription to DimensionManager.OnDimensionChanged now uses HasInstance
//     guard to avoid null-ref on scene teardown.
//   + ApplyProjectionImmediate called in Start uses current Dimension, not hardcoded TwoD.

using UnityEngine;
using Cinemachine;
using MidManStudio.Core.Logging;

namespace TestGame
{
    [RequireComponent(typeof(Camera))]
    public class DimensionCameraController : MonoBehaviour
    {
        // Simple static instance — one main camera, one controller.
        public static DimensionCameraController Instance { get; private set; }

        #region Inspector

        [Header("References (auto-found if null)")]
        [SerializeField] private Camera           _mainCamera;
        [SerializeField] private CinemachineBrain _brain;

        [Header("2D Camera Settings")]
        [SerializeField] private float _orthoSize       = 8f;
        [SerializeField] private float _blendDuration2D = 0.45f;
        [SerializeField] private float _orthoLerpSpeed  = 6f;

        [Header("3D Camera Settings")]
        [SerializeField] private float _fieldOfView     = 60f;
        [SerializeField] private float _blendDuration3D = 0.45f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        private CinemachineVirtualCamera _registeredVcam2D;
        private CinemachineVirtualCamera _registeredVcam3D;

        private Dimension _currentDimension = Dimension.TwoD;
        private float     _targetOrthoSize;
        private bool      _lerpingOrtho;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Enforce single instance — if a second exists, remove it
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
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
            // Apply projection for the current start dimension without Cinemachine blending
            Dimension start = DimensionManager.HasInstance
                ? DimensionManager.Instance.Current
                : Dimension.TwoD;
            ApplyProjectionImmediate(start);
        }

        private void Update()
        {
            if (!_lerpingOrtho || _mainCamera == null || !_mainCamera.orthographic) return;

            _mainCamera.orthographicSize = Mathf.Lerp(
                _mainCamera.orthographicSize,
                _targetOrthoSize,
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
        /// Wires the player's virtual cameras and activates the correct one immediately.
        /// </summary>
        public void RegisterPlayerCams(
            CinemachineVirtualCamera vcam2D,
            CinemachineVirtualCamera vcam3D,
            Transform                followTarget2D)
        {
            _registeredVcam2D = vcam2D;
            _registeredVcam3D = vcam3D;

            if (_registeredVcam2D != null && followTarget2D != null)
            {
                _registeredVcam2D.Follow = followTarget2D;
                _registeredVcam2D.LookAt = followTarget2D;
            }

            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Player cams registered: vcam2D={vcam2D?.name} vcam3D={vcam3D?.name}",
                nameof(DimensionCameraController));
        }

        /// <summary>Called by NetworkedDimensionPlayer.OnNetworkDespawn().</summary>
        public void UnregisterPlayerCams()
        {
            // Deactivate before clearing so Cinemachine doesn't jump
            SetVcamActive(_registeredVcam2D, false);
            SetVcamActive(_registeredVcam3D, false);
            _registeredVcam2D = null;
            _registeredVcam3D = null;
        }

        /// <summary>Smoothly change the 2D orthographic size.</summary>
        public void SetOrthoSize(float size)
        {
            _targetOrthoSize = Mathf.Max(0.5f, size);
            _lerpingOrtho    = _mainCamera != null && _mainCamera.orthographic;
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
                _brain.m_DefaultBlend.m_Style = CinemachineBlendDefinition.Style.EaseInOut;
                _brain.m_DefaultBlend.m_Time  = dim == Dimension.TwoD
                    ? _blendDuration2D
                    : _blendDuration3D;
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
                $"Camera → {(dim == Dimension.TwoD ? $"Orthographic (size {_orthoSize})" : $"Perspective FPS (fov {_fieldOfView})")}",
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
            SetVcamActive(_registeredVcam2D,  is2D);
            SetVcamActive(_registeredVcam3D, !is2D);
        }

        private static void SetVcamActive(CinemachineVirtualCamera vcam, bool active)
        {
            if (vcam != null) vcam.gameObject.SetActive(active);
        }

        #endregion
    }
}
