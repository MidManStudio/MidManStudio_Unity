// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "CompositeCommand.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MidManStudio.Patterns.Command
{
    /// <summary>
    /// Runs several undoable commands as one step. Execute runs them in order; Undo runs
    /// them in reverse. If a child throws during Execute, the children that already ran
    /// are undone (newest first) and the original exception is rethrown, so the composite
    /// either applies completely or not at all.
    /// </summary>
    public sealed class CompositeCommand : IUndoableCommand, INamedCommand
    {
        private readonly IUndoableCommand[] _children;

        public string Name { get; }
        public int Count => _children.Length;

        public CompositeCommand(string name, params IUndoableCommand[] commands)
            : this(name, (IEnumerable<IUndoableCommand>)commands) { }

        public CompositeCommand(string name, IEnumerable<IUndoableCommand> commands)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            _children = commands.ToArray();
            for (int i = 0; i < _children.Length; i++)
                if (_children[i] == null)
                    throw new ArgumentNullException(nameof(commands), $"Command at index {i} is null.");
            Name = name;
        }

        public void Execute()
        {
            for (int i = 0; i < _children.Length; i++)
            {
                try
                {
                    _children[i].Execute();
                }
                catch (Exception original)
                {
                    // Roll back what already ran. The child that threw is not undone: it
                    // reported failure, so it is the child's job to leave itself consistent.
                    List<Exception> rollbackErrors = null;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        try { _children[j].Undo(); }
                        catch (Exception undoError)
                        {
                            if (rollbackErrors == null) rollbackErrors = new List<Exception>();
                            rollbackErrors.Add(undoError);
                        }
                    }

                    if (rollbackErrors == null) throw;

                    var all = new List<Exception> { original };
                    all.AddRange(rollbackErrors);
                    throw new AggregateException(
                        "A command failed and rolling back the earlier ones also failed.", all);
                }
            }
        }

        public void Undo()
        {
            List<Exception> errors = null;
            for (int i = _children.Length - 1; i >= 0; i--)
            {
                try { _children[i].Undo(); }
                catch (Exception e)
                {
                    // Keep going so as much as possible is undone, then report everything.
                    if (errors == null) errors = new List<Exception>();
                    errors.Add(e);
                }
            }

            if (errors != null)
                throw new AggregateException("One or more commands failed to undo.", errors);
        }
    }
}
