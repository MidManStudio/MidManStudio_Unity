// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/template-method.md, section "TemplateAction.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.TemplateMethod
{
    /// <summary>
    /// Fixes the order of an action's steps and lets subclasses fill them in. <see cref="Run"/> is not
    /// virtual: it checks <see cref="CanRun"/>, calls <see cref="Begin"/>, then <see cref="Execute"/>, and
    /// always finishes with <see cref="End"/>. A subclass overrides the steps it needs, and every subclass gets
    /// the same guarantee that the cleanup in <see cref="End"/> runs.
    /// </summary>
    public abstract class TemplateAction<TContext>
    {
        /// <summary>
        /// Runs the action. Returns false when <see cref="CanRun"/> said no and nothing else was called, true when
        /// the action ran to the end. If <see cref="Execute"/> throws, <see cref="OnFailed"/> and then
        /// <see cref="End"/> run and the exception propagates. If <see cref="Begin"/> throws, the action never
        /// started: neither <see cref="OnFailed"/> nor <see cref="End"/> is called.
        /// </summary>
        public bool Run(TContext context)
        {
            if (!CanRun(context)) return false;

            Begin(context);
            try
            {
                Execute(context);
            }
            catch (Exception e)
            {
                OnFailed(context, e);
                throw;
            }
            finally
            {
                End(context);
            }
            return true;
        }

        /// <summary>Return false to skip the whole action. Default: true.</summary>
        protected virtual bool CanRun(TContext context) { return true; }

        /// <summary>Set up before <see cref="Execute"/>. Default: nothing.</summary>
        protected virtual void Begin(TContext context) { }

        /// <summary>The part that differs between actions.</summary>
        protected abstract void Execute(TContext context);

        /// <summary>Called when <see cref="Execute"/> threw, before <see cref="End"/>. Default: nothing.</summary>
        protected virtual void OnFailed(TContext context, Exception error) { }

        /// <summary>Clean up. Runs after <see cref="Execute"/> whether it succeeded or failed. Default: nothing.</summary>
        protected virtual void End(TContext context) { }
    }
}
