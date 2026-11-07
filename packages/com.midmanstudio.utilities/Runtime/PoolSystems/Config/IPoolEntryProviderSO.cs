// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "IPoolEntryProviderSO.cs"
// ============================================================================
using System.Collections.Generic;

namespace MidManStudio.Core.Pools.Generator
{
    /// <summary>
    /// Implemented by <see cref="ObjectPoolTypeProviderSO"/> and <see cref="ParticlePoolTypeProviderSO"/>. Both have the
    /// same shape but are separate types, so the generator can find object providers and particle providers
    /// independently. The interface lets the JSON import inspector work on either one.
    /// </summary>
    public interface IPoolEntryProviderSO
    {
        /// <summary>The provider's entry list. It is the list itself, not a copy.</summary>
        List<PoolEntryDefinition> Entries { get; }
    }
}
