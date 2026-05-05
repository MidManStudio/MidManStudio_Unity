// MID_UIStateManager.cs
// Singleton panel manager for a single MID_UIStateContext.
// Handles GameObject show/hide and UnityEvent callbacks on state transitions.
// State machine logic (ChangeState, GoBack, history) lives in MID_UIStateContext.
//
// SETUP:
//   1. Create a UIStateContextProviderSO, add your states, run the generator.
//      (MidManStudio > Utilities > UI State Context Generator)
//   2. Create a MID_UIStateContext SO asset per logical context (Menu, HUD, etc.).
//   3. Add MID_UIStateManager to a persistent GameObject.
//   4. Assign the context SO to the Context field.
//   5. Add UIStatePanelConfig entries — set stateMask by casting your generated enum:
//        stateMask = (int)MenuUIState.Settings
//
// USAGE (code):
//   MID_UIStateManager.Instance.ChangeState((int)MenuUIState.MainMenu);
//   MID_UIStateManager.Instance.GoBack();

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.UIState
{
    // ── Panel configuration ───────────────────────────────────────────────────

    [Serializable]
    public class UIStatePanelConfig : IArrayElementTitle
    {
        [Tooltip("Raw int value of the generated enum member for this state.\n" +
                 "Cast in code: stateMask = (int)MenuUIState.Settings\n" +
                 "The custom inspector shows named checkboxes when a context is assigned.")]
        public int stateMask;

        [Tooltip("Inspector label only.")]
        public string displayName;

        [Tooltip("GameObjects to activate when entering this state.")]
        public GameObject[] show;

        [Tooltip("GameObjects to deactivate when entering this state.")]
        public GameObject[] hide;

        public UnityEvent onEnter;
        public UnityEvent onExit;

        // IArrayElementTitle
        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            stateMask != 0                          ? $"State_{stateMask}" :
                                                      "None";
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Singleton panel manager. Wraps a MID_UIStateContext and drives
    /// panel visibility based on state transitions.
    /// </summary>
    public class MID_UIStateManager : Singleton<MID_UIStateManager>
    {
        #region Inspector

        [Tooltip("The context SO this manager drives.\n" +
                 "Create via: right-click > MidManStudio > Utilities > UI State Context")]
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

        /// <summary>The context SO this manager is currently driving.</summary>
        public MID_UIStateContext Context => _context;

        /// <summary>Current raw int state of the managed context.</summary>
        public int CurrentState => _context != null ? _context.CurrentState : 0;

        /// <summary>True if the managed context has back history.</summary>
        public bool CanGoBack => _context != null && _context.CanGoBack;

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

        /// <summary>
        /// Transition to a new state.
        /// Pass the raw int value of your generated enum:
        ///   manager.ChangeState((int)MenuUIState.Settings);
        /// </summary>
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

        /// <summary>Returns true if the context is currently in the given raw state.</summary>
        public bool IsInState(int state) => _context != null && _context.IsInState(state);

        /// <summary>
        /// Swap the managed context at runtime.
        /// Unsubscribes from the old context, subscribes to the new one.
        /// </summary>
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
            // Exit previous state panels
            foreach (var cfg in _configurations)
            {
                if (cfg.stateMask == 0) continue;
                bool wasActive = ((_context != null ? _context.CurrentState : 0) & cfg.stateMask) != 0;
                // We don't have "previous state" here — use all configs whose mask
                // doesn't match new state to trigger exit.
                if ((newState & cfg.stateMask) == 0)
                {
                    foreach (var go in cfg.show)
                        if (go != null) go.SetActive(false);
                    try { cfg.onExit?.Invoke(); }
                    catch (Exception e)
                    {
                        MID_Logger.LogError(_logLevel, $"onExit exception: {e.Message}",
                            nameof(MID_UIStateManager));
                    }
                }
            }

            // Enter new state panels
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
                    MID_Logger.LogError(_logLevel, $"onEnter exception: {e.Message}",
                        nameof(MID_UIStateManager));
                }
            }

            OnStateChanged?.Invoke(newState);

            MID_Logger.LogInfo(_logLevel,
                $"[{_context?.contextDisplayName}] → state {newState}",
                nameof(MID_UIStateManager));
        }

        #endregion
    }
}
