// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "BytecodeProgram.cs"
// ============================================================================
using System;
using System.Text;

namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>
    /// An immutable program: instructions, a table of float constants, and the number of local slots it uses.
    /// Build one with <see cref="ProgramBuilder"/> or load the arrays from data.
    /// </summary>
    public sealed class BytecodeProgram
    {
        private readonly int[] _code;
        private readonly float[] _constants;

        /// <summary>Copies the arrays, so later changes to them do not affect the program.</summary>
        public BytecodeProgram(int[] code, float[] constants, int localCount)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            if (constants == null) throw new ArgumentNullException(nameof(constants));
            if (localCount < 0) throw new ArgumentOutOfRangeException(nameof(localCount));
            _code = (int[])code.Clone();
            _constants = (float[])constants.Clone();
            LocalCount = localCount;
        }

        /// <summary>Number of ints in the code (instructions and operands).</summary>
        public int Length => _code.Length;

        public int ConstantCount => _constants.Length;

        public int LocalCount { get; private set; }

        internal int[] Code => _code;

        internal float[] Constants => _constants;

        public int CodeAt(int address) { return _code[address]; }

        public float ConstantAt(int index) { return _constants[index]; }

        internal static bool HasOperand(Opcode op)
        {
            switch (op)
            {
                case Opcode.PushConst:
                case Opcode.Load:
                case Opcode.Store:
                case Opcode.Jump:
                case Opcode.JumpIfFalse:
                case Opcode.JumpIfTrue:
                case Opcode.Call:
                case Opcode.CallHost:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>One line per instruction: address, name and operand. Meant for debugging and tests.</summary>
        public string Disassemble()
        {
            var text = new StringBuilder();
            int pc = 0;
            while (pc < _code.Length)
            {
                if (text.Length > 0) text.Append('\n');
                int address = pc;
                int value = _code[pc++];
                text.Append(address.ToString("0000")).Append("  ");

                if (value < 0 || value > (int)Opcode.CallHost)
                {
                    text.Append("??? ").Append(value);
                    continue;
                }

                Opcode op = (Opcode)value;
                text.Append(op);
                if (!HasOperand(op)) continue;

                if (pc >= _code.Length) { text.Append(" <missing operand>"); continue; }
                int operand = _code[pc++];
                text.Append(' ').Append(operand);
                if (op == Opcode.PushConst && operand >= 0 && operand < _constants.Length)
                    text.Append(" (").Append(_constants[operand].ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(')');
            }
            return text.ToString();
        }
    }
}
