// packages/com.midmanstudio.projectilesystem/Tests/Runtime/UI/LobbyEntryCard.cs
// Self-contained lobby entry card.
// One instance per discovered lobby in the browse list.
// ProjectileTestLobbyUI spawns these from _lobbyEntryPrefab and calls Populate().

using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using MidManStudio.Netcode.LocalMultiplayer;

namespace TestGame
{
    /// <summary>
    /// UI card representing one discovered LAN lobby in the browse list.
    ///
    /// Prefab layout (minimum required children):
    ///   TMP_Text  "_lobbyNameText"     — lobby name
    ///   TMP_Text  "_playerCountText"   — e.g. "2 / 4"
    ///   TMP_Text  "_gameModeText"      — e.g. "ProjectileTest"
    ///   TMP_Text  "_pingText"          — e.g. "LAN"
    ///   Button    "_joinButton"        — triggers OnJoinClicked
    ///   GameObject "_fullBadge"        — shown when lobby is full
    ///
    /// All fields are optional — missing ones are silently skipped.
    /// </summary>
    public class LobbyEntryCard : MonoBehaviour
    {
        // ── Inspector wiring ──────────────────────────────────────────────────

        [Header("Text Fields")]
        [SerializeField] private TMP_Text _lobbyNameText;
        [SerializeField] private TMP_Text _playerCountText;
        [SerializeField] private TMP_Text _gameModeText;
        [SerializeField] private TMP_Text _pingText;

        [Header("Join Button")]
        [SerializeField] private Button   _joinButton;
        [SerializeField] private TMP_Text _joinButtonLabel;

        [Header("Full Badge")]
        [SerializeField] private GameObject _fullBadge;

        [Header("Tint")]
        [Tooltip("Image component on the card root — tinted when full.")]
        [SerializeField] private Image     _cardBackground;
        [SerializeField] private Color     _availableColor = new Color(0.15f, 0.15f, 0.20f, 1f);
        [SerializeField] private Color     _fullColor      = new Color(0.25f, 0.10f, 0.10f, 1f);

        // ── Runtime data ──────────────────────────────────────────────────────

        private LocalLobbyData         _data;
        private Action<LocalLobbyData> _onJoin;

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Populate this card with lobby data and wire the join callback.
        /// Called by ProjectileTestLobbyUI immediately after Instantiate().
        /// </summary>
        public void Populate(LocalLobbyData data, Action<LocalLobbyData> onJoin)
        {
            _data   = data;
            _onJoin = onJoin;

            bool full = data.CurrentPlayers >= data.MaxPlayers;

            // ── Text ─────────────────────────────────────────────────────────
            SetText(_lobbyNameText,    data.LobbyName);
            SetText(_playerCountText,  $"{data.CurrentPlayers} / {data.MaxPlayers}");
            SetText(_gameModeText,     string.IsNullOrEmpty(data.GameMode)
                                          ? "—" : data.GameMode);
            SetText(_pingText,         "LAN");

            // ── Join button ───────────────────────────────────────────────────
            if (_joinButton != null)
            {
                _joinButton.onClick.RemoveAllListeners();
                _joinButton.onClick.AddListener(OnJoinClicked);
                _joinButton.interactable = !full;
            }

            SetText(_joinButtonLabel, full ? "FULL" : "JOIN");

            // ── Full badge / tint ─────────────────────────────────────────────
            if (_fullBadge != null)
                _fullBadge.SetActive(full);

            if (_cardBackground != null)
                _cardBackground.color = full ? _fullColor : _availableColor;
        }

        /// <summary>Refresh player count without rebuilding the whole card.</summary>
        public void UpdatePlayerCount(int current, int max)
        {
            bool full = current >= max;
            SetText(_playerCountText, $"{current} / {max}");
            SetText(_joinButtonLabel, full ? "FULL" : "JOIN");

            if (_joinButton != null)
                _joinButton.interactable = !full;

            if (_fullBadge != null)
                _fullBadge.SetActive(full);

            if (_cardBackground != null)
                _cardBackground.color = full ? _fullColor : _availableColor;

            if (_data.Key != null)
            {
                _data.CurrentPlayers = current;
                _data.MaxPlayers     = max;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Callbacks
        // ─────────────────────────────────────────────────────────────────────

        private void OnJoinClicked() => _onJoin?.Invoke(_data);

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void SetText(TMP_Text t, string value)
        {
            if (t != null) t.text = value;
        }
    }
}
