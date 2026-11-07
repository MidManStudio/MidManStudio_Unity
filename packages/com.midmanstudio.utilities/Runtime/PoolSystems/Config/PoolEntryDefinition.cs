// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "PoolEntryDefinition.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Pools.Generator
{
    /// <summary>
    /// One entry of a pool type provider: the enum member name, an optional comment written next to it, and an offset.
    /// An <c>explicitOffset</c> of -1 lets the generator choose the number. A value of 0 or more pins the entry to that
    /// offset inside the provider's block, so its number is known in advance. Use a pin for entries whose value you
    /// need to know or fix, for example one referenced from other packages.
    /// </summary>
    [System.Serializable]
    public class PoolEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the enum member name. Use PascalCase. No spaces.")]
        public string entryName;

        [Tooltip("Optional inline comment written next to the enum member.")]
        public string comment;

        [Tooltip("-1 = auto-assigned by generator.\n" +
                 ">=0 = pinned to this offset within the provider's block.\n" +
                 "Pin entries that are referenced in serialised inspector fields\n" +
                 "so their integer value never changes.")]
        public int explicitOffset = -1;

        /// <summary>Label in inspector lists: the entry name, or "Unnamed Entry" when blank.</summary>
        public string Name =>
            string.IsNullOrWhiteSpace(entryName) ? "Unnamed Entry" : entryName;
    }

}
