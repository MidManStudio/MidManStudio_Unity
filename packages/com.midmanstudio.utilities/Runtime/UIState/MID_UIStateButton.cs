// MID_UIStateButton.cs
// Button that transitions a MID_UIStateContext to a target state.
// Assign the context SO and target state raw int, or use the custom editor
// which shows an enum dropdown based on the context's generated enum type.

using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
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

        public MID_UIStateContext Context        => _context;
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
