// DimensionManager.cs
// Switches the game between 2D and 3D presentation modes.
// Physics and game logic run identically in both modes.
// Only camera, renderers, environment, and movement constraints change.

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
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Camera")]
        [SerializeField] private Camera _mainCamera;
        [SerializeField] private float  _orthoSize       = 8f;
        [SerializeField] private float  _perspectiveFov  = 60f;
        [SerializeField] private Vector3 _cam2DPosition  = new(0f, 0f, -20f);
        [SerializeField] private Vector3 _cam3DPosition  = new(0f, 6f, -12f);
        [SerializeField] private Vector3 _cam3DEuler     = new(20f, 0f, 0f);

        [Header("Transition")]
        [SerializeField] private float _transitionDuration = 0.5f;
        [SerializeField] private CanvasGroup _fadeOverlay; // optional screen fade

        [Header("Environment Roots")]
        [SerializeField] private GameObject _env2D;       // 2D tilemap / sprites
        [SerializeField] private GameObject _env3D;       // 3D mesh environment

        [Header("Projectile Renderers")]
        [SerializeField] private MidManStudio.Projectiles.Visuals.ProjectileRenderer2D _projRenderer2D;
        [SerializeField] private MidManStudio.Projectiles.Visuals.ProjectileRenderer3D _projRenderer3D;

        [Header("Start Mode")]
        [SerializeField] private Dimension _startMode = Dimension.TwoD;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        // ── State ─────────────────────────────────────────────────────────────

        public Dimension Current       { get; private set; }
        public bool      IsTransitioning { get; private set; }

        /// <summary>Fired after a dimension switch completes.</summary>
        public event Action<Dimension> OnDimensionChanged;

        // ── Init ──────────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            if (_mainCamera == null)
                _mainCamera = Camera.main;

            // Apply start mode immediately (no transition at startup)
            ApplyDimension(_startMode, instant: true);
        }

        private void Update()
        {
            // Quick test binding — Tab to switch
            if (!IsTransitioning && Input.GetKeyDown(KeyCode.Tab))
                SwitchDimension();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Toggle between 2D and 3D.</summary>
        public void SwitchDimension()
            => SetDimension(Current == Dimension.TwoD ? Dimension.ThreeD : Dimension.TwoD);

        /// <summary>Switch to a specific dimension.</summary>
        public void SetDimension(Dimension target)
        {
            if (IsTransitioning || target == Current) return;
            StartCoroutine(TransitionCoroutine(target));
        }

        // ── Transition ────────────────────────────────────────────────────────

        private IEnumerator TransitionCoroutine(Dimension target)
        {
            IsTransitioning = true;

            MID_Logger.LogInfo(_logLevel,
                $"Switching dimension: {Current} → {target}",
                nameof(DimensionManager));

            // Optional fade-out
            if (_fadeOverlay != null)
                yield return StartCoroutine(FadeOverlay(0f, 1f, _transitionDuration * 0.4f));

            ApplyDimension(target, instant: false);

            // Optional fade-in
            if (_fadeOverlay != null)
                yield return StartCoroutine(FadeOverlay(1f, 0f, _transitionDuration * 0.4f));

            IsTransitioning = false;
            OnDimensionChanged?.Invoke(Current);

            MID_Logger.LogInfo(_logLevel,
                $"Dimension switch complete: {Current}",
                nameof(DimensionManager));
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        private void ApplyDimension(Dimension target, bool instant)
        {
            Current = target;

            if (target == Dimension.TwoD)
                Apply2D(instant);
            else
                Apply3D(instant);

            // Swap environment roots
            if (_env2D != null) _env2D.SetActive(target == Dimension.TwoD);
            if (_env3D != null) _env3D.SetActive(target == Dimension.ThreeD);

            // Swap projectile renderers
            if (_projRenderer2D != null) _projRenderer2D.enabled = (target == Dimension.TwoD);
            if (_projRenderer3D != null) _projRenderer3D.enabled = (target == Dimension.ThreeD);

            // Notify player controller
            FindFirstObjectByType<NetworkedDimensionPlayer>()?.OnDimensionChanged(target);
        }

        private void Apply2D(bool instant)
        {
            _mainCamera.orthographic    = true;
            _mainCamera.orthographicSize = _orthoSize;

            if (instant)
            {
                _mainCamera.transform.position = _cam2DPosition;
                _mainCamera.transform.rotation = Quaternion.identity;
            }
            else
            {
                StopAllCoroutines();
                StartCoroutine(LerpCamera(_cam2DPosition, Quaternion.identity));
            }
        }

        private void Apply3D(bool instant)
        {
            _mainCamera.orthographic = false;
            _mainCamera.fieldOfView  = _perspectiveFov;

            var targetRot = Quaternion.Euler(_cam3DEuler);
            if (instant)
            {
                _mainCamera.transform.position = _cam3DPosition;
                _mainCamera.transform.rotation = targetRot;
            }
            else
            {
                StopAllCoroutines();
                StartCoroutine(LerpCamera(_cam3DPosition, targetRot));
            }
        }

        // ── Camera lerp ───────────────────────────────────────────────────────

        private IEnumerator LerpCamera(Vector3 targetPos, Quaternion targetRot)
        {
            Vector3    startPos = _mainCamera.transform.position;
            Quaternion startRot = _mainCamera.transform.rotation;
            float t = 0f;

            while (t < 1f)
            {
                t += Time.deltaTime / _transitionDuration;
                float ease = Mathf.SmoothStep(0f, 1f, t);
                _mainCamera.transform.position = Vector3.Lerp(startPos, targetPos, ease);
                _mainCamera.transform.rotation = Quaternion.Slerp(startRot, targetRot, ease);
                yield return null;
            }

            _mainCamera.transform.position = targetPos;
            _mainCamera.transform.rotation = targetRot;
        }

        // ── Fade helper ───────────────────────────────────────────────────────

        private IEnumerator FadeOverlay(float from, float to, float duration)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                _fadeOverlay.alpha = Mathf.Lerp(from, to, t);
                yield return null;
            }
            _fadeOverlay.alpha = to;
        }
    }
}
