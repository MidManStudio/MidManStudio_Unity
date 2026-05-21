// packages/com.midmanstudio.projectilesystem/Tests/Runtime/UI/LocalLobbyData.cs
// Plain data classes used by LobbyEntryCard and PlayerEntryCard.
// Kept in the Tests assembly so they don't pollute the package Runtime.
// If your game already has equivalents, delete this file and update the usings.

namespace TestGame
{
    /// <summary>Data describing one discovered LAN lobby.</summary>
    public class LocalLobbyData
    {
        /// <summary>Unique stable key (e.g. "IP:port" or GUID).</summary>
        public string Key;

        /// <summary>Human-readable lobby name set by the host.</summary>
        public string LobbyName;

        /// <summary>Current number of connected players.</summary>
        public int CurrentPlayers;

        /// <summary>Maximum players allowed.</summary>
        public int MaxPlayers;

        /// <summary>Game mode identifier (e.g. "ProjectileTest").</summary>
        public string GameMode;

        /// <summary>Map identifier (e.g. "TestScene").</summary>
        public string GameMap;

        /// <summary>Host IP address (LAN discovery).</summary>
        public string HostAddress;

        public override string ToString()
            => $"{LobbyName} [{CurrentPlayers}/{MaxPlayers}] {GameMode} @ {HostAddress}";
    }

    /// <summary>Data describing one player inside a lobby room.</summary>
    public class LocalLobbyPlayer
    {
        /// <summary>NGO client ID.</summary>
        public ulong ClientId;

        /// <summary>Display name chosen by the player.</summary>
        public string PlayerName;

        /// <summary>True when this player is the session host.</summary>
        public bool IsHost;

        /// <summary>True when the player has pressed the Ready button.</summary>
        public bool IsReady;

        /// <summary>True when this slot is occupied by an AI bot.</summary>
        public bool IsBot;

        public override string ToString()
            => $"{PlayerName} (id={ClientId} host={IsHost} ready={IsReady} bot={IsBot})";
    }

    /// <summary>Snapshot sent to all clients when the host starts the game.</summary>
    public class LocalLobbySnapshot
    {
        public System.Collections.Generic.List<LocalLobbyPlayer> Players
            = new System.Collections.Generic.List<LocalLobbyPlayer>();
    }

    /// <summary>Config passed to RequestHost().</summary>
    public struct LocalLobbyConfig
    {
        public string LobbyName;
        public int    MaxPlayers;
        public string GameMode;
        public string GameMap;
        public int    ServerPort;
        public int    BroadcastPort;
    }
}
