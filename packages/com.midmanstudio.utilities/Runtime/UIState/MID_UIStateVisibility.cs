// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/uistate.md, section "MID_UIStateVisibility.cs"
// ============================================================================

using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    /// <summary>
    /// Shows or hides the <see cref="MID_UIElement"/> on this GameObject depending on a context state. The element is
    /// shown when the state contains any flag of the mask. A mask of 0 means always hidden. The first state is
    /// applied every time the component is enabled, even when it matches the stored value.
    /// </summary>
    [RequireComponent(typeof(MID_UIElement))]
    public class MID_UIStateVisibility : MonoBehaviour
    {
        [Tooltip("Which context this element belongs to.")]
        [SerializeField] private MID_UIStateContext _context;

        [Tooltip("Show when the context state contains ANY of these flags.\n" +
                 "Leave 0 to always hide (which is rarely useful — consider removing the component).")]
        [SerializeField] private int _showWhenMask;

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private MID_UIElement _element;
        private bool          _visible;
        private bool          _initialised;   // false until the first state is applied after enable

        /// <summary>The context this element follows.</summary>
        public MID_UIStateContext Context      => _context;
        /// <summary>The flags that show the element (any one of them is enough).</summary>
        public int                ShowWhenMask => _showWhenMask;

        private void Awake()
        {
            _element = GetComponent<MID_UIElement>();
        }

        private void OnEnable()
        {
            if (_context == null) return;

            _context.OnStateChanged += HandleStateChanged;

            //  reset _initialised so the first HandleStateChanged call always
            // force-applies visibility, even if shouldShow equals the stale _visible value.
            _initialised = false;
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

            // skip early-return on the first call so initial state is always applied.
            // Subsequent calls use the optimisation normally.
            if (shouldShow == _visible && _initialised) return;

            _initialised = true;
            _visible     = shouldShow;

            if (shouldShow) _element.Show();
            else            _element.Hide();

            MID_Logger.LogDebug(_logLevel,
                $"{name} {(shouldShow ? "shown" : "hidden")} " +
                $"(state={newState} mask={_showWhenMask})",
                nameof(MID_UIStateVisibility));
        }
    }
}
