// packages/com.midmanstudio.projectilesystem/Tests/Runtime/DimensionCameraController.cs
// Scene-level Cinemachine camera manager for the 2D / 3D projectile test.
//
// Each player prefab owns its own CinemachineVirtualCameras (activated only for
// the local owner). This script adjusts the scene Camera's projection mode and
// the CinemachineBrain's default blend settings whenever the dimension switches.
//
// SETUP:
//   1. Place on the same GameObject as the main Camera.
//   2. Ensure a CinemachineBrain component is also on the main Camera.
//   3. Assign _mainCamera and _brain (or leave null to auto-find).
//   4. Add to scene before DimensionManager so Start() fires after dimension init.

using UnityEngine;
using Cinemachine;
using MidManStudio.Core.Logging;

namespace TestGame
{
    [RequireComponent(typeof(Camera))]
    public class DimensionCameraController : MonoBehaviour
    {
        #region Inspector

        [Header("References  (auto-found if null)")]
        [SerializeField] private Camera             _mainCamera;
        [SerializeField] private CinemachineBrain   _brain;

        [Header("2D Camera Settings")]
        [SerializeField] private float _orthoSize       = 8f;
        [Tooltip("Cinemachine Brain blend duration when switching TO 2D.")]
        [SerializeField] private float _blendDuration2D = 0.45f;
        [Tooltip("Lerp speed for ortho size adjustment.")]
        [SerializeField] private float _orthoLerpSpeed  = 6f;

        [Header("3D Camera Settings")]
        [SerializeField] private float _fieldOfView     = 60f;
        [Tooltip("Cinemachine Brain blend duration when switching TO 3D.")]
        [SerializeField] private float _blendDuration3D = 0.45f;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Private State

        private Dimension _targetDimension  = Dimension.TwoD;
        private float     _targetOrthoSize;
        private bool      _lerping;

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
            // Apply the start dimension immediately (no lerp on boot)
            ApplyProjectionImmediate(Dimension.TwoD);
        }

        private void Update()
        {
            // Smoothly lerp orthographic size during 2D mode changes
            if (!_lerping || _mainCamera == null || !_mainCamera.orthographic) return;

            _mainCamera.orthographicSize = Mathf.Lerp(
                _mainCamera.orthographicSize,
                _targetOrthoSize,
                Time.deltaTime * _orthoLerpSpeed);

            if (Mathf.Abs(_mainCamera.orthographicSize - _targetOrthoSize) < 0.01f)
            {
                _mainCamera.orthographicSize = _targetOrthoSize;
                _lerping = false;
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Zoom the 2D orthographic view to a new size (e.g. when the test area scales up).
        /// Has no effect in 3D mode.
        /// </summary>
        public void SetOrthoSize(float size)
        {
            _targetOrthoSize = Mathf.Max(0.5f, size);
            _lerping         = _mainCamera != null && _mainCamera.orthographic;
        }

        #endregion

        #region Dimension Handling

        private void HandleDimensionChanged(Dimension dim)
        {
            _targetDimension = dim;
            ApplyProjectionTransition(dim);
        }

        private void ApplyProjectionTransition(Dimension dim)
        {
            if (_mainCamera == null) return;

            // Update Brain blend so the Cinemachine cut uses the right duration
            if (_brain != null)
            {
                _brain.m_DefaultBlend.m_Style =
                    CinemachineBlendDefinition.Style.EaseInOut;

                _brain.m_DefaultBlend.m_Time =
                    dim == Dimension.TwoD ? _blendDuration2D : _blendDuration3D;
            }

            if (dim == Dimension.TwoD)
            {
                _mainCamera.orthographic     = true;
                _targetOrthoSize             = _orthoSize;
                _lerping                     = true;
            }
            else
            {
                _mainCamera.orthographic = false;
                _mainCamera.fieldOfView  = _fieldOfView;
                _lerping                 = false;
            }

            MID_Logger.LogInfo(_logLevel,
                $"Camera → {(dim == Dimension.TwoD ? $"Orthographic (size {_orthoSize})" : $"Perspective (fov {_fieldOfView})")}",
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

        #endregion
    }
}
