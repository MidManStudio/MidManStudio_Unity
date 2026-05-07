// MID_UIStateManager.cs
// Singleton panel manager for a single MID_UIStateContext.
// Drives GameObject show/hide and UnityEvent callbacks on state transitions.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState
{
    [Serializable]
    public class UIStatePanelConfig : IArrayElementTitle
    {
        [Tooltip("Raw int value of the generated enum member for this state.\n" +
                 "The custom inspector shows named dropdowns when a context is assigned.")]
        public int stateMask;

        [Tooltip("Inspector label only.")]
        public string displayName;

        [Tooltip("GameObjects to activate when entering this state.")]
        public GameObject[] show;

        [Tooltip("GameObjects to deactivate when entering this state.")]
        public GameObject[] hide;

        public UnityEvent onEnter;
        public UnityEvent onExit;

        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            stateMask != 0                           ? $"State_{stateMask}" :
                                                       "None";
    }

    public class MID_UIStateManager : Singleton<MID_UIStateManager>
    {
        #region Inspector

        [SerializeField] private MID_UIStateContext _context;

        [Header("Initial State  (raw int — cast from your generated enum)")]
        [SerializeField] private int _initialState = 0;

        [Header("Panel Configurations")]
        [MID_NamedList]
        [SerializeField] private List<UIStatePanelConfig> _configurations = new();

        [Header("Log")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Public Events

        /// <summary>Fires whenever the managed context changes state. Payload = new raw int state.</summary>
        public Action<int> OnStateChanged;

        #endregion

        #region Properties

        public MID_UIStateContext Context     => _context;
        public int  CurrentState             => _context != null ? _context.CurrentState : 0;
        public bool CanGoBack                => _context != null && _context.CanGoBack;

        #endregion

        #region Lifecycle

        protected override void Awake()
        {
            base.Awake();
            Remake(true);
        }

        private void OnEnable()
        {
            if (_context != null)
                _context.OnStateChanged += HandleStateChanged;
        }

        private void OnDisable()
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;
        }

        private void Start()
        {
            if (_context == null)
            {
                MID_Logger.LogError(_logLevel,
                    "No MID_UIStateContext assigned. Manager will not function.",
                    nameof(MID_UIStateManager));
                return;
            }

            if (_initialState != 0)
                _context.ChangeState(_initialState);
        }

        protected override void OnDestroy()
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;
            base.OnDestroy();
        }

        #endregion

        #region Public API

        /// <summary>Transition to a new state.</summary>
        public void ChangeState(int newState)
        {
            if (_context == null)
            {
                MID_Logger.LogError(_logLevel, "No context assigned.",
                    nameof(MID_UIStateManager));
                return;
            }
            _context.ChangeState(newState);
        }

        /// <summary>Return to the previous state.</summary>
        public void GoBack()
        {
            if (_context == null) return;
            _context.GoBack();
        }

        /// <summary>Clear the navigation history stack.</summary>
        public void ClearHistory() => _context?.ClearHistory();

        public bool IsInState(int state) => _context != null && _context.IsInState(state);

        /// <summary>Swap the managed context at runtime.</summary>
        public void SetContext(MID_UIStateContext context)
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;

            _context = context;

            if (_context != null)
                _context.OnStateChanged += HandleStateChanged;
        }

        #endregion

        #region Internal

        private void HandleStateChanged(int newState)
        {
            // Trigger exit events for configs whose mask is NOT active in the new state
            foreach (var cfg in _configurations)
            {
                if (cfg.stateMask == 0) continue;
                if ((newState & cfg.stateMask) != 0) continue;

                foreach (var go in cfg.show)
                    if (go != null) go.SetActive(false);

                try { cfg.onExit?.Invoke(); }
                catch (Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"onExit exception in config '{cfg.displayName}': {e.Message}",
                        nameof(MID_UIStateManager));
                }
            }

            // Trigger enter events for configs whose mask IS active in the new state
            foreach (var cfg in _configurations)
            {
                if (cfg.stateMask == 0) continue;
                if ((newState & cfg.stateMask) == 0) continue;

                foreach (var go in cfg.show)
                    if (go != null) go.SetActive(true);
                foreach (var go in cfg.hide)
                    if (go != null) go.SetActive(false);

                try { cfg.onEnter?.Invoke(); }
                catch (Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"onEnter exception in config '{cfg.displayName}': {e.Message}",
                        nameof(MID_UIStateManager));
                }
            }

            OnStateChanged?.Invoke(newState);

            MID_Logger.LogInfo(_logLevel,
                $"[{_context?.contextName}] handled state → {newState}",
                nameof(MID_UIStateManager));
        }

        #endregion
    }
}
