// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/bytecode.md, section "ProgramLabel.cs"
// ============================================================================
namespace MidManStudio.Patterns.Bytecode
{
    /// <summary>A named position in a program that is being built. Create it with <see cref="ProgramBuilder.NewLabel"/>.</summary>
    public sealed class ProgramLabel
    {
        internal readonly ProgramBuilder Owner;
        internal int Address = -1;

        internal ProgramLabel(ProgramBuilder owner) { Owner = owner; }

        /// <summary>True once <see cref="ProgramBuilder.Mark"/> has placed it.</summary>
        public bool IsMarked => Address >= 0;
    }
}
