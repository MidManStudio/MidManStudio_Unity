// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/broker-chain.md, section "BrokerChainTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.BrokerChain;

namespace MidManStudio.Patterns.BrokerChain.Tests
{
    public class BrokerChainTests
    {
        private struct Hit { public int Damage; public Hit(int d) { Damage = d; } }

        private enum Stat { Health, Speed, Attack }

        private static BrokerHandler<Hit> Log(List<string> log, string name, BrokerResult result = BrokerResult.Continue)
        {
            return (object sender, ref Hit q) => { log.Add(name); return result; };
        }

        private static BrokerHandler<Hit> AddDamage(int amount)
        {
            return (object sender, ref Hit q) => { q.Damage += amount; return BrokerResult.Continue; };
        }

        private static BrokerHandler<Hit> DoubleDamage()
        {
            return (object sender, ref Hit q) => { q.Damage *= 2; return BrokerResult.Continue; };
        }

        // ── Broker: ordering ────────────────────────────────────────────────

        [Test]
        public void Run_WithNoHandlers_LeavesTheQueryAloneAndContinues()
        {
            var broker = new Broker<Hit>();
            var q = new Hit(5);
            Assert.AreEqual(BrokerResult.Continue, broker.Run(null, ref q));
            Assert.AreEqual(5, q.Damage);
        }

        [Test]
        public void EqualPriorities_RunInRegistrationOrder()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            broker.Register(Log(log, "a"));
            broker.Register(Log(log, "b"));
            broker.Register(Log(log, "c"));
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual("a,b,c", string.Join(",", log));
        }

