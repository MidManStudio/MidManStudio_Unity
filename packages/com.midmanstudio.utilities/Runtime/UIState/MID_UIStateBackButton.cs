// MID_UIStateBackButton.cs
// Simple back-navigation button — calls GoBack() on the assigned context.
// Optionally disables itself when there is no history to go back to.
//
// SETUP:
//   Add to any GameObject with a Button component.
//   Assign the same MID_UIStateContext SO used by the rest of your UI.

using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    [RequireComponent(typeof(Button))]
    public class MID_UIStateBackButton : MonoBehaviour
    {
        [Tooltip("The context this button navigates back in.")]
        [SerializeField] private MID_UIStateContext _context;

        [Tooltip("Disables the button automatically when there is no history to return to.")]
        [SerializeField] private bool _disableWhenNoHistory = true;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private Button _button;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _button = GetComponent<Button>();
            _button.onClick.AddListener(HandleClick);
        }

        private void OnEnable()
        {
            if (_context == null) return;
            _context.OnStateChanged += OnStateChanged;
            RefreshInteractable();
        }

        private void OnDisable()
        {
            if (_context != null)
                _context.OnStateChanged -= OnStateChanged;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Assign a context at runtime and subscribe to its events.</summary>
        public void SetContext(MID_UIStateContext context)
        {
            if (_context != null)
                _context.OnStateChanged -= OnStateChanged;

            _context = context;

            if (_context != null)
                _context.OnStateChanged += OnStateChanged;

            RefreshInteractable();
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

        private void OnStateChanged(int _) => RefreshInteractable();

        private void RefreshInteractable()
        {
            if (!_disableWhenNoHistory || _button == null) return;
            _button.interactable = _context != null && _context.CanGoBack;
        }
    }
}
