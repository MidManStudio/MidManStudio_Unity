// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dependency-injection.md, section "Lifetime.cs, IResolver.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.DependencyInjection
{
    /// <summary>The read side of a <see cref="Container"/>, handed to factories so they can ask for what they depend on.</summary>
    public interface IResolver
    {
        /// <summary>Returns the service, or throws <see cref="ServiceNotFoundException"/> when nothing is registered for it.</summary>
        T Resolve<T>() where T : class;

        /// <summary>Returns true and the service when one is registered, false otherwise.</summary>
        bool TryResolve<T>(out T service) where T : class;

        object Resolve(Type serviceType);
        bool TryResolve(Type serviceType, out object service);
    }
}
