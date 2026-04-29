
using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Audio
{
    [Serializable]
    public struct MID_AudioEntry : IArrayElementTitle
    {
        [Tooltip("String key used to play this clip. e.g. 'shoot', 'menu_music'")]
        public string id;
        public AudioClip clip;
        [Range(0f, 1f)] public float volume;

        // IArrayElementTitle — id first, clip.name second, fallback third
        public string Name =>
            !string.IsNullOrEmpty(id) ? id :
            clip != null ? clip.name :
                           "Audio Entry";
    }

    [CreateAssetMenu(fileName = "MID_AudioLibrary", menuName = "MidManStudio/Utilities/Audio Library")]
    public class MID_AudioLibrarySO : ScriptableObject
    {
        [MID_NamedList]
        [SerializeField] private List<MID_AudioEntry> _entries = new List<MID_AudioEntry>();

        private Dictionary<string, MID_AudioEntry> _lookup;
        private bool _built;

        //  ScriptableObjects survive scene changes but non-serialized Dictionaries
        // are destroyed on domain reload while _built stays true → null crash.
        // Always check _lookup != null in addition to _built.
        public void BuildLookup()
        {
            if (_built && _lookup != null) return;

            _lookup = new Dictionary<string, MID_AudioEntry>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.id) || e.clip == null) continue;
                if (!_lookup.ContainsKey(e.id))
                    _lookup[e.id] = e;
            }
            _built = true;
        }

        public bool TryGet(string id, out MID_AudioEntry entry)
        {
            BuildLookup();
            return _lookup.TryGetValue(id, out entry);
        }

        public bool HasClip(string id) { BuildLookup(); return _lookup.ContainsKey(id); }

        public int Count { get { BuildLookup(); return _lookup.Count; } }

        // Called by Unity when the SO is loaded or scripts are recompiled —
        // forces the dictionary to be rebuilt on next use.
        private void OnEnable() => _built = false;
    }
}