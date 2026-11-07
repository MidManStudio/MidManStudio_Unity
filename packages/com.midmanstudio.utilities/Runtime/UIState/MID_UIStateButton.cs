// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/uistate.md, section "MID_UIStateButton.cs"
// ============================================================================

using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    /// <summary>
    /// Moves a <see cref="MID_UIStateContext"/> to one target state when its Button is clicked. With Disable When
    /// Active on, the button is not interactable while the context is exactly in the target state (an exact
    /// compare, so it never disables while several flags are set). The inspector shows named states.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class MID_UIStateButton : MonoBehaviour
    {
        [Tooltip("Which context this button drives.")]
        [SerializeField] private MID_UIStateContext _context;

        [Tooltip("The state to transition to. Use the custom inspector for enum names.")]
        [SerializeField] private int _targetStateMask;

        [SerializeField] private bool         _disableWhenActive = true;
        [SerializeField] private MID_LogLevel _logLevel          = MID_LogLevel.None;

        private Button _button;

        /// <summary>The context this button drives.</summary>
        public MID_UIStateContext Context         => _context;
        /// <summary>The state this button moves the context to (a raw value of the generated enum).</summary>
        public int                TargetStateMask => _targetStateMask;

        private void Awake()
        {
            _button = GetComponent<Button>();
            _button.onClick.AddListener(OnClick);
        }

        private void OnEnable()
        {
            if (_context == null) return;
            _context.OnStateChanged += UpdateInteractable;
            UpdateInteractable(_context.CurrentState);
        }

        private void OnDisable()
        {
            if (_context != null)
                _context.OnStateChanged -= UpdateInteractable;
        }

        private void OnClick()
        {
            if (_context == null) return;
            MID_Logger.LogDebug(_logLevel, $"Button → {_targetStateMask}.",
                nameof(MID_UIStateButton));
            _context.ChangeState(_targetStateMask);
        }

        private void UpdateInteractable(int state)
        {
            if (!_disableWhenActive) return;
            _button.interactable = state != _targetStateMask;
        }
    }
}
