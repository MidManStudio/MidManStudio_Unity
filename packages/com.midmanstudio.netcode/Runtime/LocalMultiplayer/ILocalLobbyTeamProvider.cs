// ILocalLobbyTeamProvider.cs
// Optional injectable interface for game-specific team management.
// Implement this in your game and pass it to LocalLobbyManager.SetTeamProvider().
// If no provider is set the manager still functions — players just have TeamId = -1.

namespace MidManStudio.Core.Netcode.LocalMultiplayer
{
    /// <summary>
    /// Game code implements this to plug custom team logic into LocalLobbyManager.
    /// All team IDs are plain ints — meaning is defined by the game.
    /// </summary>
    public interface ILocalLobbyTeamProvider
    {
        /// <summary>Called server-side when a player joins. Returns assigned team ID.</summary>
        int OnPlayerJoined(ulong clientId, bool isHost);

        /// <summary>Called server-side when a player leaves.</summary>
        void OnPlayerLeft(ulong clientId);

        /// <summary>
        /// Called server-side when a player requests a team change.
        /// Return true if allowed, false if the target team is full or invalid.
        /// </summary>
        bool TryChangeTeam(ulong clientId, int targetTeamId);

        /// <summary>Returns the current team ID for a player, or -1 if unassigned.</summary>
        int GetTeamId(ulong clientId);

        /// <summary>Called server-side just before game start so bots can be balanced.</summary>
        void OnPrepareGameStart(System.Collections.Generic.List<LocalLobbyPlayer> allPlayers);

        /// <summary>Serialise current team state to a string for client sync.</summary>
        string SerializeState();

        /// <summary>Deserialise team state received from server.</summary>
        void DeserializeState(string data);
    }
}
