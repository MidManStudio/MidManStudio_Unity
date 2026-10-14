// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "VirtualMachine.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>
    /// Runs <see cref="BytecodeProgram"/>s on a stack of floats. Programs are data, so designers and mods can
    /// supply behaviour without recompiling, and the machine limits what a program can do: stack size, call
    /// depth, and the number of instructions per run all have caps, and a program can only reach the game
    /// through the host functions that were registered. One machine can run many programs, one at a time.
    /// </summary>
    public sealed class VirtualMachine
    {
        public const int DefaultMaxStack = 256;
        public const int DefaultMaxCallDepth = 64;
        public const int DefaultMaxInstructions = 100000;

        private readonly float[] _stack;
        private readonly int[] _calls;
        private readonly Dictionary<int, HostFunction> _host = new Dictionary<int, HostFunction>();
        private float[] _locals = new float[0];
        private int _sp;
        private int _address = -1;
        private bool _running;
        private int _maxInstructions;

        public VirtualMachine(int maxStack = DefaultMaxStack, int maxCallDepth = DefaultMaxCallDepth, int maxInstructions = DefaultMaxInstructions)
        {
            if (maxStack < 1) throw new ArgumentOutOfRangeException(nameof(maxStack));
            if (maxCallDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxCallDepth));
            _stack = new float[maxStack];
            _calls = new int[maxCallDepth];
            MaxInstructions = maxInstructions;
        }

        /// <summary>Cap on instructions in one <see cref="Run"/>. Stops endless loops. At least 1.</summary>
        public int MaxInstructions
        {
            get { return _maxInstructions; }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maxInstructions = value;
            }
        }

        public int StackDepth => _sp;

        /// <summary>Instructions executed by the last (or current) run.</summary>
        public int InstructionsExecuted { get; private set; }

        public bool IsRunning => _running;

        /// <exception cref="ArgumentException">A function is already registered under the id.</exception>
        public void RegisterHost(int id, HostFunction function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (_host.ContainsKey(id)) throw new ArgumentException("A host function is already registered under id " + id + ".", nameof(id));
            _host.Add(id, function);
        }

        public bool UnregisterHost(int id) { return _host.Remove(id); }

        /// <summary>Pushes a value. For host functions that return something.</summary>
        /// <exception cref="BytecodeException">The stack is full.</exception>
        public void Push(float value)
        {
            if (_sp >= _stack.Length) throw new BytecodeException(BytecodeError.StackOverflow, _address, "The stack is full");
            _stack[_sp++] = value;
        }

        /// <summary>Pops a value. For host functions that take arguments, last argument first.</summary>
        /// <exception cref="BytecodeException">The stack is empty.</exception>
        public float Pop()
        {
            if (_sp == 0) throw new BytecodeException(BytecodeError.StackUnderflow, _address, "The stack is empty");
            return _stack[--_sp];
        }

        /// <exception cref="BytecodeException">The stack is empty.</exception>
        public float Peek()
        {
            if (_sp == 0) throw new BytecodeException(BytecodeError.StackUnderflow, _address, "The stack is empty");
            return _stack[_sp - 1];
        }

        /// <summary>Reads a local. Locals keep their values after a run, so the host can read results.</summary>
        public float GetLocal(int slot)
        {
            CheckLocal(slot);
            return _locals[slot];
        }

        public void SetLocal(int slot, float value)
        {
            CheckLocal(slot);
            _locals[slot] = value;
        }

        /// <summary>
        /// Runs the program from address 0 until it halts or reaches the end. Locals start at 0, except the first
        /// ones, which take <paramref name="arguments"/>. Returns the value on top of the stack, or 0 when it is empty.
        /// </summary>
        /// <exception cref="BytecodeException">The program broke a rule of the machine.</exception>
        /// <exception cref="InvalidOperationException">A run is already in progress, for example a host function called <c>Run</c>.</exception>
        public float Run(BytecodeProgram program, float[] arguments = null)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));
            if (_running) throw new InvalidOperationException("The machine is already running a program.");
            if (arguments != null && arguments.Length > program.LocalCount)
                throw new ArgumentException("The program has " + program.LocalCount + " locals but " + arguments.Length + " arguments were given.", nameof(arguments));

            _running = true;
            try
            {
                _sp = 0;
                InstructionsExecuted = 0;
                _address = -1;
                _locals = new float[program.LocalCount];
                if (arguments != null) Array.Copy(arguments, _locals, arguments.Length);
                return Execute(program);
            }
            finally
            {
                _running = false;
                _address = -1;
            }
        }

        private float Execute(BytecodeProgram program)
        {
            int[] code = program.Code;
            float[] constants = program.Constants;
            int callDepth = 0;
            int pc = 0;

            while (pc < code.Length)
            {
                _address = pc;
                if (InstructionsExecuted >= _maxInstructions)
                    throw new BytecodeException(BytecodeError.BudgetExceeded, pc, "The program ran more than " + _maxInstructions + " instructions");
                InstructionsExecuted++;

                int value = code[pc++];
                if (value < 0 || value > (int)Opcode.CallHost)
                    throw new BytecodeException(BytecodeError.InvalidOpcode, _address, "Unknown instruction " + value);

                float a, b;
                int operand;
                switch ((Opcode)value)
                {
                    case Opcode.Halt:
                        return Result();

                    case Opcode.PushConst:
                        operand = Operand(code, ref pc);
                        if (operand < 0 || operand >= constants.Length)
                            throw new BytecodeException(BytecodeError.InvalidConstant, _address, "Constant " + operand + " does not exist");
                        Push(constants[operand]);
                        break;

                    case Opcode.Pop: Pop(); break;
                    case Opcode.Dup: Push(Peek()); break;
                    case Opcode.Swap: b = Pop(); a = Pop(); Push(b); Push(a); break;

                    case Opcode.Add: b = Pop(); a = Pop(); Push(a + b); break;
                    case Opcode.Sub: b = Pop(); a = Pop(); Push(a - b); break;
                    case Opcode.Mul: b = Pop(); a = Pop(); Push(a * b); break;
                    case Opcode.Div:
                        b = Pop(); a = Pop();
                        if (b == 0f) throw new BytecodeException(BytecodeError.DivideByZero, _address, "Division by zero");
                        Push(a / b);
                        break;
                    case Opcode.Mod:
                        b = Pop(); a = Pop();
                        if (b == 0f) throw new BytecodeException(BytecodeError.DivideByZero, _address, "Division by zero");
                        Push(a % b);
                        break;
                    case Opcode.Neg: Push(-Pop()); break;

                    case Opcode.Eq: b = Pop(); a = Pop(); Push(a == b ? 1f : 0f); break;
                    case Opcode.Ne: b = Pop(); a = Pop(); Push(a != b ? 1f : 0f); break;
                    case Opcode.Lt: b = Pop(); a = Pop(); Push(a < b ? 1f : 0f); break;
                    case Opcode.Le: b = Pop(); a = Pop(); Push(a <= b ? 1f : 0f); break;
                    case Opcode.Gt: b = Pop(); a = Pop(); Push(a > b ? 1f : 0f); break;
                    case Opcode.Ge: b = Pop(); a = Pop(); Push(a >= b ? 1f : 0f); break;

                    case Opcode.Not: Push(Pop() == 0f ? 1f : 0f); break;
                    case Opcode.And: b = Pop(); a = Pop(); Push(a != 0f && b != 0f ? 1f : 0f); break;
                    case Opcode.Or: b = Pop(); a = Pop(); Push(a != 0f || b != 0f ? 1f : 0f); break;

                    case Opcode.Load:
                        operand = Operand(code, ref pc);
                        Push(GetLocal(operand));
                        break;
                    case Opcode.Store:
                        operand = Operand(code, ref pc);
                        CheckLocal(operand);
                        _locals[operand] = Pop();
                        break;

                    case Opcode.Jump:
                        pc = Target(code, Operand(code, ref pc));
                        break;
                    case Opcode.JumpIfFalse:
                        operand = Target(code, Operand(code, ref pc));
                        if (Pop() == 0f) pc = operand;
                        break;
                    case Opcode.JumpIfTrue:
                        operand = Target(code, Operand(code, ref pc));
                        if (Pop() != 0f) pc = operand;
                        break;

                    case Opcode.Call:
                        operand = Target(code, Operand(code, ref pc));
                        if (callDepth >= _calls.Length)
                            throw new BytecodeException(BytecodeError.CallDepthExceeded, _address, "More than " + _calls.Length + " nested calls");
                        _calls[callDepth++] = pc;
                        pc = operand;
                        break;
                    case Opcode.Return:
                        if (callDepth == 0) return Result();
                        pc = _calls[--callDepth];
                        break;

                    case Opcode.CallHost:
                        operand = Operand(code, ref pc);
                        HostFunction function;
                        if (!_host.TryGetValue(operand, out function))
                            throw new BytecodeException(BytecodeError.UnknownHostFunction, _address, "No host function is registered under id " + operand);
                        function(this);
                        break;
                }
            }
            return Result();
        }

        private float Result() { return _sp > 0 ? _stack[_sp - 1] : 0f; }

        private void CheckLocal(int slot)
        {
            if (slot < 0 || slot >= _locals.Length)
                throw new BytecodeException(BytecodeError.InvalidLocal, _address, "Local slot " + slot + " does not exist");
        }

        private int Operand(int[] code, ref int pc)
        {
            if (pc >= code.Length)
                throw new BytecodeException(BytecodeError.TruncatedInstruction, _address, "The instruction is missing its operand");
            return code[pc++];
        }

        private int Target(int[] code, int address)
        {
            if (address < 0 || address > code.Length)
                throw new BytecodeException(BytecodeError.InvalidJump, _address, "Address " + address + " is outside the program");
            return address;
        }
    }
}
