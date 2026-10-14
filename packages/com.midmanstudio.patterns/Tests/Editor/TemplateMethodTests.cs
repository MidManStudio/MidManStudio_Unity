// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/template-method.md, section "TemplateMethodTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using MidManStudio.Patterns.TemplateMethod;

namespace MidManStudio.Patterns.TemplateMethod.Tests
{
    public class TemplateMethodTests
    {
        private class Recorder : TemplateAction<List<string>>
        {
            public bool Allow = true;
            public Exception ExecuteError;
            public Exception BeginError;
            public Exception FailedError;
            public Exception EndError;
            public Exception SeenByOnFailed;

            protected override bool CanRun(List<string> log) { log.Add("can"); return Allow; }
            protected override void Begin(List<string> log) { log.Add("begin"); if (BeginError != null) throw BeginError; }
            protected override void Execute(List<string> log) { log.Add("execute"); if (ExecuteError != null) throw ExecuteError; }
            protected override void OnFailed(List<string> log, Exception error) { log.Add("failed"); SeenByOnFailed = error; if (FailedError != null) throw FailedError; }
            protected override void End(List<string> log) { log.Add("end"); if (EndError != null) throw EndError; }
        }

        private class OnlyExecute : TemplateAction<List<string>>
        {
            protected override void Execute(List<string> log) { log.Add("execute"); }
        }

        private static string Join(List<string> log) { return string.Join(",", log.ToArray()); }

        [Test]
        public void TheStepsRunInAFixedOrderAndRunReportsSuccess()
        {
            var log = new List<string>();
            Assert.IsTrue(new Recorder().Run(log));
            Assert.AreEqual("can,begin,execute,end", Join(log));
        }

        [Test]
        public void WhenCanRunSaysNoNothingElseIsCalled()
        {
            var log = new List<string>();
            Assert.IsFalse(new Recorder { Allow = false }.Run(log));
            Assert.AreEqual("can", Join(log));
        }

        [Test]
        public void AFailingExecuteCallsOnFailedThenEndAndRethrowsTheSameException()
        {
            var log = new List<string>();
            var error = new InvalidOperationException("boom");
            var recorder = new Recorder { ExecuteError = error };
            var thrown = Assert.Throws<InvalidOperationException>(() => recorder.Run(log));
            Assert.AreSame(error, thrown);
            Assert.AreSame(error, recorder.SeenByOnFailed);
            Assert.AreEqual("can,begin,execute,failed,end", Join(log));
        }

        [Test]
        public void OnFailedIsNotCalledOnSuccess()
        {
            var log = new List<string>();
            var recorder = new Recorder();
            recorder.Run(log);
            Assert.IsNull(recorder.SeenByOnFailed);
        }

        [Test]
        public void IfBeginThrowsTheActionNeverStartedSoThereIsNoCleanup()
        {
            var log = new List<string>();
            var recorder = new Recorder { BeginError = new InvalidOperationException("begin failed") };
            Assert.Throws<InvalidOperationException>(() => recorder.Run(log));
            Assert.AreEqual("can,begin", Join(log));
        }

        [Test]
        public void IfOnFailedThrowsEndStillRunsAndTheNewExceptionWins()
        {
            var log = new List<string>();
            var recorder = new Recorder
            {
                ExecuteError = new InvalidOperationException("execute failed"),
                FailedError = new ArgumentException("handler failed")
            };
            Assert.Throws<ArgumentException>(() => recorder.Run(log));
            Assert.AreEqual("can,begin,execute,failed,end", Join(log));
        }

        [Test]
        public void AnExceptionFromEndPropagatesAfterASuccessfulExecute()
        {
            var log = new List<string>();
            var recorder = new Recorder { EndError = new InvalidOperationException("end failed") };
            Assert.Throws<InvalidOperationException>(() => recorder.Run(log));
            Assert.AreEqual("can,begin,execute,end", Join(log));
        }

        [Test]
        public void ASubclassThatOnlyOverridesExecuteGetsNoOpDefaults()
        {
            var log = new List<string>();
            Assert.IsTrue(new OnlyExecute().Run(log));
            Assert.AreEqual("execute", Join(log));
        }

        [Test]
        public void TheSkeletonItselfCannotBeOverridden()
        {
            MethodInfo run = typeof(TemplateAction<List<string>>).GetMethod("Run");
            Assert.IsFalse(run.IsVirtual);
        }

        [Test]
        public void TheSameContextReachesEveryStep()
        {
            var seen = new List<object>();
            new ContextChecker(seen).Run("shared");
            Assert.AreEqual(3, seen.Count);
            Assert.IsTrue(ReferenceEquals(seen[0], seen[1]) && ReferenceEquals(seen[1], seen[2]));
        }

        private class ContextChecker : TemplateAction<string>
        {
            private readonly List<object> _seen;
            public ContextChecker(List<object> seen) { _seen = seen; }
            protected override void Begin(string c) { _seen.Add(c); }
            protected override void Execute(string c) { _seen.Add(c); }
            protected override void End(string c) { _seen.Add(c); }
        }

        [Test]
        public void AnActionCanRunManyTimes()
        {
            var log = new List<string>();
            var recorder = new Recorder();
            recorder.Run(log);
            recorder.Run(log);
            Assert.AreEqual("can,begin,execute,end,can,begin,execute,end", Join(log));
        }
    }
}
