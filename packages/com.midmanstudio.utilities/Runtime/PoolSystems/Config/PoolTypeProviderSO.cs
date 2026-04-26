// PoolTypeProviderSO.cs
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
using MidManStudio.Core.Utilities;

namespace MidManStudio.Core.Pools.Generator
{
    [System.Serializable]
    public class PoolEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. Use PascalCase. No spaces.")]
        public string entryName;

        [Tooltip("Optional inline comment written next to the enum member.")]
        public string comment;

        [Tooltip("-1 = auto-assigned by generator.\n" +
                 ">=0 = pinned to this offset within the provider's block.\n" +
                 "Pin entries that are referenced in serialised inspector fields\n" +
                 "so their integer value never changes.")]
        public int explicitOffset = -1;

        // IArrayElementTitle
        public string Name =>
            string.IsNullOrWhiteSpace(entryName) ? "Unnamed Entry" : entryName;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Object pool provider
    // ─────────────────────────────────────────────────────────────────────────

    [CreateAssetMenu(
        fileName = "PoolTypeProvider",
        menuName = "MidManStudio/Pool Type Provider (Object)")]
    public class PoolTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all providers.\n" +
                 "e.g. com.midmanstudio.utilities")]
        public string packageId = "com.mygame";

        [Tooltip("Human-readable name shown in the generator window.")]
        public string displayName = "My Game";

        [Header("Block Priority")]
        [Tooltip("Lower = earlier block in the generated enum.\n" +
                 "0   = utilities (reserved)\n" +
                 "10  = projectile system (reserved)\n" +
                 "100 = recommended start for user game code.")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<PoolEntryDefinition> entries = new List<PoolEntryDefinition>();

        // Convenience
        public int EntryCount => entries?.Count ?? 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Particle pool provider (identical shape, separate type so the generator
    //  can find them independently)
    // ─────────────────────────────────────────────────────────────────────────

    [CreateAssetMenu(
        fileName = "ParticlePoolTypeProvider",
        menuName = "MidManStudio/Pool Type Provider (Particle)")]
    public class ParticlePoolTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        public string packageId   = "com.mygame";
        public string displayName = "My Game";

        [Header("Block Priority")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<PoolEntryDefinition> entries = new List<PoolEntryDefinition>();

        public int EntryCount => entries?.Count ?? 0;
    }
}
