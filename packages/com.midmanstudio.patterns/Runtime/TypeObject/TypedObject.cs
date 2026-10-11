// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/type-object.md, section "TypedObject.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.TypeObject
{
    /// <summary>
    /// An instance of an <see cref="ObjectType"/>. Attribute reads use the instance's own values first (a
    /// damaged enemy, a modified item) and fall back to the type and its ancestors.
    /// </summary>
    public sealed class TypedObject
    {
        private readonly Dictionary<string, object> _own = new Dictionary<string, object>();

        public TypedObject(ObjectType type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            Type = type;
        }

        public ObjectType Type { get; private set; }

        public void Set<T>(string attribute, T value)
        {
            if (string.IsNullOrEmpty(attribute)) throw new ArgumentException("An attribute needs a name.", nameof(attribute));
            _own[attribute] = value;
        }

        /// <summary>Removes the instance's own value so the type's value shows through again.</summary>
        public bool Remove(string attribute) { return _own.Remove(attribute); }

        public bool HasOwn(string attribute) { return _own.ContainsKey(attribute); }

        public bool Has(string attribute) { return _own.ContainsKey(attribute) || Type.Has(attribute); }

        /// <exception cref="InvalidCastException">The attribute exists but its stored type is not exactly <typeparamref name="T"/>.</exception>
        public bool TryGet<T>(string attribute, out T value)
        {
            object stored;
            if (_own.TryGetValue(attribute, out stored))
            {
                value = ObjectType.Cast<T>(attribute, stored);
                return true;
            }
            return Type.TryGet(attribute, out value);
        }

        /// <exception cref="KeyNotFoundException">Neither the instance nor its type defines the attribute.</exception>
        public T Get<T>(string attribute)
        {
            T value;
            if (!TryGet(attribute, out value))
                throw new KeyNotFoundException("Object of type '" + Type.Name + "' has no attribute '" + attribute + "'.");
            return value;
        }

        public T Get<T>(string attribute, T fallback)
        {
            T value;
            return TryGet(attribute, out value) ? value : fallback;
        }
    }
}
