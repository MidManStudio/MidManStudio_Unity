// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "ICommand.cs, IUndoableCommand.cs, IMergeableCommand.cs, INamedCommand.cs"
// ============================================================================
namespace MidManStudio.Patterns.Command
{
    /// <summary>Optional label for a command, used for "Undo Move Door" style UI.</summary>
    public interface INamedCommand
    {
        string Name { get; }
    }
}
