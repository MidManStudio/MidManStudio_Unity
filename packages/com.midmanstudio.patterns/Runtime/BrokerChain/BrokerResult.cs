// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "BrokerResult.cs"
// ============================================================================
namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>What a broker handler tells the chain to do next.</summary>
    public enum BrokerResult
    {
        /// <summary>Pass the query on to the next handler.</summary>
        Continue,

        /// <summary>End the chain. Handlers after this one do not run for this query.</summary>
        Stop
    }
}
