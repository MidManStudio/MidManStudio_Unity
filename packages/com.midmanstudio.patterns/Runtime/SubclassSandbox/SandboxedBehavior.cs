// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/subclass-sandbox.md, section "SandboxedBehavior.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.SubclassSandbox
{
    /// <summary>
    /// Base class for behaviours that are written as subclasses and may only act on the world through a
    /// context object. The context (<typeparamref name="TContext"/>, usually an interface of primitive
    /// operations such as move, spawn and play a sound) is handed to <see cref="Run"/> and is available to
    /// the subclass through <see cref="Context"/> only while that call is running. A subclass therefore
    /// cannot keep a reference to the game world, and each behaviour depends on one small interface instead of
    /// on every system it touches.
    /// </summary>
    public abstract class SandboxedBehavior<TContext> where TContext : class
    {
        private TContext _context;
        private bool _running;

        /// <summary>The operations this behaviour may use. Only valid inside <see cref="Activate"/>.</summary>
        /// <exception cref="InvalidOperationException">Read outside a call to <see cref="Run"/>.</exception>
        protected TContext Context
        {
            get
            {
                if (_context == null)
                    throw new InvalidOperationException("The sandbox context is only available while the behaviour is running.");
                return _context;
            }
        }

        public bool IsRunning => _running;

        /// <summary>Runs the behaviour with the given context. The context is dropped again when the call ends, even if it throws.</summary>
        /// <exception cref="InvalidOperationException">This instance is already running, for example because <see cref="Activate"/> called <see cref="Run"/>.</exception>
        public void Run(TContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_running) throw new InvalidOperationException("This behaviour is already running.");

            _context = context;
            _running = true;
            try
            {
                Activate();
            }
            finally
            {
                _running = false;
                _context = null;
            }
        }

        /// <summary>The behaviour itself. Use <see cref="Context"/> for everything that touches the outside.</summary>
        protected abstract void Activate();
    }
}
