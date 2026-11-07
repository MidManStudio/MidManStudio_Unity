// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/libraries.md, section "LibraryTypeProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries.Generator
{
    /// <summary>
    /// One library in a <see cref="LibraryTypeProviderSO"/>: a name that becomes a <c>LibraryId</c> member and
    /// a list of item names that become <c>LibraryItemId</c> members named <c>LibraryName_ItemName</c>.
    /// </summary>
    [System.Serializable]
    public class LibraryEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the LibraryId enum member. PascalCase, no spaces.")]
        public string libraryName;

        [Tooltip("Items belonging to this library. Each becomes a LibraryItemId member " +
                 "prefixed with the library name: LibraryName_ItemName.")]
        [MID_NamedList]
        public List<string> itemNames = new();

        [Tooltip("Optional inline comment written next to the LibraryId enum member.")]
        public string comment;

        /// <summary>List element label: the library name, or "Unnamed Library" when blank.</summary>
        public string Name => string.IsNullOrWhiteSpace(libraryName) ? "Unnamed Library" : libraryName;
    }

    
    /// <summary>
    /// Contributes library and item names to the generated <c>LibraryId</c> and <c>LibraryItemId</c> enums.
    /// Same provider idea as the pool types: one asset per package or game project, one generator run.
    /// Priority: 0 is reserved for the utilities package, 100 and above is for game code.
    /// </summary>
[CreateAssetMenu(fileName="LibraryTypeProvider",
    menuName="MidManStudio/Utilities/Library Type Provider", order=210)]
    public class LibraryTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all library providers.")]
        public string packageId   = "com.mygame";
        public string displayName = "My Game";

        [Header("Priority")]
        [Tooltip("Lower = earlier block. 0 = reserved.\nStart at 100 for game code.")]
        public int priority = 100;

        [Header("Libraries")]
        [MID_NamedList]
        public List<LibraryEntryDefinition> libraries = new();

        /// <summary>Number of libraries, 0 when the list is null.</summary>
        public int LibraryCount => libraries?.Count ?? 0;
        /// <summary>Total number of item names over all libraries in this provider.</summary>
        public int TotalItemCount()
        {
            int n = 0;
            if (libraries != null) foreach (var lib in libraries) n += lib.itemNames?.Count ?? 0;
            return n;
        }
    }
}
