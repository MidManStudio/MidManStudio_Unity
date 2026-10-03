// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/command.md, section "CommandTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MidManStudio.Patterns.Command.Tests
{
    public class CommandTests
    {
        // ── test doubles ─────────────────────────────────────────────────────

        private sealed class Log
        {
            public readonly List<string> Lines = new List<string>();
            public override string ToString() => string.Join(",", Lines);
        }

        private sealed class LogCommand : IUndoableCommand, INamedCommand
        {
            private readonly Log _log;
            public string Name { get; }
            public bool ThrowOnExecute;
            public bool ThrowOnUndo;

            public LogCommand(Log log, string name) { _log = log; Name = name; }

            public void Execute()
            {
                if (ThrowOnExecute) throw new InvalidOperationException("exec " + Name);
                _log.Lines.Add("do:" + Name);
            }

            public void Undo()
            {
                if (ThrowOnUndo) throw new InvalidOperationException("undo " + Name);
                _log.Lines.Add("undo:" + Name);
            }
        }

        private sealed class Box { public int Value; }

        // Adds an amount to a box; merges with the command right after it.
        private sealed class AddCommand : IMergeableCommand
        {
            private readonly Box _box;
            private int _amount;
            public AddCommand(Box box, int amount) { _box = box; _amount = amount; }
            public void Execute() => _box.Value += _amount;
            public void Undo()    => _box.Value -= _amount;
            public bool TryMerge(IUndoableCommand next)
            {
                var other = next as AddCommand;
                if (other == null || other._box != _box) return false;
                _amount += other._amount;
                return true;
            }
        }

        private sealed class DelegateCommand : IUndoableCommand
        {
            private readonly Action _exec;
            private readonly Action _undo;
            public DelegateCommand(Action exec, Action undo = null) { _exec = exec; _undo = undo ?? (() => { }); }
            public void Execute() => _exec();
            public void Undo()    => _undo();
        }

        // ── CommandHistory ───────────────────────────────────────────────────

        [Test]
        public void Execute_Undo_Redo_RunInTheRightOrder()
        {
            var log = new Log();
            var h = new CommandHistory();
            h.Execute(new LogCommand(log, "a"));
            h.Execute(new LogCommand(log, "b"));
            Assert.AreEqual(2, h.UndoCount);

            Assert.IsTrue(h.Undo());
            Assert.IsTrue(h.Undo());
            Assert.IsFalse(h.Undo());
            Assert.IsTrue(h.Redo());
            Assert.IsTrue(h.Redo());
            Assert.IsFalse(h.Redo());
            Assert.AreEqual("do:a,do:b,undo:b,undo:a,do:a,do:b", log.ToString());
            Assert.AreEqual(2, h.UndoCount);
            Assert.AreEqual(0, h.RedoCount);
        }

        [Test]
        public void ExecutingANewCommand_ClearsRedo()
        {
            var log = new Log();
            var h = new CommandHistory();
            h.Execute(new LogCommand(log, "a"));
            h.Undo();
            Assert.AreEqual(1, h.RedoCount);
            h.Execute(new LogCommand(log, "b"));
            Assert.AreEqual(0, h.RedoCount);
            Assert.IsFalse(h.Redo());
        }

        [Test]
        public void Execute_ThatThrows_RecordsNothingAndKeepsRedo()
        {
            var log = new Log();
            var h = new CommandHistory();
            h.Execute(new LogCommand(log, "a"));
            h.Undo();
            var bad = new LogCommand(log, "bad") { ThrowOnExecute = true };
            Assert.Throws<InvalidOperationException>(() => h.Execute(bad));
            Assert.AreEqual(0, h.UndoCount);
            Assert.AreEqual(1, h.RedoCount);
            // The history is still usable afterwards.
            Assert.IsTrue(h.Redo());
        }

        [Test]
        public void Undo_ThatThrows_LeavesTheCommandOnTheUndoStack()
        {
            var log = new Log();
            var h = new CommandHistory();
            var cmd = new LogCommand(log, "a");
            h.Execute(cmd);
            cmd.ThrowOnUndo = true;
            Assert.Throws<InvalidOperationException>(() => h.Undo());
            Assert.AreEqual(1, h.UndoCount);
            Assert.AreEqual(0, h.RedoCount);
            cmd.ThrowOnUndo = false;
            Assert.IsTrue(h.Undo());
        }

        [Test]
        public void Redo_ThatThrows_LeavesTheCommandOnTheRedoStack()
        {
            var log = new Log();
            var h = new CommandHistory();
            var cmd = new LogCommand(log, "a");
            h.Execute(cmd);
            h.Undo();
            cmd.ThrowOnExecute = true;
            Assert.Throws<InvalidOperationException>(() => h.Redo());
            Assert.AreEqual(0, h.UndoCount);
            Assert.AreEqual(1, h.RedoCount);
        }

        [Test]
        public void Capacity_DropsTheOldestUndoStep()
        {
            var log = new Log();
            var h = new CommandHistory(2);
            h.Execute(new LogCommand(log, "a"));
            h.Execute(new LogCommand(log, "b"));
            h.Execute(new LogCommand(log, "c"));
            Assert.AreEqual(2, h.UndoCount);
            h.Undo();
            h.Undo();
            Assert.IsFalse(h.Undo());
            Assert.AreEqual("do:a,do:b,do:c,undo:c,undo:b", log.ToString());
        }

        [Test]
        public void SetCapacity_Lowered_TrimsImmediatelyAndRaisesChanged()
        {
            var log = new Log();
            var h = new CommandHistory(0);
            for (int i = 0; i < 5; i++) h.Execute(new LogCommand(log, i.ToString()));
            int changed = 0;
            h.Changed += () => changed++;
            h.SetCapacity(2);
            Assert.AreEqual(2, h.UndoCount);
            Assert.AreEqual(1, changed);
        }

        [Test]
        public void NegativeCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandHistory(-1));
        }

        [Test]
        public void Names_AreReportedForTheNextUndoAndRedo()
        {
            var log = new Log();
            var h = new CommandHistory();
            Assert.IsNull(h.NextUndoName);
            h.Execute(new LogCommand(log, "move"));
            h.Execute(new LogCommand(log, "scale"));
            Assert.AreEqual("scale", h.NextUndoName);
            h.Undo();
            Assert.AreEqual("move", h.NextUndoName);
            Assert.AreEqual("scale", h.NextRedoName);
        }

        [Test]
        public void Changed_FiresOncePerChange()
        {
            var log = new Log();
            var h = new CommandHistory();
            int changed = 0;
            h.Changed += () => changed++;
            h.Execute(new LogCommand(log, "a"));
            h.Undo();
            h.Redo();
            h.Clear();
            Assert.AreEqual(4, changed);
            h.Clear(); // already empty
            Assert.AreEqual(4, changed);
        }

        [Test]
        public void Clear_ForgetsEverythingWithoutUndoing()
        {
            var log = new Log();
            var h = new CommandHistory();
            h.Execute(new LogCommand(log, "a"));
            h.Execute(new LogCommand(log, "b"));
            h.Undo();
            h.Clear();
            Assert.AreEqual(0, h.UndoCount);
            Assert.AreEqual(0, h.RedoCount);
            Assert.AreEqual("do:a,do:b,undo:b", log.ToString());
        }

        // ── merging ──────────────────────────────────────────────────────────

        [Test]
        public void MergeableCommands_BecomeOneUndoStep()
        {
            var box = new Box();
            var h = new CommandHistory();
            h.Execute(new AddCommand(box, 1));
            h.Execute(new AddCommand(box, 2));
            h.Execute(new AddCommand(box, 3));
            Assert.AreEqual(6, box.Value);
            Assert.AreEqual(1, h.UndoCount);
            h.Undo();
            Assert.AreEqual(0, box.Value);
            h.Redo();
            Assert.AreEqual(6, box.Value);
        }

        [Test]
        public void BreakMerge_StartsANewUndoStep()
        {
            var box = new Box();
            var h = new CommandHistory();
            h.Execute(new AddCommand(box, 1));
            h.BreakMerge();
            h.Execute(new AddCommand(box, 2));
            Assert.AreEqual(2, h.UndoCount);
            h.Undo();
            Assert.AreEqual(1, box.Value);
        }

        [Test]
        public void AfterUndoAndRedo_TheNextCommandDoesNotMergeIntoTheRedoneOne()
        {
            var box = new Box();
            var h = new CommandHistory();
            h.Execute(new AddCommand(box, 1));
            h.Undo();
            h.Redo();
            h.Execute(new AddCommand(box, 2));
            Assert.AreEqual(2, h.UndoCount);
        }

        [Test]
        public void AfterAnUndo_TheNextCommandDoesNotMergeIntoAnOlderStep()
        {
            var box = new Box();
            var h = new CommandHistory();
            h.Execute(new AddCommand(box, 1));
            h.BreakMerge();
            h.Execute(new AddCommand(box, 2));
            h.Undo();
            h.Execute(new AddCommand(box, 3));
            Assert.AreEqual(2, h.UndoCount);
            Assert.AreEqual(4, box.Value);
            h.Undo();
            Assert.AreEqual(1, box.Value);
        }

        [Test]
        public void CommandsThatDoNotMerge_StayASeparateStep()
        {
            var box = new Box();
            var log = new Log();
            var h = new CommandHistory();
            h.Execute(new AddCommand(box, 1));
            h.Execute(new LogCommand(log, "x"));
            Assert.AreEqual(2, h.UndoCount);
        }

        // ── groups ───────────────────────────────────────────────────────────

        [Test]
        public void Group_IsOneUndoStep_UndoneInReverse_RedoneInOrder()
        {
            var log = new Log();
            var h = new CommandHistory();
            using (h.BeginGroup("all"))
            {
                h.Execute(new LogCommand(log, "a"));
                h.Execute(new LogCommand(log, "b"));
                Assert.AreEqual(0, h.UndoCount); // not recorded until the group ends
            }
            Assert.AreEqual(1, h.UndoCount);
            Assert.AreEqual("all", h.NextUndoName);
            h.Undo();
            h.Redo();
            Assert.AreEqual("do:a,do:b,undo:b,undo:a,do:a,do:b", log.ToString());
        }

        [Test]
        public void EmptyGroup_RecordsNothing()
        {
            var h = new CommandHistory();
            using (h.BeginGroup("none")) { }
            Assert.AreEqual(0, h.UndoCount);
        }

        [Test]
        public void UnnamedGroupWithOneCommand_RecordsThatCommandDirectly()
        {
            var log = new Log();
            var h = new CommandHistory();
            using (h.BeginGroup()) { h.Execute(new LogCommand(log, "solo")); }
            Assert.AreEqual(1, h.UndoCount);
            Assert.AreEqual("solo", h.NextUndoName);
        }

        [Test]
        public void Group_RulesAreEnforced()
        {
            var log = new Log();
            var h = new CommandHistory();
            h.Execute(new LogCommand(log, "before"));
            var scope = h.BeginGroup("g");
            Assert.Throws<InvalidOperationException>(() => h.BeginGroup("nested"));
            Assert.Throws<InvalidOperationException>(() => h.Undo());
            Assert.Throws<InvalidOperationException>(() => h.Redo());
            Assert.Throws<InvalidOperationException>(() => h.Clear());
            Assert.IsFalse(h.CanUndo);
            scope.Dispose();
            Assert.IsFalse(h.IsGroupOpen);
            Assert.Throws<InvalidOperationException>(() => h.EndGroup());
        }

        [Test]
        public void Group_KeepsTheCommandsThatRanBeforeOneThatThrew()
        {
            var log = new Log();
            var h = new CommandHistory();
            using (h.BeginGroup("partial"))
            {
                h.Execute(new LogCommand(log, "a"));
                Assert.Throws<InvalidOperationException>(
                    () => h.Execute(new LogCommand(log, "bad") { ThrowOnExecute = true }));
                h.Execute(new LogCommand(log, "c"));
            }
            h.Undo();
            Assert.AreEqual("do:a,do:c,undo:c,undo:a", log.ToString());
        }

        [Test]
        public void StaleGroupScope_DoesNotCloseALaterGroup()
        {
            var h = new CommandHistory();
            var first = h.BeginGroup("first");
            h.EndGroup();
            h.BeginGroup("second");
            first.Dispose();
            Assert.IsTrue(h.IsGroupOpen);
            h.EndGroup();
        }

        // ── reentrancy ───────────────────────────────────────────────────────

        [Test]
        public void ACommandThatCallsBackIntoTheHistory_Throws()
        {
            var h = new CommandHistory();
            var evil = new DelegateCommand(() => h.Execute(new DelegateCommand(() => { })));
            Assert.Throws<InvalidOperationException>(() => h.Execute(evil));
            Assert.AreEqual(0, h.UndoCount);
            // The failure did not leave the history stuck.
            h.Execute(new DelegateCommand(() => { }));
            Assert.AreEqual(1, h.UndoCount);
        }

        [Test]
        public void NullCommand_Throws()
        {
            var h = new CommandHistory();
            Assert.Throws<ArgumentNullException>(() => h.Execute(null));
        }

        // ── CompositeCommand ─────────────────────────────────────────────────

        [Test]
        public void Composite_ExecutesInOrderAndUndoesInReverse()
        {
            var log = new Log();
            var c = new CompositeCommand("c", new LogCommand(log, "a"), new LogCommand(log, "b"), new LogCommand(log, "c"));
            c.Execute();
            c.Undo();
            Assert.AreEqual("do:a,do:b,do:c,undo:c,undo:b,undo:a", log.ToString());
            Assert.AreEqual(3, c.Count);
        }

        [Test]
        public void Composite_WhenAChildThrows_RollsBackTheEarlierOnesAndRethrows()
        {
            var log = new Log();
            var c = new CompositeCommand("c",
                new LogCommand(log, "a"),
                new LogCommand(log, "b"),
                new LogCommand(log, "bad") { ThrowOnExecute = true },
                new LogCommand(log, "never"));
            var ex = Assert.Throws<InvalidOperationException>(() => c.Execute());
            Assert.AreEqual("exec bad", ex.Message);
            Assert.AreEqual("do:a,do:b,undo:b,undo:a", log.ToString());
        }

        [Test]
        public void Composite_WhenRollbackAlsoFails_ReportsBothFailures()
        {
            var log = new Log();
            var c = new CompositeCommand("c",
                new LogCommand(log, "a") { ThrowOnUndo = true },
                new LogCommand(log, "bad") { ThrowOnExecute = true });
            var ex = Assert.Throws<AggregateException>(() => c.Execute());
            Assert.AreEqual(2, ex.InnerExceptions.Count);
            Assert.AreEqual("exec bad", ex.InnerExceptions[0].Message);
            Assert.AreEqual("undo a", ex.InnerExceptions[1].Message);
        }

        [Test]
        public void Composite_Undo_ContinuesPastAFailingChildThenReports()
        {
            var log = new Log();
            var c = new CompositeCommand("c",
                new LogCommand(log, "a"),
                new LogCommand(log, "b") { ThrowOnUndo = true },
                new LogCommand(log, "c"));
            c.Execute();
            var ex = Assert.Throws<AggregateException>(() => c.Undo());
            Assert.AreEqual(1, ex.InnerExceptions.Count);
            Assert.AreEqual("do:a,do:b,do:c,undo:c,undo:a", log.ToString());
        }

        [Test]
        public void Composite_NullChild_Throws()
        {
            var log = new Log();
            Assert.Throws<ArgumentNullException>(() => new CompositeCommand("c", new LogCommand(log, "a"), null));
        }

        // ── CommandQueue ─────────────────────────────────────────────────────

        [Test]
        public void Queue_RunsFirstInFirstOutAndRespectsTheLimit()
        {
            var log = new Log();
            var q = new CommandQueue();
            q.Enqueue(() => log.Lines.Add("1"));
            q.Enqueue(() => log.Lines.Add("2"));
            q.Enqueue(() => log.Lines.Add("3"));
            Assert.AreEqual(2, q.Process(2));
            Assert.AreEqual("1,2", log.ToString());
            Assert.AreEqual(1, q.Count);
            Assert.AreEqual(1, q.Process());
            Assert.IsTrue(q.IsEmpty);
            Assert.AreEqual(0, q.Process());
        }

        [Test]
        public void Queue_CommandsQueuedWhileProcessingWaitForTheNextCall()
        {
            var log = new Log();
            var q = new CommandQueue();
            q.Enqueue(() => { log.Lines.Add("first"); q.Enqueue(() => log.Lines.Add("later")); });
            Assert.AreEqual(1, q.Process());
            Assert.AreEqual("first", log.ToString());
            Assert.AreEqual(1, q.Process());
            Assert.AreEqual("first,later", log.ToString());
        }

        [Test]
        public void Queue_WithNoFailureSubscriber_RethrowsAndDropsTheFailedCommand()
        {
            var log = new Log();
            var q = new CommandQueue();
            q.Enqueue(() => throw new InvalidOperationException("boom"));
            q.Enqueue(() => log.Lines.Add("after"));
            Assert.Throws<InvalidOperationException>(() => q.Process());
            Assert.AreEqual(1, q.Count);
            Assert.AreEqual(1, q.Process());
            Assert.AreEqual("after", log.ToString());
        }

        [Test]
        public void Queue_WithAFailureSubscriber_ReportsAndKeepsGoing()
        {
            var log = new Log();
            var q = new CommandQueue();
            var failures = new List<string>();
            q.CommandFailed += (cmd, e) => failures.Add(e.Message);
            q.Enqueue(() => throw new InvalidOperationException("boom"));
            q.Enqueue(() => log.Lines.Add("after"));
            Assert.AreEqual(2, q.Process());
            Assert.AreEqual("boom", string.Join(",", failures));
            Assert.AreEqual("after", log.ToString());
        }

        [Test]
        public void Queue_ProcessFromInsideACommand_Throws()
        {
            var q = new CommandQueue();
            var failures = new List<Exception>();
            q.CommandFailed += (cmd, e) => failures.Add(e);
            q.Enqueue(() => q.Process());
            q.Process();
            Assert.AreEqual(1, failures.Count);
            Assert.IsTrue(failures[0] is InvalidOperationException);
        }

        [Test]
        public void Queue_ClearDropsPendingCommands()
        {
            var log = new Log();
            var q = new CommandQueue();
            q.Enqueue(() => log.Lines.Add("x"));
            q.Clear();
            Assert.AreEqual(0, q.Process());
            Assert.AreEqual("", log.ToString());
        }

        [Test]
        public void Queue_NullAndNegativeArguments_Throw()
        {
            var q = new CommandQueue();
            Assert.Throws<ArgumentNullException>(() => q.Enqueue((ICommand)null));
            Assert.Throws<ArgumentOutOfRangeException>(() => q.Process(-1));
        }

        // ── delegate commands ────────────────────────────────────────────────

        [Test]
        public void DelegateCommands_RunTheirDelegatesAndRejectNull()
        {
            int x = 0;
            var c = new UndoableActionCommand(() => x += 5, () => x -= 5, "add");
            c.Execute();
            Assert.AreEqual(5, x);
            c.Undo();
            Assert.AreEqual(0, x);
            Assert.AreEqual("add", c.Name);
            Assert.Throws<ArgumentNullException>(() => new ActionCommand(null));
            Assert.Throws<ArgumentNullException>(() => new UndoableActionCommand(() => { }, null));
        }
    }
}
