// DimensionManager.cs — FIXED
//
// Fixes vs original:
//   + Apply2D / Apply3D: StopAllCoroutines() replaced with tracked _cameraLerpCoroutine.
//     Original called StopAllCoroutines() which killed the TransitionCoroutine itself,
//     leaving IsTransitioning=true permanently and breaking all subsequent switches.
//   + ApplyDimension: removed FindFirstObjectByType<NetworkedDimensionPlayer>()?.OnDimensionChanged().
//     This caused every player to be notified twice — once via the direct call and again
//     via the OnDimensionChanged event. Players subscribe in OnNetworkSpawn; the event is enough.
//   + Apply2D / Apply3D: skip direct camera manipulation when DimensionCameraController
//     is present — they would fight over the camera transform / projection.
//   + Start(): was hardcoded ApplyProjectionImmediate(Dimension.TwoD) — now uses _startMode.
//   + LerpCamera / FadeOverlay: guarded against zero duration divide.

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

        [Header("Camera — only used when DimensionCameraController is NOT in scene")]
        [SerializeField] private Camera   _mainCamera;
        [SerializeField] private float    _orthoSize       = 8f;
        [SerializeField] private float    _perspectiveFov  = 60f;
        [SerializeField] private Vector3  _cam2DPosition   = new Vector3(0f,  0f, -20f);
        [SerializeField] private Vector3  _cam3DPosition   = new Vector3(0f,  6f, -12f);
        [SerializeField] private Vector3  _cam3DEuler      = new Vector3(20f, 0f,   0f);

        [Header("Transition")]
        [SerializeField] private float       _transitionDuration = 0.5f;
        [SerializeField] private CanvasGroup _fadeOverlay;       // optional screen fade

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

        public Dimension Current         { get; private set; }
        public bool      IsTransitioning { get; private set; }

        /// <summary>
        /// Fired once after a transition fully completes.
        /// NOT fired during startup — use DimensionManager.Instance.Current at spawn time.
        /// </summary>
        public event Action<Dimension> OnDimensionChanged;

        /// <summary>
        /// Tracked reference so we can cancel only the camera lerp coroutine,
        /// not the outer transition coroutine.
        /// </summary>
        private Coroutine _cameraLerpCoroutine;

        /// <summary>
        /// True when DimensionCameraController is active.
        /// When true, DimensionManager skips direct camera manipulation entirely.
        /// </summary>
        private static bool HasCameraController
            => DimensionCameraController.Instance != null;

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (_mainCamera == null)
                _mainCamera = Camera.main;

            // Apply start mode immediately — no fade, no event fired
            ApplyDimension(_startMode, instant: true);
        }

        private void Start()
        {
            // Re-apply projection in Start as safety measure in case Camera.main
            // wasn't assigned yet when Awake ran. Skip when DimensionCameraController
            // is present — it reads DimensionManager.Instance.Current in its own Start().
            if (!HasCameraController && _mainCamera != null)
                ApplyProjectionImmediate(_startMode);
        }

        private void Update()
        {
            // Quick test binding — Tab key toggles dimension
            if (!IsTransitioning && Input.GetKeyDown(KeyCode.Tab))
                SwitchDimension();
        }

        #endregion

        #region Public API

        /// <summary>Toggle between 2D and 3D with a transition.</summary>
        public void SwitchDimension()
            => SetDimension(Current == Dimension.TwoD ? Dimension.ThreeD : Dimension.TwoD);

        /// <summary>Switch to a specific dimension with a transition.</summary>
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

            // Optional screen fade-out
            if (_fadeOverlay != null)
                yield return StartCoroutine(FadeOverlay(0f, 1f, _transitionDuration * 0.4f));

            // Apply environment / renderer / camera changes
            ApplyDimension(target, instant: false);

            // Optional screen fade-in
            if (_fadeOverlay != null)
                yield return StartCoroutine(FadeOverlay(1f, 0f, _transitionDuration * 0.4f));

            IsTransitioning = false;

            // Fire event ONCE here — all subscribers (players, HUD, audio) get notified once.
            // Do NOT also call OnDimensionChanged directly on individual player instances;
            // they subscribe to this event in OnNetworkSpawn.
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

            // Environment roots
            if (_env2D != null) _env2D.SetActive(target == Dimension.TwoD);
            if (_env3D != null) _env3D.SetActive(target == Dimension.ThreeD);

            // Projectile renderer swap
            if (_projRenderer2D != null) _projRenderer2D.enabled = target == Dimension.TwoD;
            if (_projRenderer3D != null) _projRenderer3D.enabled = target == Dimension.ThreeD;

            // ── DO NOT call FindFirstObjectByType<NetworkedDimensionPlayer>() here. ──
            // That caused every player to receive OnDimensionChanged twice:
            // once from this direct call and again from the event at the end of
            // TransitionCoroutine. Players subscribe to the event themselves.
        }

        private void Apply2D(bool instant)
        {
            // DimensionCameraController owns all camera state when present.
            // It subscribes to OnDimensionChanged and handles projection + vcam blend.
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
                // Stop only the camera lerp, NOT the outer transition coroutine
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
