// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dependency-injection.md, section "ServiceExceptions.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.DependencyInjection
{
    /// <summary>Thrown when a service was requested that nothing is registered for.</summary>
    public sealed class ServiceNotFoundException : InvalidOperationException
    {
        public Type ServiceType { get; }

        public ServiceNotFoundException(Type serviceType)
            : base("No service is registered for " + (serviceType != null ? serviceType.FullName : "(null)") + ".")
        {
            ServiceType = serviceType;
        }
    }

    /// <summary>Thrown when resolving a service needs, directly or not, an instance of that same service.</summary>
    public sealed class CircularDependencyException : InvalidOperationException
    {
        /// <summary>The services in the order they were being resolved, ending with the one that closed the loop.</summary>
        public IReadOnlyList<Type> Chain { get; }

        public CircularDependencyException(IReadOnlyList<Type> chain)
            : base("Circular dependency: " + Describe(chain))
        {
            Chain = chain;
        }

        private static string Describe(IReadOnlyList<Type> chain)
        {
            var names = new string[chain.Count];
            for (int i = 0; i < names.Length; i++) names[i] = chain[i].Name;
            return string.Join(" -> ", names);
        }
    }
}
