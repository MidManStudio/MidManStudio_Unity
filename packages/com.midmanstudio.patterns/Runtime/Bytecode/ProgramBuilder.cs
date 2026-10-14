// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "ProgramBuilder.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>
    /// Assembles a <see cref="BytecodeProgram"/>. Methods return the builder so calls can be chained.
    /// Jumps and calls name a <see cref="ProgramLabel"/>, which may be placed before or after them; the
    /// addresses are filled in by <see cref="Build"/>.
    /// </summary>
    public sealed class ProgramBuilder
    {
        private struct Fixup
        {
            public int Position;
            public ProgramLabel Label;
        }

        private readonly List<int> _code = new List<int>();
        private readonly List<float> _constants = new List<float>();
        private readonly Dictionary<int, int> _constantIndex = new Dictionary<int, int>();
        private readonly List<Fixup> _fixups = new List<Fixup>();
        private int _highestSlot = -1;

        /// <summary>Address the next instruction will get.</summary>
        public int Position => _code.Count;

        /// <summary>Pushes a constant. Equal constants share one table entry.</summary>
        public ProgramBuilder Push(float value)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            int index;
            if (!_constantIndex.TryGetValue(bits, out index))
            {
                index = _constants.Count;
                _constants.Add(value);
                _constantIndex.Add(bits, index);
            }
            _code.Add((int)Opcode.PushConst);
            _code.Add(index);
            return this;
        }

        /// <summary>Adds an instruction that has no operand.</summary>
        /// <exception cref="ArgumentException">The instruction needs an operand; use the method for it.</exception>
        public ProgramBuilder Emit(Opcode op)
        {
            if (BytecodeProgram.HasOperand(op))
                throw new ArgumentException(op + " takes an operand. Use Push, Load, Store, CallHost or a jump method.", nameof(op));
            _code.Add((int)op);
            return this;
        }

        public ProgramBuilder Load(int slot) { return Slot(Opcode.Load, slot); }

        public ProgramBuilder Store(int slot) { return Slot(Opcode.Store, slot); }

        public ProgramBuilder CallHost(int functionId)
        {
            _code.Add((int)Opcode.CallHost);
            _code.Add(functionId);
            return this;
        }

        public ProgramLabel NewLabel() { return new ProgramLabel(this); }

        /// <summary>Places the label at the current position.</summary>
        /// <exception cref="InvalidOperationException">The label is already placed or belongs to another builder.</exception>
        public ProgramBuilder Mark(ProgramLabel label)
        {
            CheckOwner(label);
            if (label.IsMarked) throw new InvalidOperationException("The label is already marked.");
            label.Address = _code.Count;
            return this;
        }

        public ProgramBuilder Jump(ProgramLabel label) { return Branch(Opcode.Jump, label); }

        public ProgramBuilder JumpIfFalse(ProgramLabel label) { return Branch(Opcode.JumpIfFalse, label); }

        public ProgramBuilder JumpIfTrue(ProgramLabel label) { return Branch(Opcode.JumpIfTrue, label); }

        public ProgramBuilder Call(ProgramLabel label) { return Branch(Opcode.Call, label); }

        /// <summary>Resolves the labels and returns the program.</summary>
        /// <param name="localCount">Number of local slots. -1 uses one more than the highest slot the code touches.</param>
        /// <exception cref="InvalidOperationException">A jump or call refers to a label that was never marked.</exception>
        /// <exception cref="ArgumentException"><paramref name="localCount"/> is too small for the slots the code uses.</exception>
        public BytecodeProgram Build(int localCount = -1)
        {
            int needed = _highestSlot + 1;
            if (localCount < 0) localCount = needed;
            else if (localCount < needed)
                throw new ArgumentException("The code uses local slot " + _highestSlot + " but only " + localCount + " locals were requested.", nameof(localCount));

            int[] code = _code.ToArray();
            for (int i = 0; i < _fixups.Count; i++)
            {
                if (!_fixups[i].Label.IsMarked)
                    throw new InvalidOperationException("A jump or call refers to a label that was never marked.");
                code[_fixups[i].Position] = _fixups[i].Label.Address;
            }
            return new BytecodeProgram(code, _constants.ToArray(), localCount);
        }

        private ProgramBuilder Slot(Opcode op, int slot)
        {
            if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
            if (slot > _highestSlot) _highestSlot = slot;
            _code.Add((int)op);
            _code.Add(slot);
            return this;
        }

        private ProgramBuilder Branch(Opcode op, ProgramLabel label)
        {
            CheckOwner(label);
            _code.Add((int)op);
            _fixups.Add(new Fixup { Position = _code.Count, Label = label });
            _code.Add(0);
            return this;
        }

        private void CheckOwner(ProgramLabel label)
        {
            if (label == null) throw new ArgumentNullException(nameof(label));
            if (!ReferenceEquals(label.Owner, this)) throw new InvalidOperationException("The label belongs to another builder.");
        }
    }
}
