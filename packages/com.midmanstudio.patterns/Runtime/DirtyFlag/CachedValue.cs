// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dirty-flag.md, section "CachedValue.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.DirtyFlag
{
    /// <summary>
    /// A value that is computed on first read and again only after it was invalidated or one of its
    /// dependencies changed. Dependencies are <see cref="IVersioned"/> objects (a <see cref="DirtyFlag"/> or
    /// another <c>CachedValue</c>), so chains of derived values recompute only the parts that are stale.
    /// Reading <see cref="Value"/> or <see cref="Version"/> brings the value up to date first.
    /// </summary>
    public sealed class CachedValue<T> : IVersioned
    {
        private readonly Func<T> _compute;
        private readonly IVersioned[] _dependencies;
        private readonly int[] _seen;
        private T _value;
        private bool _dirty = true;
        private bool _refreshing;
        private int _version;

        public CachedValue(Func<T> compute, params IVersioned[] dependencies)
        {
            if (compute == null) throw new ArgumentNullException(nameof(compute));
            _compute = compute;
            _dependencies = dependencies ?? new IVersioned[0];
            for (int i = 0; i < _dependencies.Length; i++)
                if (_dependencies[i] == null) throw new ArgumentNullException(nameof(dependencies));
            _seen = new int[_dependencies.Length];
        }

        /// <summary>The current value, recomputed first if it is stale.</summary>
        public T Value
        {
            get { Refresh(); return _value; }
        }

        /// <summary>Raised each time the value is recomputed. Reading it brings the value up to date.</summary>
        public int Version
        {
            get { Refresh(); return _version; }
        }

        /// <summary>True when the next read will recompute. Does not recompute and does not touch dependencies' values.</summary>
        public bool IsInvalidated => _dirty;

        /// <summary>Forces a recompute on the next read.</summary>
        public void Invalidate() { _dirty = true; }

        private void Refresh()
        {
            if (_refreshing) throw new InvalidOperationException("A CachedValue depends on itself.");
            _refreshing = true;
            try
            {
                if (!_dirty && !DependenciesChanged()) return;

                for (int i = 0; i < _dependencies.Length; i++) _seen[i] = _dependencies[i].Version;
                _value = _compute();      // an exception leaves the value dirty, so the next read retries
                _dirty = false;
                unchecked { _version++; }
            }
            finally
            {
                _refreshing = false;
            }
        }

        private bool DependenciesChanged()
        {
            for (int i = 0; i < _dependencies.Length; i++)
                if (_dependencies[i].Version != _seen[i]) return true;
            return false;
        }
    }
}
