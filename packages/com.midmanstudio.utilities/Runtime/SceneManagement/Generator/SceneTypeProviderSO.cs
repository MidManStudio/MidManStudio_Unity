// SceneTypeProviderSO.cs
// One asset per package / game project. Contributes scene entries to the
// generated SceneId enum and SceneRegistry.
// Create via: MidManStudio > Utilities > Scene Type Provider

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.SceneManagement.Generator
{
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

        public string Name => string.IsNullOrWhiteSpace(enumName) ? $"Scene_{buildIndex}" : enumName;
    }

    [CreateAssetMenu(
        fileName = "SceneTypeProvider",
        menuName = "MidManStudio/Utilities/Scene Type Provider")]
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

        public int SceneCount => scenes?.Count ?? 0;
    }
}
