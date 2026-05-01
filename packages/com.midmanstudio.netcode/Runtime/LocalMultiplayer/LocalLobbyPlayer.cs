// LocalLobbyPlayer.cs
// Generic player representation for a local lobby session.
// NetworkLobbyPlayerData is the wire format for NGO NetworkList syncing.

using System;
using Unity.Collections;
using Unity.Netcode;

namespace MidManStudio.Core.Netcode.LocalMultiplayer
{
    [Serializable]
    public class LocalLobbyPlayer
    {
        public ulong  ClientId;
        public string PlayerName;
        public string PlayerIconId;
        public bool   IsReady;
        public bool   IsHost;
        public bool   IsBot;

        /// <summary>
        /// Game-defined team or slot index. -1 = unassigned.
        /// The lobby system carries this opaquely; game code assigns meaning.
        /// </summary>
        public int TeamId = -1;

        public LocalLobbyPlayer(ulong clientId, string playerName,
                                bool isHost = false, bool isBot = false)
        {
            ClientId   = clientId;
            PlayerName = playerName;
            IsHost     = isHost;
            IsBot      = isBot;
        }

        public override string ToString() =>
            $"[{ClientId}] {PlayerName} host={IsHost} bot={IsBot} ready={IsReady} team={TeamId}";
    }

    // ── NGO wire format ────────────────────────────────────────────────────────

    public struct NetworkLobbyPlayerData : INetworkSerializable, IEquatable<NetworkLobbyPlayerData>
    {
        public ulong             ClientId;
        public FixedString128Bytes PlayerName;
        public FixedString64Bytes  PlayerIconId;
        public bool              IsReady;
        public bool              IsHost;
        public int               TeamId;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ClientId);
            s.SerializeValue(ref PlayerName);
            s.SerializeValue(ref PlayerIconId);
            s.SerializeValue(ref IsReady);
            s.SerializeValue(ref IsHost);
            s.SerializeValue(ref TeamId);
        }

        public bool Equals(NetworkLobbyPlayerData o) =>
            ClientId == o.ClientId &&
            PlayerName.Equals(o.PlayerName) &&
            IsReady   == o.IsReady &&
            IsHost    == o.IsHost &&
            TeamId    == o.TeamId;

        public override bool Equals(object obj) =>
            obj is NetworkLobbyPlayerData o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(ClientId, IsReady, IsHost, TeamId);
    }

    // ── Snapshot passed to game code at game-start time ───────────────────────

    [Serializable]
    public class LocalLobbySnapshot
    {
        public LocalLobbyData              LobbyData;
        public System.Collections.Generic.List<LocalLobbyPlayer> Players;

        public LocalLobbySnapshot(LocalLobbyData data,
            System.Collections.Generic.List<LocalLobbyPlayer> players)
        {
            LobbyData = data;
            Players   = new System.Collections.Generic.List<LocalLobbyPlayer>(players);
        }
    }
}
