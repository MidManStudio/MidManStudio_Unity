// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "StatQuery.cs"
// ============================================================================
namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>
    /// The query type used by <see cref="StatSheet{TStat}"/>: which stat is being asked for and its value so
    /// far. The sheet starts <see cref="Value"/> at the stat's base value and the handlers change it.
    /// </summary>
    public struct StatQuery<TStat>
    {
        public TStat Stat;
        public float Value;

        public StatQuery(TStat stat, float value)
        {
            Stat = stat;
            Value = value;
        }
    }
}
