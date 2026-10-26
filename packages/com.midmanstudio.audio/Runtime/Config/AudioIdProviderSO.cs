// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioIdProviderSO.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.Audio.Generator
{
    /// <summary>
    /// Contributes entries to the generated AudioId enum. The audio package ships one,
    /// and a game or another package can add its own.
    /// </summary>
    [CreateAssetMenu(
        fileName = "AudioIdProvider",
        menuName = "MidManStudio/Audio/Audio Id Provider",
        order = 191)]
    public class AudioIdProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all providers.")]
        public string packageId = "com.mygame";

        [Tooltip("Human-readable name shown in the generator window and in the generated file.")]
        public string displayName = "My Game";

        [Header("Block Priority")]
        [Tooltip("0 = audio package (reserved). 100+ = user game. A provider seen for the first time " +
                 "gets a block after the existing ones, in priority order.")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<AudioEntryDefinition> entries = new List<AudioEntryDefinition>();

        public int EntryCount { get { return entries == null ? 0 : entries.Count; } }
    }
}
