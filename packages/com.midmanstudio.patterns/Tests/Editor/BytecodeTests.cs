// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "BytecodeTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.Bytecode;

namespace MidManStudio.Patterns.Bytecode.Tests
{
    public class BytecodeTests
    {
        private static float Run(ProgramBuilder b, params float[] args)
        {
            return new VirtualMachine().Run(b.Build(), args.Length == 0 ? null : args);
        }

        private static BytecodeException Fails(Action action)
        {
            return Assert.Throws<BytecodeException>(() => action());
        }

        // ── Arithmetic, logic and stack ─────────────────────────────────────

        [Test]
        public void ArithmeticFollowsTheOrderTheProgramGives()
        {
            var b = new ProgramBuilder().Push(2).Push(3).Push(4).Emit(Opcode.Mul).Emit(Opcode.Add).Emit(Opcode.Halt);
            Assert.AreEqual(14f, Run(b), 0.0001f);
        }

        [Test]
        public void SubtractionAndDivisionUseTheFirstPushedValueAsTheLeftSide()
        {
            Assert.AreEqual(6f, Run(new ProgramBuilder().Push(10).Push(4).Emit(Opcode.Sub)), 0.0001f);
            Assert.AreEqual(4f, Run(new ProgramBuilder().Push(20).Push(5).Emit(Opcode.Div)), 0.0001f);
        }

