
// Combines back navigation AND state-based visibility in one component.
// Requires MID_UIElement (CanvasGroup) on the same GameObject.
//
// SETUP:
//   1. Add to a Button GameObject that also has MID_UIElement
//   2. Assign the MID_UIStateContext SO
//   3. Use the "Visible In States" checkboxes to choose when the button shows
//      (leave mask = 0 to always show regardless of state)
//   4. "Disable When No History" will additionally grey out the button
//      when there is nothing to go back to

using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    [RequireComponent(typeof(Button))]
    [RequireComponent(typeof(MID_UIElement))]
    public class MID_UIStateBackButton : MonoBehaviour
    {
        [Tooltip("The context this button navigates back in.")]
        [SerializeField] private MID_UIStateContext _context;

        [Header("Visibility")]
        [Tooltip("Show this button only when the context state contains ANY of these flags.\n" +
                 "Set to 0 (no flags selected) to always show regardless of state.")]
        [SerializeField] private int _showWhenMask;

        [Header("Interactability")]
        [Tooltip("Disables (greyed out) the button when there is no history to return to.")]
        [SerializeField] private bool _disableWhenNoHistory = true;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private Button       _button;
        private MID_UIElement _element;
        private bool          _isVisible;

        // ── Properties ────────────────────────────────────────────────────────

        public MID_UIStateContext Context      => _context;
        public int                ShowWhenMask => _showWhenMask;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _button  = GetComponent<Button>();
            _element = GetComponent<MID_UIElement>();
            _button.onClick.AddListener(HandleClick);
        }

        private void OnEnable()
        {
            if (_context == null) return;
            _context.OnStateChanged += HandleStateChanged;

            // Initialise immediately — don't wait for the first state change
            HandleStateChanged(_context.CurrentState);
        }

        private void OnDisable()
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Swap the context at runtime.</summary>
        public void SetContext(MID_UIStateContext context)
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;

            _context = context;

            if (_context != null)
            {
                _context.OnStateChanged += HandleStateChanged;
                HandleStateChanged(_context.CurrentState);
            }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void HandleClick()
        {
            if (_context == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    "No context assigned — cannot go back.",
                    nameof(MID_UIStateBackButton));
                return;
            }

            if (!_context.CanGoBack)
            {
                MID_Logger.LogDebug(_logLevel,
                    "No history — GoBack ignored.",
                    nameof(MID_UIStateBackButton));
                return;
            }

            _context.GoBack();
        }

        private void HandleStateChanged(int newState)
        {
            RefreshVisibility(newState);
            RefreshInteractable();
        }

        private void RefreshVisibility(int state)
        {
            // showWhenMask == 0 means "always visible regardless of state"
            bool shouldShow = _showWhenMask == 0 || (state & _showWhenMask) != 0;

            if (shouldShow == _isVisible) return;
            _isVisible = shouldShow;

            if (shouldShow) _element.Show();
            else            _element.Hide();

            MID_Logger.LogDebug(_logLevel,
                $"BackButton {(shouldShow ? "shown" : "hidden")} " +
                $"(state={state} mask={_showWhenMask})",
                nameof(MID_UIStateBackButton));
        }

        private void RefreshInteractable()
        {
            if (!_disableWhenNoHistory || _button == null) return;
            _button.interactable = _context != null && _context.CanGoBack;
        }
    }
}
