// MID_Button.cs
// Generic animated UI button. Zero game-specific dependencies.
// Animations implemented with coroutines — no external tween library required.
//
// SOUND:  Assign an AudioClip directly, or hook OnClickSound UnityEvent
//         to route through MID_AudioManager.Instance.PlaySFX("click").
// ACTIONS: Use OnClickAction UnityEvent in inspector for any game logic.

using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MidManStudio.Core.UI
{
    [RequireComponent(typeof(Button))]
    public class MID_Button : MonoBehaviour
    {
        #region Enums

        public enum AnimationType
        {
            ScalePop,
            MoveLeft, MoveRight, MoveUp, MoveDown,
            Bounce,
            Pulse,
            Shake,
            Rotate,
            FadeFlash
        }

        #endregion

        #region Serialized Fields

        [Header("Animation")]
        [SerializeField] private AnimationType _animationType = AnimationType.ScalePop;
        [SerializeField] private float _animDuration  = 0.22f;
        [SerializeField] private float _moveDistance  = 10f;
        [SerializeField] private float _bounceHeight  = 15f;
        [SerializeField] private float _rotateAmount  = 15f;

        [Header("Rate Limiting")]
        [SerializeField] private float _cooldown = 0.3f;

        [Header("Sound (optional)")]
        [Tooltip("Assign a clip to play on click, or leave null and handle via OnClickSound.")]
        [SerializeField] private AudioClip _clickSound;
        [Range(0f, 1f)]
        [SerializeField] private float _soundVolume = 1f;

        [Header("Events")]
        [Tooltip("Fires on every valid click — wire game logic here.")]
        public UnityEvent OnClickAction;

        [Tooltip("Fires so you can route sound through your audio system.")]
        public UnityEvent OnClickSound;

        #endregion

        #region Private Fields

        private Button        _button;
        private RectTransform _rect;
        private LayoutGroup   _parentLayout;
        private bool          _inLayout;
        private bool          _canClick = true;

        private Vector3 _origPosition;
        private Vector3 _origScale;
        private float   _origRotation;

        private Coroutine _animCoroutine;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _button = GetComponent<Button>();
            _rect   = GetComponent<RectTransform>();

            _origPosition = _rect.localPosition;
            _origScale    = _rect.localScale;
            _origRotation = _rect.localEulerAngles.z;

            _parentLayout = GetComponentInParent<LayoutGroup>();
            _inLayout     = _parentLayout != null;

            _button.onClick.AddListener(HandleClick);
        }

        private void OnDisable()
        {
            if (_rect == null) return;
            if (_animCoroutine != null) { StopCoroutine(_animCoroutine); _animCoroutine = null; }
            _rect.localPosition    = _origPosition;
            _rect.localScale       = _origScale;
            _rect.localEulerAngles = new Vector3(0f, 0f, _origRotation);
        }

        #endregion

        #region Public Methods

        public void SetInteractable(bool value) => _button.interactable = value;

        #endregion

        #region Private — Click

        private void HandleClick()
        {
            if (!_canClick) return;

            if (_animCoroutine != null) StopCoroutine(_animCoroutine);
            _animCoroutine = StartCoroutine(PlayAnimation());

            PlaySound();
            OnClickAction?.Invoke();

            _canClick             = false;
            _button.interactable  = false;
            StartCoroutine(ResetCooldown());
        }

        private IEnumerator ResetCooldown()
        {
            yield return new WaitForSeconds(_cooldown);
            _canClick            = true;
            if (_button != null) _button.interactable = true;
        }

        private void PlaySound()
        {
            if (_clickSound != null)
            {
                var cam = Camera.main;
                if (cam != null)
                    AudioSource.PlayClipAtPoint(_clickSound,
                        cam.transform.position, _soundVolume);
            }
            else
            {
                OnClickSound?.Invoke();
            }
        }

        #endregion

        #region Private — Animations

        private IEnumerator PlayAnimation()
        {
            switch (_animationType)
            {
                case AnimationType.ScalePop:  yield return StartCoroutine(AnimScalePop());  break;
                case AnimationType.MoveLeft:  yield return StartCoroutine(AnimMove(Vector3.left));  break;
                case AnimationType.MoveRight: yield return StartCoroutine(AnimMove(Vector3.right)); break;
                case AnimationType.MoveUp:    yield return StartCoroutine(AnimMove(Vector3.up));    break;
                case AnimationType.MoveDown:  yield return StartCoroutine(AnimMove(Vector3.down));  break;
                case AnimationType.Bounce:    yield return StartCoroutine(_inLayout ? AnimPulse() : AnimBounce()); break;
                case AnimationType.Pulse:     yield return StartCoroutine(AnimPulse());     break;
                case AnimationType.Shake:     yield return StartCoroutine(AnimShake());     break;
                case AnimationType.Rotate:    yield return StartCoroutine(AnimRotate());    break;
                case AnimationType.FadeFlash: yield return StartCoroutine(AnimFadeFlash()); break;
            }
        }

        // ── Scale pop ─────────────────────────────────────────────────────────

        private IEnumerator AnimScalePop()
        {
            Vector3 small = _origScale * 0.8f;
            float   half  = _animDuration * 0.5f;

            yield return StartCoroutine(ScaleTo(small, half));
            RebuildLayout();
            yield return StartCoroutine(ScaleTo(_origScale, half));
            RebuildLayout();
        }

        // ── Move ──────────────────────────────────────────────────────────────

        private IEnumerator AnimMove(Vector3 direction)
        {
            if (_inLayout) { yield return StartCoroutine(AnimScalePop()); yield break; }

            Vector3 target = _origPosition + direction * _moveDistance;
            float   half   = _animDuration * 0.5f;

            yield return StartCoroutine(MoveTo(target, half));
            yield return StartCoroutine(MoveTo(_origPosition, half));
        }

        // ── Bounce ────────────────────────────────────────────────────────────

        private IEnumerator AnimBounce()
        {
            float   third = _animDuration / 3f;
            Vector3 up    = _origPosition + Vector3.up * _bounceHeight;

            yield return StartCoroutine(MoveTo(up, third));
            yield return StartCoroutine(MoveTo(_origPosition, third));
            yield return StartCoroutine(MoveTo(_origPosition + Vector3.up * (_bounceHeight * 0.3f), third * 0.5f));
            yield return StartCoroutine(MoveTo(_origPosition, third * 0.5f));
        }

        // ── Pulse ─────────────────────────────────────────────────────────────

        private IEnumerator AnimPulse()
        {
            Vector3 big  = _origScale * 1.2f;
            float   half = _animDuration * 0.5f;

            yield return StartCoroutine(ScaleTo(big, half));
            RebuildLayout();
            yield return StartCoroutine(ScaleTo(_origScale, half));
            RebuildLayout();
        }

        // ── Shake ─────────────────────────────────────────────────────────────

        private IEnumerator AnimShake()
        {
            if (_inLayout)
            {
                // Rotation shake for layout groups
                float sixth = _animDuration / 6f;
                yield return StartCoroutine(RotateTo(-12f, sixth));
                yield return StartCoroutine(RotateTo(12f,  sixth * 2f));
                yield return StartCoroutine(RotateTo(0f,   sixth));
                yield break;
            }

            float s     = 5f;
            float fifth = _animDuration / 5f;
            yield return StartCoroutine(MoveTo(_origPosition + new Vector3(s,        0, 0), fifth));
            yield return StartCoroutine(MoveTo(_origPosition + new Vector3(-s,       0, 0), fifth));
            yield return StartCoroutine(MoveTo(_origPosition + new Vector3(s * 0.5f, 0, 0), fifth));
            yield return StartCoroutine(MoveTo(_origPosition + new Vector3(-s * 0.5f,0, 0), fifth));
            yield return StartCoroutine(MoveTo(_origPosition,                               fifth));
        }

        // ── Rotate ────────────────────────────────────────────────────────────

        private IEnumerator AnimRotate()
        {
            float half = _animDuration * 0.5f;
            yield return StartCoroutine(RotateTo(_rotateAmount, half));
            yield return StartCoroutine(RotateTo(0f, half));
        }

        // ── Fade flash ────────────────────────────────────────────────────────

        private IEnumerator AnimFadeFlash()
        {
            var cg   = _rect.GetComponent<CanvasGroup>()
                    ?? _rect.gameObject.AddComponent<CanvasGroup>();
            float half = _animDuration * 0.5f;

            yield return StartCoroutine(AlphaTo(cg, 0.4f, half));
            yield return StartCoroutine(AlphaTo(cg, 1f,   half));
        }

        #endregion

        #region Private — Tween Helpers

        private IEnumerator ScaleTo(Vector3 target, float duration)
        {
            Vector3 start   = _rect.localScale;
            float   elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed           += Time.unscaledDeltaTime;
                _rect.localScale   = Vector3.Lerp(start, target,
                                        EaseInOut(Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            _rect.localScale = target;
        }

        private IEnumerator MoveTo(Vector3 target, float duration)
        {
            Vector3 start   = _rect.localPosition;
            float   elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed              += Time.unscaledDeltaTime;
                _rect.localPosition   = Vector3.Lerp(start, target,
                                            EaseInOut(Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            _rect.localPosition = target;
        }

        private IEnumerator RotateTo(float targetZ, float duration)
        {
            float startZ  = _rect.localEulerAngles.z;
            // Normalize to signed range so lerp is sensible
            if (startZ > 180f) startZ -= 360f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float z  = Mathf.Lerp(startZ, targetZ,
                               EaseInOut(Mathf.Clamp01(elapsed / duration)));
                _rect.localEulerAngles = new Vector3(0f, 0f, z);
                yield return null;
            }
            _rect.localEulerAngles = new Vector3(0f, 0f, targetZ);
        }

        private IEnumerator AlphaTo(CanvasGroup cg, float targetAlpha, float duration)
        {
            float startAlpha = cg.alpha;
            float elapsed    = 0f;
            while (elapsed < duration)
            {
                elapsed   += Time.unscaledDeltaTime;
                cg.alpha   = Mathf.Lerp(startAlpha, targetAlpha,
                                 EaseInOut(Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            cg.alpha = targetAlpha;
        }

        private static float EaseInOut(float t) =>
            t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t;

        private void RebuildLayout()
        {
            if (_inLayout && _parentLayout != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(
                    _parentLayout.GetComponent<RectTransform>());
        }

        #endregion
    }
}
