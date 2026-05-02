// MID_UIStateVisibility.cs
// Shows this UI element when the current UIStateId matches any of the flagged states.
// Replace MenuStateVisibilityUI — no game-specific code, works with any generated UIStateId.
//
// USAGE:
//   Attach to any UI panel alongside MID_UIElement.
//   In the inspector, set showWhen to one or more UIStateId flags using the custom drawer.
//   The panel is shown when (currentState & showWhen) != 0.

using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    /// <summary>
    /// Shows/hides this UI element based on UIStateId flags.
    /// Automatically registers with MID_UIStateManager on Start.
    /// </summary>
    [RequireComponent(typeof(MID_UIElement))]
    public class MID_UIStateVisibility : MonoBehaviour
    {
        [Tooltip("Show this element when the current state contains ANY of these flags.")]
        [SerializeField] private UIStateId _showWhen;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private MID_UIElement _element;
        private bool          _isVisible;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _element = GetComponent<MID_UIElement>();
        }

        private void Start()
        {
            if (MID_UIStateManager.Instance == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Info,
                    $"MID_UIStateManager not found on {gameObject.name}.",
                    nameof(MID_UIStateVisibility));
                return;
            }

            MID_UIStateManager.Instance.OnStateChanged += HandleStateChanged;
            // Initialise with current state
            HandleStateChanged(MID_UIStateManager.Instance.CurrentState);
        }

        private void OnDestroy()
        {
            if (MID_UIStateManager.Instance != null)
                MID_UIStateManager.Instance.OnStateChanged -= HandleStateChanged;
        }

        // ── Logic ─────────────────────────────────────────────────────────────

        private void HandleStateChanged(UIStateId newState)
        {
            // Show if the current state has ANY of the flagged bits
            bool shouldShow = _showWhen != UIStateId.None &&
                              (newState & _showWhen) != 0;

            if (shouldShow == _isVisible) return;

            _isVisible = shouldShow;

            if (shouldShow)
            {
                _element.Show();
                MID_Logger.LogDebug(_logLevel, $"{name} shown for state {newState}.",
                    nameof(MID_UIStateVisibility));
            }
            else
            {
                _element.Hide();
                MID_Logger.LogDebug(_logLevel, $"{name} hidden for state {newState}.",
                    nameof(MID_UIStateVisibility));
            }
        }

        // ── Editor helpers ────────────────────────────────────────────────────

        /// <summary>The flags this element responds to. Read-only at runtime.</summary>
        public UIStateId ShowWhen => _showWhen;
    }
}
