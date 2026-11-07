// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/uistate.md, section "MID_UIStateManager.cs"
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState
{
    /// <summary>One row of the manager: for a state flag, the objects to activate and deactivate, and two events.</summary>
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

        /// <summary>Label in inspector lists: the display name, else "State_N", else "None".</summary>
        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            stateMask != 0                           ? $"State_{stateMask}" :
                                                       "None";
    }

    /// <summary>
    /// Singleton that applies a <see cref="MID_UIStateContext"/> to scene objects. On each state change it deactivates
    /// the Show objects of every config whose flag is not in the new state, then activates the Show objects and
    /// deactivates the Hide objects of every config whose flag is in the new state, and invokes onExit or onEnter.
    /// It persists across scenes (<c>Awake</c> calls <c>Remake(true)</c>). <c>Start</c> applies the initial state.
    /// </summary>
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

        /// <summary>The context this manager applies.</summary>
        public MID_UIStateContext Context     => _context;
        /// <summary>The context's current state, or 0 when no context is assigned.</summary>
        public int  CurrentState             => _context != null ? _context.CurrentState : 0;
        /// <summary>True when the context has a state to go back to.</summary>
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
            {
                // Transition to the configured initial state.
                // This fires OnStateChanged → HandleStateChanged on this manager
                // AND on all MID_UIStateVisibility components listening to the context.
                _context.ChangeState(_initialState);
            }
            else
            {
                // For initial state 0 (None), ChangeState is a no-op (0→0).
                // Explicitly apply the None state to this manager's config arrays
                // so that cfg.show objects active in the scene are properly hidden.
                // MID_UIStateVisibility components apply their own initial state through _initialised.
                ApplyNoneState();
            }
        }

        protected override void OnDestroy()
        {
            if (_context != null)
                _context.OnStateChanged -= HandleStateChanged;
            base.OnDestroy();
        }

        #endregion

        #region Public API

        /// <summary>Moves the context to <paramref name="newState"/>. Logs an error when no context is assigned.</summary>
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

        /// <summary>Returns the context to the previous state.</summary>
        public void GoBack()
        {
            if (_context == null) return;
            _context.GoBack();
        }

        /// <summary>Clears the context's back history.</summary>
        public void ClearHistory() => _context?.ClearHistory();

        /// <summary>True when the context is exactly in <paramref name="state"/>.</summary>
        public bool IsInState(int state) => _context != null && _context.IsInState(state);

        /// <summary>Swaps the context at run time and subscribes to the new one. It does not apply the new context's current state.</summary>
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

        /// <summary>
        /// Hides all cfg.show objects for every config whose mask is not in the new state,
        /// and shows cfg.show / hides cfg.hide for configs that ARE in the new state.
        /// </summary>
        private void HandleStateChanged(int newState)
        {
            // Exit pass — hide show-objects for configs NOT active in the new state
            foreach (var cfg in _configurations)
            {
                if (cfg.stateMask == 0) continue;
                if ((newState & cfg.stateMask) != 0) continue;   // still active → skip

                foreach (var go in cfg.show)
                    if (go != null) go.SetActive(false);

                try { cfg.onExit?.Invoke(); }
                catch (Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"onExit exception in '{cfg.displayName}': {e.Message}",
                        nameof(MID_UIStateManager));
                }
            }

            // Enter pass — show show-objects and hide hide-objects for active configs
            foreach (var cfg in _configurations)
            {
                if (cfg.stateMask == 0) continue;
                if ((newState & cfg.stateMask) == 0) continue;   // not active → skip

                foreach (var go in cfg.show)
                    if (go != null) go.SetActive(true);
                foreach (var go in cfg.hide)
                    if (go != null) go.SetActive(false);

                try { cfg.onEnter?.Invoke(); }
                catch (Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"onEnter exception in '{cfg.displayName}': {e.Message}",
                        nameof(MID_UIStateManager));
                }
            }

            OnStateChanged?.Invoke(newState);

            MID_Logger.LogInfo(_logLevel,
                $"[{_context?.contextName}] handled state → {newState}",
                nameof(MID_UIStateManager));
        }

        /// <summary>
        /// Applied when _initialState == 0 (None).
        /// Hides all cfg.show objects across every config without firing onExit events,
        /// since we are not "leaving" any state — we are simply setting initial scene state.
        /// Does not notify context or fire OnStateChanged (context state stays at 0).
        /// </summary>
        private void ApplyNoneState()
        {
            foreach (var cfg in _configurations)
            {
                foreach (var go in cfg.show)
                    if (go != null) go.SetActive(false);
            }

            MID_Logger.LogInfo(_logLevel,
                $"[{_context?.contextName}] initial state None — all show-objects hidden.",
                nameof(MID_UIStateManager));
        }

        #endregion
    }
}
