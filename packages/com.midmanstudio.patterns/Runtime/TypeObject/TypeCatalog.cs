// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/type-object.md, section "TypeCatalog.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.TypeObject
{
    /// <summary>
    /// Registry of <see cref="ObjectType"/> by name. Types are defined parent first, so a type can only
    /// extend one that already exists.
    /// </summary>
    public sealed class TypeCatalog
    {
        private readonly Dictionary<string, ObjectType> _types = new Dictionary<string, ObjectType>();

        public int Count => _types.Count;

        public IEnumerable<string> Names => _types.Keys;

        /// <summary>Creates and registers a type, optionally extending an existing one.</summary>
        /// <exception cref="ArgumentException">The name is taken, or the parent name is unknown.</exception>
        public ObjectType Define(string name, string parentName = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A type needs a name.", nameof(name));
            if (_types.ContainsKey(name)) throw new ArgumentException("A type named '" + name + "' already exists.", nameof(name));

            ObjectType parent = null;
            if (parentName != null && !_types.TryGetValue(parentName, out parent))
                throw new ArgumentException("Unknown parent type '" + parentName + "'.", nameof(parentName));

            var type = new ObjectType(name, parent);
            _types.Add(name, type);
            return type;
        }

        /// <exception cref="KeyNotFoundException">No type has that name.</exception>
        public ObjectType Get(string name)
        {
            ObjectType type;
            if (!_types.TryGetValue(name, out type)) throw new KeyNotFoundException("No type named '" + name + "'.");
            return type;
        }

        public bool TryGet(string name, out ObjectType type) { return _types.TryGetValue(name, out type); }

        public bool Contains(string name) { return _types.ContainsKey(name); }
    }
}
