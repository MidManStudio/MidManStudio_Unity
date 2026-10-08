// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/state.md, section "IState.cs, IPausableState.cs"
// ============================================================================
namespace MidManStudio.Patterns.State
{
    /// <summary>
    /// One state of a <see cref="StateMachine{TContext}"/>. The machine passes its context to every
    /// call, so a state object holds no reference to the thing it controls and one instance can be
    /// shared by several machines.
    /// </summary>
    public interface IState<TContext>
    {
        /// <summary>Called when the state becomes current.</summary>
        void Enter(TContext context);

        /// <summary>Called once per <see cref="StateMachine{TContext}.Update"/> while the state is on top.</summary>
        void Update(TContext context, float deltaTime);

        /// <summary>Called when the state stops being current and will not be resumed.</summary>
        void Exit(TContext context);
    }
}
