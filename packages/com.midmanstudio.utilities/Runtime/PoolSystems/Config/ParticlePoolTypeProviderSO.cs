// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "ParticlePoolTypeProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools.Generator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Particle pool provider (identical shape, separate type so the generator
    //  can find them independently)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the particle pool types that one package or game project adds to <c>PoolableParticleType</c>. Same shape and
    /// rules as <see cref="ObjectPoolTypeProviderSO"/>. Create it with Create > MidManStudio > Utilities > Pool Type Provider
    /// (Particle), then run the Pool Type Generator.
    /// </summary>
    [CreateAssetMenu(fileName = "ParticlePoolTypeProvider",
        menuName = "MidManStudio/Utilities/Pool Type Provider (Particle)", order = 170)]
    public class ParticlePoolTypeProviderSO : ScriptableObject, IPoolEntryProviderSO
    {
        [Header("Identity")]
        public string packageId = "com.mygame";
        public string displayName = "My Game";

        [Header("Block Priority")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<PoolEntryDefinition> entries = new List<PoolEntryDefinition>();

        /// <summary>Number of entries in the list.</summary>
        public int EntryCount => entries?.Count ?? 0;

        // IPoolEntryProviderSO — see ObjectPoolTypeProviderSO for why this exists.
        List<PoolEntryDefinition> IPoolEntryProviderSO.Entries => entries;
    }
}
