// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "ICommand.cs, IUndoableCommand.cs, IMergeableCommand.cs, INamedCommand.cs"
// ============================================================================
namespace MidManStudio.Patterns.Command
{
    /// <summary>One action that can be run directly, or through a <see cref="CommandQueue"/>.</summary>
    public interface ICommand
    {
        void Execute();
    }
}
