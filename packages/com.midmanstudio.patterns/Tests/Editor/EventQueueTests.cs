// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/event-queue.md, section "EventQueueTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.EventQueue;

namespace MidManStudio.Patterns.EventQueue.Tests
{
    public class EventQueueTests
    {
        private struct Hit { public int Damage; public Hit(int d) { Damage = d; } }

        private static string Join(List<int> l) => string.Join(",", l);

        [Test]
        public void Post_DoesNotDeliver_ProcessDoesInOrder()
        {
            var q = new EventQueue<int>();
            var got = new List<int>();
            q.Subscribe(e => got.Add(e));
            q.Post(1); q.Post(2); q.Post(3);
            Assert.AreEqual("", Join(got));
            Assert.AreEqual(3, q.Count);
            Assert.AreEqual(3, q.Process());
            Assert.AreEqual("1,2,3", Join(got));
            Assert.IsTrue(q.IsEmpty);
        }

        [Test]
        public void EveryEvent_GoesToEverySubscriberInSubscriptionOrder()
        {
            var q = new EventQueue<int>();
            var log = new List<string>();
            q.Subscribe(e => log.Add("a" + e));
            q.Subscribe(e => log.Add("b" + e));
            q.Post(1); q.Post(2);
            q.Process();
            Assert.AreEqual("a1,b1,a2,b2", string.Join(",", log));
        }

        [Test]
        public void Process_RespectsTheLimit()
        {
            var q = new EventQueue<int>();
            var got = new List<int>();
            q.Subscribe(e => got.Add(e));
            for (int i = 1; i <= 5; i++) q.Post(i);
            Assert.AreEqual(2, q.Process(2));
            Assert.AreEqual("1,2", Join(got));
            Assert.AreEqual(3, q.Count);
            Assert.AreEqual(3, q.Process());
            Assert.AreEqual(0, q.Process());
        }

        [Test]
        public void EventsPostedByAHandler_WaitForTheNextProcessCall()
        {
            var q = new EventQueue<int>();
            var got = new List<int>();
            q.Subscribe(e => { got.Add(e); if (e == 1) q.Post(99); });
            q.Post(1);
            Assert.AreEqual(1, q.Process());
            Assert.AreEqual("1", Join(got));
            Assert.AreEqual(1, q.Process());
            Assert.AreEqual("1,99", Join(got));
        }

        [Test]
        public void ASubscriberAddedWhileDelivering_MissesTheCurrentEventButGetsTheNext()
        {
            var q = new EventQueue<int>();
            var late = new List<int>();
            bool added = false;
            q.Subscribe(e =>
            {
                if (!added) { added = true; q.Subscribe(x => late.Add(x)); }
            });
            q.Post(1); q.Post(2);
            q.Process();
            Assert.AreEqual("2", Join(late));
        }

        [Test]
        public void AnUnsubscribeDuringDelivery_TakesEffectForTheCurrentEvent()
        {
            var q = new EventQueue<int>();
            var got = new List<string>();
            IDisposable second = null;
            q.Subscribe(e => { got.Add("first" + e); if (second != null) second.Dispose(); });
            second = q.Subscribe(e => got.Add("second" + e));
            q.Post(1); q.Post(2);
            q.Process();
            Assert.AreEqual("first1,first2", string.Join(",", got));
            Assert.AreEqual(1, q.SubscriberCount);
        }

        [Test]
        public void DisposingASubscriptionTwice_IsHarmless()
        {
            var q = new EventQueue<int>();
            var sub = q.Subscribe(e => { });
            sub.Dispose();
            sub.Dispose();
            Assert.AreEqual(0, q.SubscriberCount);
        }

        [Test]
        public void TheSameDelegateSubscribedTwice_IsCalledTwice_AndDisposingOneLeavesOne()
        {
            var q = new EventQueue<int>();
            int calls = 0;
            Action<int> h = e => calls++;
            var a = q.Subscribe(h);
            q.Subscribe(h);
            q.Post(1);
            q.Process();
            Assert.AreEqual(2, calls);
            a.Dispose();
            q.Post(2);
            q.Process();
            Assert.AreEqual(3, calls);
        }

        [Test]
        public void DropOldest_KeepsTheNewestEvents_AndCountsDrops()
        {
            var q = new EventQueue<int>(3, EventQueueOverflow.DropOldest);
            var got = new List<int>();
            q.Subscribe(e => got.Add(e));
            for (int i = 1; i <= 5; i++) Assert.IsTrue(q.Post(i));
            Assert.AreEqual(3, q.Count);
            Assert.AreEqual(2, q.DroppedCount);
            q.Process();
            Assert.AreEqual("3,4,5", Join(got));
        }

        [Test]
        public void DropNewest_KeepsTheOldestEvents_AndReportsTheDiscard()
        {
            var q = new EventQueue<int>(3, EventQueueOverflow.DropNewest);
            var got = new List<int>();
            q.Subscribe(e => got.Add(e));
            Assert.IsTrue(q.Post(1)); Assert.IsTrue(q.Post(2)); Assert.IsTrue(q.Post(3));
            Assert.IsFalse(q.Post(4));
            Assert.AreEqual(1, q.DroppedCount);
            q.Process();
            Assert.AreEqual("1,2,3", Join(got));
        }

