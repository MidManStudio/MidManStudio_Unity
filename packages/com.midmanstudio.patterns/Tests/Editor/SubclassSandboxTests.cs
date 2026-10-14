// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/subclass-sandbox.md, section "SubclassSandboxTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.SubclassSandbox;

namespace MidManStudio.Patterns.SubclassSandbox.Tests
{
    public class SubclassSandboxTests
    {
        private interface IWorld
        {
            void Move(float distance);
            void Spawn(string what);
            int Health { get; }
        }

        private class FakeWorld : IWorld
        {
            public float Moved;
            public List<string> Spawned = new List<string>();
            public int Health { get; set; }
            public void Move(float distance) { Moved += distance; }
            public void Spawn(string what) { Spawned.Add(what); }
        }

        private class Dash : SandboxedBehavior<IWorld>
        {
            protected override void Activate() { Context.Move(5f); Context.Spawn("dust"); }
        }

        private class Probe : SandboxedBehavior<IWorld>
        {
            public bool RunningInside;
            public bool Fail;
            public IWorld Kept;
            protected override void Activate()
            {
                RunningInside = IsRunning;
                Kept = Context;
                if (Fail) throw new InvalidOperationException("boom");
            }
            public IWorld ReadContext() { return Context; }
        }

        private class Reenter : SandboxedBehavior<IWorld>
        {
            protected override void Activate() { Run(Context); }
        }

        private class Heal : SandboxedFunction<IWorld, int>
        {
            public bool Fail;
            protected override int Evaluate()
            {
                if (Fail) throw new InvalidOperationException("boom");
                return Context.Health / 2;
            }
            public IWorld ReadContext() { return Context; }
        }

        private class ReenterFunction : SandboxedFunction<IWorld, int>
        {
            protected override int Evaluate() { return Run(Context); }
        }

        [Test]
        public void ABehaviourActsOnlyThroughItsContext()
        {
            var world = new FakeWorld();
            new Dash().Run(world);
            Assert.AreEqual(5f, world.Moved, 0.0001f);
            Assert.AreEqual("dust", world.Spawned[0]);
        }

        [Test]
        public void TheContextIsOnlyReadableWhileTheBehaviourRuns()
        {
            var probe = new Probe();
            Assert.Throws<InvalidOperationException>(() => probe.ReadContext());
            probe.Run(new FakeWorld());
            Assert.IsNotNull(probe.Kept);
            Assert.Throws<InvalidOperationException>(() => probe.ReadContext());
        }

        [Test]
        public void IsRunningIsTrueOnlyInsideTheCall()
        {
            var probe = new Probe();
            Assert.IsFalse(probe.IsRunning);
            probe.Run(new FakeWorld());
            Assert.IsTrue(probe.RunningInside);
            Assert.IsFalse(probe.IsRunning);
        }

        [Test]
        public void AThrowingBehaviourStillDropsTheContextAndCanRunAgain()
        {
            var probe = new Probe { Fail = true };
            Assert.Throws<InvalidOperationException>(() => probe.Run(new FakeWorld()));
            Assert.IsFalse(probe.IsRunning);
            Assert.Throws<InvalidOperationException>(() => probe.ReadContext());
            probe.Fail = false;
            probe.Run(new FakeWorld());
            Assert.IsTrue(probe.RunningInside);
        }

        [Test]
        public void ARunInsideARunIsRejectedAndTheStateIsRestored()
        {
            var behaviour = new Reenter();
            Assert.Throws<InvalidOperationException>(() => behaviour.Run(new FakeWorld()));
            Assert.IsFalse(behaviour.IsRunning);
            var probe = new Probe();
            probe.Run(new FakeWorld());
            Assert.IsTrue(probe.RunningInside);
        }

        [Test]
        public void ANullContextIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new Dash().Run(null));
            Assert.Throws<ArgumentNullException>(() => new Heal().Run(null));
        }

        [Test]
        public void OneBehaviourCanRunWithDifferentContextsOneAfterTheOther()
        {
            var dash = new Dash();
            var a = new FakeWorld();
            var b = new FakeWorld();
            dash.Run(a);
            dash.Run(b);
            dash.Run(b);
            Assert.AreEqual(5f, a.Moved, 0.0001f);
            Assert.AreEqual(10f, b.Moved, 0.0001f);
        }

        [Test]
        public void SeparateInstancesDoNotBlockEachOther()
        {
            var inner = new Probe();
            var world = new FakeWorld();
            var outer = new NestedRunner(inner);
            outer.Run(world);
            Assert.IsTrue(inner.RunningInside);
        }

        private class NestedRunner : SandboxedBehavior<IWorld>
        {
            private readonly Probe _inner;
            public NestedRunner(Probe inner) { _inner = inner; }
            protected override void Activate() { _inner.Run(Context); }
        }

        [Test]
        public void AFunctionReturnsItsResultFromTheContext()
        {
            Assert.AreEqual(20, new Heal().Run(new FakeWorld { Health = 40 }));
        }

        [Test]
        public void AFunctionHasTheSameContextRules()
        {
            var heal = new Heal();
            Assert.Throws<InvalidOperationException>(() => heal.ReadContext());
            heal.Run(new FakeWorld { Health = 2 });
            Assert.Throws<InvalidOperationException>(() => heal.ReadContext());
            Assert.IsFalse(heal.IsRunning);
        }

        [Test]
        public void AThrowingFunctionLeavesItReadyToRunAgain()
        {
            var heal = new Heal { Fail = true };
            Assert.Throws<InvalidOperationException>(() => heal.Run(new FakeWorld()));
            Assert.IsFalse(heal.IsRunning);
            heal.Fail = false;
            Assert.AreEqual(5, heal.Run(new FakeWorld { Health = 10 }));
        }

        [Test]
        public void AFunctionThatRunsItselfIsRejected()
        {
            Assert.Throws<InvalidOperationException>(() => new ReenterFunction().Run(new FakeWorld()));
        }
    }
}
