// MID_UIStateButton.cs
// Button that transitions MID_UIStateManager to a target UIStateId.
// Also disables itself when the current state already matches the target.
// Replaces MenuStateUIController and GenericUIStateButton.
//
// USAGE:
//   Attach to any Button. Set targetState in the inspector.

using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.UIState
{
    /// <summary>
    /// Wires a UI Button to a UIStateId transition.
    /// Disables when current state already equals target state.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class MID_UIStateButton : MonoBehaviour
    {
        [SerializeField] private UIStateId   _targetState;
        [SerializeField] private bool        _disableWhenActive = true;
        [SerializeField] private MID_LogLevel _logLevel         = MID_LogLevel.None;

        private Button _button;

        private void Awake()
        {
            _button = GetComponent<Button>();
            _button.onClick.AddListener(OnClick);
        }

        private void Start()
        {
            if (MID_UIStateManager.Instance == null)
            {
                MID_Logger.LogWarning(MID_LogLevel.Info,
                    $"MID_UIStateManager not found on {gameObject.name}.",
                    nameof(MID_UIStateButton));
                return;
            }

            MID_UIStateManager.Instance.OnStateChanged += UpdateInteractable;
            UpdateInteractable(MID_UIStateManager.Instance.CurrentState);
        }

        private void OnDestroy()
        {
            if (MID_UIStateManager.Instance != null)
                MID_UIStateManager.Instance.OnStateChanged -= UpdateInteractable;
        }

        private void OnClick()
        {
            if (MID_UIStateManager.Instance == null) return;
            MID_Logger.LogDebug(_logLevel, $"Button → {_targetState}.",
                nameof(MID_UIStateButton));
            MID_UIStateManager.Instance.ChangeState(_targetState);
        }

        private void UpdateInteractable(UIStateId currentState)
        {
            if (!_disableWhenActive) return;
            _button.interactable = currentState != _targetState;
        }
    }
}
