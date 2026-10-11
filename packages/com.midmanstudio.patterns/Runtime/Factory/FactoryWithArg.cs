// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/factory.md, section "FactoryWithArg.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Factory
{
    /// <summary>
    /// Like <see cref="Factory{TKey, TProduct}"/>, but every creator receives an argument, for example the
    /// position, owner or settings of the object being created.
    /// </summary>
    public sealed class Factory<TKey, TArg, TProduct>
    {
        private readonly Dictionary<TKey, Func<TArg, TProduct>> _creators;

        public Factory(IEqualityComparer<TKey> comparer = null)
        {
            _creators = new Dictionary<TKey, Func<TArg, TProduct>>(comparer);
        }

        public int Count => _creators.Count;

        public IEnumerable<TKey> Keys => _creators.Keys;

        /// <exception cref="ArgumentException">The key is already registered. Use <see cref="Set"/> to replace.</exception>
        public void Register(TKey key, Func<TArg, TProduct> creator)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            if (_creators.ContainsKey(key)) throw new ArgumentException("A creator is already registered for '" + key + "'.");
            _creators.Add(key, creator);
        }

        public void Set(TKey key, Func<TArg, TProduct> creator)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            _creators[key] = creator;
        }

        public bool Unregister(TKey key) { return _creators.Remove(key); }

        public bool Contains(TKey key) { return _creators.ContainsKey(key); }

        /// <exception cref="KeyNotFoundException">No creator is registered for the key.</exception>
        /// <exception cref="InvalidOperationException">The creator returned null.</exception>
        public TProduct Create(TKey key, TArg arg)
        {
            Func<TArg, TProduct> creator;
            if (!_creators.TryGetValue(key, out creator))
                throw new KeyNotFoundException("No creator is registered for '" + key + "'.");
            return Run(creator, key, arg);
        }

        public bool TryCreate(TKey key, TArg arg, out TProduct product)
        {
            Func<TArg, TProduct> creator;
            if (!_creators.TryGetValue(key, out creator)) { product = default(TProduct); return false; }
            product = Run(creator, key, arg);
            return true;
        }

        private static TProduct Run(Func<TArg, TProduct> creator, TKey key, TArg arg)
        {
            TProduct product = creator(arg);
            if (ReferenceEquals(product, null)) throw new InvalidOperationException("The creator for '" + key + "' returned null.");
            return product;
        }
    }
}
