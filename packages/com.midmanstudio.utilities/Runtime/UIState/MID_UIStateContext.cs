// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/uistate.md, section "MID_UIStateContext.cs"
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState
{
    /// <summary>One state of a context: the enum member name and an optional comment written next to it.</summary>
    [Serializable]
    public class UIStateEntry : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. PascalCase, no spaces.")]
        public string enumName;
        [Tooltip("Optional inline comment written next to the generated enum member.")]
        public string comment;
        /// <summary>Label in inspector lists: the enum name, or "Unnamed State" when blank.</summary>
        public string Name => string.IsNullOrWhiteSpace(enumName) ? "Unnamed State" : enumName;
    }

    /// <summary>
    /// ScriptableObject that is both the definition and the run time state machine of one UI context. It lists the
    /// state names that the UI State Context Generator turns into a [Flags] enum (<c>{contextName}UIState</c>), and it
    /// keeps the current state (an int that holds enum flags), a back history and the <see cref="OnStateChanged"/> event.
    /// Create it with Create > MidManStudio > Utilities > UI State Context, set <c>contextName</c> (PascalCase, no
    /// spaces), add states, run MidManStudio > Utilities > UI State Context Generator, then assign the asset to
    /// <see cref="MID_UIStateManager"/>, <see cref="MID_UIStateVisibility"/> and <see cref="MID_UIStateButton"/>.
    /// The run time state is reset when the asset is enabled.
    /// </summary>
    [CreateAssetMenu(fileName = "UIStateContext",
        menuName = "MidManStudio/Utilities/UI State Context", order = 190)]
    public class MID_UIStateContext : ScriptableObject
    {
        // ── Generator / Identity ──────────────────────────────────────────────

        [Header("Identity")]
        [Tooltip("Used as display name AND for code generation.\n" +
                 "PascalCase, no spaces. e.g. 'Menu' generates MenuUIState.cs.")]
        public string contextName = "Menu";

        [Tooltip("Reverse-domain package ID. Used by the generator.")]
        public string packageId = "com.mygame";

        [Header("States  (add states here, then run the generator)")]
        [MID_NamedList]
        public List<UIStateEntry> states = new();

        [Header("Log")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        // ── Computed properties ───────────────────────────────────────────────

        /// <summary>Full type name of the generated enum. Auto-computed from contextName.</summary>
        public string enumTypeName => $"MidManStudio.Core.UIState.{contextName}UIState";

        /// <summary>Friendly display string (same as contextName).</summary>
        public string contextDisplayName => contextName;

        /// <summary>Number of entries in the state list.</summary>
        public int StateCount => states?.Count ?? 0;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fires whenever state changes. Payload is the new raw int state.</summary>
        public event Action<int> OnStateChanged;

        // ── Runtime State — reset on every OnEnable so play mode starts clean ─

        private int        _currentState;
        private Stack<int> _history    = new();
        private bool       _isGoingBack;

        // ── Properties ────────────────────────────────────────────────────────

        /// <summary>The current state as a raw enum value. 0 means None.</summary>
        public int  CurrentState => _currentState;
        /// <summary>True when the history holds a state to go back to.</summary>
        public bool CanGoBack    => _history.Count > 0;
        /// <summary>Number of states in the back history.</summary>
        public int  HistoryDepth => _history.Count;

        // ── Unity SO Lifecycle ────────────────────────────────────────────────

        private void OnEnable()
        {
            // ScriptableObjects persist across scene loads in the same play session.
            // Reset runtime state here so each Play session starts fresh.
            _currentState  = 0;
            _history       = new Stack<int>();
            _isGoingBack   = false;
            OnStateChanged = null;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Transition to a new state.
        /// Pushes the current state to history unless we are navigating back.
        /// </summary>
        public void ChangeState(int newState)
        {
            if (_currentState == newState) return;

            // Only push to history when NOT going back — prevents back-navigation loops.
            if (!_isGoingBack && _currentState != 0)
                _history.Push(_currentState);

            _isGoingBack  = false;
            _currentState = newState;
            OnStateChanged?.Invoke(newState);

            MID_Logger.LogInfo(_logLevel,
                $"[{contextName}] → {newState}  history depth={_history.Count}",
                nameof(MID_UIStateContext));
        }

        /// <summary>
        /// Return to the previous state without adding the current state back to history.
        /// </summary>
        public void GoBack()
        {
            if (_history.Count == 0)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"[{contextName}] GoBack called with empty history.",
                    nameof(MID_UIStateContext));
                return;
            }

            _isGoingBack = true;
            int previous = _history.Pop();

            MID_Logger.LogInfo(_logLevel,
                $"[{contextName}] GoBack → {previous}  remaining={_history.Count}",
                nameof(MID_UIStateContext));

            ChangeState(previous);
        }

        /// <summary>Clear the navigation history stack.</summary>
        public void ClearHistory()
        {
            _history.Clear();
            MID_Logger.LogDebug(_logLevel, $"[{contextName}] History cleared.",
                nameof(MID_UIStateContext));
        }

        /// <summary>True when the current state equals <paramref name="state"/> exactly.</summary>
        public bool IsInState(int state) => _currentState == state;
        /// <summary>True when the current state contains any bit of <paramref name="flag"/>.</summary>
        public bool HasFlag(int flag)    => (_currentState & flag) != 0;
    }
}
