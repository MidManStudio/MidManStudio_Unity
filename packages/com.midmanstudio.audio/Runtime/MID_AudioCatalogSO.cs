// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "MID_AudioCatalogSO.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Audio
{
    /// <summary>
    /// Settings for one audio category: the mixer group its clips play through and a volume
    /// scale. The category is stored as the int value of the generated AudioCategory enum.
    /// </summary>
    [Serializable]
    public class MID_AudioCategorySettings : IArrayElementTitle
    {
        [Tooltip("The category these settings apply to.")]
        [MID_AudioCategory] public int category;

        [Tooltip("Mixer group for clips in this category. Empty means the manager's default music or SFX group.")]
        public AudioMixerGroup mixerGroup;

        [Tooltip("Volume scale applied to every clip in this category.")]
        [Range(0f, 1f)] public float volume = 1f;

        // IArrayElementTitle
        public string Name
        {
            get
            {
#if UNITY_EDITOR
                string enumName;
                if (MID_AudioEnumLookup.TryGetName(MID_AudioEnumKind.Category, category, out enumName))
                    return enumName;
#endif
                return "Category " + category;
            }
        }
    }

    /// <summary>
    /// One playable clip. The id and category are stored as the int values of the generated
    /// AudioId and AudioCategory enums.
    /// </summary>
    [Serializable]
    public class MID_AudioClipEntry : IArrayElementTitle
    {
        [Tooltip("The audio id that plays this clip.")]
        [MID_AudioId] public int id;

        [Tooltip("The category this clip belongs to. None uses the manager's default group, unless the catalog has a settings row for None.")]
        [MID_AudioCategory] public int category;

        public AudioClip clip;

        [Tooltip("Volume of this clip before the category scale and the master volume.")]
        [Range(0f, 1f)] public float volume = 1f;

        // IArrayElementTitle
        public string Name
        {
            get
            {
#if UNITY_EDITOR
                string enumName;
                if (MID_AudioEnumLookup.TryGetName(MID_AudioEnumKind.Id, id, out enumName))
                    return enumName;
#endif
                if (clip != null) return clip.name;
                return "Audio Entry";
            }
        }
    }

    /// <summary>
    /// Maps generated audio ids to clips, and generated categories to a mixer group and volume.
    /// Assign one or more catalogs to <see cref="MID_AudioManager"/>. A package can ship its
    /// own catalog and a game can add another; the manager merges them.
    /// </summary>
    [CreateAssetMenu(fileName = "MID_AudioCatalog",
        menuName = "MidManStudio/Audio/Audio Catalog", order = 101)]
    public class MID_AudioCatalogSO : ScriptableObject
    {
        [Header("Categories")]
        [MID_NamedList]
        [SerializeField] private List<MID_AudioCategorySettings> _categories = new List<MID_AudioCategorySettings>();

        [Header("Clips")]
        [MID_NamedList]
        [SerializeField] private List<MID_AudioClipEntry> _clips = new List<MID_AudioClipEntry>();

        /// <summary>Category settings in this catalog.</summary>
        public IReadOnlyList<MID_AudioCategorySettings> Categories { get { return _categories; } }

        /// <summary>Clip entries in this catalog.</summary>
        public IReadOnlyList<MID_AudioClipEntry> Clips { get { return _clips; } }

        /// <summary>
        /// Adds a description of every problem found to <paramref name="problems"/> and returns
        /// how many there were. Checks for repeated ids, missing clips, id 0, repeated category rows,
        /// and (in the editor) values that are not in the generated enums.
        /// </summary>
        public int Validate(List<string> problems)
        {
            int before = problems.Count;
            var seenIds = new HashSet<int>();
            for (int i = 0; i < _clips.Count; i++)
            {
                MID_AudioClipEntry e = _clips[i];
                if (e == null) { problems.Add(name + ": clip row " + i + " is empty."); continue; }
                if (e.clip == null) problems.Add(name + ": clip row " + i + " (" + e.Name + ") has no clip.");
                if (e.id == 0) problems.Add(name + ": clip row " + i + " uses id 0, which is normally the None placeholder.");
                if (!seenIds.Add(e.id)) problems.Add(name + ": id " + e.id + " (" + e.Name + ") appears more than once.");
#if UNITY_EDITOR
                CheckInEnum(problems, MID_AudioEnumKind.Id, e.id, "clip row " + i + " id");
                CheckInEnum(problems, MID_AudioEnumKind.Category, e.category, "clip row " + i + " category");
#endif
            }

            var seenCategories = new HashSet<int>();
            for (int i = 0; i < _categories.Count; i++)
            {
                MID_AudioCategorySettings c = _categories[i];
                if (c == null) { problems.Add(name + ": category row " + i + " is empty."); continue; }
                if (!seenCategories.Add(c.category))
                    problems.Add(name + ": category " + c.category + " (" + c.Name + ") has more than one settings row.");
#if UNITY_EDITOR
                CheckInEnum(problems, MID_AudioEnumKind.Category, c.category, "category row " + i);
#endif
            }
            return problems.Count - before;
        }

#if UNITY_EDITOR
        private void CheckInEnum(List<string> problems, MID_AudioEnumKind kind, int value, string where)
        {
            int[] values = MID_AudioEnumLookup.GetValues(kind);
            if (values.Length == 0) return;
            if (Array.IndexOf(values, value) < 0)
                problems.Add(name + ": " + where + " " + value + " is not in the generated " + kind + " enum.");
        }
#endif

        [ContextMenu("Validate Catalog")]
        private void ValidateFromMenu()
        {
            var problems = new List<string>();
            if (Validate(problems) == 0)
            {
                Debug.Log("[MID_AudioCatalogSO] " + name + ": no problems found.", this);
                return;
            }
            foreach (string p in problems) Debug.LogWarning("[MID_AudioCatalogSO] " + p, this);
        }
    }
}
