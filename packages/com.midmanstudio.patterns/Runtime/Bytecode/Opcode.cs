// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "Opcode.cs"
// ============================================================================
namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>
    /// Instructions of the virtual machine. Values are numbers on a stack of floats; 0 is false and any other
    /// value is true. Instructions marked "operand" are followed by one int in the code.
    /// </summary>
    public enum Opcode
    {
        /// <summary>Stop the program. The result is the value on top of the stack, or 0 when the stack is empty.</summary>
        Halt = 0,

        /// <summary>Operand: constant index. Pushes that constant.</summary>
        PushConst = 1,

        Pop = 2,
        Dup = 3,
        Swap = 4,

        /// <summary>Pops b, then a, and pushes a + b.</summary>
        Add = 5,
        Sub = 6,
        Mul = 7,

        /// <summary>Pops b, then a, and pushes a / b. A zero divisor is an error.</summary>
        Div = 8,
        Mod = 9,
        Neg = 10,

        /// <summary>Comparisons pop b, then a, and push 1 or 0.</summary>
        Eq = 11,
        Ne = 12,
        Lt = 13,
        Le = 14,
        Gt = 15,
        Ge = 16,

        Not = 17,
        And = 18,
        Or = 19,

        /// <summary>Operand: local slot. Pushes the local.</summary>
        Load = 20,

        /// <summary>Operand: local slot. Pops a value into the local.</summary>
        Store = 21,

        /// <summary>Operand: address. Continues at that address.</summary>
        Jump = 22,

        /// <summary>Operand: address. Pops a value and jumps when it is 0.</summary>
        JumpIfFalse = 23,

        /// <summary>Operand: address. Pops a value and jumps when it is not 0.</summary>
        JumpIfTrue = 24,

        /// <summary>Operand: address. Remembers where to come back to and continues at the address.</summary>
        Call = 25,

        /// <summary>Goes back to after the matching Call. At the top level it stops the program like Halt.</summary>
        Return = 26,

        /// <summary>Operand: host function id. Runs the registered host function, which reads and writes the stack.</summary>
        CallHost = 27
    }
}
