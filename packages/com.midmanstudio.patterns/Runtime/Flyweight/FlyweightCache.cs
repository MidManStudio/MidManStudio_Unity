// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/flyweight.md, section "FlyweightCache.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Flyweight
{
    /// <summary>
    /// Shares one immutable instance per key. Many objects ask for the same shared data (a mesh, a stats
    /// table, a sprite sheet) and get the same instance back; it is created the first time a key is asked for.
    /// The cached objects must not be changed by their users.
    /// </summary>
    public sealed class FlyweightCache<TKey, TShared>
    {
        private readonly Dictionary<TKey, TShared> _items;
        private readonly HashSet<TKey> _creating;
        private readonly Func<TKey, TShared> _factory;

        public FlyweightCache(Func<TKey, TShared> factory, IEqualityComparer<TKey> comparer = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _factory = factory;
            _items = new Dictionary<TKey, TShared>(comparer);
            _creating = new HashSet<TKey>(comparer);
        }

        /// <summary>Number of shared instances currently held.</summary>
        public int Count => _items.Count;

        /// <summary>How many times the factory has run, including instances that were removed since.</summary>
        public int CreatedCount { get; private set; }

        /// <summary>
        /// Returns the shared instance for <paramref name="key"/>, creating it on first use. A factory that
        /// throws or returns null caches nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">The factory returned null, or asked for its own key.</exception>
        public TShared Get(TKey key)
        {
            TShared shared;
            if (_items.TryGetValue(key, out shared)) return shared;

            if (!_creating.Add(key))
                throw new InvalidOperationException("The factory for key '" + key + "' asked for the same key.");
            try
            {
                shared = _factory(key);
            }
            finally
            {
                _creating.Remove(key);
            }

            if (ReferenceEquals(shared, null))
                throw new InvalidOperationException("The factory returned null for key '" + key + "'.");

            CreatedCount++;
            _items[key] = shared;
            return shared;
        }

        public bool TryGet(TKey key, out TShared shared) { return _items.TryGetValue(key, out shared); }

        public bool Contains(TKey key) { return _items.ContainsKey(key); }

        /// <summary>Forgets the instance. Users that already hold it keep it; the next <see cref="Get"/> creates a new one.</summary>
        public bool Remove(TKey key) { return _items.Remove(key); }

        public void Clear() { _items.Clear(); }
    }
}
