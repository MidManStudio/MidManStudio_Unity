// NetworkPoolTypeProviderSO.cs
// Contributes entries to the generated PoolableNetworkObjectType enum.
// Same system as PoolTypeProviderSO but for the network object pool.
//
// Create via: MidManStudio > Pool Type Provider (Network Object)
//
// PRIORITY RANGES:
//   0   = com.midmanstudio.netcode    (reserved)
//   10  = com.midmanstudio.projectilesystem (reserved)
//   100 = user game code

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.Utilities;
using MidManStudio.Core.Pools.Generator;

namespace MidManStudio.Core.Netcode.Generator
{
    [CreateAssetMenu(
        fileName = "NetworkPoolTypeProvider",
        menuName = "MidManStudio/Pool Type Provider (Network Object)")]
    public class NetworkPoolTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all network pool providers.")]
        public string packageId   = "com.mygame";
        public string displayName = "My Game";

        [Header("Priority")]
        [Tooltip("Lower = earlier block. 0 = reserved for netcode package.\n" +
                 "Start at 100 for your own game entries.")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<PoolEntryDefinition> entries = new List<PoolEntryDefinition>();

        public int EntryCount => entries?.Count ?? 0;
    }
}
