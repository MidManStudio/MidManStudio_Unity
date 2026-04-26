// MID_Button.cs
// Generic animated UI button. Zero game-specific dependencies.
//
// ANIMATIONS: scale-pop, move, bounce, pulse, shake, rotate, fade.
// SOUND:      Assign an AudioClip directly, or hook OnClickSound UnityEvent
//             to route through MID_AudioManager.Instance.PlaySFX("click").
// ACTIONS:    Use OnClickAction UnityEvent in inspector for any game logic.
//
// REQUIRES: LeanTween (free on Asset Store)


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
        [SerializeField] private float _animDuration = 0.22f;
        [SerializeField] private float _moveDistance = 10f;
        [SerializeField] private float _bounceHeight = 15f;
        [SerializeField] private float _rotateAmount = 15f;
        [SerializeField]
        private AnimationCurve _curve =
            AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

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

        [Tooltip("Fires so you can route sound through your audio system. " +
                 "e.g. drag MID_AudioManager and call PlaySFX('click').")]
        public UnityEvent OnClickSound;

        #endregion

        #region Private Fields

        private Button _button;
        private RectTransform _rect;
        private LayoutGroup _parentLayout;
        private bool _inLayout;
        private bool _canClick = true;

        private Vector3 _origPosition;
        private Vector3 _origScale;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _button = GetComponent<Button>();
            _rect = GetComponent<RectTransform>();

            _origPosition = _rect.localPosition;
            _origScale = _rect.localScale;

            _parentLayout = GetComponentInParent<LayoutGroup>();
            _inLayout = _parentLayout != null;

            _button.onClick.AddListener(HandleClick);
        }

        private void OnDisable()
        {
            if (_rect == null) return;
            _rect.localPosition = _origPosition;
            _rect.localScale = _origScale;
        }

        #endregion

        #region Public Methods

        public void SetInteractable(bool value) => _button.interactable = value;

        #endregion

        #region Private — Click

        private void HandleClick()
        {
            if (!_canClick) return;

            PlayAnimation();
            PlaySound();
            OnClickAction?.Invoke();

            _canClick = false;
            _button.interactable = false;
            LeanTween.delayedCall(gameObject, _cooldown, () =>
            {
                _canClick = true;
                if (_button != null) _button.interactable = true;
            });
        }

        private void PlaySound()
        {
            // Prioritise direct clip assignment; fall through to event for routing
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

        private void PlayAnimation()
        {
            LeanTween.cancel(_rect.gameObject);

            switch (_animationType)
            {
                case AnimationType.ScalePop: AnimScalePop(); break;
                case AnimationType.MoveLeft: AnimMove(Vector3.left); break;
                case AnimationType.MoveRight: AnimMove(Vector3.right); break;
                case AnimationType.MoveUp: AnimMove(Vector3.up); break;
                case AnimationType.MoveDown: AnimMove(Vector3.down); break;
                case AnimationType.Bounce: AnimBounce(); break;
                case AnimationType.Pulse: AnimPulse(); break;
                case AnimationType.Shake: AnimShake(); break;
                case AnimationType.Rotate: AnimRotate(); break;
                case AnimationType.FadeFlash: AnimFadeFlash(); break;
            }
        }

        private void AnimScalePop()
        {
            Vector3 small = _origScale * 0.8f;
            float half = _animDuration * 0.5f;
            LeanTween.scale(_rect, small, half).setEase(_curve)
                .setOnComplete(() =>
                {
                    LeanTween.scale(_rect, _origScale, half).setEase(_curve);
                    RebuildLayout();
                });
        }

        private void AnimMove(Vector3 direction)
        {
            // Use scale animation inside layout groups
            if (_inLayout) { AnimScalePop(); return; }

            Vector3 target = _origPosition + direction * _moveDistance;
            float half = _animDuration * 0.5f;
            LeanTween.moveLocal(_rect.gameObject, target, half).setEase(_curve)
                .setOnComplete(() =>
                    LeanTween.moveLocal(_rect.gameObject, _origPosition, half).setEase(_curve));
        }

        private void AnimBounce()
        {
            if (_inLayout) { AnimPulse(); return; }

            float third = _animDuration / 3f;
            Vector3 up = _origPosition + Vector3.up * _bounceHeight;
            LeanTween.moveLocal(_rect.gameObject, up, third)
                .setEase(LeanTweenType.easeOutQuad)
                .setOnComplete(() =>
                    LeanTween.moveLocal(_rect.gameObject, _origPosition, third)
                        .setEase(LeanTweenType.easeInQuad)
                        .setOnComplete(() =>
                            LeanTween.moveLocal(_rect.gameObject,
                                    _origPosition + Vector3.up * (_bounceHeight * 0.3f),
                                    third * 0.5f)
                                .setEase(LeanTweenType.easeOutQuad)
                                .setOnComplete(() =>
                                    LeanTween.moveLocal(_rect.gameObject, _origPosition, third * 0.5f)
                                        .setEase(LeanTweenType.easeInQuad))));
        }

        private void AnimPulse()
        {
            Vector3 big = _origScale * 1.2f;
            float half = _animDuration * 0.5f;
            LeanTween.scale(_rect, big, half).setEase(LeanTweenType.easeOutQuad)
                .setOnComplete(() =>
                {
                    LeanTween.scale(_rect, _origScale, half).setEase(LeanTweenType.easeInQuad);
                    RebuildLayout();
                });
        }

        private void AnimShake()
        {
            if (_inLayout)
            {
                // Rotation shake for layout groups
                float sixth = _animDuration / 6f;
                LeanTween.rotateZ(_rect.gameObject, -12f, sixth)
                    .setOnComplete(() =>
                        LeanTween.rotateZ(_rect.gameObject, 12f, sixth * 2f)
                            .setOnComplete(() =>
                                LeanTween.rotateZ(_rect.gameObject, 0f, sixth)));
                return;
            }

            float s = 5f; float fifth = _animDuration / 5f;
            Vector3 p = _origPosition;
            LeanTween.moveLocal(_rect.gameObject, p + new Vector3(s, 0, 0), fifth)
                .setEase(LeanTweenType.easeShake)
                .setOnComplete(() =>
                    LeanTween.moveLocal(_rect.gameObject, p + new Vector3(-s, 0, 0), fifth)
                        .setEase(LeanTweenType.easeShake)
                        .setOnComplete(() =>
                            LeanTween.moveLocal(_rect.gameObject, p + new Vector3(s * 0.5f, 0, 0), fifth)
                                .setEase(LeanTweenType.easeShake)
                                .setOnComplete(() =>
                                    LeanTween.moveLocal(_rect.gameObject, p + new Vector3(-s * 0.5f, 0, 0), fifth)
                                        .setEase(LeanTweenType.easeShake)
                                        .setOnComplete(() =>
                                            LeanTween.moveLocal(_rect.gameObject, p, fifth)
                                                .setEase(LeanTweenType.easeShake)))));
        }

        private void AnimRotate()
        {
            float half = _animDuration * 0.5f;
            LeanTween.rotateZ(_rect.gameObject, _rotateAmount, half).setEase(_curve)
                .setOnComplete(() =>
                    LeanTween.rotateZ(_rect.gameObject, 0f, half).setEase(_curve));
        }

        private void AnimFadeFlash()
        {
            var cg = _rect.GetComponent<CanvasGroup>()
                  ?? _rect.gameObject.AddComponent<CanvasGroup>();
            float half = _animDuration * 0.5f;
            LeanTween.alphaCanvas(cg, 0.4f, half).setEase(_curve)
                .setOnComplete(() =>
                    LeanTween.alphaCanvas(cg, 1f, half).setEase(_curve));
        }

        private void RebuildLayout()
        {
            if (_inLayout && _parentLayout != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(
                    _parentLayout.GetComponent<RectTransform>());
        }

        #endregion
    }
}
