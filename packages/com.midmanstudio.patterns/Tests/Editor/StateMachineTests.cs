// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/state.md, section "StateMachineTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.State;

namespace MidManStudio.Patterns.State.Tests
{
    public class StateMachineTests
    {
        private sealed class Ctx
        {
            public readonly List<string> Log = new List<string>();
            public bool Flag;
            public override string ToString() => string.Join(",", Log);
        }

        private class Rec : State<Ctx>
        {
            public readonly string Name;
            public Action<Ctx> OnEnter, OnExit;
            public Action<Ctx, float> OnUpdate;
            public Rec(string name) { Name = name; }
            public override void Enter(Ctx c)  { c.Log.Add(Name + ".enter");  if (OnEnter  != null) OnEnter(c); }
            public override void Exit(Ctx c)   { c.Log.Add(Name + ".exit");   if (OnExit   != null) OnExit(c); }
            public override void Update(Ctx c, float dt) { c.Log.Add(Name + ".update(" + dt + ")"); if (OnUpdate != null) OnUpdate(c, dt); }
        }

        private sealed class PausableRec : Rec, IPausableState<Ctx>
        {
            public PausableRec(string name) : base(name) { }
            public void Pause(Ctx c)  { c.Log.Add(Name + ".pause"); }
            public void Resume(Ctx c) { c.Log.Add(Name + ".resume"); }
        }

        private static StateMachine<Ctx> Make(out Ctx ctx, int maxChained = 64)
        {
            ctx = new Ctx();
            return new StateMachine<Ctx>(ctx, maxChained);
        }

        [Test]
        public void ChangeState_OnAFreshMachine_EntersWithoutExiting()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a");
            Assert.IsFalse(m.IsRunning);
            m.ChangeState(a);
            Assert.AreEqual("a.enter", c.ToString());
            Assert.IsTrue(ReferenceEquals(a, m.Current));
            Assert.IsTrue(m.IsRunning);
            Assert.AreEqual(1, m.StackDepth);
        }

