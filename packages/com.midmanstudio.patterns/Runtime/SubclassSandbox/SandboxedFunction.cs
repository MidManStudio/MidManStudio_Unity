// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/subclass-sandbox.md, section "SandboxedFunction.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.SubclassSandbox
{
    /// <summary>
    /// Like <see cref="SandboxedBehavior{TContext}"/>, but the behaviour returns a value, for example a
    /// target to attack, a damage amount or a yes/no decision.
    /// </summary>
    public abstract class SandboxedFunction<TContext, TResult> where TContext : class
    {
        private TContext _context;
        private bool _running;

        /// <exception cref="InvalidOperationException">Read outside a call to <see cref="Run"/>.</exception>
        protected TContext Context
        {
            get
            {
                if (_context == null)
                    throw new InvalidOperationException("The sandbox context is only available while the function is running.");
                return _context;
            }
        }

        public bool IsRunning => _running;

        /// <exception cref="InvalidOperationException">This instance is already running.</exception>
        public TResult Run(TContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_running) throw new InvalidOperationException("This function is already running.");

            _context = context;
            _running = true;
            try
            {
                return Evaluate();
            }
            finally
            {
                _running = false;
                _context = null;
            }
        }

        protected abstract TResult Evaluate();
    }
}
