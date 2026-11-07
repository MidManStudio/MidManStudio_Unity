// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/uistate.md, section "MID_UIElement.cs"
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    /// <summary>
    /// Base class for managed UI panels. Shows and hides the panel through its CanvasGroup (alpha, interactable,
    /// blocksRaycasts); the GameObject stays active. Raises the <c>onVisibilityChanged</c> UnityEvent on itself and
    /// on every MID_UIElement below it (all descendants, collected in <c>Start</c>). Attach it to a panel and call
    /// <c>Show()</c>, <c>Hide()</c> or <c>Toggle()</c>. A subclass that overrides <c>Start</c> must call
    /// <c>base.Start()</c>, or the descendants are not notified.
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

        /// <summary>True after <c>Show</c>, false after <c>Hide</c>. It starts false, even when the CanvasGroup is visible in the scene.</summary>
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
            // Collect all MID_UIElement descendants (not self), including inactive ones
            foreach (var c in GetComponentsInChildren<MID_UIElement>(true))
                if (c != this) _children.Add(c);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Calls <c>Hide</c> when showing, otherwise <c>Show</c>.</summary>
        public virtual void Toggle()
        {
            if (_isShowing) Hide();
            else            Show();
        }

        /// <summary>Makes the panel visible and raises the visibility event on itself and its descendants.</summary>
        public virtual void Show() => Show(propagateToChildren: true);

        /// <summary>Makes the panel visible. When <paramref name="propagateToChildren"/> is false the descendants are not notified.</summary>
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

        /// <summary>Hides the panel (alpha 0, not interactable, no raycasts) and notifies its descendants.</summary>
        public virtual void Hide() => Hide(targetAlpha: 0f);

        /// <summary>Hides the panel with a chosen alpha. Interaction and raycasts are still switched off. Descendants are always notified.</summary>
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
