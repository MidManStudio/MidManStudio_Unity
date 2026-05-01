// LocalLobbyData.cs
// Generic lobby data container. No game-specific fields.
// Game code can use CustomData (JSON string) for game-specific fields
// without creating a dependency on this package.

using System;
using UnityEngine;

namespace MidManStudio.Core.Netcode.LocalMultiplayer
{
    [Serializable]
    public class LocalLobbyData
    {
        public string LobbyName;
        public string HostName;
        public string HostAddress;
        public int    Port;
        public int    CurrentPlayers;
        public int    MaxPlayers;

        /// <summary>
        /// Optional game-defined mode identifier (e.g. "TeamDeathmatch").
        /// The lobby system treats this as an opaque string.
        /// </summary>
        public string GameMode;

        /// <summary>
        /// Optional game-defined map identifier (e.g. "GrassyLand").
        /// The lobby system treats this as an opaque string.
        /// </summary>
        public string GameMap;

        /// <summary>
        /// Free-form JSON payload for game-specific lobby properties.
        /// Serialised / deserialised by game code. The lobby system
        /// carries it verbatim without inspecting it.
        /// </summary>
        public string CustomData;

        public float LastDiscoveryTime;
        public float TimeoutTime;

        public bool IsFull => CurrentPlayers >= MaxPlayers;

        public string Key => $"{HostAddress}:{Port}";

        public override string ToString() =>
            $"[{LobbyName}] {HostAddress}:{Port} " +
            $"({CurrentPlayers}/{MaxPlayers}) mode={GameMode} map={GameMap}";
    }
  }
