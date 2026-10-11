// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/data-locality.md, section "DataLocalityTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.DataLocality;

namespace MidManStudio.Patterns.DataLocality.Tests
{
    public class DataLocalityTests
    {
        [Test]
        public void AddReturnsAValidHandleAndCountsTheItem()
        {
            var list = new PackedList<string>();
            var h = list.Add("a");
            Assert.IsTrue(list.IsValid(h));
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("a", list[h]);
        }

        [Test]
        public void ItemsAreStoredBackToBackInInsertionOrder()
        {
            var list = new PackedList<int>();
            list.Add(10); list.Add(20); list.Add(30);
            Assert.AreEqual(10, list.Items[0]);
            Assert.AreEqual(20, list.Items[1]);
            Assert.AreEqual(30, list.Items[2]);
        }

        [Test]
        public void RemovingAMiddleItemMovesTheLastIntoTheHoleAndKeepsOtherHandlesValid()
        {
            var list = new PackedList<string>();
            var a = list.Add("a"); var b = list.Add("b"); var c = list.Add("c");
            Assert.IsTrue(list.Remove(a));
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("c", list.Items[0]);
            Assert.AreEqual("b", list[b]);
            Assert.AreEqual("c", list[c]);
            Assert.AreEqual(0, list.IndexOf(c));
        }

        [Test]
        public void RemovingTheLastItemLeavesTheOthersWhereTheyWere()
        {
            var list = new PackedList<string>();
            var a = list.Add("a"); var b = list.Add("b");
            list.Remove(b);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("a", list[a]);
            Assert.AreEqual(0, list.IndexOf(a));
        }

        [Test]
        public void ARemovedHandleStaysInvalidEvenWhenItsSlotIsReused()
        {
            var list = new PackedList<string>();
            var old = list.Add("old");
            list.Remove(old);
            var fresh = list.Add("fresh");
            Assert.IsFalse(list.IsValid(old));
            Assert.IsTrue(list.IsValid(fresh));
            string item;
            Assert.IsFalse(list.TryGet(old, out item));
            Assert.AreEqual("fresh", list[fresh]);
        }

        [Test]
        public void RemoveReturnsFalseForAnInvalidHandle()
        {
            var list = new PackedList<int>();
            var h = list.Add(1);
            Assert.IsTrue(list.Remove(h));
            Assert.IsFalse(list.Remove(h));
            Assert.IsFalse(list.Remove(default(PackedHandle)));
        }

        [Test]
        public void TheDefaultHandleIsNeverValid()
        {
            var list = new PackedList<int>();
            list.Add(1);
            Assert.IsFalse(default(PackedHandle).IsAssigned);
            Assert.IsFalse(list.IsValid(default(PackedHandle)));
            Assert.AreEqual(-1, list.IndexOf(default(PackedHandle)));
        }

        [Test]
        public void GrowingBeyondTheInitialCapacityKeepsEveryHandleValid()
        {
            var list = new PackedList<int>(2);
            var handles = new List<PackedHandle>();
            for (int i = 0; i < 50; i++) handles.Add(list.Add(i * 3));
            Assert.AreEqual(50, list.Count);
            for (int i = 0; i < 50; i++) Assert.AreEqual(i * 3, list[handles[i]]);
        }

        [Test]
        public void TheIndexerSetsAndTryGetReads()
        {
            var list = new PackedList<int>();
            var h = list.Add(1);
            list[h] = 99;
            int value;
            Assert.IsTrue(list.TryGet(h, out value));
            Assert.AreEqual(99, value);
        }

        [Test]
        public void TheIndexerThrowsForAnInvalidHandle()
        {
            var list = new PackedList<int>();
            var h = list.Add(1);
            list.Remove(h);
            Assert.Throws<ArgumentException>(() => { int x = list[h]; });
            Assert.Throws<ArgumentException>(() => { list[h] = 5; });
        }

        [Test]
        public void ClearInvalidatesEveryHandleAndTheListIsReusable()
        {
            var list = new PackedList<int>();
            var a = list.Add(1); var b = list.Add(2);
            list.Clear();
            Assert.AreEqual(0, list.Count);
            Assert.IsFalse(list.IsValid(a));
            Assert.IsFalse(list.IsValid(b));
            var c = list.Add(3);
            Assert.AreEqual(3, list[c]);
            Assert.IsFalse(list.IsValid(a));
        }

        [Test]
        public void RemovedItemsAreReleasedFromTheBackingArray()
        {
            var list = new PackedList<string>();
            var h = list.Add("x");
            list.Remove(h);
            Assert.IsNull(list.Items[0]);
        }

        [Test]
        public void ARandomMixOfAddsAndRemovalsMatchesASimpleModel()
        {
            var rng = new Random(12345);
            var list = new PackedList<int>(4);
            var model = new Dictionary<PackedHandle, int>();
            var live = new List<PackedHandle>();
            for (int step = 0; step < 3000; step++)
            {
                if (live.Count == 0 || rng.Next(100) < 55)
                {
                    int value = rng.Next();
                    var h = list.Add(value);
                    model[h] = value;
                    live.Add(h);
                }
                else
                {
                    int at = rng.Next(live.Count);
                    var h = live[at];
                    Assert.IsTrue(list.Remove(h));
                    model.Remove(h);
                    live.RemoveAt(at);
                    Assert.IsFalse(list.IsValid(h));
                }
                Assert.AreEqual(model.Count, list.Count);
            }
            foreach (var pair in model) Assert.AreEqual(pair.Value, list[pair.Key]);
            var seen = new HashSet<int>();
            foreach (var pair in model) Assert.IsTrue(seen.Add(list.IndexOf(pair.Key)));
            foreach (int index in seen) Assert.IsTrue(index >= 0 && index < list.Count);
        }

        [Test]
        public void ACapacityBelowOneIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackedList<int>(0));
        }
    }
}
