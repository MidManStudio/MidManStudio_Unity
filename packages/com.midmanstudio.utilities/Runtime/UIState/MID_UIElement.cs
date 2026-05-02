// MID_UIElement.cs
// Base UI element with CanvasGroup-based show/hide.
// Propagates visibility events to direct children.
// Replaces MenuManager and GenericUIElement.
//
// USAGE:
//   Attach to any UI panel. Call Show() / Hide() / Toggle().
//   Wire onVisibilityChanged UnityEvent in inspector for additional hooks.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    /// <summary>
    /// Base class for all managed UI panels. Provides CanvasGroup visibility control.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class MID_UIElement : MonoBehaviour
    {
        [SerializeField] private   UnityEvent<bool> _onVisibilityChanged;
        [SerializeField] protected MID_LogLevel     _logLevel = MID_LogLevel.None;

        protected bool _isShowing;

        private CanvasGroup           _canvasGroup;
        private List<MID_UIElement>   _children = new();

        // ── Properties ────────────────────────────────────────────────────────

        public bool IsShowing => _isShowing;

        protected CanvasGroup CG
        {
            get
            {
                if (_canvasGroup != null) return _canvasGroup;
                return _canvasGroup = GetComponent<CanvasGroup>();
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected virtual void Start()
        {
            // Collect immediate MID_UIElement children (not self)
            foreach (var c in GetComponentsInChildren<MID_UIElement>(true))
                if (c != this) _children.Add(c);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public virtual void Toggle()
        {
            if (_isShowing) Hide();
            else            Show();
        }

        public virtual void Show() => Show(propagateToChildren: true);

        public virtual void Show(bool propagateToChildren)
        {
            CG.alpha          = 1f;
            CG.interactable   = true;
            CG.blocksRaycasts = true;
            _isShowing        = true;

            _onVisibilityChanged?.Invoke(true);

            if (propagateToChildren)
                foreach (var child in _children)
                    child._onVisibilityChanged?.Invoke(true);

            MID_Logger.LogDebug(_logLevel, $"{name} shown.", nameof(MID_UIElement));
        }

        public virtual void Hide() => Hide(targetAlpha: 0f);

        public virtual void Hide(float targetAlpha)
        {
            CG.alpha          = targetAlpha;
            CG.interactable   = false;
            CG.blocksRaycasts = false;
            _isShowing        = false;

            _onVisibilityChanged?.Invoke(false);

            foreach (var child in _children)
                child._onVisibilityChanged?.Invoke(false);

            MID_Logger.LogDebug(_logLevel, $"{name} hidden.", nameof(MID_UIElement));
        }
    }
}
