// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "StatSheet.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.BrokerChain
{
    /// <summary>
    /// A set of base stat values with a broker in front of them. <see cref="Get"/> starts from the base value
    /// and runs the broker's handlers over it, so buffs, debuffs and equipment can change a stat without the
    /// stat knowing about them. The helper methods register the handlers from <see cref="StatModifiers"/>;
    /// custom handlers go through <see cref="Broker"/>.
    /// </summary>
    public sealed class StatSheet<TStat>
    {
        private readonly Dictionary<TStat, float> _base = new Dictionary<TStat, float>();
        private readonly Broker<StatQuery<TStat>> _broker = new Broker<StatQuery<TStat>>();

        /// <summary>The broker behind this sheet, for registering custom handlers.</summary>
        public Broker<StatQuery<TStat>> Broker => _broker;

        public void SetBase(TStat stat, float value) { _base[stat] = value; }

        /// <summary>The base value, or 0 when none was set.</summary>
        public float GetBase(TStat stat)
        {
            float value;
            return _base.TryGetValue(stat, out value) ? value : 0f;
        }

        public bool HasBase(TStat stat) { return _base.ContainsKey(stat); }

        /// <summary>The base value after every registered handler has run. <paramref name="sender"/> defaults to the sheet.</summary>
        public float Get(TStat stat, object sender = null)
        {
            var query = new StatQuery<TStat>(stat, GetBase(stat));
            _broker.Run(sender ?? this, ref query);
            return query.Value;
        }

        /// <summary>Adds a flat bonus. Dispose the handle to remove it, or pass a lifetime and call <see cref="Tick"/>.</summary>
        public IDisposable AddModifier(TStat stat, float amount, float lifetimeSeconds = 0f, Action onExpired = null)
        {
            return _broker.Register(StatModifiers.Add(stat, amount), BrokerPriority.Add, lifetimeSeconds, onExpired);
        }

        /// <summary>Multiplies the stat. Runs after every flat bonus.</summary>
        public IDisposable MultiplyModifier(TStat stat, float factor, float lifetimeSeconds = 0f, Action onExpired = null)
        {
            return _broker.Register(StatModifiers.Multiply(stat, factor), BrokerPriority.Multiply, lifetimeSeconds, onExpired);
        }

        /// <summary>Forces the stat to a value (a stun setting speed to 0, for example). Runs after multipliers.</summary>
        public IDisposable OverrideModifier(TStat stat, float value, float lifetimeSeconds = 0f, Action onExpired = null)
        {
            return _broker.Register(StatModifiers.Override(stat, value), BrokerPriority.Override, lifetimeSeconds, onExpired);
        }

        /// <summary>Keeps the stat inside a range. Runs last.</summary>
        public IDisposable ClampModifier(TStat stat, float min, float max)
        {
            return _broker.Register(StatModifiers.Clamp(stat, min, max), BrokerPriority.Clamp);
        }

        /// <summary>Advances timed modifiers. Call it from the update loop with the frame's delta time.</summary>
        public void Tick(float deltaTime) { _broker.Tick(deltaTime); }
    }
}
