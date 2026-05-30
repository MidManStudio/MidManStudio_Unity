// PlayerEntryCard.cs
// FIX: Replaced \u2713 (✓) and \u2026 (…) with ASCII-safe alternatives.
// LiberationSans SDF (TMP default font) does not include these glyphs, causing
// the "character not found" warning and the □ fallback character to render instead.

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using MidManStudio.Netcode.LocalMultiplayer;

namespace TestGame
{
    /// <summary>
    /// UI card representing one player inside the lobby room panel.
    ///
    /// Prefab layout (minimum required children):
    ///   TMP_Text   "_nameText"       — player display name
    ///   TMP_Text   "_roleText"       — "HOST" or "Player"
    ///   TMP_Text   "_readyText"      — "READY" or "WAITING"
    ///   TMP_Text   "_pingText"       — "LAN"
    ///   Image      "_readyIndicator" — green/grey dot
    ///   Image      "_hostCrown"      — visible when player is host
    ///   Image      "_botIcon"        — visible when player is a bot
    ///   Image      "_cardBackground" — tinted by ready state
    /// </summary>
    public class PlayerEntryCard : MonoBehaviour
    {
        #region Inspector

        [Header("Text")]
        [SerializeField] private TMP_Text _nameText;
        [SerializeField] private TMP_Text _roleText;
        [SerializeField] private TMP_Text _readyText;
        [SerializeField] private TMP_Text _pingText;

        [Header("Icons / Indicators")]
        [SerializeField] private Image      _readyIndicator;
        [SerializeField] private GameObject _hostCrown;
        [SerializeField] private GameObject _botIcon;

        [Header("Card Background")]
        [SerializeField] private Image _cardBackground;
        [SerializeField] private Color _readyColor    = new Color(0.10f, 0.22f, 0.12f, 1f);
        [SerializeField] private Color _notReadyColor = new Color(0.15f, 0.15f, 0.20f, 1f);
        [SerializeField] private Color _hostColor     = new Color(0.18f, 0.16f, 0.10f, 1f);

        [Header("Ready Indicator Colors")]
        [SerializeField] private Color _indicatorReady    = new Color(0.25f, 1.00f, 0.40f, 1f);
        [SerializeField] private Color _indicatorNotReady = new Color(0.40f, 0.40f, 0.40f, 1f);

        #endregion

        #region Runtime

        private LocalLobbyPlayer _player;

        #endregion

        #region Public API

        /// <summary>Initial population. Called by ProjectileTestLobbyUI after Instantiate().</summary>
        public void Populate(LocalLobbyPlayer player)
        {
            _player = player;
            Refresh(player);
        }

        /// <summary>Update card without recreating it. Called on OnPlayerReadyChanged.</summary>
        public void Refresh(LocalLobbyPlayer player)
        {
            _player = player;

            string displayName = string.IsNullOrEmpty(player.PlayerName)
                ? $"Player {player.ClientId}" : player.PlayerName;
            SetText(_nameText, displayName);

            SetText(_roleText, player.IsHost ? "HOST" : "Player");

            // FIX: was "✓ Ready" / "…" — \u2713 and \u2026 not in LiberationSans SDF.
            // Replaced with ASCII-safe strings to eliminate TMP glyph-fallback warnings.
            SetText(_readyText, player.IsReady ? "READY" : "WAITING");

            SetText(_pingText, "LAN");

            if (_readyIndicator != null)
                _readyIndicator.color = player.IsReady
                    ? _indicatorReady : _indicatorNotReady;

            if (_hostCrown != null)
                _hostCrown.SetActive(player.IsHost);

            if (_botIcon != null)
                _botIcon.SetActive(player.IsBot);

            if (_cardBackground != null)
            {
                _cardBackground.color = player.IsHost
                    ? _hostColor
                    : (player.IsReady ? _readyColor : _notReadyColor);
            }
        }

        public ulong ClientId => _player.ClientId;

        #endregion

        private static void SetText(TMP_Text t, string value)
        {
            if (t != null) t.text = value;
        }
    }
}
