// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/decorator.md, section "Decorator.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.Decorator
{
    /// <summary>
    /// Base class for a decorator: it holds the object it wraps in <see cref="Inner"/>. A subclass implements
    /// the same interface as <typeparamref name="T"/>, forwards calls to <see cref="Inner"/> and adds its own
    /// behaviour before or after.
    /// </summary>
    public abstract class Decorator<T> where T : class
    {
        protected readonly T Inner;

        protected Decorator(T inner)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            Inner = inner;
        }
    }
}
