
// Merged: was two assets (UIStateContextProviderSO + MID_UIStateContext).
// Now one SO serves as both the code-generation source and the runtime state machine.
//
// SETUP:
//   1. Create via right-click > MidManStudio > Utilities > UI State Context
//   2. Set contextName (PascalCase, no spaces — e.g. "Menu" → generates MenuUIState.cs)
//   3. Add states, then run: MidManStudio > Utilities > UI State Context Generator
//   4. Assign this SO asset to MID_UIStateManager, MID_UIStateVisibility, MID_UIStateButton

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState
{
    [Serializable]
    public class UIStateEntry : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. PascalCase, no spaces.")]
        public string enumName;
        [Tooltip("Optional inline comment written next to the generated enum member.")]
        public string comment;
        public string Name => string.IsNullOrWhiteSpace(enumName) ? "Unnamed State" : enumName;
    }

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

        public int StateCount => states?.Count ?? 0;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fires whenever state changes. Payload is the new raw int state.</summary>
        public event Action<int> OnStateChanged;

        // ── Runtime State — reset on every OnEnable so play mode starts clean ─

        private int        _currentState;
        private Stack<int> _history    = new();
        private bool       _isGoingBack;

        // ── Properties ────────────────────────────────────────────────────────

        public int  CurrentState => _currentState;
        public bool CanGoBack    => _history.Count > 0;
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

        public bool IsInState(int state) => _currentState == state;
        public bool HasFlag(int flag)    => (_currentState & flag) != 0;
    }
}
