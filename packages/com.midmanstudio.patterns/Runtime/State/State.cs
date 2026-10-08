// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/state.md, section "State.cs"
// ============================================================================
namespace MidManStudio.Patterns.State
{
    /// <summary>Base class with empty callbacks, so a state only overrides the ones it needs.</summary>
    public abstract class State<TContext> : IState<TContext>
    {
        public virtual void Enter(TContext context) { }
        public virtual void Update(TContext context, float deltaTime) { }
        public virtual void Exit(TContext context) { }
    }
}
