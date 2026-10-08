// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dependency-injection.md, section "Lifetime.cs, IResolver.cs"
// ============================================================================
namespace MidManStudio.Patterns.DependencyInjection
{
    /// <summary>How long an instance made by a <see cref="Container"/> factory lives.</summary>
    public enum Lifetime
    {
        /// <summary>One instance, created on first use, shared by the container that registered it and all its scopes.</summary>
        Singleton,

        /// <summary>One instance per scope, created on first use in that scope. Cannot be resolved from the root container.</summary>
        Scoped,

        /// <summary>A new instance on every resolve. The container does not keep or dispose it.</summary>
        Transient
    }
}
