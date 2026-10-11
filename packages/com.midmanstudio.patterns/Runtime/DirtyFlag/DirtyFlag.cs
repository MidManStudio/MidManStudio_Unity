// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dirty-flag.md, section "DirtyFlag.cs"
// ============================================================================
namespace MidManStudio.Patterns.DirtyFlag
{
    /// <summary>
    /// A flag that says "something changed, recompute". <see cref="Mark"/> sets it and raises
    /// <see cref="Version"/>; <see cref="Consume"/> reads and clears it. Use it directly for one consumer, or as
    /// a dependency of <see cref="CachedValue{T}"/> for several.
    /// </summary>
    public sealed class DirtyFlag : IVersioned
    {
        private bool _dirty;
        private int _version;

        /// <param name="startDirty">True when the first consumer should recompute.</param>
        public DirtyFlag(bool startDirty = true) { _dirty = startDirty; }

        public bool IsDirty => _dirty;

        /// <summary>Raised by every <see cref="Mark"/>. Wraps around after int.MaxValue.</summary>
        public int Version => _version;

        public void Mark()
        {
            _dirty = true;
            unchecked { _version++; }
        }

        /// <summary>Returns whether the flag was set and clears it.</summary>
        public bool Consume()
        {
            bool was = _dirty;
            _dirty = false;
            return was;
        }
    }
}
