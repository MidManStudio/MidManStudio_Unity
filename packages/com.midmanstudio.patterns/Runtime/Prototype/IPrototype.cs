// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/prototype.md, section "IPrototype.cs"
// ============================================================================
namespace MidManStudio.Patterns.Prototype
{
    /// <summary>An object that can produce an independent copy of itself.</summary>
    public interface IPrototype<T>
    {
        /// <summary>Returns a new instance with the same state. It must not share mutable state with the original.</summary>
        T Clone();
    }
}
