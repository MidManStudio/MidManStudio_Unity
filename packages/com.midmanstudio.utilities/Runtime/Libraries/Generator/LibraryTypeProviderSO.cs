// LibraryTypeProviderSO.cs
// Contributes library and item entries to the generated LibraryId and LibraryItemId enums.
// Same provider pattern as pool types — one asset per package, one generator run.
//
// PRIORITY RANGES:
//   0   = com.midmanstudio.utilities (reserved)
//   100 = user game code

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Libraries.Generator
{
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

        public string Name => string.IsNullOrWhiteSpace(libraryName) ? "Unnamed Library" : libraryName;
    }

    
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

        public int LibraryCount => libraries?.Count ?? 0;
        public int TotalItemCount()
        {
            int n = 0;
            if (libraries != null) foreach (var lib in libraries) n += lib.itemNames?.Count ?? 0;
            return n;
        }
    }
}
