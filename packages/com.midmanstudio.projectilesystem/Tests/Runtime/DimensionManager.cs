
using System;
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.Logging;

namespace TestGame
{
    public enum Dimension { TwoD, ThreeD }

    public class DimensionManager : Singleton<DimensionManager>
    {
        #region Inspector

        [Header("Dimension Toggle Key")]
        [Tooltip("Key to toggle 2D ↔ 3D. Avoid Tab (hides cursor / navigates UI).")]
        [SerializeField] private KeyCode _dimensionToggleKey = KeyCode.BackQuote;

        [Header("Camera — only used when DimensionCameraController is NOT in scene")]
        [SerializeField] private Camera   _mainCamera;
        [SerializeField] private float    _orthoSize       = 8f;
        [SerializeField] private float    _perspectiveFov  = 60f;
        [SerializeField] private Vector3  _cam2DPosition   = new Vector3(0f,  0f, -20f);
        [SerializeField] private Vector3  _cam3DPosition   = new Vector3(0f,  6f, -12f);
        [SerializeField] private Vector3  _cam3DEuler      = new Vector3(20f, 0f,   0f);

        [Header("Transition")]
        [SerializeField] private float       _transitionDuration = 0.5f;
        [SerializeField] private CanvasGroup _fadeOverlay;

        [Header("Environment Roots")]
        [SerializeField] private GameObject _env2D;
        [SerializeField] private GameObject _env3D;

        [Header("Projectile Renderers")]
        [SerializeField] private MidManStudio.Projectiles.Visuals.ProjectileRenderer2D _projRenderer2D;
        [SerializeField] private MidManStudio.Projectiles.Visuals.ProjectileRenderer3D _projRenderer3D;

        [Header("Start Mode")]
        [SerializeField] private Dimension _startMode = Dimension.TwoD;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region State

        public  Dimension Current         { get; private set; }
        public bool      IsTransitioning { get; private set; }

        public event Action<Dimension> OnDimensionChanged;

        private Coroutine _cameraLerpCoroutine;

        private static bool HasCameraController
            => DimensionCameraController.Instance != null;

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (_mainCamera == null)
                _mainCamera = Camera.main;

            ApplyDimension(_startMode, instant: true);
        }

        private void Start()
        {
            if (!HasCameraController && _mainCamera != null)
                ApplyProjectionImmediate(_startMode);
        }

        private void Update()
        {
            // Configurable toggle key — no hard-coded Tab
            if (!IsTransitioning && Input.GetKeyDown(_dimensionToggleKey))
                SwitchDimension();
        }

        #endregion

        #region Public API

        public void SwitchDimension()
            => SetDimension(Current == Dimension.TwoD ? Dimension.ThreeD : Dimension.TwoD);

        public void SetDimension(Dimension target)
        {
            if (IsTransitioning || target == Current) return;
            StartCoroutine(TransitionCoroutine(target));
        }

        #endregion

        #region Transition

        private IEnumerator TransitionCoroutine(Dimension target)
        {
            IsTransitioning = true;

            MID_Logger.LogInfo(_logLevel,
                $"Switching dimension: {Current} → {target}",
                nameof(DimensionManager));

            if (_fadeOverlay != null)
                yield return StartCoroutine(FadeOverlay(0f, 1f, _transitionDuration * 0.4f));

            ApplyDimension(target, instant: false);

            if (_fadeOverlay != null)
                yield return StartCoroutine(FadeOverlay(1f, 0f, _transitionDuration * 0.4f));

            IsTransitioning = false;

            OnDimensionChanged?.Invoke(Current);

            MID_Logger.LogInfo(_logLevel,
                $"Dimension switch complete: {Current}",
                nameof(DimensionManager));
        }

        #endregion

        #region Apply

        private void ApplyDimension(Dimension target, bool instant)
        {
            Current = target;

            if (target == Dimension.TwoD) Apply2D(instant);
            else                          Apply3D(instant);

            if (_env2D != null) _env2D.SetActive(target == Dimension.TwoD);
            if (_env3D != null) _env3D.SetActive(target == Dimension.ThreeD);

            if (_projRenderer2D != null) _projRenderer2D.enabled = target == Dimension.TwoD;
            if (_projRenderer3D != null) _projRenderer3D.enabled = target == Dimension.ThreeD;
        }

        private void Apply2D(bool instant)
        {
            if (HasCameraController) return;
            if (_mainCamera == null)  return;

            _mainCamera.orthographic     = true;
            _mainCamera.orthographicSize = _orthoSize;

            if (instant)
            {
                _mainCamera.transform.position = _cam2DPosition;
                _mainCamera.transform.rotation = Quaternion.identity;
            }
            else
            {
                if (_cameraLerpCoroutine != null) StopCoroutine(_cameraLerpCoroutine);
                _cameraLerpCoroutine = StartCoroutine(
                    LerpCamera(_cam2DPosition, Quaternion.identity));
            }
        }

        private void Apply3D(bool instant)
        {
            if (HasCameraController) return;
            if (_mainCamera == null)  return;

            _mainCamera.orthographic = false;
            _mainCamera.fieldOfView  = _perspectiveFov;

            Quaternion targetRot = Quaternion.Euler(_cam3DEuler);
            if (instant)
            {
                _mainCamera.transform.position = _cam3DPosition;
                _mainCamera.transform.rotation = targetRot;
            }
            else
            {
                if (_cameraLerpCoroutine != null) StopCoroutine(_cameraLerpCoroutine);
                _cameraLerpCoroutine = StartCoroutine(
                    LerpCamera(_cam3DPosition, targetRot));
            }
        }

        private void ApplyProjectionImmediate(Dimension dim)
        {
            if (_mainCamera == null) return;
            if (dim == Dimension.TwoD)
            {
                _mainCamera.orthographic     = true;
                _mainCamera.orthographicSize = _orthoSize;
            }
            else
            {
                _mainCamera.orthographic = false;
                _mainCamera.fieldOfView  = _perspectiveFov;
            }
        }

        #endregion

        #region Camera Lerp

        private IEnumerator LerpCamera(Vector3 targetPos, Quaternion targetRot)
        {
            if (_mainCamera == null) yield break;

            Vector3    startPos = _mainCamera.transform.position;
            Quaternion startRot = _mainCamera.transform.rotation;
            float dur = Mathf.Max(_transitionDuration, 0.001f);
            float t   = 0f;

            while (t < 1f)
            {
                t += Time.deltaTime / dur;
                float ease = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                _mainCamera.transform.position = Vector3.Lerp(startPos, targetPos, ease);
                _mainCamera.transform.rotation = Quaternion.Slerp(startRot, targetRot, ease);
                yield return null;
            }

            _mainCamera.transform.position = targetPos;
            _mainCamera.transform.rotation = targetRot;
            _cameraLerpCoroutine           = null;
        }

        #endregion

        #region Fade

        private IEnumerator FadeOverlay(float from, float to, float duration)
        {
            if (_fadeOverlay == null) yield break;
            float dur = Mathf.Max(duration, 0.001f);
            float t   = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / dur;
                _fadeOverlay.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(t));
                yield return null;
            }
            _fadeOverlay.alpha = to;
        }

        #endregion
    }
}
