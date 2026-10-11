// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/game-loop.md, section "GameLoopTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.GameLoop;

namespace MidManStudio.Patterns.GameLoop.Tests
{
    public class GameLoopTests
    {
        [Test]
        public void AFullStepOfFrameTimeRunsOneStepWithTheStepLength()
        {
            var seen = new List<float>();
            var loop = new FixedStepLoop(0.25f, dt => seen.Add(dt));
            Assert.AreEqual(1, loop.Advance(0.25f));
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual(0.25f, seen[0], 0.00001f);
        }

        [Test]
        public void ShortFramesAccumulateUntilAStepIsDue()
        {
            int steps = 0;
            var loop = new FixedStepLoop(0.25f, dt => steps++);
            Assert.AreEqual(0, loop.Advance(0.125f));
            Assert.AreEqual(0.125f, loop.Accumulator, 0.00001f);
            Assert.AreEqual(1, loop.Advance(0.125f));
            Assert.AreEqual(0f, loop.Accumulator, 0.00001f);
            Assert.AreEqual(1, steps);
        }

        [Test]
        public void ALongFrameRunsSeveralSteps()
        {
            int steps = 0;
            var loop = new FixedStepLoop(0.25f, dt => steps++);
            Assert.AreEqual(4, loop.Advance(1f));
            Assert.AreEqual(4, steps);
            Assert.AreEqual(0L, loop.DroppedSteps);
        }

        [Test]
        public void AHitchIsCappedAndTheExtraStepsAreDroppedNotCarriedOver()
        {
            int steps = 0;
            var loop = new FixedStepLoop(0.25f, dt => steps++, 5);
            Assert.AreEqual(5, loop.Advance(10.125f));
            Assert.AreEqual(5, steps);
            Assert.AreEqual(35L, loop.DroppedSteps);
            Assert.AreEqual(0.125f, loop.Accumulator, 0.00001f);
            Assert.AreEqual(0, loop.Advance(0f));
        }

        [Test]
        public void AlphaIsTheFractionOfTheNextStepAlreadyElapsed()
        {
            var loop = new FixedStepLoop(0.25f, dt => { });
            loop.Advance(0.0625f);
            Assert.AreEqual(0.25f, loop.Alpha, 0.00001f);
            loop.Advance(0.1875f);
            Assert.AreEqual(0f, loop.Alpha, 0.00001f);
        }

        [Test]
        public void TheTotalNumberOfStepsDoesNotDependOnHowTheTimeIsSplitIntoFrames()
        {
            int a = 0, b = 0, c = 0;
            var one = new FixedStepLoop(0.125f, dt => a++, 100);
            one.Advance(2f);
            var many = new FixedStepLoop(0.125f, dt => b++, 100);
            for (int i = 0; i < 32; i++) many.Advance(0.0625f);
            var uneven = new FixedStepLoop(0.125f, dt => c++, 100);
            float[] frames = { 0.5f, 0.0625f, 0.4375f, 0.25f, 0.75f };
            foreach (float f in frames) uneven.Advance(f);
            Assert.AreEqual(16, a);
            Assert.AreEqual(16, b);
            Assert.AreEqual(16, c);
        }

        [Test]
        public void TimeScaleMultipliesTheFrameTime()
        {
            int steps = 0;
            var loop = new FixedStepLoop(0.25f, dt => steps++, 100);
            loop.TimeScale = 2f;
            Assert.AreEqual(2, loop.Advance(0.25f));
            loop.TimeScale = 0.5f;
            Assert.AreEqual(0, loop.Advance(0.25f));
            Assert.AreEqual(1, loop.Advance(0.25f));
            Assert.AreEqual(3, steps);
        }

        [Test]
        public void ATimeScaleOfZeroFreezesTheSimulation()
        {
            int steps = 0;
            var loop = new FixedStepLoop(0.25f, dt => steps++);
            loop.Advance(0.125f);
            loop.TimeScale = 0f;
            Assert.AreEqual(0, loop.Advance(5f));
            Assert.AreEqual(0.125f, loop.Accumulator, 0.00001f);
            Assert.AreEqual(0, steps);
        }

        [Test]
        public void PausedDoesNothingAndKeepsTheAccumulator()
        {
            int steps = 0;
            var loop = new FixedStepLoop(0.25f, dt => steps++);
            loop.Advance(0.125f);
            loop.Paused = true;
            Assert.AreEqual(0, loop.Advance(1f));
            Assert.AreEqual(0.125f, loop.Accumulator, 0.00001f);
            loop.Paused = false;
            Assert.AreEqual(1, loop.Advance(0.125f));
        }

        [Test]
        public void ResetClearsTheAccumulatorAndCountersButKeepsTheSettings()
        {
            var loop = new FixedStepLoop(0.25f, dt => { }, 2);
            loop.TimeScale = 2f;
            loop.Paused = true;
            loop.Paused = false;
            loop.Advance(5f);
            Assert.IsTrue(loop.StepCount > 0);
            loop.Reset();
            Assert.AreEqual(0f, loop.Accumulator, 0.00001f);
            Assert.AreEqual(0L, loop.StepCount);
            Assert.AreEqual(0L, loop.DroppedSteps);
            Assert.AreEqual(2f, loop.TimeScale, 0.00001f);
        }

        [Test]
        public void StepCountAddsUpAcrossCalls()
        {
            var loop = new FixedStepLoop(0.25f, dt => { }, 100);
            loop.Advance(0.5f);
            loop.Advance(0.75f);
            Assert.AreEqual(5L, loop.StepCount);
        }

        [Test]
        public void AStepThatThrowsPropagatesAndTheLoopKeepsGoingAfterwards()
        {
            int calls = 0;
            var loop = new FixedStepLoop(0.25f, dt => { calls++; if (calls == 2) throw new InvalidOperationException("boom"); }, 10);
            Assert.Throws<InvalidOperationException>(() => loop.Advance(1f));
            Assert.AreEqual(2L, loop.StepCount);
            Assert.AreEqual(2, loop.Advance(0f));
            Assert.AreEqual(4, calls);
        }

        [Test]
        public void BadArgumentsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepLoop(0f, dt => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepLoop(float.NaN, dt => { }));
            Assert.Throws<ArgumentNullException>(() => new FixedStepLoop(0.25f, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepLoop(0.25f, dt => { }, 0));
            var loop = new FixedStepLoop(0.25f, dt => { });
            Assert.Throws<ArgumentOutOfRangeException>(() => loop.Advance(-0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => loop.Advance(float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => { loop.TimeScale = -1f; });
        }
    }
}
