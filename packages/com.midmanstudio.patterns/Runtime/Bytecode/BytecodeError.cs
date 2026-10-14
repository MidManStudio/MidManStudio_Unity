// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "BytecodeError.cs"
// ============================================================================
namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>What went wrong when a program could not run.</summary>
    public enum BytecodeError
    {
        StackOverflow,
        StackUnderflow,
        CallDepthExceeded,
        BudgetExceeded,
        InvalidOpcode,
        TruncatedInstruction,
        InvalidJump,
        InvalidConstant,
        InvalidLocal,
        DivideByZero,
        UnknownHostFunction
    }
}
