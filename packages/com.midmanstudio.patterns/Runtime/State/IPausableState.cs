// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/state.md, section "IState.cs, IPausableState.cs"
// ============================================================================
namespace MidManStudio.Patterns.State
{
    /// <summary>
    /// A state that can sit underneath another one and come back. When a state is pushed over a
    /// pausable state the machine calls <see cref="Pause"/> instead of Exit, and when the state above
    /// is popped it calls <see cref="Resume"/> instead of Enter. A state that does not implement this
    /// is exited when something is pushed over it and entered again when it is uncovered.
    /// </summary>
    public interface IPausableState<TContext> : IState<TContext>
    {
        void Pause(TContext context);
        void Resume(TContext context);
    }
}