        [Test]
        public void ModuloAndNegation()
        {
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(10).Push(3).Emit(Opcode.Mod)), 0.0001f);
            Assert.AreEqual(-5f, Run(new ProgramBuilder().Push(5).Emit(Opcode.Neg)), 0.0001f);
        }

        [Test]
        public void ComparisonsPushOneOrZero()
        {
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(1).Push(2).Emit(Opcode.Lt)), 0f);
            Assert.AreEqual(0f, Run(new ProgramBuilder().Push(2).Push(2).Emit(Opcode.Lt)), 0f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(2).Push(2).Emit(Opcode.Le)), 0f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(3).Push(2).Emit(Opcode.Gt)), 0f);
            Assert.AreEqual(0f, Run(new ProgramBuilder().Push(2).Push(3).Emit(Opcode.Ge)), 0f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(2).Push(2).Emit(Opcode.Eq)), 0f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(2).Push(3).Emit(Opcode.Ne)), 0f);
        }

        [Test]
        public void NotAndOrTreatAnyNonZeroValueAsTrue()
        {
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(0).Emit(Opcode.Not)), 0f);
            Assert.AreEqual(0f, Run(new ProgramBuilder().Push(7).Emit(Opcode.Not)), 0f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(5).Push(-2).Emit(Opcode.And)), 0f);
            Assert.AreEqual(0f, Run(new ProgramBuilder().Push(5).Push(0).Emit(Opcode.And)), 0f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(0).Push(3).Emit(Opcode.Or)), 0f);
            Assert.AreEqual(0f, Run(new ProgramBuilder().Push(0).Push(0).Emit(Opcode.Or)), 0f);
        }

        [Test]
        public void DupSwapAndPopRearrangeTheStack()
        {
            Assert.AreEqual(25f, Run(new ProgramBuilder().Push(5).Emit(Opcode.Dup).Emit(Opcode.Mul)), 0.0001f);
            Assert.AreEqual(-7f, Run(new ProgramBuilder().Push(10).Push(3).Emit(Opcode.Swap).Emit(Opcode.Sub)), 0.0001f);
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(1).Push(2).Emit(Opcode.Pop)), 0f);
        }

        // ── Locals and control flow ─────────────────────────────────────────

        [Test]
        public void ArgumentsFillTheFirstLocals()
        {
            var b = new ProgramBuilder().Load(0).Load(1).Emit(Opcode.Mul);
            Assert.AreEqual(42f, Run(b, 6f, 7f), 0.0001f);
        }

        [Test]
        public void LocalsKeepTheirValuesAfterTheRunSoTheHostCanReadResults()
        {
            var vm = new VirtualMachine();
            vm.Run(new ProgramBuilder().Push(9).Store(2).Build());
            Assert.AreEqual(9f, vm.GetLocal(2), 0f);
            Assert.AreEqual(0f, vm.GetLocal(0), 0f);
        }

        [Test]
        public void ALoopCanSumOneToTen()
        {
            var b = new ProgramBuilder();
            var loop = b.NewLabel();
            var end = b.NewLabel();
            b.Push(1).Store(0).Push(0).Store(1);
            b.Mark(loop);
            b.Load(0).Push(10).Emit(Opcode.Gt).JumpIfTrue(end);
            b.Load(1).Load(0).Emit(Opcode.Add).Store(1);
            b.Load(0).Push(1).Emit(Opcode.Add).Store(0);
            b.Jump(loop);
            b.Mark(end);
            b.Load(1).Emit(Opcode.Halt);
            Assert.AreEqual(55f, Run(b), 0.0001f);
        }

        [Test]
        public void JumpIfFalseBranchesOnZero()
        {
            var b = new ProgramBuilder();
            var otherwise = b.NewLabel();
            var end = b.NewLabel();
            b.Load(0).Push(5).Emit(Opcode.Gt).JumpIfFalse(otherwise);
            b.Push(100).Jump(end);
            b.Mark(otherwise);
            b.Push(200);
            b.Mark(end);
            var program = b.Build();
            var vm = new VirtualMachine();
            Assert.AreEqual(100f, vm.Run(program, new[] { 9f }), 0f);
            Assert.AreEqual(200f, vm.Run(program, new[] { 2f }), 0f);
        }

        [Test]
        public void CallAndReturnRunASubroutine()
        {
            var b = new ProgramBuilder();
            var square = b.NewLabel();
            b.Push(7).Call(square).Emit(Opcode.Halt);
            b.Mark(square);
            b.Emit(Opcode.Dup).Emit(Opcode.Mul).Emit(Opcode.Return);
            Assert.AreEqual(49f, Run(b), 0.0001f);
        }

        [Test]
        public void ReturnAtTheTopLevelStopsTheProgram()
        {
            Assert.AreEqual(3f, Run(new ProgramBuilder().Push(3).Emit(Opcode.Return).Push(9)), 0f);
        }

        [Test]
        public void HaltStopsEarlyAndFallingOffTheEndReturnsTheTopOfTheStack()
        {
            Assert.AreEqual(1f, Run(new ProgramBuilder().Push(1).Emit(Opcode.Halt).Push(2)), 0f);
            Assert.AreEqual(2f, Run(new ProgramBuilder().Push(1).Push(2)), 0f);
            Assert.AreEqual(0f, Run(new ProgramBuilder()), 0f);
        }

        [Test]
        public void JumpingToTheEndOfTheCodeEndsTheProgram()
        {
            var b = new ProgramBuilder();
            var end = b.NewLabel();
            b.Push(4).Jump(end).Push(8);
            b.Mark(end);
            Assert.AreEqual(4f, Run(b), 0f);
        }

        // ── Host functions ──────────────────────────────────────────────────

        [Test]
        public void AHostFunctionTakesArgumentsFromTheStackAndPushesAResult()
        {
            var vm = new VirtualMachine();
            vm.RegisterHost(1, m => { float x = m.Pop(); m.Push(x + 3f); });
            Assert.AreEqual(7f, vm.Run(new ProgramBuilder().Push(4).CallHost(1).Build()), 0f);
        }

        [Test]
        public void HostArgumentsArePoppedLastFirst()
        {
            var vm = new VirtualMachine();
            vm.RegisterHost(1, m => { float b = m.Pop(); float a = m.Pop(); m.Push(a - b); });
            Assert.AreEqual(8f, vm.Run(new ProgramBuilder().Push(10).Push(2).CallHost(1).Build()), 0f);
        }

        [Test]
        public void AHostFunctionCanChangeTheGame()
        {
            float health = 100f;
            var vm = new VirtualMachine();
            vm.RegisterHost(7, m => { health = m.Pop(); });
            vm.Run(new ProgramBuilder().Push(25).CallHost(7).Build());
            Assert.AreEqual(25f, health, 0f);
        }

        [Test]
        public void AnUnknownHostFunctionIsAnError()
        {
            var ex = Fails(() => new VirtualMachine().Run(new ProgramBuilder().CallHost(5).Build()));
            Assert.AreEqual(BytecodeError.UnknownHostFunction, ex.Error);
            Assert.AreEqual(0, ex.Address);
        }

        [Test]
        public void AHostFunctionCannotStartAnotherRun()
        {
            var vm = new VirtualMachine();
            var inner = new ProgramBuilder().Push(1).Build();
            vm.RegisterHost(1, m => m.Run(inner));
            Assert.Throws<InvalidOperationException>(() => vm.Run(new ProgramBuilder().CallHost(1).Build()));
            Assert.IsFalse(vm.IsRunning);
            Assert.AreEqual(1f, vm.Run(inner), 0f);
        }

        [Test]
        public void AnExceptionFromAHostFunctionPropagatesAndTheMachineStaysUsable()
        {
            var vm = new VirtualMachine();
            vm.RegisterHost(1, m => { throw new InvalidOperationException("host failed"); });
            Assert.Throws<InvalidOperationException>(() => vm.Run(new ProgramBuilder().CallHost(1).Build()));
            Assert.IsFalse(vm.IsRunning);
            Assert.AreEqual(2f, vm.Run(new ProgramBuilder().Push(2).Build()), 0f);
        }

        [Test]
        public void HostFunctionsCanBeRegisteredOnceAndRemoved()
        {
            var vm = new VirtualMachine();
            vm.RegisterHost(1, m => { });
            Assert.Throws<ArgumentException>(() => vm.RegisterHost(1, m => { }));
            Assert.Throws<ArgumentNullException>(() => vm.RegisterHost(2, null));
            Assert.IsTrue(vm.UnregisterHost(1));
            Assert.IsFalse(vm.UnregisterHost(1));
        }

        // ── Safety limits and errors ────────────────────────────────────────

        [Test]
        public void PoppingFromAnEmptyStackIsAnError()
        {
            var ex = Fails(() => Run(new ProgramBuilder().Emit(Opcode.Add)));
            Assert.AreEqual(BytecodeError.StackUnderflow, ex.Error);
            Assert.AreEqual(0, ex.Address);
        }

        [Test]
        public void AFullStackIsAnError()
        {
            var vm = new VirtualMachine(maxStack: 4);
            var b = new ProgramBuilder().Push(1).Push(1).Push(1).Push(1).Push(1);
            Assert.AreEqual(BytecodeError.StackOverflow, Fails(() => vm.Run(b.Build())).Error);
        }

        [Test]
        public void AnEndlessLoopIsStoppedByTheInstructionBudgetAndTheMachineStaysUsable()
        {
            var b = new ProgramBuilder();
            var loop = b.NewLabel();
            b.Mark(loop);
            b.Jump(loop);
            var vm = new VirtualMachine(maxInstructions: 100);
            Assert.AreEqual(BytecodeError.BudgetExceeded, Fails(() => vm.Run(b.Build())).Error);
            Assert.AreEqual(100, vm.InstructionsExecuted);
            Assert.AreEqual(5f, vm.Run(new ProgramBuilder().Push(5).Build()), 0f);
        }

        [Test]
        public void RunawayRecursionIsStoppedByTheCallDepthLimit()
        {
            var b = new ProgramBuilder();
            var self = b.NewLabel();
            b.Mark(self);
            b.Call(self);
            var vm = new VirtualMachine(maxCallDepth: 8);
            Assert.AreEqual(BytecodeError.CallDepthExceeded, Fails(() => vm.Run(b.Build())).Error);
        }

        [Test]
        public void DividingByZeroIsAnErrorForDivAndMod()
        {
            Assert.AreEqual(BytecodeError.DivideByZero, Fails(() => Run(new ProgramBuilder().Push(1).Push(0).Emit(Opcode.Div))).Error);
            Assert.AreEqual(BytecodeError.DivideByZero, Fails(() => Run(new ProgramBuilder().Push(1).Push(0).Emit(Opcode.Mod))).Error);
        }

        [Test]
        public void MalformedCodeIsReportedNotExecuted()
        {
            var vm = new VirtualMachine();
            Assert.AreEqual(BytecodeError.InvalidOpcode, Fails(() => vm.Run(new BytecodeProgram(new[] { 99 }, new float[0], 0))).Error);
            Assert.AreEqual(BytecodeError.InvalidOpcode, Fails(() => vm.Run(new BytecodeProgram(new[] { -1 }, new float[0], 0))).Error);
            Assert.AreEqual(BytecodeError.TruncatedInstruction, Fails(() => vm.Run(new BytecodeProgram(new[] { (int)Opcode.PushConst }, new float[0], 0))).Error);
            Assert.AreEqual(BytecodeError.InvalidJump, Fails(() => vm.Run(new BytecodeProgram(new[] { (int)Opcode.Jump, 999 }, new float[0], 0))).Error);
            Assert.AreEqual(BytecodeError.InvalidJump, Fails(() => vm.Run(new BytecodeProgram(new[] { (int)Opcode.Jump, -1 }, new float[0], 0))).Error);
            Assert.AreEqual(BytecodeError.InvalidConstant, Fails(() => vm.Run(new BytecodeProgram(new[] { (int)Opcode.PushConst, 3 }, new float[1], 0))).Error);
            Assert.AreEqual(BytecodeError.InvalidLocal, Fails(() => vm.Run(new BytecodeProgram(new[] { (int)Opcode.Load, 5 }, new float[0], 1))).Error);
            Assert.AreEqual(BytecodeError.InvalidLocal, Fails(() => vm.Run(new BytecodeProgram(new[] { (int)Opcode.Store, -1 }, new float[0], 1))).Error);
        }

        [Test]
        public void TheErrorCarriesTheAddressAndMentionsItInTheMessage()
        {
            var ex = Fails(() => Run(new ProgramBuilder().Push(1).Push(0).Emit(Opcode.Div)));
            Assert.AreEqual(4, ex.Address);
            Assert.IsTrue(ex.Message.Contains("address 4"));
        }

        [Test]
        public void EachRunStartsWithAnEmptyStackAndZeroedLocals()
        {
            var vm = new VirtualMachine();
            vm.Run(new ProgramBuilder().Push(1).Push(2).Push(3).Store(0).Build());
            Assert.AreEqual(2, vm.StackDepth);
            Assert.AreEqual(0f, vm.Run(new ProgramBuilder().Load(0).Build()), 0f);
            Assert.AreEqual(0, vm.StackDepth - 1);
        }

        [Test]
        public void MoreArgumentsThanLocalsIsRejected()
        {
            var vm = new VirtualMachine();
            Assert.Throws<ArgumentException>(() => vm.Run(new ProgramBuilder().Load(0).Build(), new[] { 1f, 2f }));
            Assert.Throws<ArgumentNullException>(() => vm.Run(null));
        }

        [Test]
        public void InstructionsExecutedCountsEveryInstruction()
        {
            var vm = new VirtualMachine();
            vm.Run(new ProgramBuilder().Push(1).Push(2).Emit(Opcode.Add).Build());
            Assert.AreEqual(3, vm.InstructionsExecuted);
        }

        [Test]
        public void MachineSettingsAreValidated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualMachine(4, 0));
            var vm = new VirtualMachine();
            Assert.Throws<ArgumentOutOfRangeException>(() => { vm.MaxInstructions = 0; });
        }

        // ── Builder and program ─────────────────────────────────────────────

        [Test]
        public void ALabelThatWasNeverMarkedFailsAtBuildTime()
        {
            var b = new ProgramBuilder();
            b.Jump(b.NewLabel());
            Assert.Throws<InvalidOperationException>(() => b.Build());
        }

        [Test]
        public void LabelsCannotBeMarkedTwiceOrSharedBetweenBuilders()
        {
            var a = new ProgramBuilder();
            var label = a.NewLabel();
            a.Mark(label);
            Assert.Throws<InvalidOperationException>(() => a.Mark(label));
            var other = new ProgramBuilder();
            Assert.Throws<InvalidOperationException>(() => other.Jump(label));
            Assert.Throws<InvalidOperationException>(() => other.Mark(label));
            Assert.Throws<ArgumentNullException>(() => other.Jump(null));
        }

        [Test]
        public void EmitRejectsInstructionsThatNeedAnOperand()
        {
            var b = new ProgramBuilder();
            Assert.Throws<ArgumentException>(() => b.Emit(Opcode.PushConst));
            Assert.Throws<ArgumentException>(() => b.Emit(Opcode.Jump));
            Assert.Throws<ArgumentException>(() => b.Emit(Opcode.CallHost));
        }

        [Test]
        public void EqualConstantsShareOneEntryButZeroAndNegativeZeroStaySeparate()
        {
            var shared = new ProgramBuilder().Push(1.5f).Push(1.5f).Push(2f).Build();
            Assert.AreEqual(2, shared.ConstantCount);
            var zeros = new ProgramBuilder().Push(0f).Push(-0f).Build();
            Assert.AreEqual(2, zeros.ConstantCount);
        }

        [Test]
        public void TheLocalCountIsInferredOrCheckedAgainstTheSlotsUsed()
        {
            Assert.AreEqual(4, new ProgramBuilder().Store(3).Build().LocalCount);
            Assert.AreEqual(0, new ProgramBuilder().Push(1).Build().LocalCount);
            Assert.AreEqual(8, new ProgramBuilder().Store(3).Build(8).LocalCount);
            Assert.Throws<ArgumentException>(() => new ProgramBuilder().Store(3).Build(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProgramBuilder().Load(-1));
        }

        [Test]
        public void AProgramCopiesTheArraysItWasGiven()
        {
            var code = new[] { (int)Opcode.PushConst, 0 };
            var constants = new[] { 5f };
            var program = new BytecodeProgram(code, constants, 0);
            code[1] = 7;
            constants[0] = 9f;
            Assert.AreEqual(5f, new VirtualMachine().Run(program), 0f);
            Assert.AreEqual(5f, program.ConstantAt(0), 0f);
            Assert.AreEqual((int)Opcode.PushConst, program.CodeAt(0));
        }

        [Test]
        public void DisassembleListsEveryInstructionWithItsOperand()
        {
            var b = new ProgramBuilder();
            var end = b.NewLabel();
            b.Push(1.5f).Store(0).Load(0).Jump(end).Emit(Opcode.Pop);
            b.Mark(end);
            b.Emit(Opcode.Halt);
            string expected = "0000  PushConst 0 (1.5)\n0002  Store 0\n0004  Load 0\n0006  Jump 9\n0008  Pop\n0009  Halt";
            Assert.AreEqual(expected, b.Build().Disassemble());
        }

        [Test]
        public void DisassembleMarksBadCodeInsteadOfThrowing()
        {
            var program = new BytecodeProgram(new[] { 99, (int)Opcode.Jump }, new float[0], 0);
            Assert.AreEqual("0000  ??? 99\n0001  Jump <missing operand>", program.Disassemble());
        }

        [Test]
        public void ConstructorArgumentsAreValidated()
        {
            Assert.Throws<ArgumentNullException>(() => new BytecodeProgram(null, new float[0], 0));
            Assert.Throws<ArgumentNullException>(() => new BytecodeProgram(new int[0], null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BytecodeProgram(new int[0], new float[0], -1));
        }
    }
}
