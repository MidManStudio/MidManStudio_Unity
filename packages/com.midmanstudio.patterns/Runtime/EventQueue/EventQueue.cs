// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/event-queue.md, section "EventQueue.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace MidManStudio.Patterns.EventQueue
{
    /// <summary>
    /// Typed event queue that separates posting an event from delivering it. <see cref="Post"/> only
    /// stores the event; <see cref="Process"/> delivers it, oldest first, to every subscriber. Use it to
    /// move delivery to a known point in the frame, to spread it over several frames, or to keep a handler
    /// from running in the middle of the code that raised the event.
    /// Not thread-safe; use it from one thread (the Unity main thread).
    /// </summary>
    public sealed class EventQueue<TEvent>
    {
        private sealed class Subscription : IDisposable
        {
            private EventQueue<TEvent> _owner;
            public readonly Action<TEvent> Handler;
            public bool Active = true;

            public Subscription(EventQueue<TEvent> owner, Action<TEvent> handler)
            {
                _owner = owner;
                Handler = handler;
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

        private readonly int _capacity;
        private readonly EventQueueOverflow _overflow;
        private TEvent[] _buffer;
        private int _head;
        private int _count;
        private int _dropped;
        private bool _processing;

        // Replaced, never edited in place, so a dispatch in progress keeps iterating the array it started with.
        private Subscription[] _subscriptions = new Subscription[0];

        /// <summary>
        /// Raised when a handler throws during <see cref="Process"/>, with the event and the exception.
        /// With a subscriber, delivery carries on. With none, the exceptions are rethrown once every handler
        /// has seen the event.
        /// </summary>
        public event Action<TEvent, Exception> HandlerFailed;

        /// <param name="capacity">Most events held at once. 0 means unbounded: the queue grows as needed.</param>
        /// <param name="overflow">What to do when a bounded queue is full. Ignored when unbounded.</param>
        public EventQueue(int capacity = 0, EventQueueOverflow overflow = EventQueueOverflow.DropOldest)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _overflow = overflow;
            _buffer = new TEvent[capacity > 0 ? capacity : 16];
        }

        public int Capacity        => _capacity;
        public int Count           => _count;
        public bool IsEmpty        => _count == 0;
        public int SubscriberCount => _subscriptions.Length;

        /// <summary>Events discarded by the overflow policy since the queue was created.</summary>
        public int DroppedCount    => _dropped;

        /// <summary>
        /// Adds an event to the queue. Returns false only when the event itself was discarded
        /// (<see cref="EventQueueOverflow.DropNewest"/> on a full queue).
        /// </summary>
        public bool Post(TEvent evt)
        {
            if (_capacity > 0 && _count == _capacity)
            {
                switch (_overflow)
                {
                    case EventQueueOverflow.DropNewest:
                        _dropped++;
                        return false;
                    case EventQueueOverflow.DropOldest:
                        _buffer[_head] = default(TEvent);
                        _head = (_head + 1) % _buffer.Length;
                        _count--;
                        _dropped++;
                        break;
                    default:
                        throw new InvalidOperationException("The event queue is full.");
                }
            }
            else if (_capacity == 0 && _count == _buffer.Length)
            {
                Grow();
            }

            _buffer[(_head + _count) % _buffer.Length] = evt;
            _count++;
            return true;
        }

        /// <summary>
        /// Subscribes a handler. Handlers run in subscription order. Dispose the returned handle to
        /// unsubscribe; that takes effect at once, even for an event being delivered right now.
        /// The same delegate can be subscribed more than once and is then called once per subscription.
        /// </summary>
        public IDisposable Subscribe(Action<TEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var subscription = new Subscription(this, handler);
            var next = new Subscription[_subscriptions.Length + 1];
            Array.Copy(_subscriptions, next, _subscriptions.Length);
            next[next.Length - 1] = subscription;
            _subscriptions = next;
            return subscription;
        }

        /// <summary>Discards every queued event without delivering it. Not counted as dropped.</summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Delivers up to <paramref name="maxEvents"/> of the events that were queued when this call began,
        /// oldest first, and returns how many were delivered. Events posted by a handler wait for the next
        /// call, so a handler that always posts another event cannot keep one call from returning.
        /// Throws if called from inside a handler.
        /// </summary>
        public int Process(int maxEvents = int.MaxValue)
        {
            if (maxEvents < 0) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (_processing) throw new InvalidOperationException("Process was called from inside an event handler.");

            int toRun = Math.Min(_count, maxEvents);
            int delivered = 0;
            _processing = true;
            try
            {
                while (delivered < toRun)
                {
                    var evt = Dequeue();
                    delivered++;
                    Deliver(evt);
                }
            }
            finally
            {
                _processing = false;
            }
            return delivered;
        }

        private TEvent Dequeue()
        {
            var evt = _buffer[_head];
            _buffer[_head] = default(TEvent);
            _head = (_head + 1) % _buffer.Length;
            _count--;
            return evt;
        }

        private void Deliver(TEvent evt)
        {
            var subscriptions = _subscriptions;
            List<Exception> errors = null;

            for (int i = 0; i < subscriptions.Length; i++)
            {
                var subscription = subscriptions[i];
                if (!subscription.Active) continue;

                try
                {
                    subscription.Handler(evt);
                }
                catch (Exception e)
                {
                    var onFailed = HandlerFailed;
                    if (onFailed != null)
                    {
                        onFailed(evt, e);
                    }
                    else
                    {
                        if (errors == null) errors = new List<Exception>();
                        errors.Add(e);
                    }
                }
            }

            if (errors == null) return;
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            else throw new AggregateException("More than one event handler failed.", errors);
        }

        private void Grow()
        {
            var bigger = new TEvent[_buffer.Length * 2];
            for (int i = 0; i < _count; i++) bigger[i] = _buffer[(_head + i) % _buffer.Length];
            _buffer = bigger;
            _head = 0;
        }

        private void Remove(Subscription subscription)
        {
            int index = Array.IndexOf(_subscriptions, subscription);
            if (index < 0) return;
            var next = new Subscription[_subscriptions.Length - 1];
            Array.Copy(_subscriptions, 0, next, 0, index);
            Array.Copy(_subscriptions, index + 1, next, index, _subscriptions.Length - index - 1);
            _subscriptions = next;
        }
    }
}
