using System;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools
{
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

        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            prefab != null ? prefab.name :
                                                       objectType.ToString();
    }

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

        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            prefab != null ? prefab.name :
                                                       particleType.ToString();
    }

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