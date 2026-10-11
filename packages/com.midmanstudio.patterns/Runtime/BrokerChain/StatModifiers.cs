// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "StatModifiers.cs"
// ============================================================================
using System.Collections.Generic;

namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>
    /// Ready-made handlers for <see cref="StatQuery{TStat}"/>. Each one only touches queries for its own
    /// stat and always lets the chain continue. Register them with the priority from
    /// <see cref="BrokerPriority"/> that matches what they do.
    /// </summary>
    public static class StatModifiers
    {
        /// <summary>Adds <paramref name="amount"/> to the stat.</summary>
        public static BrokerHandler<StatQuery<TStat>> Add<TStat>(TStat stat, float amount)
        {
            return (object sender, ref StatQuery<TStat> query) =>
            {
                if (EqualityComparer<TStat>.Default.Equals(query.Stat, stat)) query.Value += amount;
                return BrokerResult.Continue;
            };
        }

        /// <summary>Multiplies the stat by <paramref name="factor"/>.</summary>
        public static BrokerHandler<StatQuery<TStat>> Multiply<TStat>(TStat stat, float factor)
        {
            return (object sender, ref StatQuery<TStat> query) =>
            {
                if (EqualityComparer<TStat>.Default.Equals(query.Stat, stat)) query.Value *= factor;
                return BrokerResult.Continue;
            };
        }

        /// <summary>Replaces the stat's value, whatever it was.</summary>
        public static BrokerHandler<StatQuery<TStat>> Override<TStat>(TStat stat, float value)
        {
            return (object sender, ref StatQuery<TStat> query) =>
            {
                if (EqualityComparer<TStat>.Default.Equals(query.Stat, stat)) query.Value = value;
                return BrokerResult.Continue;
            };
        }

        /// <summary>Keeps the stat between <paramref name="min"/> and <paramref name="max"/>.</summary>
        public static BrokerHandler<StatQuery<TStat>> Clamp<TStat>(TStat stat, float min, float max)
        {
            return (object sender, ref StatQuery<TStat> query) =>
            {
                if (EqualityComparer<TStat>.Default.Equals(query.Stat, stat))
                {
                    if (query.Value < min) query.Value = min;
                    else if (query.Value > max) query.Value = max;
                }
                return BrokerResult.Continue;
            };
        }
    }
}
