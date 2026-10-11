// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio.md, section "AudioPoolProviderBootstrapper.cs"
// ============================================================================
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Pools.Generator;

namespace MidManStudio.Core.EditorUtils.Audio
{
    /// <summary>
    /// Creates the audio package's pool type provider asset after import. The provider defines the
    /// SpawnableAudio pool type used by MID_SpawnableAudio.
    /// </summary>
    [InitializeOnLoad]
    internal static class AudioPoolProviderBootstrapper
    {
        private const string AudioDir = "Assets/MidManStudio/Audio/PoolProviders";

        static AudioPoolProviderBootstrapper()
        {
            EditorApplication.delayCall += Bootstrap;
        }

        [MenuItem("MidManStudio/Audio/Internal/Recreate Audio Pool Provider")]
        public static void Bootstrap()
        {
            if (!EnsureAudioObjectProvider()) return;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AudioPoolProviderBootstrapper] Audio pool provider asset created. " +
                      "Run MidManStudio > Utilities > Pool Type Generator to add SpawnableAudio to the enum.");
        }

        private static bool EnsureAudioObjectProvider()
        {
            const string path = AudioDir + "/ObjectPoolTypeProvider_Audio.asset";
            if (File.Exists(path)) return false;

            if (!Directory.Exists(AudioDir)) Directory.CreateDirectory(AudioDir);
            var so = ScriptableObject.CreateInstance<ObjectPoolTypeProviderSO>();
            so.packageId   = "com.midmanstudio.audio";
            so.displayName = "MidMan Studio Audio";
            so.priority    = 20;
            so.entries.Add(new PoolEntryDefinition
            {
                entryName      = "SpawnableAudio",
                comment        = "Pooled one-shot / looping audio source",
                explicitOffset = 0
            });

            AssetDatabase.CreateAsset(so, path);
            return true;
        }
    }
}
#endif
