// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/prototype.md, section "PrototypeRegistry.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Prototype
{
    /// <summary>
    /// Keeps configured template objects by key and creates new objects by cloning them. Set a template up
    /// once (stats, parts, settings), then create as many copies as needed without repeating the setup.
    /// </summary>
    public sealed class PrototypeRegistry<TKey, T> where T : class, IPrototype<T>
    {
        private readonly Dictionary<TKey, T> _prototypes;

        public PrototypeRegistry(IEqualityComparer<TKey> comparer = null)
        {
            _prototypes = new Dictionary<TKey, T>(comparer);
        }

        public int Count => _prototypes.Count;

        /// <exception cref="ArgumentException">The key is already registered. Use <see cref="Set"/> to replace.</exception>
        public void Register(TKey key, T prototype)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            if (_prototypes.ContainsKey(key)) throw new ArgumentException("A prototype is already registered for '" + key + "'.");
            _prototypes.Add(key, prototype);
        }

        /// <summary>Registers the prototype, replacing any earlier one under the key.</summary>
        public void Set(TKey key, T prototype)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            _prototypes[key] = prototype;
        }

        public bool Unregister(TKey key) { return _prototypes.Remove(key); }

        public bool Contains(TKey key) { return _prototypes.ContainsKey(key); }

        /// <summary>Returns a clone of the prototype under <paramref name="key"/>.</summary>
        /// <exception cref="KeyNotFoundException">No prototype is registered under the key.</exception>
        /// <exception cref="InvalidOperationException">Clone returned null or the prototype itself.</exception>
        public T Create(TKey key)
        {
            T prototype;
            if (!_prototypes.TryGetValue(key, out prototype))
                throw new KeyNotFoundException("No prototype is registered for '" + key + "'.");
            return CloneOf(prototype, key);
        }

        /// <summary>Clones the prototype and passes the copy to <paramref name="customize"/> before returning it.</summary>
        public T Create(TKey key, Action<T> customize)
        {
            if (customize == null) throw new ArgumentNullException(nameof(customize));
            T clone = Create(key);
            customize(clone);
            return clone;
        }

        public bool TryCreate(TKey key, out T clone)
        {
            T prototype;
            if (!_prototypes.TryGetValue(key, out prototype)) { clone = null; return false; }
            clone = CloneOf(prototype, key);
            return true;
        }

        private static T CloneOf(T prototype, TKey key)
        {
            T clone = prototype.Clone();
            if (clone == null) throw new InvalidOperationException("Clone returned null for '" + key + "'.");
            if (ReferenceEquals(clone, prototype))
                throw new InvalidOperationException("Clone returned the prototype itself for '" + key + "'.");
            return clone;
        }
    }
}
