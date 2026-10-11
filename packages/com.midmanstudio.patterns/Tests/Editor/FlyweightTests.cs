// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/flyweight.md, section "FlyweightTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.Flyweight;

namespace MidManStudio.Patterns.Flyweight.Tests
{
    public class FlyweightTests
    {
        private class Shared { public string Key; }

        [Test]
        public void TheSameKeyGivesTheSameInstanceAndTheFactoryRunsOnce()
        {
            int runs = 0;
            var cache = new FlyweightCache<string, Shared>(k => { runs++; return new Shared { Key = k }; });
            var a = cache.Get("tree");
            var b = cache.Get("tree");
            Assert.AreSame(a, b);
            Assert.AreEqual(1, runs);
        }

        [Test]
        public void DifferentKeysGiveDifferentInstances()
        {
            var cache = new FlyweightCache<string, Shared>(k => new Shared { Key = k });
            Assert.IsTrue(!ReferenceEquals(cache.Get("a"), cache.Get("b")));
            Assert.AreEqual(2, cache.Count);
            Assert.AreEqual(2, cache.CreatedCount);
        }

        [Test]
        public void TryGetAndContainsDoNotCreate()
        {
            int runs = 0;
            var cache = new FlyweightCache<string, Shared>(k => { runs++; return new Shared(); });
            Shared s;
            Assert.IsFalse(cache.TryGet("x", out s));
            Assert.IsFalse(cache.Contains("x"));
            Assert.AreEqual(0, runs);
            cache.Get("x");
            Assert.IsTrue(cache.TryGet("x", out s));
            Assert.IsTrue(cache.Contains("x"));
        }

        [Test]
        public void RemoveForgetsTheInstanceAndTheNextGetCreatesANewOne()
        {
            var cache = new FlyweightCache<string, Shared>(k => new Shared { Key = k });
            var first = cache.Get("a");
            Assert.IsTrue(cache.Remove("a"));
            Assert.IsFalse(cache.Remove("a"));
            var second = cache.Get("a");
            Assert.IsTrue(!ReferenceEquals(first, second));
            Assert.AreEqual("a", first.Key);
            Assert.AreEqual(2, cache.CreatedCount);
            Assert.AreEqual(1, cache.Count);
        }

        [Test]
        public void AFactoryThatReturnsNullThrowsAndCachesNothing()
        {
            var cache = new FlyweightCache<string, Shared>(k => null);
            Assert.Throws<InvalidOperationException>(() => cache.Get("a"));
            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(0, cache.CreatedCount);
        }

        [Test]
        public void AFactoryThatThrowsCachesNothingAndTheNextCallRetries()
        {
            int calls = 0;
            var cache = new FlyweightCache<string, Shared>(k =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("boom");
                return new Shared();
            });
            Assert.Throws<InvalidOperationException>(() => cache.Get("a"));
            Assert.IsNotNull(cache.Get("a"));
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void AFactoryMayAskForAnotherKey()
        {
            FlyweightCache<string, Shared> cache = null;
            cache = new FlyweightCache<string, Shared>(k => k == "outer" ? new Shared { Key = cache.Get("inner").Key + "!" } : new Shared { Key = k });
            Assert.AreEqual("inner!", cache.Get("outer").Key);
            Assert.AreEqual(2, cache.Count);
        }

        [Test]
        public void AFactoryAskingForItsOwnKeyThrowsAndTheCacheStaysUsable()
        {
            bool recurse = true;
            FlyweightCache<string, Shared> cache = null;
            cache = new FlyweightCache<string, Shared>(k => recurse ? cache.Get(k) : new Shared());
            Assert.Throws<InvalidOperationException>(() => cache.Get("a"));
            recurse = false;
            Assert.IsNotNull(cache.Get("a"));
        }

        [Test]
        public void ClearEmptiesTheCache()
        {
            var cache = new FlyweightCache<int, Shared>(k => new Shared());
            cache.Get(1);
            cache.Get(2);
            cache.Clear();
            Assert.AreEqual(0, cache.Count);
        }

        [Test]
        public void TheKeyComparerIsUsed()
        {
            var cache = new FlyweightCache<string, Shared>(k => new Shared(), StringComparer.OrdinalIgnoreCase);
            Assert.AreSame(cache.Get("Tree"), cache.Get("TREE"));
        }

        [Test]
        public void ANullFactoryIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new FlyweightCache<string, Shared>(null));
        }
    }
}
