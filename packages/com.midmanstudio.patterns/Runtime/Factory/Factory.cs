// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/factory.md, section "Factory.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Factory
{
    /// <summary>
    /// Creates objects by key. Each key maps to a creator function, so callers ask for "orc" or "fireball"
    /// without knowing the concrete class, and new kinds are added by registering a creator. Every
    /// <see cref="Create"/> call runs the creator again; for shared instances use the DI container or a flyweight.
    /// </summary>
    public sealed class Factory<TKey, TProduct>
    {
        private readonly Dictionary<TKey, Func<TProduct>> _creators;

        public Factory(IEqualityComparer<TKey> comparer = null)
        {
            _creators = new Dictionary<TKey, Func<TProduct>>(comparer);
        }

        public int Count => _creators.Count;

        public IEnumerable<TKey> Keys => _creators.Keys;

        /// <exception cref="ArgumentException">The key is already registered. Use <see cref="Set"/> to replace.</exception>
        public void Register(TKey key, Func<TProduct> creator)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            if (_creators.ContainsKey(key)) throw new ArgumentException("A creator is already registered for '" + key + "'.");
            _creators.Add(key, creator);
        }

        public void Set(TKey key, Func<TProduct> creator)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            _creators[key] = creator;
        }

        public bool Unregister(TKey key) { return _creators.Remove(key); }

        public bool Contains(TKey key) { return _creators.ContainsKey(key); }

        /// <exception cref="KeyNotFoundException">No creator is registered for the key.</exception>
        /// <exception cref="InvalidOperationException">The creator returned null.</exception>
        public TProduct Create(TKey key)
        {
            Func<TProduct> creator;
            if (!_creators.TryGetValue(key, out creator))
                throw new KeyNotFoundException("No creator is registered for '" + key + "'.");
            return Run(creator, key);
        }

        public bool TryCreate(TKey key, out TProduct product)
        {
            Func<TProduct> creator;
            if (!_creators.TryGetValue(key, out creator)) { product = default(TProduct); return false; }
            product = Run(creator, key);
            return true;
        }

        private static TProduct Run(Func<TProduct> creator, TKey key)
        {
            TProduct product = creator();
            if (ReferenceEquals(product, null)) throw new InvalidOperationException("The creator for '" + key + "' returned null.");
            return product;
        }
    }
}
