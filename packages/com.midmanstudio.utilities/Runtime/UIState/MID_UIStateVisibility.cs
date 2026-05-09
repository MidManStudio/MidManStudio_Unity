
// No changes to runtime logic — multi-object editing is handled in the editor class.

using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    [RequireComponent(typeof(MID_UIElement))]
    public class MID_UIStateVisibility : MonoBehaviour
    {
        [Tooltip("Which context this element belongs to.")]
        [SerializeField] private MID_UIStateContext _context;

        [Tooltip("Show when the context state contains ANY of these flags.")]
        [SerializeField] private int _showWhenMask;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private MID_UIElement _element;
        private bool          _visible;

        public MID_UIStateContext Context      => _context;
        public int                ShowWhenMask => _showWhenMask;

        private void Awake()
        {
            _element = GetComponent<MID_UIElement>();
        }

        private void OnEnable()
        {
            if (_context == null) return;
            _context.OnStateChanged += HandleStateChanged;
            HandleStateChanged(_context.CurrentState);
        }

        private void OnDisable()
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;
        }

        private void HandleStateChanged(int newState)
        {
            bool shouldShow = _showWhenMask != 0 && (newState & _showWhenMask) != 0;
            if (shouldShow == _visible) return;
            _visible = shouldShow;

            if (shouldShow) _element.Show();
            else            _element.Hide();

            MID_Logger.LogDebug(_logLevel,
                $"{name} {(shouldShow ? "shown" : "hidden")} " +
                $"(state={newState} mask={_showWhenMask})",
                nameof(MID_UIStateVisibility));
        }
    }
}
