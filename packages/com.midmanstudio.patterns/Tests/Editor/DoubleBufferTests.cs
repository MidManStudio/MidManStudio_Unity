// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/double-buffer.md, section "DoubleBufferTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.DoubleBuffer;

namespace MidManStudio.Patterns.DoubleBuffer.Tests
{
    public class DoubleBufferTests
    {
        [Test]
        public void FrontAndBack_AreWhatWasPassedIn()
        {
            var a = new List<int>();
            var b = new List<int>();
            var buffer = new DoubleBuffer<List<int>>(a, b);
            Assert.AreSame(a, buffer.Front);
            Assert.AreSame(b, buffer.Back);
            Assert.AreEqual(0, buffer.SwapCount);
        }

        [Test]
        public void Swap_ExchangesTheTwoBuffersAndCountsTheSwap()
        {
            var a = new List<int>();
            var b = new List<int>();
            var buffer = new DoubleBuffer<List<int>>(a, b);
            buffer.Swap();
            Assert.AreSame(b, buffer.Front);
            Assert.AreSame(a, buffer.Back);
            buffer.Swap();
            Assert.AreSame(a, buffer.Front);
            Assert.AreEqual(2, buffer.SwapCount);
        }

        [Test]
        public void WritesToTheBackAreInvisibleToReadersUntilTheSwap()
        {
            var buffer = DoubleBuffer<List<int>>.Create(() => new List<int>());
            buffer.Back.Add(5);
            Assert.AreEqual(0, buffer.Front.Count);
            buffer.Swap();
            Assert.AreEqual(1, buffer.Front.Count);
            Assert.AreEqual(5, buffer.Front[0]);
        }

        [Test]
        public void SwapWithReset_ClearsTheNewBackBufferAndKeepsTheFront()
        {
            var buffer = DoubleBuffer<List<int>>.Create(() => new List<int>());
            buffer.Back.Add(1);
            buffer.Back.Add(2);
            buffer.Swap(list => list.Clear());
            Assert.AreEqual(2, buffer.Front.Count);
            Assert.AreEqual(0, buffer.Back.Count);
        }

        [Test]
        public void SwapWithReset_ResetsTheOldFrontNotTheNewFront()
        {
            var a = new List<int> { 9 };
            var b = new List<int> { 7 };
            var buffer = new DoubleBuffer<List<int>>(a, b);
            buffer.Swap(list => list.Clear());
            Assert.AreEqual(0, a.Count);
            Assert.AreEqual(1, b.Count);
        }

        [Test]
        public void EverythingSeesTheSamePreviousStateWhateverTheUpdateOrder()
        {
            // Each cell takes its left neighbour's value. In place this would smear the first value across the array.
            var buffer = new DoubleBuffer<int[]>(new[] { 1, 0, 0, 0 }, new int[4]);
            for (int step = 0; step < 2; step++)
            {
                int[] read = buffer.Front, write = buffer.Back;
                for (int i = 3; i >= 0; i--) write[i] = i == 0 ? 0 : read[i - 1];
                buffer.Swap();
            }
            Assert.AreEqual("0,0,1,0", string.Join(",", buffer.Front));
        }

        [Test]
        public void TheSameReferenceTwiceIsRejectedForReferenceTypes()
        {
            var list = new List<int>();
            Assert.Throws<ArgumentException>(() => new DoubleBuffer<List<int>>(list, list));
        }

        [Test]
        public void EqualValueTypesAreAllowed()
        {
            var buffer = new DoubleBuffer<int>(3, 3);
            buffer.Swap();
            Assert.AreEqual(3, buffer.Front);
        }

        [Test]
        public void NullDelegatesAreRejected()
        {
            var buffer = DoubleBuffer<List<int>>.Create(() => new List<int>());
            Assert.Throws<ArgumentNullException>(() => buffer.Swap(null));
            Assert.Throws<ArgumentNullException>(() => DoubleBuffer<List<int>>.Create(null));
        }
    }
}
