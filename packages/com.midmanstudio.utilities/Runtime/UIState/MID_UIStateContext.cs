// MID_UIStateContext.cs
// ScriptableObject that IS a UI state machine for one context (Menu, Lobby, etc.).
// Components hold a reference to this asset — no singleton needed.
// Works across scenes because SO assets are persistent in memory.
//
// SETUP:
//   1. Create a UIStateContextProviderSO, add states, run the generator.
//   2. Create a MID_UIStateContext asset (one per context).
//   3. Set its contextTypeName to match the generated enum (e.g. "MenuUIState").
//   4. Assign this asset to MID_UIStateVisibility / MID_UIStateButton components.
//
// USAGE (code):
//   menuContext.ChangeState((int)MenuUIState.Settings);
//   menuContext.GoBack();
//   menuContext.OnStateChanged += HandleMenuState;

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    
[CreateAssetMenu(fileName="UIStateContext",
    menuName="MidManStudio/Utilities/UI State Context", order=190)]
    public class MID_UIStateContext : ScriptableObject
    {
        #region Inspector

        [Tooltip("Friendly name shown in the editor. e.g. Menu, Lobby, HUD.")]
        public string contextDisplayName = "Menu";

        [Tooltip("Full type name of the generated enum for this context.\n" +
                 "e.g. MidManStudio.Core.UIState.MenuUIState\n" +
                 "The editor uses this to show flag checkboxes.")]
        public string enumTypeName = "MidManStudio.Core.UIState.MenuUIState";

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        /// <summary>Fires when state changes. Payload is the raw int flag value.</summary>
        public Action<int> OnStateChanged;

        #endregion

        #region Runtime State

        // ScriptableObjects persist state across scene loads in the same play session.
        // Reset() is called OnEnable to clear between runs.
        private int            _currentState;
        private Stack<int>     _history = new();
        private bool           _isGoingBack;

        #endregion

        #region Properties

        public int  CurrentState => _currentState;
        public bool CanGoBack    => _history.Count > 0;

        #endregion

        #region Unity SO Lifecycle

        private void OnEnable()
        {
            // Reset runtime state when SO is loaded (entering play mode / domain reload)
            _currentState = 0;
            _history      = new Stack<int>();
            _isGoingBack  = false;
            OnStateChanged = null;
        }

        #endregion

        #region Public API

        /// <summary>Transition to a new state by raw int (cast from your generated enum).</summary>
        public void ChangeState(int newState)
        {
            if (_currentState == newState) return;

            if (!_isGoingBack && _currentState != 0)
                _history.Push(_currentState);

            _isGoingBack  = false;
            _currentState = newState;

            OnStateChanged?.Invoke(newState);

            MID_Logger.LogInfo(_logLevel,
                $"[{contextDisplayName}] State → {newState}",
                nameof(MID_UIStateContext));
        }

        /// <summary>Return to the previous state.</summary>
        public void GoBack()
        {
            if (_history.Count == 0)
            {
                MID_Logger.LogWarning(_logLevel, $"[{contextDisplayName}] No history.",
                    nameof(MID_UIStateContext));
                return;
            }
            _isGoingBack = true;
            ChangeState(_history.Pop());
        }

        public void ClearHistory() => _history.Clear();

        public bool IsInState(int state) => _currentState == state;

        /// <summary>Returns true if the current state contains the given flag bit(s).</summary>
        public bool HasFlag(int flag) => (_currentState & flag) != 0;

        #endregion
    }
}
