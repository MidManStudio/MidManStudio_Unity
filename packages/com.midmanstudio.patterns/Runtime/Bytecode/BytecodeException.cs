// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "BytecodeException.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>A program broke a rule of the virtual machine. <see cref="Address"/> is where it happened, or -1 outside a program.</summary>
    public sealed class BytecodeException : Exception
    {
        public BytecodeError Error { get; private set; }

        public int Address { get; private set; }

        public BytecodeException(BytecodeError error, int address, string message)
            : base(message + (address >= 0 ? " (at address " + address + ")" : string.Empty))
        {
            Error = error;
            Address = address;
        }
    }
}