        [Test]
        public void ThrowPolicy_ThrowsWhenFull_AndLeavesTheQueueIntact()
        {
            var q = new EventQueue<int>(2, EventQueueOverflow.Throw);
            q.Post(1); q.Post(2);
            Assert.Throws<InvalidOperationException>(() => q.Post(3));
            Assert.AreEqual(2, q.Count);
            Assert.AreEqual(0, q.DroppedCount);
        }

        [Test]
        public void Unbounded_GrowsAndKeepsOrder_AcrossWrapAround()
        {
            var q = new EventQueue<int>();
            var got = new List<int>();
            q.Subscribe(e => got.Add(e));
            for (int i = 0; i < 10; i++) q.Post(i);
            q.Process(8);                       // head moves forward, leaving room at the front
            for (int i = 10; i < 60; i++) q.Post(i); // wraps, then grows past the starting size
            q.Process();
            var expected = new List<int>();
            for (int i = 0; i < 60; i++) expected.Add(i);
            Assert.AreEqual(Join(expected), Join(got));
        }

        [Test]
        public void Bounded_WrapAround_KeepsOrder()
        {
            var q = new EventQueue<int>(4, EventQueueOverflow.Throw);
            var got = new List<int>();
            q.Subscribe(e => got.Add(e));
            q.Post(1); q.Post(2); q.Post(3);
            q.Process(2);
            q.Post(4); q.Post(5); q.Post(6);
            q.Process();
            Assert.AreEqual("1,2,3,4,5,6", Join(got));
        }

        [Test]
        public void AFailingHandler_WithNoSubscriber_LetsTheOthersRunThenRethrowsTheOriginal()
        {
            var q = new EventQueue<int>();
            var got = new List<string>();
            q.Subscribe(e => { got.Add("a"); throw new InvalidOperationException("boom"); });
            q.Subscribe(e => got.Add("b"));
            q.Post(1); q.Post(2);
            var ex = Assert.Throws<InvalidOperationException>(() => q.Process());
            Assert.AreEqual("boom", ex.Message);
            Assert.AreEqual("a,b", string.Join(",", got));
            Assert.AreEqual(1, q.Count); // event 2 is still queued
        }

        [Test]
        public void TwoFailingHandlers_AreReportedTogether()
        {
            var q = new EventQueue<int>();
            q.Subscribe(e => { throw new InvalidOperationException("one"); });
            q.Subscribe(e => { throw new ArgumentException("two"); });
            q.Post(1);
            var ex = Assert.Throws<AggregateException>(() => q.Process());
            Assert.AreEqual(2, ex.InnerExceptions.Count);
        }

        [Test]
        public void AFailingHandler_WithAFailureSubscriber_IsReportedAndDeliveryContinues()
        {
            var q = new EventQueue<int>();
            var seen = new List<string>();
            var failures = new List<string>();
            q.HandlerFailed += (e, ex) => failures.Add(e + ":" + ex.Message);
            q.Subscribe(e => { throw new InvalidOperationException("boom"); });
            q.Subscribe(e => seen.Add("b" + e));
            q.Post(1); q.Post(2);
            Assert.AreEqual(2, q.Process());
            Assert.AreEqual("1:boom,2:boom", string.Join(",", failures));
            Assert.AreEqual("b1,b2", string.Join(",", seen));
        }

        [Test]
        public void ProcessFromInsideAHandler_IsReportedAsAHandlerFailure()
        {
            var q = new EventQueue<int>();
            var failures = new List<Exception>();
            q.HandlerFailed += (e, ex) => failures.Add(ex);
            q.Subscribe(e => q.Process());
            q.Post(1);
            q.Process();
            Assert.AreEqual(1, failures.Count);
            Assert.IsTrue(failures[0] is InvalidOperationException);
        }

        [Test]
        public void Clear_DiscardsWithoutDeliveringOrCounting()
        {
            var q = new EventQueue<int>(3, EventQueueOverflow.DropOldest);
            int calls = 0;
            q.Subscribe(e => calls++);
            q.Post(1); q.Post(2);
            q.Clear();
            Assert.AreEqual(0, q.Count);
            Assert.AreEqual(0, q.DroppedCount);
            Assert.AreEqual(0, q.Process());
            Assert.AreEqual(0, calls);
            q.Post(7);                       // still usable afterwards
            Assert.AreEqual(1, q.Process());
        }

        [Test]
        public void StructEvents_AreDeliveredByValue()
        {
            var q = new EventQueue<Hit>();
            int total = 0;
            q.Subscribe(h => total += h.Damage);
            q.Post(new Hit(5)); q.Post(new Hit(7));
            q.Process();
            Assert.AreEqual(12, total);
        }

        [Test]
        public void BadArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EventQueue<int>(-1));
            var q = new EventQueue<int>();
            Assert.Throws<ArgumentNullException>(() => q.Subscribe(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => q.Process(-1));
        }
    }
}