        [Test]
        public void ChangeState_ExitsTheOldStateBeforeEnteringTheNew()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b");
            m.ChangeState(a);
            m.ChangeState(b);
            Assert.AreEqual("a.enter,a.exit,b.enter", c.ToString());
            Assert.IsTrue(ReferenceEquals(a, m.Previous));
            Assert.AreEqual(1, m.StackDepth);
        }

        [Test]
        public void ChangeState_ToTheCurrentState_DoesNothing()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a");
            m.ChangeState(a);
            m.ChangeState(a);
            Assert.AreEqual("a.enter", c.ToString());
        }

        [Test]
        public void Update_CallsTheCurrentStateWithTheDeltaTime_AndIsIgnoredWhenNotRunning()
        {
            Ctx c; var m = Make(out c);
            m.Update(0.5f);
            Assert.AreEqual("", c.ToString());
            m.ChangeState(new Rec("a"));
            m.Update(0.25f);
            Assert.AreEqual("a.enter,a.update(0.25)", c.ToString());
        }

        [Test]
        public void StateChanged_ReportsPreviousAndCurrent()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b");
            var seen = new List<string>();
            m.StateChanged += (from, to) =>
                seen.Add((from == null ? "null" : ((Rec)from).Name) + ">" + (to == null ? "null" : ((Rec)to).Name));
            m.ChangeState(a);
            m.ChangeState(b);
            m.Stop();
            Assert.AreEqual("null>a,a>b,b>null", string.Join(",", seen));
        }

        // ── push and pop ────────────────────────────────────────────────────

        [Test]
        public void Push_OverAPausableState_PausesItAndPopResumesIt()
        {
            Ctx c; var m = Make(out c);
            m.ChangeState(new PausableRec("game"));
            m.PushState(new Rec("menu"));
            Assert.AreEqual(2, m.StackDepth);
            m.PopState();
            Assert.AreEqual("game.enter,game.pause,menu.enter,menu.exit,game.resume", c.ToString());
            Assert.AreEqual(1, m.StackDepth);
        }

        [Test]
        public void Push_OverAPlainState_ExitsItAndPopEntersItAgain()
        {
            Ctx c; var m = Make(out c);
            m.ChangeState(new Rec("game"));
            m.PushState(new Rec("menu"));
            m.PopState();
            Assert.AreEqual("game.enter,game.exit,menu.enter,menu.exit,game.enter", c.ToString());
        }

        [Test]
        public void Update_OnlyReachesTheTopState()
        {
            Ctx c; var m = Make(out c);
            m.ChangeState(new PausableRec("game"));
            m.PushState(new Rec("menu"));
            c.Log.Clear();
            m.Update(1f);
            Assert.AreEqual("menu.update(1)", c.ToString());
        }

        [Test]
        public void Pop_WithOneStateOrNone_ThrowsAndChangesNothing()
        {
            Ctx c; var m = Make(out c);
            Assert.Throws<InvalidOperationException>(() => m.PopState());
            m.ChangeState(new Rec("a"));
            Assert.Throws<InvalidOperationException>(() => m.PopState());
            Assert.AreEqual("a.enter", c.ToString());
            Assert.AreEqual(1, m.StackDepth);
        }

        [Test]
        public void ChangeState_WithAStackedMachine_ReplacesOnlyTheTop()
        {
            Ctx c; var m = Make(out c);
            m.ChangeState(new PausableRec("game"));
            m.PushState(new Rec("menu"));
            m.ChangeState(new Rec("options"));
            Assert.AreEqual(2, m.StackDepth);
            Assert.AreEqual("game.enter,game.pause,menu.enter,menu.exit,options.enter", c.ToString());
        }

        [Test]
        public void Stop_ExitsEveryStateTopFirst_IncludingPausedOnes()
        {
            Ctx c; var m = Make(out c);
            m.ChangeState(new PausableRec("game"));
            m.PushState(new Rec("menu"));
            c.Log.Clear();
            m.Stop();
            Assert.AreEqual("menu.exit,game.exit", c.ToString());
            Assert.IsFalse(m.IsRunning);
            Assert.IsNull(m.Current);
            m.Stop(); // already stopped
            Assert.AreEqual("menu.exit,game.exit", c.ToString());
        }

        // ── requests made from inside callbacks ─────────────────────────────

        [Test]
        public void ARequestFromEnter_RunsAfterThatEnterReturns()
        {
            Ctx c; var m = Make(out c);
            var b = new Rec("b");
            var a = new Rec("a");
            a.OnEnter = ctx => { ctx.Log.Add("a.requests-b"); m.ChangeState(b); ctx.Log.Add("a.enter-done"); };
            var seen = new List<string>();
            m.StateChanged += (from, to) => seen.Add(((Rec)to).Name);
            m.ChangeState(a);
            Assert.AreEqual("a.enter,a.requests-b,a.enter-done,a.exit,b.enter", c.ToString());
            Assert.IsTrue(ReferenceEquals(b, m.Current));
            Assert.AreEqual("a,b", string.Join(",", seen));
        }

        [Test]
        public void ARequestFromUpdate_IsAppliedBeforeUpdateReturns()
        {
            Ctx c; var m = Make(out c);
            var b = new Rec("b");
            var a = new Rec("a");
            a.OnUpdate = (ctx, dt) => m.ChangeState(b);
            m.ChangeState(a);
            c.Log.Clear();
            m.Update(1f);
            Assert.AreEqual("a.update(1),a.exit,b.enter", c.ToString());
        }

        [Test]
        public void StatesThatKeepSendingEachOtherBack_HitTheLimitAndThrow()
        {
            Ctx c; var m = Make(out c, 8);
            Rec a = null, b = null;
            a = new Rec("a"); b = new Rec("b");
            a.OnEnter = ctx => m.ChangeState(b);
            b.OnEnter = ctx => m.ChangeState(a);
            Assert.Throws<InvalidOperationException>(() => m.ChangeState(a));
            // The machine is not left stuck: a new request still works.
            var calm = new Rec("calm");
            m.ChangeState(calm);
            Assert.IsTrue(ReferenceEquals(calm, m.Current));
        }

        [Test]
        public void Update_FromInsideACallback_Throws()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a");
            a.OnUpdate = (ctx, dt) => m.Update(1f);
            m.ChangeState(a);
            Assert.Throws<InvalidOperationException>(() => m.Update(1f));
            // Still usable afterwards.
            a.OnUpdate = null;
            m.Update(1f);
        }

        // ── exceptions ──────────────────────────────────────────────────────

        [Test]
        public void ExitThatThrows_LeavesTheOldStateCurrentAndTheNewOneNotEntered()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b");
            a.OnExit = ctx => { throw new InvalidOperationException("exit failed"); };
            m.ChangeState(a);
            Assert.Throws<InvalidOperationException>(() => m.ChangeState(b));
            Assert.IsTrue(ReferenceEquals(a, m.Current));
            Assert.AreEqual("a.enter,a.exit", c.ToString());
        }

        [Test]
        public void EnterThatThrows_LeavesTheNewStateCurrent()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b"); var next = new Rec("next");
            b.OnEnter = ctx => { throw new InvalidOperationException("enter failed"); };
            m.ChangeState(a);
            Assert.Throws<InvalidOperationException>(() => m.ChangeState(b));
            Assert.IsTrue(ReferenceEquals(b, m.Current));
            m.ChangeState(next);
            Assert.AreEqual("a.enter,a.exit,b.enter,b.exit,next.enter", c.ToString());
        }

        [Test]
        public void AFailure_DropsRequestsThatWereWaiting()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b"); var never = new Rec("never");
            a.OnEnter = ctx => { m.ChangeState(b); m.ChangeState(never); };
            b.OnEnter = ctx => { throw new InvalidOperationException("boom"); };
            Assert.Throws<InvalidOperationException>(() => m.ChangeState(a));
            Assert.IsTrue(ReferenceEquals(b, m.Current));
            Assert.AreEqual("a.enter,a.exit,b.enter", c.ToString());
        }

        [Test]
        public void UpdateThatThrows_DropsRequestsMadeDuringIt_AndTheMachineStaysUsable()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b");
            a.OnUpdate = (ctx, dt) => { m.ChangeState(b); throw new InvalidOperationException("update failed"); };
            m.ChangeState(a);
            Assert.Throws<InvalidOperationException>(() => m.Update(1f));
            Assert.IsTrue(ReferenceEquals(a, m.Current));
            a.OnUpdate = null;
            m.Update(1f);
            m.ChangeState(b);
            Assert.IsTrue(ReferenceEquals(b, m.Current));
        }

        // ── transitions ─────────────────────────────────────────────────────

        [Test]
        public void Transition_FiresAfterTheStatesUpdate_WhenItsConditionIsTrue()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b");
            m.AddTransition(a, b, ctx => ctx.Flag);
            m.ChangeState(a);
            m.Update(1f);
            Assert.AreEqual("a.enter,a.update(1)", c.ToString());
            c.Flag = true;
            m.Update(1f);
            Assert.AreEqual("a.enter,a.update(1),a.update(1),a.exit,b.enter", c.ToString());
        }

        [Test]
        public void Transition_FromAStateThatIsNotCurrent_IsNotChecked()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b"); var other = new Rec("other");
            m.AddTransition(other, b, ctx => true);
            m.ChangeState(a);
            m.Update(1f);
            Assert.IsTrue(ReferenceEquals(a, m.Current));
        }

        [Test]
        public void TransitionsAreCheckedInOrder_FirstTrueWins()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b"); var d = new Rec("d");
            m.AddTransition(a, b, ctx => true);
            m.AddTransition(a, d, ctx => true);
            m.ChangeState(a);
            m.Update(1f);
            Assert.IsTrue(ReferenceEquals(b, m.Current));
        }

        [Test]
        public void AnyTransition_IsCheckedBeforeThePerStateOnes_AndSkipsTheCurrentState()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b"); var dead = new Rec("dead");
            m.AddTransition(a, b, ctx => true);
            m.AddAnyTransition(dead, ctx => ctx.Flag);
            m.ChangeState(a);
            c.Flag = true;
            m.Update(1f);
            Assert.IsTrue(ReferenceEquals(dead, m.Current)); // any-transition beat a>b
            c.Log.Clear();
            m.Update(1f); // any-transition targets the current state, so it is skipped and nothing re-enters
            Assert.AreEqual("dead.update(1)", c.ToString());
        }

        [Test]
        public void ClearTransitions_RemovesThemAll()
        {
            Ctx c; var m = Make(out c);
            var a = new Rec("a"); var b = new Rec("b");
            m.AddTransition(a, b, ctx => true);
            m.AddAnyTransition(b, ctx => true);
            m.ClearTransitions();
            m.ChangeState(a);
            m.Update(1f);
            Assert.IsTrue(ReferenceEquals(a, m.Current));
        }

        // ── small API ───────────────────────────────────────────────────────

        [Test]
        public void IsIn_ChecksTheCurrentStatesType()
        {
            Ctx c; var m = Make(out c);
            m.ChangeState(new PausableRec("a"));
            Assert.IsTrue(m.IsIn<PausableRec>());
            Assert.IsFalse(m.IsIn<MenuState>());
        }

        private sealed class MenuState : State<Ctx> { }

        [Test]
        public void NullArgumentsAndBadLimits_Throw()
        {
            Ctx c; var m = Make(out c);
            Assert.Throws<ArgumentNullException>(() => m.ChangeState(null));
            Assert.Throws<ArgumentNullException>(() => m.PushState(null));
            Assert.Throws<ArgumentNullException>(() => m.AddTransition(null, new Rec("b"), x => true));
            Assert.Throws<ArgumentNullException>(() => m.AddTransition(new Rec("a"), null, x => true));
            Assert.Throws<ArgumentNullException>(() => m.AddTransition(new Rec("a"), new Rec("b"), null));
            Assert.Throws<ArgumentNullException>(() => m.AddAnyTransition(null, x => true));
            Assert.Throws<ArgumentNullException>(() => m.AddAnyTransition(new Rec("a"), null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StateMachine<Ctx>(new Ctx(), 0));
        }
    }
}
