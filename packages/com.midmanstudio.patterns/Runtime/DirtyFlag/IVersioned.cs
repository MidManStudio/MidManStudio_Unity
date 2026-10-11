// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dirty-flag.md, section "IVersioned.cs"
// ============================================================================
namespace MidManStudio.Patterns.DirtyFlag
{
    /// <summary>
    /// Something whose <see cref="Version"/> changes whenever its content changes. A consumer remembers the
    /// version it last used and treats any other value as "changed".
    /// </summary>
    public interface IVersioned
    {
        int Version { get; }
    }
}
