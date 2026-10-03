// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "CommandHistory.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Command
{
    /// <summary>
    /// Undo/redo stack. <see cref="Execute"/> runs a command and records it; <see cref="Undo"/>
    /// and <see cref="Redo"/> walk the stack. Executing a new command clears the redo stack.
    /// Not thread-safe; use it from one thread (the Unity main thread).
    /// </summary>
    public sealed class CommandHistory
    {
        private readonly List<IUndoableCommand> _undo = new List<IUndoableCommand>();
        private readonly List<IUndoableCommand> _redo = new List<IUndoableCommand>();
        private int  _capacity;
        private bool _busy;
        private bool _breakMerge = true;
        private List<IUndoableCommand> _group;
        private string _groupName;
        private int _groupId;

        /// <summary>Raised after the history changes (execute, undo, redo, clear, group end).</summary>
        public event Action Changed;

        /// <param name="capacity">Most undo steps kept. The oldest is dropped past that. 0 means unlimited.</param>
        public CommandHistory(int capacity = 100)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int  Capacity => _capacity;
        public int  UndoCount => _undo.Count;
        public int  RedoCount => _redo.Count;
        public bool CanUndo => _undo.Count > 0 && _group == null && !_busy;
        public bool CanRedo => _redo.Count > 0 && _group == null && !_busy;
        public bool IsGroupOpen => _group != null;

        /// <summary>Name of the command Undo would reverse, or null if it has no name or there is none.</summary>
        public string NextUndoName => _undo.Count > 0 ? (_undo[_undo.Count - 1] as INamedCommand)?.Name : null;
        /// <summary>Name of the command Redo would run, or null if it has no name or there is none.</summary>
        public string NextRedoName => _redo.Count > 0 ? (_redo[_redo.Count - 1] as INamedCommand)?.Name : null;

        /// <summary>Changes the capacity. Lowering it drops the oldest undo steps immediately.</summary>
        public void SetCapacity(int capacity)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            if (TrimToCapacity()) RaiseChanged();
        }

        /// <summary>
        /// Runs <paramref name="command"/> and records it. If Execute throws, nothing is
        /// recorded, the redo stack is kept, and the exception propagates.
        /// </summary>
        public void Execute(IUndoableCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            EnterBusy();
            try
            {
                command.Execute();
            }
            finally
            {
                _busy = false;
            }

            if (_group != null)
            {
                // Inside a group the command has run, but is recorded once, at EndGroup.
                _group.Add(command);
                return;
            }

            _redo.Clear();
            Record(command);
            RaiseChanged();
        }

        /// <summary>
        /// Reverses the newest command. Returns false when there is nothing to undo. If the
        /// command's Undo throws, the stacks are left as they were and the exception propagates.
        /// </summary>
        public bool Undo()
        {
            if (_group != null) throw new InvalidOperationException("Cannot undo while a group is open.");
            if (_undo.Count == 0) return false;
            EnterBusy();
            var command = _undo[_undo.Count - 1];
            try
            {
                command.Undo();
            }
            finally
            {
                _busy = false;
            }

            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(command);
            _breakMerge = true;
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Runs the newest undone command again. Returns false when there is nothing to redo.
        /// If Execute throws, the stacks are left as they were and the exception propagates.
        /// </summary>
        public bool Redo()
        {
            if (_group != null) throw new InvalidOperationException("Cannot redo while a group is open.");
            if (_redo.Count == 0) return false;
            EnterBusy();
            var command = _redo[_redo.Count - 1];
            try
            {
                command.Execute();
            }
            finally
            {
                _busy = false;
            }

            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(command);
            TrimToCapacity();
            _breakMerge = true;
            RaiseChanged();
            return true;
        }

        /// <summary>Forgets all undo and redo steps. Does not undo anything.</summary>
        public void Clear()
        {
            if (_group != null) throw new InvalidOperationException("Cannot clear while a group is open.");
            if (_busy) throw new InvalidOperationException("Cannot clear from inside a command.");
            if (_undo.Count == 0 && _redo.Count == 0) return;
            _undo.Clear();
            _redo.Clear();
            _breakMerge = true;
            RaiseChanged();
        }

        /// <summary>
        /// Stops the next command from merging into the previous one. Call it when a
        /// continuous edit ends (mouse released, drag finished).
        /// </summary>
        public void BreakMerge() => _breakMerge = true;

        /// <summary>
        /// Opens a group: every command executed until the returned handle is disposed (or
        /// <see cref="EndGroup"/> is called) runs immediately but becomes one undo step. A
        /// command that throws is not part of the group; the ones before it still are.
        /// Groups cannot be nested.
        /// </summary>
        public GroupScope BeginGroup(string name = null)
        {
            if (_group != null) throw new InvalidOperationException("A group is already open.");
            if (_busy) throw new InvalidOperationException("Cannot open a group from inside a command.");
            _group = new List<IUndoableCommand>();
            _groupName = name;
            _groupId++;
            return new GroupScope(this, _groupId);
        }

        /// <summary>Closes the open group. An empty group records nothing.</summary>
        public void EndGroup()
        {
            if (_group == null) throw new InvalidOperationException("No group is open.");
            var children = _group;
            string name = _groupName;
            _group = null;
            _groupName = null;
            if (children.Count == 0) return;

            _redo.Clear();
            _breakMerge = true;
            // The commands already ran, so the composite is recorded without being executed.
            // Redo later runs it, which executes the children in order again.
            Record(children.Count == 1 && name == null
                ? children[0]
                : new CompositeCommand(name, children));
            _breakMerge = true;
            RaiseChanged();
        }

        private void Record(IUndoableCommand command)
        {
            if (!_breakMerge && _undo.Count > 0
                && _undo[_undo.Count - 1] is IMergeableCommand top
                && top.TryMerge(command))
            {
                return;
            }

            _undo.Add(command);
            _breakMerge = false;
            TrimToCapacity();
        }

        private bool TrimToCapacity()
        {
            if (_capacity == 0 || _undo.Count <= _capacity) return false;
            _undo.RemoveRange(0, _undo.Count - _capacity);
            return true;
        }

        private void EnterBusy()
        {
            if (_busy) throw new InvalidOperationException(
                "A command called back into the history while one was running. Commands must not call Execute, Undo or Redo.");
            _busy = true;
        }

        private void RaiseChanged() => Changed?.Invoke();

        /// <summary>Handle returned by <see cref="BeginGroup"/>. Dispose it to close the group.</summary>
        public struct GroupScope : IDisposable
        {
            private CommandHistory _owner;
            private readonly int _id;
            internal GroupScope(CommandHistory owner, int id) { _owner = owner; _id = id; }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                // The id check stops a scope from closing a different, later group if its own
                // group was already ended with EndGroup.
                if (owner != null && owner.IsGroupOpen && owner._groupId == _id) owner.EndGroup();
            }
        }
    }
}
