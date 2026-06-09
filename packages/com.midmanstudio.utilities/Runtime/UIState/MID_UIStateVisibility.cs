// packages/com.midmanstudio.utilities/Runtime/UIState/MID_UIStateVisibility.cs
//
// FIX (initial state not applied):
//   _visible defaults to false. When OnEnable fires (context state is 0, None),
//   HandleStateChanged sets shouldShow=false which equals _visible=false → early return.
//   Active-in-scene objects that should be hidden in the initial state were never hidden.
//
//   Fix: _initialised flag. The FIRST call to HandleStateChanged (triggered by OnEnable)
//   bypasses the equality check and force-applies visibility regardless. Subsequent calls
//   use the normal early-return optimisation.
//
//   Flow with fix:
//     1. OnEnable → _initialised=false → HandleStateChanged(0)
//        → shouldShow=false, _initialised=false → skip early return → Hide() called ✓
//     2. UIStateManager.Start → ChangeState(initialState) → fires OnStateChanged
//        → HandleStateChanged(initialState)
//        → shouldShow matches mask → Show() or Hide() with early-return optimisation ✓

using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
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
        private bool          _initialised;   // FIX: forces first call through regardless of state

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

            // FIX: reset _initialised so the first HandleStateChanged call always
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

            // FIX: skip early-return on the first call so initial state is always applied.
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
