// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "BasicPoolConfig.cs"
// ============================================================================
using System;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools
{
    /// <summary>One prefab entry of <see cref="LocalObjectPool"/>: pool type, prefab, how many to create up front and the largest idle queue kept.</summary>
    [Serializable]
    public class BasicPoolConfig : IArrayElementTitle
    {
        [Tooltip("Pool type for this entry.")]
        public PoolableObjectType objectType;

        [Tooltip("Human-readable label shown in the inspector.")]
        public string displayName;

        public GameObject prefab;

        [Min(0)]
        public int prewarmCount = 5;

        [Min(1)]
        public int maxPoolSize = 15;

        /// <summary>Label in inspector lists: the display name, else the prefab name, else the pool type.</summary>
        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            prefab != null ? prefab.name :
                                                       objectType.ToString();
    }

    /// <summary>One prefab entry of <see cref="LocalParticlePool"/>. Like <see cref="BasicPoolConfig"/> plus the lifetime after which <see cref="LocalParticleReturn"/> returns the object.</summary>
    [Serializable]
    public class ParticlePoolConfig : IArrayElementTitle
    {
        [Tooltip("Particle pool type for this entry.")]
        public PoolableParticleType particleType;

        [Tooltip("Human-readable label shown in the inspector.")]
        public string displayName;

        public GameObject prefab;

        [Min(0)]
        public int prewarmCount = 10;

        [Min(1)]
        public int maxPoolSize = 30;

        [Tooltip("Seconds before LocalParticleReturn auto-returns the object.")]
        [Min(0.1f)]
        public float defaultLifetime = 5f;

        /// <summary>Label in inspector lists: the display name, else the prefab name, else the pool type.</summary>
        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            prefab != null ? prefab.name :
                                                       particleType.ToString();
    }

    /// <summary>Counters for one object pool type, shown in the pool's inspector monitor. <c>totalSpawned</c> counts instances created after the prewarm.</summary>
    [Serializable]
    public class PoolStats
    {
        public string typeName;
        public int totalSpawned;
        public int activeCount;
        public int availableInPool;
        public int maxPoolSize;

        public PoolStats(string name, int spawned, int active, int available, int max)
        {
            typeName = name;
            totalSpawned = spawned;
            activeCount = active;
            availableInPool = available;
            maxPoolSize = max;
        }
    }

    /// <summary>Counters for one particle pool type, shown in the pool's inspector monitor. Same fields as <see cref="PoolStats"/>.</summary>
    [Serializable]
    public class ParticlePoolStats
    {
        public string typeName;
        public int totalSpawned;
        public int activeCount;
        public int availableInPool;
        public int maxPoolSize;

        public ParticlePoolStats(string name, int spawned, int active, int available, int max)
        {
            typeName = name;
            totalSpawned = spawned;
            activeCount = active;
            availableInPool = available;
            maxPoolSize = max;
        }
    }
}