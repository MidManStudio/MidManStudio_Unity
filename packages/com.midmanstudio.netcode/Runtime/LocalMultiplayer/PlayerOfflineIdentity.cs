// PlayerOfflineIdentity.cs
// Persistent offline player identity. Survives scene loads via DontDestroyOnLoad.
// Saves to PlayerPrefs. Structured for clean migration to an online account.

using System;
using UnityEngine;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Netcode.LocalMultiplayer
{
    public class PlayerOfflineIdentity : Singleton<PlayerOfflineIdentity>
    {
        private const string KEY_NAME = "OfflineIdentity_PlayerName";
        private const string KEY_ICON = "OfflineIdentity_PlayerIconId";
        private const string DEFAULT_NAME = "Player";
        private const string DEFAULT_ICON = "default";

        public event Action<string> OnPlayerNameChanged;
        public event Action<string> OnPlayerIconIdChanged;

        private string _playerName;
        private string _playerIconId;

        public string PlayerName   => _playerName;
        public string PlayerIconId => _playerIconId;

        protected override void Awake()
        {
            base.Awake();
            Remake(true);
            Load();
        }

        protected override void Remake(bool dontDestroyOnLoad)
        {
            base.Remake(true);
        }

        private void Load()
        {
            _playerName   = PlayerPrefs.GetString(KEY_NAME, DEFAULT_NAME);
            _playerIconId = PlayerPrefs.GetString(KEY_ICON, DEFAULT_ICON);

            if (string.IsNullOrWhiteSpace(_playerName))   _playerName   = DEFAULT_NAME;
            if (string.IsNullOrWhiteSpace(_playerIconId)) _playerIconId = DEFAULT_ICON;
        }

        public void SetPlayerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (_playerName == name) return;

            _playerName = name;
            PlayerPrefs.SetString(KEY_NAME, name);
            PlayerPrefs.Save();
            OnPlayerNameChanged?.Invoke(name);
        }

        public void SetPlayerIconId(string iconId)
        {
            if (string.IsNullOrWhiteSpace(iconId) || _playerIconId == iconId) return;
            _playerIconId = iconId;
            PlayerPrefs.SetString(KEY_ICON, iconId);
            PlayerPrefs.Save();
            OnPlayerIconIdChanged?.Invoke(iconId);
        }

        /// <summary>
        /// Returns a snapshot ready for online account creation.
        /// Call when the player goes online — do not persist the snapshot itself.
        /// </summary>
        public OfflineIdentitySnapshot ExportForOnlineAccount() => new()
        {
            PlayerName   = _playerName,
            PlayerIconId = _playerIconId,
            ExportedAtUtc = DateTime.UtcNow.ToString("o")
        };

        [Serializable]
        public class OfflineIdentitySnapshot
        {
            public string PlayerName;
            public string PlayerIconId;
            public string ExportedAtUtc;
        }
    }
}