        [Test]
        public void LowerPriority_RunsFirstWhateverTheRegistrationOrder()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            broker.Register(Log(log, "late"), priority: 10);
            broker.Register(Log(log, "early"), priority: -5);
            broker.Register(Log(log, "mid"), priority: 0);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual("early,mid,late", string.Join(",", log));
        }

        [Test]
        public void MixedPriorities_KeepRegistrationOrderInsideEachPriority()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            broker.Register(Log(log, "b1"), priority: 1);
            broker.Register(Log(log, "a1"), priority: 0);
            broker.Register(Log(log, "b2"), priority: 1);
            broker.Register(Log(log, "a2"), priority: 0);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual("a1,a2,b1,b2", string.Join(",", log));
        }

        [Test]
        public void EachHandler_SeesTheResultOfTheOnesBeforeIt()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(3), priority: 0);
            broker.Register(DoubleDamage(), priority: 1);
            var q = new Hit(2);
            broker.Run(null, ref q);
            Assert.AreEqual(10, q.Damage); // (2 + 3) * 2, not 2 * 2 + 3
        }

        // ── Broker: query and result ────────────────────────────────────────

        [Test]
        public void Query_ReturnsTheChangedCopyAndLeavesTheSeedVariableAlone()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(4));
            var seed = new Hit(1);
            var result = broker.Query(null, seed);
            Assert.AreEqual(5, result.Damage);
            Assert.AreEqual(1, seed.Damage);
        }

        [Test]
        public void Stop_EndsTheChainAndRunReportsIt()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            broker.Register(Log(log, "a"));
            broker.Register(Log(log, "stop", BrokerResult.Stop));
            broker.Register(Log(log, "never"));
            var q = new Hit(0);
            Assert.AreEqual(BrokerResult.Stop, broker.Run(null, ref q));
            Assert.AreEqual("a,stop", string.Join(",", log));
        }

        [Test]
        public void Sender_IsPassedToEveryHandler()
        {
            var broker = new Broker<Hit>();
            var seen = new List<object>();
            broker.Register((object sender, ref Hit q) => { seen.Add(sender); return BrokerResult.Continue; });
            broker.Register((object sender, ref Hit q) => { seen.Add(sender); return BrokerResult.Continue; });
            var who = new object();
            var query = new Hit(0);
            broker.Run(who, ref query);
            Assert.AreEqual(2, seen.Count);
            Assert.IsTrue(ReferenceEquals(who, seen[0]) && ReferenceEquals(who, seen[1]));
        }

        // ── Broker: registration handles ────────────────────────────────────

        [Test]
        public void DisposingTheHandle_UnregistersTheHandler()
        {
            var broker = new Broker<Hit>();
            var handle = broker.Register(AddDamage(5));
            Assert.AreEqual(1, broker.Count);
            handle.Dispose();
            Assert.AreEqual(0, broker.Count);
            var q = new Hit(1);
            broker.Run(null, ref q);
            Assert.AreEqual(1, q.Damage);
        }

        [Test]
        public void DisposingTwice_IsHarmless()
        {
            var broker = new Broker<Hit>();
            var other = broker.Register(AddDamage(1));
            var handle = broker.Register(AddDamage(5));
            handle.Dispose();
            handle.Dispose();
            Assert.AreEqual(1, broker.Count);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(1, q.Damage);
            other.Dispose();
        }

        [Test]
        public void TheSameDelegateRegisteredTwice_RunsTwiceAndEachHandleRemovesOnlyItsOwn()
        {
            var broker = new Broker<Hit>();
            var add = AddDamage(2);
            var first = broker.Register(add);
            var second = broker.Register(add);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(4, q.Damage);
            first.Dispose();
            q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(2, q.Damage);
            second.Dispose();
        }

        [Test]
        public void ADisposeDuringARun_StopsALaterHandlerFromRunningInThatRun()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            IDisposable later = null;
            broker.Register((object sender, ref Hit q) => { log.Add("first"); later.Dispose(); return BrokerResult.Continue; });
            later = broker.Register(Log(log, "later"));
            var query = new Hit(0);
            broker.Run(null, ref query);
            Assert.AreEqual("first", string.Join(",", log));
        }

        [Test]
        public void ARegistrationDuringARun_MissesThatRunAndTakesPartInTheNext()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            bool added = false;
            broker.Register((object sender, ref Hit q) =>
            {
                log.Add("first");
                if (!added) { added = true; broker.Register(Log(log, "new")); }
                return BrokerResult.Continue;
            });
            var query = new Hit(0);
            broker.Run(null, ref query);
            Assert.AreEqual("first", string.Join(",", log));
            log.Clear();
            broker.Run(null, ref query);
            Assert.AreEqual("first,new", string.Join(",", log));
        }

        [Test]
        public void Clear_RemovesEveryHandlerAndMakesTheirHandlesInert()
        {
            var broker = new Broker<Hit>();
            var a = broker.Register(AddDamage(1));
            var b = broker.Register(AddDamage(1));
            broker.Clear();
            Assert.AreEqual(0, broker.Count);
            var keep = broker.Register(AddDamage(7));
            a.Dispose();
            b.Dispose();
            Assert.AreEqual(1, broker.Count);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(7, q.Damage);
            keep.Dispose();
        }

        [Test]
        public void ClearDuringARun_SkipsTheHandlersThatHaveNotRunYet()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            broker.Register((object sender, ref Hit q) => { log.Add("first"); broker.Clear(); return BrokerResult.Continue; });
            broker.Register(Log(log, "later"));
            var query = new Hit(0);
            broker.Run(null, ref query);
            Assert.AreEqual("first", string.Join(",", log));
            Assert.AreEqual(0, broker.Count);
        }

        // ── Broker: lifetimes ───────────────────────────────────────────────

        [Test]
        public void ATimedHandler_RunsUntilItsLifetimeIsUsedUp()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(5), lifetimeSeconds: 2f);
            broker.Tick(1.5f);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(5, q.Damage);
            broker.Tick(0.4f);
            Assert.AreEqual(1, broker.Count);
            broker.Tick(0.2f);
            Assert.AreEqual(0, broker.Count);
            q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(0, q.Damage);
        }

        [Test]
        public void ATimedHandler_ExpiresExactlyWhenTheTimeReachesZero()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(1), lifetimeSeconds: 1f);
            broker.Tick(1f);
            Assert.AreEqual(0, broker.Count);
        }

        [Test]
        public void AHandlerWithoutALifetime_NeverExpires()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(1));
            broker.Tick(1000f);
            Assert.AreEqual(1, broker.Count);
        }

        [Test]
        public void OnExpired_RunsOnceAfterTheHandlerIsGoneAndNotOnDispose()
        {
            var broker = new Broker<Hit>();
            int calls = 0;
            int countInsideCallback = -1;
            broker.Register(AddDamage(1), lifetimeSeconds: 1f, onExpired: () => { calls++; countInsideCallback = broker.Count; });
            var disposed = broker.Register(AddDamage(1), lifetimeSeconds: 10f, onExpired: () => { calls += 100; });
            disposed.Dispose();
            broker.Tick(1f);
            broker.Tick(1f);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(0, countInsideCallback);
        }

        [Test]
        public void AnOnExpiredCallback_CanRegisterANewHandler()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(1), lifetimeSeconds: 1f, onExpired: () => broker.Register(AddDamage(9)));
            broker.Tick(1f);
            var q = new Hit(0);
            broker.Run(null, ref q);
            Assert.AreEqual(9, q.Damage);
        }

        [Test]
        public void ACallbackThatThrows_DoesNotSkipTheOthersAndTheFirstExceptionIsRethrown()
        {
            var broker = new Broker<Hit>();
            int second = 0;
            broker.Register(AddDamage(1), lifetimeSeconds: 1f, onExpired: () => { throw new InvalidOperationException("first"); });
            broker.Register(AddDamage(1), lifetimeSeconds: 1f, onExpired: () => { second++; });
            var ex = Assert.Throws<InvalidOperationException>(() => broker.Tick(1f));
            Assert.AreEqual("first", ex.Message);
            Assert.AreEqual(1, second);
            Assert.AreEqual(0, broker.Count);
        }

        // ── Broker: nesting and failures ────────────────────────────────────

        [Test]
        public void AHandlerCanQueryTheSameBroker()
        {
            var broker = new Broker<Hit>();
            broker.Register(AddDamage(1), priority: 0);
            broker.Register((object sender, ref Hit q) =>
            {
                if (q.Damage == 1) { var inner = broker.Query(sender, new Hit(100)); q.Damage = inner.Damage; }
                return BrokerResult.Continue;
            }, priority: 1);
            var query = new Hit(0);
            broker.Run(null, ref query);
            Assert.AreEqual(101, query.Damage);
        }

        [Test]
        public void EndlessNesting_ThrowsAndTheBrokerWorksAfterwards()
        {
            var broker = new Broker<Hit>();
            var recurse = broker.Register((object sender, ref Hit q) => { broker.Run(sender, ref q); return BrokerResult.Continue; });
            var query = new Hit(0);
            var captured = query;
            Assert.Throws<InvalidOperationException>(() => broker.Run(null, ref captured));
            recurse.Dispose();
            broker.Register(AddDamage(2));
            broker.Run(null, ref query);
            Assert.AreEqual(2, query.Damage);
        }

        [Test]
        public void AThrowingHandler_EndsTheChainKeepsEarlierChangesAndLeavesTheBrokerUsable()
        {
            var broker = new Broker<Hit>();
            var log = new List<string>();
            broker.Register(AddDamage(3), priority: 0);
            var bad = broker.Register((object sender, ref Hit q) => { throw new InvalidOperationException("boom"); }, priority: 1);
            broker.Register(Log(log, "never"), priority: 2);
            var query = new Hit(0);
            var captured = query;
            Assert.Throws<InvalidOperationException>(() => broker.Run(null, ref captured));
            Assert.AreEqual("", string.Join(",", log));
            bad.Dispose();
            broker.Run(null, ref query);
            Assert.AreEqual(3, query.Damage);
            Assert.AreEqual("never", string.Join(",", log));
        }

        // ── Broker: argument checks ─────────────────────────────────────────

        [Test]
        public void BadArguments_Throw()
        {
            var broker = new Broker<Hit>();
            Assert.Throws<ArgumentNullException>(() => broker.Register(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => broker.Register(AddDamage(1), lifetimeSeconds: -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => broker.Register(AddDamage(1), lifetimeSeconds: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => broker.Tick(-0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => broker.Tick(float.NaN));
        }

        // ── Stat helpers ────────────────────────────────────────────────────

        [Test]
        public void StatSheet_ReturnsTheBaseValueAndZeroForAnUnsetStat()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Health, 100f);
            Assert.AreEqual(100f, sheet.Get(Stat.Health), 0.0001f);
            Assert.AreEqual(0f, sheet.Get(Stat.Speed), 0.0001f);
            Assert.IsTrue(sheet.HasBase(Stat.Health));
            Assert.IsFalse(sheet.HasBase(Stat.Speed));
        }

        [Test]
        public void StatSheet_AFlatBonusAppliesAndDisposingRemovesIt()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Attack, 10f);
            var bonus = sheet.AddModifier(Stat.Attack, 5f);
            Assert.AreEqual(15f, sheet.Get(Stat.Attack), 0.0001f);
            bonus.Dispose();
            Assert.AreEqual(10f, sheet.Get(Stat.Attack), 0.0001f);
        }

        [Test]
        public void StatSheet_FlatBonusesRunBeforeMultipliersWhateverTheRegistrationOrder()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Attack, 10f);
            sheet.MultiplyModifier(Stat.Attack, 2f);
            sheet.AddModifier(Stat.Attack, 5f);
            Assert.AreEqual(30f, sheet.Get(Stat.Attack), 0.0001f); // (10 + 5) * 2
        }

        [Test]
        public void StatSheet_OverrideRunsAfterMultipliersAndClampRunsLast()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Speed, 10f);
            sheet.ClampModifier(Stat.Speed, 0f, 8f);
            sheet.MultiplyModifier(Stat.Speed, 3f);
            Assert.AreEqual(8f, sheet.Get(Stat.Speed), 0.0001f);
            sheet.OverrideModifier(Stat.Speed, 1f);
            Assert.AreEqual(1f, sheet.Get(Stat.Speed), 0.0001f);
        }

        [Test]
        public void StatSheet_AModifierOnlyAffectsItsOwnStat()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Health, 50f);
            sheet.SetBase(Stat.Speed, 5f);
            sheet.AddModifier(Stat.Health, 10f);
            sheet.MultiplyModifier(Stat.Speed, 2f);
            Assert.AreEqual(60f, sheet.Get(Stat.Health), 0.0001f);
            Assert.AreEqual(10f, sheet.Get(Stat.Speed), 0.0001f);
        }

        [Test]
        public void StatSheet_ATimedModifierEndsWithTick()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Speed, 5f);
            bool ended = false;
            sheet.MultiplyModifier(Stat.Speed, 2f, lifetimeSeconds: 3f, onExpired: () => ended = true);
            sheet.Tick(2f);
            Assert.AreEqual(10f, sheet.Get(Stat.Speed), 0.0001f);
            Assert.IsFalse(ended);
            sheet.Tick(1f);
            Assert.AreEqual(5f, sheet.Get(Stat.Speed), 0.0001f);
            Assert.IsTrue(ended);
        }

        [Test]
        public void StatSheet_ACustomHandlerCanReadAnotherStatThroughTheSheet()
        {
            var sheet = new StatSheet<Stat>();
            sheet.SetBase(Stat.Attack, 10f);
            sheet.SetBase(Stat.Speed, 4f);
            sheet.Broker.Register((object sender, ref StatQuery<Stat> q) =>
            {
                if (q.Stat == Stat.Attack) q.Value += sheet.Get(Stat.Speed) * 0.5f;
                return BrokerResult.Continue;
            });
            sheet.AddModifier(Stat.Speed, 4f);
            Assert.AreEqual(14f, sheet.Get(Stat.Attack), 0.0001f); // 10 + (4 + 4) * 0.5
        }

        [Test]
        public void StatSheet_PassesTheSenderOrItselfToHandlers()
        {
            var sheet = new StatSheet<Stat>();
            object seen = null;
            sheet.Broker.Register((object sender, ref StatQuery<Stat> q) => { seen = sender; return BrokerResult.Continue; });
            sheet.Get(Stat.Health);
            Assert.IsTrue(ReferenceEquals(sheet, seen));
            var who = new object();
            sheet.Get(Stat.Health, who);
            Assert.IsTrue(ReferenceEquals(who, seen));
        }
    }
}
