// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioTypeGeneratorSettingsSO.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Core.Audio.Generator
{
    /// <summary>
    /// Project-wide settings for the Audio Type Generator. The audio package creates a default
    /// asset under Assets/MidManStudio/Generated/Audio on first import.
    /// </summary>
    [CreateAssetMenu(
        fileName = "AudioTypeGeneratorSettings",
        menuName = "MidManStudio/Audio/Audio Type Generator Settings",
        order = 192)]
    public class AudioTypeGeneratorSettingsSO : ScriptableObject
    {
        [Header("Output Paths")]
        [Tooltip("Project-relative path of the generated category enum. Keep it under Assets so it can be written.")]
        public string categoryEnumOutputPath = "Assets/MidManStudio/Generated/Audio/AudioCategory.cs";

        [Tooltip("Project-relative path of the generated id enum. Keep it under Assets so it can be written.")]
        public string idEnumOutputPath = "Assets/MidManStudio/Generated/Audio/AudioId.cs";

        [Tooltip("Commit this to source control. It keeps enum values stable across regenerations.")]
        public string lockFilePath = "Assets/MidManStudio/Generated/Audio/AudioTypeLock.json";

        [Header("Block Sizing")]
        [Tooltip("A category block is at least this big and grows in multiples of it.")]
        [Min(1)]
        public int categoryBlockSize = 100;

        [Tooltip("An id block is at least this big and grows in multiples of it.")]
        [Min(1)]
        public int idBlockSize = 1000;

        [Header("Namespace")]
        public string generatedNamespace = "MidManStudio.Core.Audio";

        [Header("Auto-Generate")]
        [Tooltip("Regenerate when a provider or settings asset is saved.")]
        public bool autoGenerateOnAssetChange = false;
    }
}
