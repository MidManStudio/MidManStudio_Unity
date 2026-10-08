// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/state.md, section "StateMachine.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.State
{
    /// <summary>
    /// Stack-based state machine. The state on top of the stack is the current one. ChangeState
    /// replaces it, PushState covers it with another, and PopState uncovers the one below.
    /// A transition requested from inside a state callback (Enter, Exit, Update, Pause, Resume) or
    /// from a StateChanged handler is queued and applied after that callback returns, so a state is
    /// never changed while one of its own methods is still running.
    /// Not thread-safe; use it from one thread (the Unity main thread).
    /// </summary>
    public sealed class StateMachine<TContext>
    {
        private enum RequestKind { Change, Push, Pop, Stop }

        private struct Request
        {
            public RequestKind Kind;
            public IState<TContext> State;
        }

        private sealed class Transition
        {
            public IState<TContext> From;
            public IState<TContext> To;
            public Func<TContext, bool> Condition;
        }

        private readonly TContext _context;
        private readonly int _maxChainedTransitions;
        private readonly List<IState<TContext>> _stack = new List<IState<TContext>>();
        private readonly Queue<Request> _pending = new Queue<Request>();
        private readonly List<Transition> _transitions = new List<Transition>();
        private readonly List<Transition> _anyTransitions = new List<Transition>();
        private IState<TContext> _previous;
        private bool _busy;

        /// <summary>
        /// Raised after a change, push, pop or stop completes, with the state that was current before
        /// and the one that is current now. Either can be null (first start, or stop).
        /// </summary>
        public event Action<IState<TContext>, IState<TContext>> StateChanged;

        /// <param name="context">Passed to every state callback and every transition condition.</param>
        /// <param name="maxChainedTransitions">
        /// Most transitions applied in one go. States that keep sending each other back and forth
        /// hit this limit and throw instead of looping forever.
        /// </param>
        public StateMachine(TContext context, int maxChainedTransitions = 64)
        {
            if (maxChainedTransitions < 1) throw new ArgumentOutOfRangeException(nameof(maxChainedTransitions));
            _context = context;
            _maxChainedTransitions = maxChainedTransitions;
        }

        public TContext Context => _context;

        /// <summary>The state on top of the stack, or null when the machine has not started or was stopped.</summary>
        public IState<TContext> Current => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        /// <summary>The state that was current just before the most recent transition completed.</summary>
        public IState<TContext> Previous => _previous;

        public int  StackDepth => _stack.Count;
        public bool IsRunning  => _stack.Count > 0;

        /// <summary>True when the current state is a <typeparamref name="TState"/>.</summary>
        public bool IsIn<TState>() where TState : class, IState<TContext> => Current is TState;

        /// <summary>
        /// Replaces the current state: the old one is exited, then the new one is entered. Changing to the
        /// state that is already current does nothing. With no current state this starts the machine.
        /// </summary>
        public void ChangeState(IState<TContext> next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            Submit(new Request { Kind = RequestKind.Change, State = next });
        }

        /// <summary>Covers the current state with <paramref name="next"/>. See <see cref="IPausableState{TContext}"/>.</summary>
        public void PushState(IState<TContext> next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            Submit(new Request { Kind = RequestKind.Push, State = next });
        }

        /// <summary>
        /// Exits the top state and uncovers the one below it. Throws <see cref="InvalidOperationException"/>
        /// when the stack holds one state or none, since there would be nothing to return to. If the
        /// request was queued from inside a callback, the exception comes from the call that applies it.
        /// </summary>
        public void PopState() => Submit(new Request { Kind = RequestKind.Pop });

        /// <summary>Exits every state on the stack, top first, and leaves the machine not running.</summary>
        public void Stop() => Submit(new Request { Kind = RequestKind.Stop });

        /// <summary>
        /// Calls Update on the current state, then checks the transitions. A transition that fires is
        /// applied before this call returns. Throws if called from inside a state callback.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_busy) throw new InvalidOperationException("Update was called from inside a state callback.");
            if (_stack.Count == 0) return;

            _busy = true;
            try
            {
                _stack[_stack.Count - 1].Update(_context, deltaTime);
                EvaluateTransitions();
            }
            catch
            {
                _pending.Clear();
                _busy = false;
                throw;
            }
            _busy = false;
            if (_pending.Count > 0) Flush();
        }

        /// <summary>
        /// Changes to <paramref name="to"/> during Update when <paramref name="condition"/> is true while
        /// <paramref name="from"/> is current. Conditions are checked in the order they were added and the
        /// first true one wins.
        /// </summary>
        public void AddTransition(IState<TContext> from, IState<TContext> to, Func<TContext, bool> condition)
        {
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            if (condition == null) throw new ArgumentNullException(nameof(condition));
            _transitions.Add(new Transition { From = from, To = to, Condition = condition });
        }

        /// <summary>
        /// Like <see cref="AddTransition"/>, but from whatever state is current. These are checked before
        /// the per-state transitions. One that targets the current state is skipped.
        /// </summary>
        public void AddAnyTransition(IState<TContext> to, Func<TContext, bool> condition)
        {
            if (to == null) throw new ArgumentNullException(nameof(to));
            if (condition == null) throw new ArgumentNullException(nameof(condition));
            _anyTransitions.Add(new Transition { To = to, Condition = condition });
        }

        public void ClearTransitions()
        {
            _transitions.Clear();
            _anyTransitions.Clear();
        }

        private void EvaluateTransitions()
        {
            var current = Current;
            if (current == null) return;

            for (int i = 0; i < _anyTransitions.Count; i++)
            {
                var t = _anyTransitions[i];
                if (ReferenceEquals(t.To, current)) continue;
                if (t.Condition(_context))
                {
                    _pending.Enqueue(new Request { Kind = RequestKind.Change, State = t.To });
                    return;
                }
            }

            for (int i = 0; i < _transitions.Count; i++)
            {
                var t = _transitions[i];
                if (!ReferenceEquals(t.From, current)) continue;
                if (t.Condition(_context))
                {
                    _pending.Enqueue(new Request { Kind = RequestKind.Change, State = t.To });
                    return;
                }
            }
        }

        private void Submit(Request request)
        {
            _pending.Enqueue(request);
            if (!_busy) Flush();
        }

        private void Flush()
        {
            _busy = true;
            int applied = 0;
            try
            {
                while (_pending.Count > 0)
                {
                    if (++applied > _maxChainedTransitions)
                        throw new InvalidOperationException(
                            "More than " + _maxChainedTransitions + " transitions were applied in a row. " +
                            "States are probably sending each other back and forth.");
                    Apply(_pending.Dequeue());
                }
            }
            catch
            {
                _pending.Clear();
                throw;
            }
            finally
            {
                _busy = false;
            }
        }

        private void Apply(Request request)
        {
            switch (request.Kind)
            {
                case RequestKind.Change: ApplyChange(request.State); break;
                case RequestKind.Push:   ApplyPush(request.State);   break;
                case RequestKind.Pop:    ApplyPop();                 break;
                default:                 ApplyStop();                break;
            }
        }

        // If Exit throws, the old state stays current. If Enter throws, the new state is already
        // current and the exception propagates.
        private void ApplyChange(IState<TContext> next)
        {
            var old = Current;
            if (ReferenceEquals(old, next)) return;

            if (old != null) old.Exit(_context);

            if (_stack.Count > 0) _stack[_stack.Count - 1] = next;
            else                  _stack.Add(next);
            _previous = old;

            next.Enter(_context);
            StateChanged?.Invoke(old, next);
        }

        // If Pause or Exit of the covered state throws, nothing is pushed.
        private void ApplyPush(IState<TContext> next)
        {
            var old = Current;
            if (old != null)
            {
                var pausable = old as IPausableState<TContext>;
                if (pausable != null) pausable.Pause(_context);
                else                  old.Exit(_context);
            }

            _stack.Add(next);
            _previous = old;

            next.Enter(_context);
            StateChanged?.Invoke(old, next);
        }

        // If Exit of the top state throws, it stays on the stack. If Resume or Enter of the uncovered
        // state throws, that state is already current and the exception propagates.
        private void ApplyPop()
        {
            if (_stack.Count <= 1)
                throw new InvalidOperationException("There is no earlier state to return to.");

            var top = _stack[_stack.Count - 1];
            top.Exit(_context);
            _stack.RemoveAt(_stack.Count - 1);

            var resumed = _stack[_stack.Count - 1];
            _previous = top;

            var pausable = resumed as IPausableState<TContext>;
            if (pausable != null) pausable.Resume(_context);
            else                  resumed.Enter(_context);
            StateChanged?.Invoke(top, resumed);
        }

        // Each state is removed as soon as its Exit returns, so a throwing Exit leaves the states below
        // it still on the stack.
        private void ApplyStop()
        {
            if (_stack.Count == 0) return;

            var oldTop = _stack[_stack.Count - 1];
            while (_stack.Count > 0)
            {
                var state = _stack[_stack.Count - 1];
                state.Exit(_context);
                _stack.RemoveAt(_stack.Count - 1);
            }
            _previous = oldTop;
            StateChanged?.Invoke(oldTop, null);
        }
    }
}
