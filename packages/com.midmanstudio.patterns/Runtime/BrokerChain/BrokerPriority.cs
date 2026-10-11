// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "BrokerPriority.cs"
// ============================================================================
namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>
    /// Priorities used by the stat helpers. Lower values run first, so a flat bonus is applied before a
    /// multiplier, and a clamp sees the final value. Any int works for custom handlers.
    /// </summary>
    public static class BrokerPriority
    {
        public const int Add      = 0;
        public const int Multiply = 100;
        public const int Override = 200;
        public const int Clamp    = 1000;
    }
}
