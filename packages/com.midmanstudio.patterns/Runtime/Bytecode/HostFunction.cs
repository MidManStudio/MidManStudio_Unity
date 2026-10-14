// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "HostFunction.cs"
// ============================================================================
namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>
    /// A function of the game that a program can call with <see cref="Opcode.CallHost"/>. It takes its
    /// arguments with <see cref="VirtualMachine.Pop"/> (last argument first) and returns a value, if any, with
    /// <see cref="VirtualMachine.Push"/>.
    /// </summary>
    public delegate void HostFunction(VirtualMachine vm);
}
