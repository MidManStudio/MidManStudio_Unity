// packages/com.midmanstudio.projectilesystem/Tests/Runtime/UI/PlayerEntryCard.cs
// Self-contained player entry card shown inside a lobby room.
// One instance per connected player.
// ProjectileTestLobbyUI spawns these from _playerEntryPrefab and calls Populate().

using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TestGame
{
    /// <summary>
    /// UI card representing one player inside the lobby room panel.
    ///
    /// Prefab layout (minimum required children):
    ///   TMP_Text   "_nameText"       — player display name
    ///   TMP_Text   "_roleText"       — "HOST" or "Player"
    ///   TMP_Text   "_readyText"      — "✓ Ready" or "…"
    ///   TMP_Text   "_pingText"       — "LAN" (or ms if available)
    ///   Image      "_readyIndicator" — green/grey dot
    ///   Image      "_hostCrown"      — visible when player is host
    ///   Image      "_botIcon"        — visible when player is a bot
    ///   Image      "_cardBackground" — tinted by ready state
    ///
    /// All fields are optional — missing ones are silently skipped.
    /// </summary>
    public class PlayerEntryCard : MonoBehaviour
    {
        // ── Inspector wiring ──────────────────────────────────────────────────

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

        // ── Runtime data ──────────────────────────────────────────────────────

        private LocalLobbyPlayer _player;

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Initial population. Called by ProjectileTestLobbyUI after Instantiate().
        /// </summary>
        public void Populate(LocalLobbyPlayer player)
        {
            _player = player;
            Refresh(player);
        }

        /// <summary>
        /// Update ready state, name, role without recreating the card.
        /// Called by ProjectileTestLobbyUI when it receives OnPlayerReadyChanged.
        /// </summary>
        public void Refresh(LocalLobbyPlayer player)
        {
            _player = player;

            // ── Name ──────────────────────────────────────────────────────────
            string displayName = string.IsNullOrEmpty(player.PlayerName)
                ? $"Player {player.ClientId}" : player.PlayerName;
            SetText(_nameText, displayName);

            // ── Role ──────────────────────────────────────────────────────────
            SetText(_roleText, player.IsHost ? "HOST" : "Player");

            // ── Ready ─────────────────────────────────────────────────────────
            SetText(_readyText, player.IsReady ? "✓ Ready" : "…");

            // ── Ping ──────────────────────────────────────────────────────────
            SetText(_pingText, "LAN");

            // ── Ready indicator dot ───────────────────────────────────────────
            if (_readyIndicator != null)
                _readyIndicator.color = player.IsReady
                    ? _indicatorReady : _indicatorNotReady;

            // ── Host crown ────────────────────────────────────────────────────
            if (_hostCrown != null)
                _hostCrown.SetActive(player.IsHost);

            // ── Bot icon ──────────────────────────────────────────────────────
            if (_botIcon != null)
                _botIcon.SetActive(player.IsBot);

            // ── Card background tint ──────────────────────────────────────────
            if (_cardBackground != null)
            {
                _cardBackground.color = player.IsHost
                    ? _hostColor
                    : (player.IsReady ? _readyColor : _notReadyColor);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Accessors
        // ─────────────────────────────────────────────────────────────────────

        public ulong ClientId => _player.ClientId;

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void SetText(TMP_Text t, string value)
        {
            if (t != null) t.text = value;
        }
    }
}
