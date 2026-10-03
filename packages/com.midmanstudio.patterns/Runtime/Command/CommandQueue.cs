// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "CommandQueue.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Command
{
    /// <summary>
    /// First-in, first-out queue of commands to run later, for spreading work over frames or
    /// deferring it to a safe point. Call <see cref="Process"/> from your own update loop.
    /// Not thread-safe; use it from one thread (the Unity main thread).
    /// </summary>
    public sealed class CommandQueue
    {
        private readonly Queue<ICommand> _queue = new Queue<ICommand>();
        private bool _processing;

        /// <summary>
        /// Raised when a command throws during <see cref="Process"/>. With a subscriber, the
        /// failed command is dropped and processing continues. With none, the exception is
        /// rethrown after the command is removed, so a failure is never silent.
        /// </summary>
        public event Action<ICommand, Exception> CommandFailed;

        public int  Count    => _queue.Count;
        public bool IsEmpty  => _queue.Count == 0;

        public void Enqueue(ICommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            _queue.Enqueue(command);
        }

        /// <summary>Convenience for queueing a delegate.</summary>
        public void Enqueue(Action action, string name = null) => Enqueue(new ActionCommand(action, name));

        /// <summary>Drops everything that has not run yet.</summary>
        public void Clear() => _queue.Clear();

        /// <summary>
        /// Runs up to <paramref name="maxCommands"/> of the commands that were queued when this
        /// call began, oldest first, and returns how many ran (including ones that threw).
        /// Commands queued by a running command wait for the next call, so a command that
        /// always queues another cannot keep one call from returning.
        /// </summary>
        public int Process(int maxCommands = int.MaxValue)
        {
            if (maxCommands < 0) throw new ArgumentOutOfRangeException(nameof(maxCommands));
            if (_processing) throw new InvalidOperationException("Process was called from inside a running command.");

            int toRun = Math.Min(_queue.Count, maxCommands);
            int ran = 0;
            _processing = true;
            try
            {
                while (ran < toRun)
                {
                    var command = _queue.Dequeue();
                    ran++;
                    try
                    {
                        command.Execute();
                    }
                    catch (Exception e)
                    {
                        var handler = CommandFailed;
                        if (handler == null) throw;
                        handler(command, e);
                    }
                }
            }
            finally
            {
                _processing = false;
            }
            return ran;
        }
    }
}
