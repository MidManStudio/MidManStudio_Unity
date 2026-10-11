// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "BrokerHandler.cs"
// ============================================================================
namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>
    /// One link in a broker chain. Reads or changes <paramref name="query"/> and returns whether the chain
    /// carries on. <paramref name="sender"/> is whatever the caller passed to <c>Run</c> or <c>Query</c>,
    /// usually the object being asked. The query is passed by reference so a struct query is changed in
    /// place, without copying or boxing.
    /// </summary>
    public delegate BrokerResult BrokerHandler<TQuery>(object sender, ref TQuery query);
}
