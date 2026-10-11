// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/decorator.md, section "DecoratorStack.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Decorator
{
    /// <summary>
    /// An ordered list of decorators that can be added and removed at runtime. <see cref="Build"/> wraps a
    /// core object in every decorator, first registered innermost, so the last registered one is the one
    /// callers talk to. Build again after the list changes; the stack does not track objects it already built.
    /// </summary>
    public sealed class DecoratorStack<T> where T : class
    {
        private sealed class Entry : IDisposable
        {
            private DecoratorStack<T> _owner;
            public readonly Func<T, T> Wrap;

            public Entry(DecoratorStack<T> owner, Func<T, T> wrap)
            {
                _owner = owner;
                Wrap = wrap;
            }

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner._entries.Remove(this);
            }
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public int Count => _entries.Count;

        /// <summary>Adds a decorator on the outside. It receives the object built so far and returns the wrapper. Dispose the handle to remove it.</summary>
        public IDisposable Add(Func<T, T> decorator)
        {
            if (decorator == null) throw new ArgumentNullException(nameof(decorator));
            var entry = new Entry(this, decorator);
            _entries.Add(entry);
            return entry;
        }

        /// <exception cref="InvalidOperationException">A decorator returned null.</exception>
        public T Build(T core)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            T current = core;
            for (int i = 0; i < _entries.Count; i++)
            {
                current = _entries[i].Wrap(current);
                if (current == null) throw new InvalidOperationException("Decorator number " + i + " returned null.");
            }
            return current;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
