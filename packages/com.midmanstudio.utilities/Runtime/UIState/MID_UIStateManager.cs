// MID_UIStateManager.cs
// Singleton manager for UIStateId state transitions.
// Supports history-based back navigation and panel configuration.
// Game code creates a UIStateTypeProviderSO, generates UIStateId, then uses this.
//
// USAGE:
//   MID_UIStateManager.Instance.ChangeState(UIStateId.MainMenu);
//   MID_UIStateManager.Instance.GoBack();
//   MID_UIStateManager.Instance.OnStateChanged += HandleState;

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState
{
    // ── Panel configuration assigned in inspector ─────────────────────────────

    [Serializable]
    public class UIStatePanelConfig
    {
        [Tooltip("The exact state this config applies to (single flag value).")]
        public UIStateId state;

        [Tooltip("GameObjects to activate when entering this state.")]
        public GameObject[] show;

        [Tooltip("GameObjects to deactivate when entering this state.")]
        public GameObject[] hide;

        public UnityEvent onEnter;
        public UnityEvent onExit;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Singleton state manager for generated UIStateId. Manages transitions,
    /// panel configurations, and back-navigation history.
    /// </summary>
    public class MID_UIStateManager : Singleton<MID_UIStateManager>
    {
        #region Inspector

        [Header("Initial State")]
        [SerializeField] private UIStateId _initialState = UIStateId.None;

        [Header("Panel Configurations")]
        [MID_NamedList]
        [SerializeField] private List<UIStatePanelConfig> _configurations = new();

        [Header("Log")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Events

        /// <summary>Fires whenever state changes. Payload is the new state.</summary>
        public Action<UIStateId> OnStateChanged;

        #endregion

        #region State

        private UIStateId       _currentState = UIStateId.None;
        private Stack<UIStateId> _history      = new();
        private bool            _isGoingBack;

        #endregion

        #region Properties

        public UIStateId CurrentState => _currentState;
        public bool      CanGoBack    => _history.Count > 0;

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();
            Remake(true);
        }

        private void Start()
        {
            if (_initialState != UIStateId.None)
                ChangeState(_initialState);
        }

        #endregion

        #region Public API

        /// <summary>Transition to a new state.</summary>
        public void ChangeState(UIStateId newState)
        {
            if (_currentState == newState) return;

            ExitCurrentState();

            if (!_isGoingBack && _currentState != UIStateId.None)
                _history.Push(_currentState);

            _isGoingBack  = false;
            _currentState = newState;

            EnterNewState(newState);
            OnStateChanged?.Invoke(newState);

            MID_Logger.LogInfo(_logLevel, $"State → {newState}",
                nameof(MID_UIStateManager));
        }

        /// <summary>Return to the previous state in history.</summary>
        public void GoBack()
        {
            if (_history.Count == 0)
            {
                MID_Logger.LogWarning(_logLevel, "No previous state in history.",
                    nameof(MID_UIStateManager));
                return;
            }
            _isGoingBack = true;
            ChangeState(_history.Pop());
        }

        /// <summary>Clear the navigation history stack.</summary>
        public void ClearHistory() => _history.Clear();

        public bool IsInState(UIStateId state) => _currentState == state;

        #endregion

        #region State Transition Helpers

        private void ExitCurrentState()
        {
            var config = FindConfig(_currentState);
            if (config == null) return;

            foreach (var p in config.show)
                if (p) p.SetActive(false);

            config.onExit?.Invoke();
        }

        private void EnterNewState(UIStateId state)
        {
            var config = FindConfig(state);
            if (config == null) return;

            foreach (var p in config.show)
                if (p) p.SetActive(true);

            foreach (var p in config.hide)
                if (p) p.SetActive(false);

            config.onEnter?.Invoke();
        }

        private UIStatePanelConfig FindConfig(UIStateId state) =>
            _configurations.Find(c => c.state == state);

        #endregion
    }
}
