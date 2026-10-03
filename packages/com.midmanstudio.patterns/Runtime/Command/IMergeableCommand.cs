// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "ICommand.cs, IUndoableCommand.cs, IMergeableCommand.cs, INamedCommand.cs"
// ============================================================================
namespace MidManStudio.Patterns.Command
{
    /// <summary>
    /// An undoable command that can absorb the command executed right after it, so a
    /// burst of small edits (a slider drag, nudging an object) becomes one undo step.
    /// </summary>
    public interface IMergeableCommand : IUndoableCommand
    {
        /// <summary>
        /// Called by <see cref="CommandHistory"/> after <paramref name="next"/> has already
        /// been executed. Return true if this command now represents both: its Undo must
        /// then reverse both, and <paramref name="next"/> is dropped. Must not throw.
        /// </summary>
        bool TryMerge(IUndoableCommand next);
    }
}
