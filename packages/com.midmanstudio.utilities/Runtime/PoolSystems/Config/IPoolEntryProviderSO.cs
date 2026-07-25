// Implemented by ObjectPoolTypeProviderSO and ParticlePoolTypeProviderSO —
// both have an identical shape (packageId / displayName / priority / entries)
// but are deliberately separate concrete types so PoolTypeGenerator can find
// object-pool and particle-pool providers independently.
//
// This interface exists purely so editor tooling (the JSON import button on
// their custom Inspector) can be written ONCE and work against either type,
// instead of duplicating the same UI code per provider type.

using System.Collections.Generic;

namespace MidManStudio.Core.Pools.Generator
{
    public interface IPoolEntryProviderSO
    {
        List<PoolEntryDefinition> Entries { get; }
    }
}
