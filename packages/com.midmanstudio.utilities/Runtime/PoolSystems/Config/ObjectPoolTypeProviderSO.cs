// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "ObjectPoolTypeProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Object pool provider
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the object pool types that one package or game project adds to <c>PoolableObjectType</c>. Create it with
    /// Create > MidManStudio > Utilities > Pool Type Provider (Object), then run the Pool Type Generator. Each provider owns
    /// a block of numbers and <c>priority</c> orders the blocks (0 utilities, 10 projectile system, 20 audio, 100 and up
    /// for game code). An entry with an <c>explicitOffset</c> of 0 or more is pinned to that offset inside the block. An
    /// entry with -1 is numbered by the generator and keeps its number through the lock file, wherever it sits in the list.
    /// The inspector adds an Import Entries from JSON panel.
    /// </summary>
[CreateAssetMenu(fileName="PoolTypeProvider",
    menuName="MidManStudio/Utilities/Pool Type Provider (Object)", order=160)]
    public class ObjectPoolTypeProviderSO : ScriptableObject, IPoolEntryProviderSO
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

        /// <summary>Number of entries in the list.</summary>
        public int EntryCount => entries?.Count ?? 0;

        // IPoolEntryProviderSO: lets the shared "Import from JSON" inspector (PoolTypeProviderEditor.cs)
        // read and write `entries` without knowing which provider type it has.
        List<PoolEntryDefinition> IPoolEntryProviderSO.Entries => entries;
    }

}
