// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dirty-flag.md, section "DirtyFlagTests.cs"
// ============================================================================
using System;
using NUnit.Framework;
using MidManStudio.Patterns.DirtyFlag;

namespace MidManStudio.Patterns.DirtyFlag.Tests
{
    public class DirtyFlagTests
    {
        [Test]
        public void DirtyFlag_StartsDirtyUnlessToldOtherwise()
        {
            Assert.IsTrue(new DirtyFlag().IsDirty);
            Assert.IsFalse(new DirtyFlag(false).IsDirty);
        }

        [Test]
        public void DirtyFlag_MarkSetsTheFlagAndRaisesTheVersion()
        {
            var flag = new DirtyFlag(false);
            int before = flag.Version;
            flag.Mark();
            flag.Mark();
            Assert.IsTrue(flag.IsDirty);
            Assert.AreEqual(before + 2, flag.Version);
        }

        [Test]
        public void DirtyFlag_ConsumeReturnsTrueOnceAndClears()
        {
            var flag = new DirtyFlag(false);
            flag.Mark();
            Assert.IsTrue(flag.Consume());
            Assert.IsFalse(flag.IsDirty);
            Assert.IsFalse(flag.Consume());
        }

        [Test]
        public void DirtyFlag_ConsumeDoesNotChangeTheVersion()
        {
            var flag = new DirtyFlag(false);
            flag.Mark();
            int version = flag.Version;
            flag.Consume();
            Assert.AreEqual(version, flag.Version);
        }

        [Test]
        public void CachedValue_ComputesLazilyAndOnlyOnce()
        {
            int runs = 0;
            var cached = new CachedValue<int>(() => { runs++; return 42; });
            Assert.AreEqual(0, runs);
            Assert.AreEqual(42, cached.Value);
            Assert.AreEqual(42, cached.Value);
            Assert.AreEqual(1, runs);
        }

        [Test]
        public void CachedValue_InvalidateForcesOneRecompute()
        {
            int runs = 0;
            var cached = new CachedValue<int>(() => ++runs);
            Assert.AreEqual(1, cached.Value);
            cached.Invalidate();
            Assert.IsTrue(cached.IsInvalidated);
            Assert.AreEqual(2, cached.Value);
            Assert.AreEqual(2, cached.Value);
            Assert.IsFalse(cached.IsInvalidated);
        }

        [Test]
        public void CachedValue_RecomputesWhenADependencyFlagIsMarked()
        {
            var flag = new DirtyFlag(false);
            int runs = 0;
            var cached = new CachedValue<int>(() => ++runs, flag);
            Assert.AreEqual(1, cached.Value);
            Assert.AreEqual(1, cached.Value);
            flag.Mark();
            Assert.AreEqual(2, cached.Value);
            Assert.AreEqual(2, cached.Value);
        }

        [Test]
        public void CachedValue_IgnoresFlagsItDoesNotDependOn()
        {
            var mine = new DirtyFlag(false);
            var other = new DirtyFlag(false);
            int runs = 0;
            var cached = new CachedValue<int>(() => ++runs, mine);
            int first = cached.Value;
            other.Mark();
            Assert.AreEqual(first, cached.Value);
            Assert.AreEqual(1, runs);
        }

        [Test]
        public void CachedValue_ChainRecomputesOnlyStaleLinksAndOnlyOnce()
        {
            var flag = new DirtyFlag(false);
            int bRuns = 0, cRuns = 0;
            var b = new CachedValue<int>(() => { bRuns++; return 10; }, flag);
            var c = new CachedValue<int>(() => { cRuns++; return b.Value * 2; }, b);
            Assert.AreEqual(20, c.Value);
            Assert.AreEqual(20, c.Value);
            Assert.AreEqual(1, bRuns);
            Assert.AreEqual(1, cRuns);
            flag.Mark();
            Assert.AreEqual(20, c.Value);
            Assert.AreEqual(2, bRuns);
            Assert.AreEqual(2, cRuns);
            Assert.AreEqual(20, c.Value);
            Assert.AreEqual(2, bRuns);
            Assert.AreEqual(2, cRuns);
        }

        [Test]
        public void CachedValue_ADownstreamValueSeesAnUpstreamInvalidationEvenIfNobodyReadTheMiddle()
        {
            int source = 1;
            var flag = new DirtyFlag(false);
            var b = new CachedValue<int>(() => source, flag);
            var c = new CachedValue<int>(() => b.Value + 100, b);
            Assert.AreEqual(101, c.Value);
            source = 5;
            flag.Mark();
            Assert.AreEqual(105, c.Value);
        }

        [Test]
        public void CachedValue_VersionRisesOnlyWhenTheValueIsRecomputed()
        {
            var cached = new CachedValue<int>(() => 7);
            int v1 = cached.Version;
            Assert.AreEqual(v1, cached.Version);
            cached.Invalidate();
            Assert.IsTrue(cached.Version != v1);
        }

        [Test]
        public void CachedValue_AFailingComputeLeavesTheValueInvalidatedAndRetries()
        {
            int calls = 0;
            var cached = new CachedValue<int>(() =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("boom");
                return 9;
            });
            Assert.Throws<InvalidOperationException>(() => { int x = cached.Value; });
            Assert.IsTrue(cached.IsInvalidated);
            Assert.AreEqual(9, cached.Value);
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void CachedValue_ACircularDependencyThrowsAndLeavesBothUsable()
        {
            CachedValue<int> a = null, b = null;
            bool cycle = true;
            a = new CachedValue<int>(() => cycle ? b.Value : 1);
            b = new CachedValue<int>(() => cycle ? a.Value : 2);
            Assert.Throws<InvalidOperationException>(() => { int x = a.Value; });
            cycle = false;
            Assert.AreEqual(1, a.Value);
            Assert.AreEqual(2, b.Value);
        }

        [Test]
        public void CachedValue_NullArgumentsThrow()
        {
            Assert.Throws<ArgumentNullException>(() => new CachedValue<int>(null));
            Assert.Throws<ArgumentNullException>(() => new CachedValue<int>(() => 1, new IVersioned[] { null }));
        }
    }
}
