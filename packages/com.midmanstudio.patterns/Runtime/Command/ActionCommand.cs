// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "ActionCommand.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.Command
{
    /// <summary>Wraps a delegate as a one-way command, for queues where nothing needs undoing.</summary>
    public sealed class ActionCommand : ICommand, INamedCommand
    {
        private readonly Action _execute;

        public string Name { get; }

        public ActionCommand(Action execute, string name = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            Name = name;
        }

        public void Execute() => _execute();
    }

    /// <summary>Wraps a pair of delegates as an undoable command, for one-off edits that do not need a class.</summary>
    public sealed class UndoableActionCommand : IUndoableCommand, INamedCommand
    {
        private readonly Action _execute;
        private readonly Action _undo;

        public string Name { get; }

        public UndoableActionCommand(Action execute, Action undo, string name = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _undo    = undo    ?? throw new ArgumentNullException(nameof(undo));
            Name = name;
        }

        public void Execute() => _execute();
        public void Undo()    => _undo();
    }
}
