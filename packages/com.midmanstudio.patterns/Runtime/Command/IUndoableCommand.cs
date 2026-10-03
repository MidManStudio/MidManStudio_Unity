// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "ICommand.cs, IUndoableCommand.cs, IMergeableCommand.cs, INamedCommand.cs"
// ============================================================================
namespace MidManStudio.Patterns.Command
{
    /// <summary>
    /// A command that can reverse itself. <see cref="CommandHistory"/> only accepts these.
    /// Undo must leave the world exactly as it was before Execute, and Execute must be
    /// safe to call again after an Undo (that is how redo works).
    /// </summary>
    public interface IUndoableCommand : ICommand
    {
        void Undo();
    }
}
