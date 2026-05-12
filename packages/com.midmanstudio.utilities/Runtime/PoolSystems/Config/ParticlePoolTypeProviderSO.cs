
// One of these lives in every package (and in the user's game project) that wants
// to contribute entries to the shared PoolableObjectType / PoolableParticleType enums.
//
// SETUP:
//   Right-click in Project window >
//       MidManStudio > Pool Type Provider  — object pool entries
//       MidManStudio > Particle Pool Type Provider — particle pool entries
//
// ENTRY OFFSET RULES:
//   explicitOffset = -1  →  generator assigns automatically (blockStart + auto index)
//   explicitOffset >= 0  →  pinned to that offset within the provider's block.
//                           Use this to stop an entry's value shifting when you
//                           add entries above it.
//
// ORDERING WITHIN A BLOCK:
//   The list order only matters for auto-offset entries.
//   Pin critical entries (ones referenced in serialized inspector fields) so they
//   never move even if you insert new entries above them.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Particle pool provider (identical shape, separate type so the generator
    //  can find them independently)
    // ─────────────────────────────────────────────────────────────────────────

    [CreateAssetMenu(fileName = "ParticlePoolTypeProvider",
        menuName = "MidManStudio/Utilities/Pool Type Provider (Particle)", order = 170)]
    public class ParticlePoolTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        public string packageId = "com.mygame";
        public string displayName = "My Game";

        [Header("Block Priority")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<PoolEntryDefinition> entries = new List<PoolEntryDefinition>();

        public int EntryCount => entries?.Count ?? 0;
    }
}
