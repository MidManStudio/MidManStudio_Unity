// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/type-object.md, section "ObjectType.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.TypeObject
{
    /// <summary>
    /// A data-defined "class": a name, an optional parent type, and named attributes. Reading an attribute
    /// looks at this type first and then up the parent chain, so a child type only states what differs from
    /// its parent. Use it for things designers define in data (enemy breeds, item kinds) instead of one C#
    /// subclass per kind. The parent is fixed at construction, so a chain can never loop.
    /// </summary>
    public sealed class ObjectType
    {
        private readonly Dictionary<string, object> _own = new Dictionary<string, object>();

        public ObjectType(string name, ObjectType parent = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A type needs a name.", nameof(name));
            Name = name;
            Parent = parent;
        }

        public string Name { get; private set; }

        public ObjectType Parent { get; private set; }

        /// <summary>Sets this type's own value for the attribute, hiding any parent value.</summary>
        public void Set<T>(string attribute, T value)
        {
            CheckName(attribute);
            _own[attribute] = value;
        }

        /// <summary>Removes this type's own value so the parent's value (if any) shows through again.</summary>
        public bool Remove(string attribute) { return _own.Remove(attribute); }

        public bool HasOwn(string attribute) { return _own.ContainsKey(attribute); }

        /// <summary>True when this type or any ancestor defines the attribute.</summary>
        public bool Has(string attribute)
        {
            for (ObjectType t = this; t != null; t = t.Parent)
                if (t._own.ContainsKey(attribute)) return true;
            return false;
        }

        /// <exception cref="InvalidCastException">The attribute exists but its stored type is not exactly <typeparamref name="T"/>.</exception>
        public bool TryGet<T>(string attribute, out T value)
        {
            for (ObjectType t = this; t != null; t = t.Parent)
            {
                object stored;
                if (t._own.TryGetValue(attribute, out stored))
                {
                    value = Cast<T>(attribute, stored);
                    return true;
                }
            }
            value = default(T);
            return false;
        }

        /// <exception cref="KeyNotFoundException">Neither this type nor an ancestor defines the attribute.</exception>
        public T Get<T>(string attribute)
        {
            T value;
            if (!TryGet(attribute, out value))
                throw new KeyNotFoundException("Type '" + Name + "' has no attribute '" + attribute + "'.");
            return value;
        }

        public T Get<T>(string attribute, T fallback)
        {
            T value;
            return TryGet(attribute, out value) ? value : fallback;
        }

        /// <summary>True when <paramref name="other"/> is this type or one of its ancestors.</summary>
        public bool IsA(ObjectType other)
        {
            for (ObjectType t = this; t != null; t = t.Parent)
                if (ReferenceEquals(t, other)) return true;
            return false;
        }

        /// <summary>Every attribute name visible from this type, each once, own names first.</summary>
        public IEnumerable<string> AttributeNames()
        {
            var seen = new HashSet<string>();
            for (ObjectType t = this; t != null; t = t.Parent)
                foreach (string key in t._own.Keys)
                    if (seen.Add(key)) yield return key;
        }

        internal static T Cast<T>(string attribute, object stored)
        {
            if (stored is T) return (T)stored;
            if (stored == null && default(T) == null) return default(T);
            string actual = stored == null ? "null" : stored.GetType().Name;
            throw new InvalidCastException("Attribute '" + attribute + "' is " + actual + ", not " + typeof(T).Name + ".");
        }

        private static void CheckName(string attribute)
        {
            if (string.IsNullOrEmpty(attribute)) throw new ArgumentException("An attribute needs a name.", nameof(attribute));
        }
    }
}
