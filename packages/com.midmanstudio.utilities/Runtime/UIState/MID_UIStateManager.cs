// packages/com.midmanstudio.utilities/Runtime/UIState/MID_UIStateManager.cs
//
// FIX (initial state for _initialState == 0):
//   When _initialState is 0 (None), ChangeState is never called, so HandleStateChanged
//   never fires through the manager. Objects in cfg.show arrays that were active in
//   the scene were not hidden. Fix: explicitly call HandleStateChanged(0) in Start
//   when _initialState is 0 so the manager's config arrays are properly applied.
//
//   Note: MID_UIStateVisibility handles its own initial state independently via
//   its _initialised fix — this fix covers objects in the manager's _configurations.

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
            {
                // Transition to the configured initial state.
                // This fires OnStateChanged → HandleStateChanged on this manager
                // AND on all MID_UIStateVisibility components listening to the context.
                _context.ChangeState(_initialState);
            }
            else
            {
                // FIX: For initial state 0 (None), ChangeState is a no-op (0→0).
                // Explicitly apply the None state to this manager's config arrays
                // so that cfg.show objects active in the scene are properly hidden.
                // MID_UIStateVisibility components handle themselves via their own _initialised fix.
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

        public void GoBack()
        {
            if (_context == null) return;
            _context.GoBack();
        }

        public void ClearHistory() => _context?.ClearHistory();

        public bool IsInState(int state) => _context != null && _context.IsInState(state);

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
        /// FIX: Applied when _initialState == 0 (None).
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
