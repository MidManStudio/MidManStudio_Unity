// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioEnumProviderBootstrapper.cs"
// ============================================================================
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Audio.Generator;

namespace MidManStudio.Core.EditorUtils.Audio
{
    /// <summary>
    /// Creates the audio package's default category and id providers and the generator settings
    /// after import, and generates the enums once when they do not exist yet.
    /// </summary>
    [InitializeOnLoad]
    internal static class AudioEnumProviderBootstrapper
    {
        private const string PackageId = "com.midmanstudio.audio";
        private const string ProviderDir = "Assets/MidManStudio/Audio/EnumProviders";
        private const string SettingsDir = "Assets/MidManStudio/Generated/Audio";
        private const string TriedKey = "MidManStudio.Audio.EnumFirstGenerationTried";

        static AudioEnumProviderBootstrapper()
        {
            EditorApplication.delayCall += Bootstrap;
        }

        [MenuItem("MidManStudio/Audio/Internal/Recreate Default Audio Providers")]
        public static void Recreate()
        {
            SessionState.SetBool(TriedKey, false);
            Bootstrap();
        }

        public static void Bootstrap()
        {
            bool changed = false;
            changed |= EnsureCategoryProvider();
            changed |= EnsureIdProvider();
            if (FindSettingsOrNull() == null)
            {
                CreateDefaultSettings();
                changed = true;
            }
            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[AudioEnumProviderBootstrapper] Default audio provider and settings assets created.");
            }

            // Generate once per editor session when the enums are missing. A failure is logged
            // once, not on every domain reload.
            AudioTypeGeneratorSettingsSO settings = FindSettingsOrNull();
            if (settings == null || AudioTypeGeneratorCore.OutputsExist(settings)) return;
            if (SessionState.GetBool(TriedKey, false)) return;
            SessionState.SetBool(TriedKey, true);

            AudioGenerationResult result = AudioTypeGeneratorCore.Generate(settings);
            foreach (string w in result.Warnings) Debug.LogWarning("[AudioEnumProviderBootstrapper] " + w);
            foreach (string e in result.Errors) Debug.LogError("[AudioEnumProviderBootstrapper] " + e);
        }

        /// <summary>Creates the settings asset at its default location and returns it.</summary>
        public static AudioTypeGeneratorSettingsSO CreateDefaultSettings()
        {
            if (!Directory.Exists(SettingsDir)) Directory.CreateDirectory(SettingsDir);
            var asset = ScriptableObject.CreateInstance<AudioTypeGeneratorSettingsSO>();
            AssetDatabase.CreateAsset(asset, SettingsDir + "/AudioTypeGeneratorSettings.asset");
            AssetDatabase.SaveAssets();
            return asset;
        }

        private static AudioTypeGeneratorSettingsSO FindSettingsOrNull()
        {
            return AudioTypeGeneratorCore.FindSettings();
        }

        private static bool EnsureCategoryProvider()
        {
            // Looked up by packageId, so a provider the user moved is not created a second time.
            foreach (string guid in AssetDatabase.FindAssets("t:AudioCategoryProviderSO"))
            {
                var existing = AssetDatabase.LoadAssetAtPath<AudioCategoryProviderSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (existing != null && existing.packageId == PackageId) return false;
            }

            EnsureDir();
            var so = ScriptableObject.CreateInstance<AudioCategoryProviderSO>();
            so.packageId = PackageId;
            so.displayName = "MidMan Studio Audio";
            so.priority = 0;
            so.entries.Add(Pinned("None", "No category. Plays through the default group with no scaling.", 0));
            so.entries.Add(Pinned("Music", "Background music", 1));
            so.entries.Add(Pinned("Sfx", "Sound effects", 2));
            so.entries.Add(Pinned("Ui", "Interface sounds", 3));
            so.entries.Add(Pinned("Ambience", "Ambient loops and beds", 4));
            so.entries.Add(Pinned("Voice", "Dialogue and voice lines", 5));
            AssetDatabase.CreateAsset(so, ProviderDir + "/AudioCategoryProvider_Audio.asset");
            return true;
        }

        private static bool EnsureIdProvider()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:AudioIdProviderSO"))
            {
                var existing = AssetDatabase.LoadAssetAtPath<AudioIdProviderSO>(AssetDatabase.GUIDToAssetPath(guid));
                if (existing != null && existing.packageId == PackageId) return false;
            }

            EnsureDir();
            var so = ScriptableObject.CreateInstance<AudioIdProviderSO>();
            so.packageId = PackageId;
            so.displayName = "MidMan Studio Audio";
            so.priority = 0;
            so.entries.Add(Pinned("None", "No sound. The value of an unassigned field.", 0));
            AssetDatabase.CreateAsset(so, ProviderDir + "/AudioIdProvider_Audio.asset");
            return true;
        }

        private static AudioEntryDefinition Pinned(string name, string comment, int offset)
        {
            return new AudioEntryDefinition { entryName = name, comment = comment, explicitOffset = offset };
        }

        private static void EnsureDir()
        {
            if (!Directory.Exists(ProviderDir)) Directory.CreateDirectory(ProviderDir);
        }
    }
}
#endif
