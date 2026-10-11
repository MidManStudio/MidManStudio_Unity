// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "Broker.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>
    /// Central broker for a query type. Objects that want to change the answer to a question (a stat, a
    /// damage amount, a price) register a handler with the broker instead of linking to each other. Asking
    /// the question runs every registered handler in order over the same query, so each one sees the result
    /// of the ones before it. Handlers run lowest priority first; equal priorities run in registration
    /// order. A handler can end the chain early by returning <see cref="BrokerResult.Stop"/>.
    /// Not thread-safe; use it from one thread (the Unity main thread).
    /// </summary>
    public sealed class Broker<TQuery>
    {
        /// <summary>Deepest allowed nesting of <see cref="Run"/> calls on one broker.</summary>
        public const int MaxDepth = 16;

        private sealed class Entry : IDisposable
        {
            private Broker<TQuery> _owner;
            public readonly BrokerHandler<TQuery> Handler;
            public readonly int Priority;
            public readonly bool Timed;
            public readonly Action OnExpired;
            public float Remaining;
            public bool Active = true;

            public Entry(Broker<TQuery> owner, BrokerHandler<TQuery> handler, int priority,
                         float lifetime, Action onExpired)
            {
                _owner = owner;
                Handler = handler;
                Priority = priority;
                Timed = lifetime > 0f;
                Remaining = lifetime;
                OnExpired = onExpired;
            }

            public void Dispose()
            {
                if (!Active) return;
                Active = false;
                var owner = _owner;
                _owner = null;
                owner.Remove(this);
            }
        }

        // Replaced, never edited in place, so a run in progress keeps iterating the array it started with.
        private Entry[] _entries = new Entry[0];
        private int _depth;

        /// <summary>Number of handlers currently registered.</summary>
        public int Count => _entries.Length;

        /// <summary>
        /// Registers a handler. Dispose the returned handle to unregister it; that takes effect at once,
        /// even for a run in progress. The same delegate can be registered more than once and is then called
        /// once per registration.
        /// </summary>
        /// <param name="priority">Lower runs first. Equal priorities run in registration order.</param>
        /// <param name="lifetimeSeconds">0 means the handler stays until disposed. Above 0, <see cref="Tick"/> removes it once that much time has passed.</param>
        /// <param name="onExpired">Called once when <see cref="Tick"/> removes the handler. Not called when the handle is disposed.</param>
        public IDisposable Register(BrokerHandler<TQuery> handler, int priority = 0,
                                    float lifetimeSeconds = 0f, Action onExpired = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (float.IsNaN(lifetimeSeconds) || lifetimeSeconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));

            var entry = new Entry(this, handler, priority, lifetimeSeconds, onExpired);

            // Insert after the last entry whose priority is not higher, which keeps registration order stable.
            int at = _entries.Length;
            while (at > 0 && _entries[at - 1].Priority > priority) at--;

            var next = new Entry[_entries.Length + 1];
            Array.Copy(_entries, 0, next, 0, at);
            next[at] = entry;
            Array.Copy(_entries, at, next, at + 1, _entries.Length - at);
            _entries = next;
            return entry;
        }

        /// <summary>
        /// Runs the chain over <paramref name="query"/>, which handlers change in place. Returns
        /// <see cref="BrokerResult.Stop"/> if a handler ended the chain, otherwise
        /// <see cref="BrokerResult.Continue"/>. A handler may call <c>Run</c> on the same broker, up to
        /// <see cref="MaxDepth"/> levels deep. An exception from a handler ends the chain and propagates;
        /// the query keeps the changes made before it.
        /// </summary>
        public BrokerResult Run(object sender, ref TQuery query)
        {
            if (_depth >= MaxDepth)
                throw new InvalidOperationException(
                    "The broker was queried more than " + MaxDepth + " levels deep. " +
                    "A handler is probably asking for the value it is changing.");

            var snapshot = _entries;
            _depth++;
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var entry = snapshot[i];
                    if (!entry.Active) continue;
                    if (entry.Handler(sender, ref query) == BrokerResult.Stop) return BrokerResult.Stop;
                }
                return BrokerResult.Continue;
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>Runs the chain over a copy of <paramref name="seed"/> and returns the changed copy.</summary>
        public TQuery Query(object sender, TQuery seed)
        {
            Run(sender, ref seed);
            return seed;
        }

        /// <summary>
        /// Advances the lifetime of timed handlers by <paramref name="deltaTime"/> seconds and removes the
        /// ones that ran out. Handlers registered without a lifetime are not affected. Every
        /// <c>onExpired</c> callback runs even if an earlier one throws; the first exception is then rethrown.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || deltaTime < 0f) throw new ArgumentOutOfRangeException(nameof(deltaTime));

            var current = _entries;
            List<Entry> expired = null;
            for (int i = 0; i < current.Length; i++)
            {
                var entry = current[i];
                if (!entry.Active || !entry.Timed) continue;
                entry.Remaining -= deltaTime;
                if (entry.Remaining > 0f) continue;
                entry.Active = false;
                if (expired == null) expired = new List<Entry>();
                expired.Add(entry);
            }
            if (expired == null) return;

            var survivors = new Entry[current.Length - expired.Count];
            int n = 0;
            for (int i = 0; i < current.Length; i++)
                if (current[i].Active) survivors[n++] = current[i];
            _entries = survivors;

            ExceptionDispatchInfo failure = null;
            for (int i = 0; i < expired.Count; i++)
            {
                var callback = expired[i].OnExpired;
                if (callback == null) continue;
                try { callback(); }
                catch (Exception e) { if (failure == null) failure = ExceptionDispatchInfo.Capture(e); }
            }
            if (failure != null) failure.Throw();
        }

        /// <summary>Unregisters every handler. Their handles become inert.</summary>
        public void Clear()
        {
            var old = _entries;
            _entries = new Entry[0];
            for (int i = 0; i < old.Length; i++) old[i].Active = false;
        }

        private void Remove(Entry entry)
        {
            int index = Array.IndexOf(_entries, entry);
            if (index < 0) return;
            var next = new Entry[_entries.Length - 1];
            Array.Copy(_entries, 0, next, 0, index);
            Array.Copy(_entries, index + 1, next, index, _entries.Length - index - 1);
            _entries = next;
        }
    }
}
