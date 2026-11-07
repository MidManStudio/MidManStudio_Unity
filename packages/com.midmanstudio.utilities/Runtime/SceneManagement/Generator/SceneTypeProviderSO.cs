// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/scenemanagement.md, section "SceneTypeProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.SceneManagement.Generator
{
    /// <summary>
    /// One scene in a <see cref="SceneTypeProviderSO"/>: the enum member name, the scene name, its build
    /// index (which becomes the enum value), its network dependency and an optional comment.
    /// </summary>
    [System.Serializable]
    public class SceneEntryDefinition : IArrayElementTitle
    {
        [Tooltip("Becomes the SceneId enum member name. PascalCase, no spaces.")]
        public string enumName;

        [Tooltip("Exact scene name as it appears in Build Settings.")]
        public string sceneName;

        [Tooltip("Build index as set in File > Build Settings. " +
                 "This becomes the enum integer value. Must be unique across all providers.")]
        public int buildIndex = 0;

        public SceneNetworkDependency networkDependency = SceneNetworkDependency.None;

        [Tooltip("Optional comment written next to the enum member.")]
        public string comment;

        /// <summary>List element label: the enum name, or <c>Scene_&lt;buildIndex&gt;</c> when blank.</summary>
        public string Name => string.IsNullOrWhiteSpace(enumName) ? $"Scene_{buildIndex}" : enumName;
    }

    
    /// <summary>
    /// Contributes scenes to the generated <c>SceneId</c> enum and <c>SceneRegistry</c>. One asset per package
    /// or game project. Create via Create > MidManStudio > Utilities > Scene Type Provider, then run
    /// MidManStudio > Utilities > Scene Type Generator.
    /// </summary>
[CreateAssetMenu(fileName="SceneTypeProvider",
    menuName="MidManStudio/Utilities/Scene Type Provider", order=180)]
    public class SceneTypeProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all scene providers.")]
        public string packageId   = "com.mygame";
        public string displayName = "My Game";

        [Header("Priority")]
        [Tooltip("Lower = processed first. Use 100+ for game code.")]
        public int priority = 100;

        [Header("Scenes")]
        [MID_NamedList]
        public List<SceneEntryDefinition> scenes = new();

        /// <summary>Number of scenes, 0 when the list is null.</summary>
        public int SceneCount => scenes?.Count ?? 0;
    }
}
