// packages/com.midmanstudio.projectilesystem/Tests/Runtime/DimensionCameraController.cs
// REWRITTEN:
//   - No longer sets camera position directly (Cinemachine handles it via vcams)
//   - Exposes RegisterPlayerCams() called by NetworkedDimensionPlayer.OnNetworkSpawn()
//   - Activates correct vcam when dimension changes
//   - Handles orthographic ↔ perspective switch for Cinemachine Brain

using UnityEngine;
using Cinemachine;
using MidManStudio.Core.Logging;

namespace TestGame
{
    [RequireComponent(typeof(Camera))]
    public class DimensionCameraController : MonoBehaviour
    {
        #region Inspector

        [Header("References (auto-found if null)")]
        [SerializeField] private Camera           _mainCamera;
        [SerializeField] private CinemachineBrain _brain;

        [Header("2D Camera Settings")]
        [SerializeField] private float _orthoSize        = 8f;
        [SerializeField] private float _blendDuration2D  = 0.45f;
        [SerializeField] private float _orthoLerpSpeed   = 6f;

        [Header("3D Camera Settings")]
        [SerializeField] private float _fieldOfView      = 60f;
        [SerializeField] private float _blendDuration3D  = 0.45f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        // Set by NetworkedDimensionPlayer.OnNetworkSpawn (owner only)
        private CinemachineVirtualCamera _registeredVcam2D;
        private CinemachineVirtualCamera _registeredVcam3D;

        private Dimension _currentDimension = Dimension.TwoD;
        private float     _targetOrthoSize;
        private bool      _lerpingOrtho;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_mainCamera == null) _mainCamera = GetComponent<Camera>();
            if (_brain      == null) _brain       = GetComponent<CinemachineBrain>();
            _targetOrthoSize = _orthoSize;
        }

        private void OnEnable()
        {
            if (DimensionManager.Instance != null)
                DimensionManager.Instance.OnDimensionChanged += HandleDimensionChanged;
        }

        private void OnDisable()
        {
            if (DimensionManager.Instance != null)
                DimensionManager.Instance.OnDimensionChanged -= HandleDimensionChanged;
        }

        private void Start()
        {
            // Apply projection for the start dimension without a lerp
            ApplyProjectionImmediate(Dimension.TwoD);
        }

        private void Update()
        {
            // Smooth orthographic size transitions in 2D mode
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

        #endregion

        #region Public API — Player Registration

        /// <summary>
        /// Called by NetworkedDimensionPlayer.OnNetworkSpawn() for the local owner.
        /// Registers the player's Cinemachine virtual cameras so the controller can
        /// activate the right one when the dimension changes.
        ///
        /// vcam2D: overhead / side-scroll cam (Follow = player root).
        /// vcam3D: first-person cam on HeadPivot — Body/Aim = Do Nothing.
        ///         Follow and LookAt do not need to be set (inherits parent transform).
        /// followTarget2D: player root transform (for vcam2D Follow/LookAt).
        /// </summary>
        public void RegisterPlayerCams(
            CinemachineVirtualCamera vcam2D,
            CinemachineVirtualCamera vcam3D,
            Transform                followTarget2D)
        {
            _registeredVcam2D = vcam2D;
            _registeredVcam3D = vcam3D;

            // Wire 2D cam follow — the 3D FPS cam inherits HeadPivot and needs no target
            if (_registeredVcam2D != null && followTarget2D != null)
            {
                _registeredVcam2D.Follow = followTarget2D;
                _registeredVcam2D.LookAt = followTarget2D;
            }

            // Activate the correct cam for the current dimension immediately
            RefreshVcamState();

            MID_Logger.LogInfo(_logLevel,
                $"Player cams registered: vcam2D={vcam2D?.name} vcam3D={vcam3D?.name}",
                nameof(DimensionCameraController));
        }

        /// <summary>Called by NetworkedDimensionPlayer.OnNetworkDespawn().</summary>
        public void UnregisterPlayerCams()
        {
            _registeredVcam2D = null;
            _registeredVcam3D = null;
        }

        /// <summary>Change the 2D orthographic size with a smooth lerp.</summary>
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

            // Tell Cinemachine Brain the blend duration for this switch
            if (_brain != null)
            {
                _brain.m_DefaultBlend.m_Style = CinemachineBlendDefinition.Style.EaseInOut;
                _brain.m_DefaultBlend.m_Time  = dim == Dimension.TwoD
                    ? _blendDuration2D
                    : _blendDuration3D;
            }

            // Switch projection
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
        }

        /// <summary>
        /// Activate the vcam that matches the current dimension.
        /// The Cinemachine Brain will blend between them automatically.
        /// Both start inactive; we never touch vcams that aren't ours.
        /// </summary>
        private void RefreshVcamState()
        {
            bool is2D = _currentDimension == Dimension.TwoD;
            if (_registeredVcam2D != null)
                _registeredVcam2D.gameObject.SetActive(is2D);
            if (_registeredVcam3D != null)
                _registeredVcam3D.gameObject.SetActive(!is2D);
        }

        #endregion
    }
}
