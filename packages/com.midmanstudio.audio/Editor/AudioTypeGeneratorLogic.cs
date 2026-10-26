// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioTypeGeneratorLogic.cs"
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("MidManStudio.Audio.Editor.Tests")]

namespace MidManStudio.Core.EditorUtils.Audio
{
    /// <summary>Outcome of one run of the Audio Type Generator.</summary>
    public sealed class AudioGenerationResult
    {
        public bool Success;
        public int CategoryBlocks;
        public int IdBlocks;
        public int FilesChanged;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public bool HasErrors { get { return Errors.Count > 0; } }
        public void AddError(string message) { Errors.Add(message); }
        public void AddWarning(string message) { Warnings.Add(message); }
    }

    internal sealed class AudioEntryData
    {
        public readonly string Name;
        public readonly string Comment;
        public readonly int Offset;

        public AudioEntryData(string name, string comment, int offset)
        {
            Name = name;
            Comment = comment;
            Offset = offset;
        }
    }

    internal sealed class AudioProviderData
    {
        public string PackageId;
        public string DisplayName;
        public int Priority;
        public List<AudioEntryData> Entries = new List<AudioEntryData>();
    }

    internal sealed class AudioResolvedEntry
    {
        public string Name;
        public int Value;
        public string Comment;
        public bool Pinned;
    }

    internal sealed class AudioResolvedBlock
    {
        public string PackageId;
        public string DisplayName;
        public int Priority;
        public int BlockStart;
        public int BlockSize;
        public List<AudioResolvedEntry> Entries = new List<AudioResolvedEntry>();
    }

    [Serializable]
    internal sealed class AudioLockEntry
    {
        public string packageId;
        public string name;
        public int value;
    }

    [Serializable]
    internal sealed class AudioLockBlock
    {
        public string packageId;
        public int start;
        public int size;
    }

    [Serializable]
    internal sealed class AudioLockSection
    {
        public List<AudioLockBlock> blocks = new List<AudioLockBlock>();
        public List<AudioLockEntry> entries = new List<AudioLockEntry>();
        public List<AudioLockEntry> retired = new List<AudioLockEntry>();
    }

    [Serializable]
    internal sealed class AudioLockFile
    {
        public int version = 1;
        public AudioLockSection categories = new AudioLockSection();
        public AudioLockSection ids = new AudioLockSection();
    }

    /// <summary>
    /// The Unity-free part of the Audio Type Generator: validation, block placement, value
    /// assignment, and the text of the generated enum files.
    /// </summary>
    internal static class AudioTypeLogic
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(new string[]
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
            "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while", "value__"
        });

        // ── Names ─────────────────────────────────────────────────────────────

        public static bool IsValidIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!IsLetterOrUnderscore(s[0])) return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (!IsLetterOrUnderscore(c) && !(c >= '0' && c <= '9')) return false;
            }
            return !Keywords.Contains(s);
        }

        public static bool IsValidNamespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split('.');
            for (int i = 0; i < parts.Length; i++)
                if (!IsValidIdentifier(parts[i])) return false;
            return true;
        }

        private static bool IsLetterOrUnderscore(char c)
        {
            return c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        // ── Block assignment ──────────────────────────────────────────────────

        /// <summary>
        /// Places every provider's block and gives every entry a value. Returns null when
        /// there are errors (they are added to <paramref name="result"/>). The lock section is
        /// not changed; the new state is returned in <paramref name="updated"/>.
        /// </summary>
        public static List<AudioResolvedBlock> Assign(
            List<AudioProviderData> providers, int minBlock, AudioLockSection old,
            AudioGenerationResult result, string kind, out AudioLockSection updated)
        {
            updated = null;
            if (minBlock < 1) minBlock = 1;
            if (old == null) old = new AudioLockSection();
            int errorsBefore = result.Errors.Count;

            List<AudioProviderData> sorted = new List<AudioProviderData>(providers);
            sorted.Sort(CompareProviders);

            // ── Validation ────────────────────────────────────────────────────
            List<AudioProviderData> present = new List<AudioProviderData>();
            Dictionary<string, List<AudioEntryData>> cleaned =
                new Dictionary<string, List<AudioEntryData>>(StringComparer.Ordinal);
            Dictionary<string, string> nameOwner = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (AudioProviderData p in sorted)
            {
                if (string.IsNullOrWhiteSpace(p.PackageId))
                {
                    result.AddError("[" + kind + "] A provider named '" + p.DisplayName + "' has an empty packageId.");
                    continue;
                }
                if (cleaned.ContainsKey(p.PackageId))
                {
                    result.AddError("[" + kind + "] Duplicate packageId '" + p.PackageId + "'.");
                    continue;
                }

                List<AudioEntryData> list = new List<AudioEntryData>();
                foreach (AudioEntryData e in p.Entries)
                {
                    string n = e.Name == null ? "" : e.Name.Trim();
                    if (n.Length == 0)
                    {
                        result.AddWarning("[" + kind + "] Provider '" + p.PackageId + "' has an entry with no name. It was ignored.");
                        continue;
                    }
                    if (!IsValidIdentifier(n))
                    {
                        result.AddError("[" + kind + "] '" + n + "' in '" + p.PackageId + "' is not a valid C# member name.");
                        continue;
                    }
                    string owner;
                    if (nameOwner.TryGetValue(n, out owner))
                    {
                        result.AddError("[" + kind + "] Duplicate entry name '" + n + "' (in '" + owner + "' and '" + p.PackageId + "').");
                        continue;
                    }
                    nameOwner[n] = p.PackageId;
                    list.Add(new AudioEntryData(n, e.Comment, e.Offset));
                }
                cleaned[p.PackageId] = list;
                present.Add(p);
            }
            if (result.Errors.Count > errorsBefore) return null;

            // ── Block sizes ───────────────────────────────────────────────────
            Dictionary<string, int> need = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (AudioProviderData p in present)
            {
                int span = 0;
                foreach (AudioEntryData e in cleaned[p.PackageId])
                    span = Math.Max(span, e.Offset >= 0 ? e.Offset + 1 : 0);
                span = Math.Max(span, cleaned[p.PackageId].Count);
                need[p.PackageId] = Math.Max(minBlock, RoundUp(span, minBlock));
            }

            // ── Block placement (blocks keep the place the lock gave them) ─────
            Dictionary<string, int[]> claim = ReadLockBlocks(old, result, kind);
            HashSet<string> presentIds = new HashSet<string>(cleaned.Keys, StringComparer.Ordinal);

            List<AudioProviderData> lockedPresent = new List<AudioProviderData>();
            foreach (AudioProviderData p in present)
                if (claim.ContainsKey(p.PackageId)) lockedPresent.Add(p);
            lockedPresent.Sort((a, b) => claim[a.PackageId][0].CompareTo(claim[b.PackageId][0]));

            foreach (AudioProviderData p in lockedPresent)
            {
                int[] c = claim[p.PackageId];
                int newSize = Math.Max(c[1], need[p.PackageId]);
                if (newSize == c[1]) continue;
                if (OverlapsOthers(claim, p.PackageId, c[0], newSize))
                {
                    claim.Remove(p.PackageId);
                    result.AddWarning("[" + kind + "] '" + p.PackageId + "' now needs " + newSize +
                        " slots but the space after its block is taken. It was moved to a new block, so its values changed.");
                }
                else
                {
                    c[1] = newSize;
                }
            }

            int cursor = 0;
            foreach (int[] c in claim.Values) cursor = Math.Max(cursor, c[0] + c[1]);
            foreach (AudioProviderData p in present)
            {
                if (claim.ContainsKey(p.PackageId)) continue;
                claim[p.PackageId] = new int[] { cursor, need[p.PackageId] };
                cursor += need[p.PackageId];
            }

            // ── Lock lookups ──────────────────────────────────────────────────
            Dictionary<string, int> liveLock = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (AudioLockEntry le in old.entries)
                if (le != null && le.packageId != null && le.name != null) liveLock[Key(le.packageId, le.name)] = le.value;
            Dictionary<string, int> retiredLock = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (AudioLockEntry le in old.retired)
                if (le != null && le.packageId != null && le.name != null) retiredLock[Key(le.packageId, le.name)] = le.value;

            // ── Entry values ──────────────────────────────────────────────────
            List<AudioResolvedBlock> blocks = new List<AudioResolvedBlock>();
            List<AudioLockEntry> newEntries = new List<AudioLockEntry>();
            List<AudioLockEntry> newRetired = new List<AudioLockEntry>();

            foreach (AudioProviderData p in present)
            {
                int[] c = claim[p.PackageId];
                List<AudioEntryData> entries = cleaned[p.PackageId];
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                foreach (AudioEntryData e in entries) names.Add(e.Name);

                // Numbers of removed entries stay reserved so a new entry never takes one over.
                HashSet<int> retiredHere = new HashSet<int>();
                List<AudioLockEntry> retiredNow = new List<AudioLockEntry>();
                foreach (AudioLockEntry le in old.retired)
                {
                    if (le == null || le.packageId != p.PackageId) continue;
                    if (InRange(le.value, c)) retiredHere.Add(le.value);
                }
                foreach (AudioLockEntry le in old.entries)
                {
                    if (le == null || le.packageId != p.PackageId || names.Contains(le.name)) continue;
                    retiredNow.Add(le);
                    if (InRange(le.value, c)) retiredHere.Add(le.value);
                }

                List<AudioResolvedEntry> resolved = ResolveEntries(
                    p.PackageId, entries, c[0], c[1], liveLock, retiredLock, retiredHere, result, kind);
                if (resolved == null) return null;

                HashSet<int> used = new HashSet<int>();
                foreach (AudioResolvedEntry r in resolved)
                {
                    used.Add(r.Value);
                    newEntries.Add(new AudioLockEntry { packageId = p.PackageId, name = r.Name, value = r.Value });
                }

                foreach (AudioLockEntry le in old.retired)
                {
                    if (le == null || le.packageId != p.PackageId) continue;
                    if (names.Contains(le.name) || used.Contains(le.value)) continue;
                    newRetired.Add(le);
                }
                foreach (AudioLockEntry le in retiredNow)
                    if (!used.Contains(le.value)) newRetired.Add(le);

                blocks.Add(new AudioResolvedBlock
                {
                    PackageId = p.PackageId,
                    DisplayName = p.DisplayName,
                    Priority = p.Priority,
                    BlockStart = c[0],
                    BlockSize = c[1],
                    Entries = resolved
                });
            }

            // Providers that are not here now (package removed, asset missing) keep their numbers.
            foreach (AudioLockEntry le in old.entries)
                if (le != null && le.packageId != null && !presentIds.Contains(le.packageId)) newEntries.Add(le);
            foreach (AudioLockEntry le in old.retired)
                if (le != null && le.packageId != null && !presentIds.Contains(le.packageId)) newRetired.Add(le);

            blocks.Sort((a, b) => a.BlockStart.CompareTo(b.BlockStart));

            updated = new AudioLockSection();
            foreach (KeyValuePair<string, int[]> kv in claim)
                updated.blocks.Add(new AudioLockBlock { packageId = kv.Key, start = kv.Value[0], size = kv.Value[1] });
            updated.blocks.Sort((a, b) => a.start.CompareTo(b.start));
            updated.entries = DistinctSorted(newEntries);
            updated.retired = DistinctSorted(newRetired);
            return blocks;
        }

        private static List<AudioResolvedEntry> ResolveEntries(
            string pkg, List<AudioEntryData> entries, int start, int size,
            Dictionary<string, int> liveLock, Dictionary<string, int> retiredLock,
            HashSet<int> retiredHere, AudioGenerationResult result, string kind)
        {
            List<AudioResolvedEntry> resolved = new List<AudioResolvedEntry>();
            Dictionary<int, string> taken = new Dictionary<int, string>();
            bool failed = false;

            // Pass 1: pinned entries.
            foreach (AudioEntryData e in entries)
            {
                if (e.Offset < 0) continue;
                int abs = start + e.Offset;
                string other;
                if (taken.TryGetValue(abs, out other))
                {
                    result.AddError("[" + kind + "] '" + e.Name + "' and '" + other + "' in '" + pkg + "' pin to the same offset " + e.Offset + ".");
                    failed = true;
                    continue;
                }
                taken[abs] = e.Name;
                resolved.Add(new AudioResolvedEntry { Name = e.Name, Value = abs, Comment = e.Comment, Pinned = true });

                int before;
                if (liveLock.TryGetValue(Key(pkg, e.Name), out before) && before != abs)
                    result.AddWarning("[" + kind + "] '" + e.Name + "' in '" + pkg + "' changed " + before + " -> " + abs + " (pinned).");
            }
            if (failed) return null;

            // Pass 2: entries that already had a number keep it, whatever their position in the list.
            List<AudioEntryData> pending = new List<AudioEntryData>();
            foreach (AudioEntryData e in entries)
            {
                if (e.Offset >= 0) continue;
                int remembered;
                if (TryRemembered(pkg, e.Name, liveLock, retiredLock, out remembered) &&
                    remembered >= start && remembered < start + size && !taken.ContainsKey(remembered))
                {
                    taken[remembered] = e.Name;
                    resolved.Add(new AudioResolvedEntry { Name = e.Name, Value = remembered, Comment = e.Comment, Pinned = false });
                }
                else
                {
                    pending.Add(e);
                }
            }

            // Pass 3: new entries take the lowest free number that no removed entry used.
            int scan = start;
            foreach (AudioEntryData e in pending)
            {
                int slot = -1;
                for (; scan < start + size; scan++)
                {
                    if (taken.ContainsKey(scan) || retiredHere.Contains(scan)) continue;
                    slot = scan++;
                    break;
                }

                bool reused = false;
                if (slot < 0)
                {
                    for (int s = start; s < start + size; s++)
                    {
                        if (taken.ContainsKey(s)) continue;
                        slot = s;
                        reused = true;
                        break;
                    }
                }
                if (slot < 0)
                {
                    result.AddError("[" + kind + "] Block overflow for '" + pkg + "'.");
                    return null;
                }

                taken[slot] = e.Name;
                resolved.Add(new AudioResolvedEntry { Name = e.Name, Value = slot, Comment = e.Comment, Pinned = false });
                if (reused)
                    result.AddWarning("[" + kind + "] '" + e.Name + "' in '" + pkg + "' took " + slot + ", which a removed entry used before.");

                int was;
                if (TryRemembered(pkg, e.Name, liveLock, retiredLock, out was) && was != slot)
                    result.AddWarning("[" + kind + "] '" + e.Name + "' in '" + pkg + "' changed " + was + " -> " + slot + ".");
            }

            resolved.Sort((a, b) => a.Value.CompareTo(b.Value));
            return resolved;
        }

        private static bool TryRemembered(string pkg, string name,
            Dictionary<string, int> liveLock, Dictionary<string, int> retiredLock, out int value)
        {
            string key = Key(pkg, name);
            if (liveLock.TryGetValue(key, out value)) return true;
            return retiredLock.TryGetValue(key, out value);
        }

        private static Dictionary<string, int[]> ReadLockBlocks(AudioLockSection old, AudioGenerationResult result, string kind)
        {
            List<AudioLockBlock> valid = new List<AudioLockBlock>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (AudioLockBlock b in old.blocks)
            {
                if (b == null || string.IsNullOrEmpty(b.packageId)) continue;
                if (b.start < 0 || b.size < 1)
                {
                    result.AddWarning("[" + kind + "] The lock file has an invalid block for '" + b.packageId + "'. It was ignored.");
                    continue;
                }
                if (!seen.Add(b.packageId)) continue;
                valid.Add(b);
            }
            valid.Sort((a, b) => a.start.CompareTo(b.start));

            Dictionary<string, int[]> claim = new Dictionary<string, int[]>(StringComparer.Ordinal);
            int end = 0;
            foreach (AudioLockBlock b in valid)
            {
                if (b.start < end)
                {
                    result.AddWarning("[" + kind + "] The lock file blocks for '" + b.packageId + "' overlap an earlier block. It will be placed again.");
                    continue;
                }
                claim[b.packageId] = new int[] { b.start, b.size };
                end = b.start + b.size;
            }
            return claim;
        }

        private static bool OverlapsOthers(Dictionary<string, int[]> claim, string self, int start, int size)
        {
            foreach (KeyValuePair<string, int[]> kv in claim)
            {
                if (kv.Key == self) continue;
                if (start < kv.Value[0] + kv.Value[1] && kv.Value[0] < start + size) return true;
            }
            return false;
        }

        private static bool InRange(int value, int[] block)
        {
            return value >= block[0] && value < block[0] + block[1];
        }

        private static int RoundUp(int n, int multiple)
        {
            return ((n + multiple - 1) / multiple) * multiple;
        }

        private static string Key(string pkg, string name)
        {
            return pkg + "\n" + name;
        }

        private static int CompareProviders(AudioProviderData a, AudioProviderData b)
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : string.CompareOrdinal(a.PackageId, b.PackageId);
        }

        private static List<AudioLockEntry> DistinctSorted(List<AudioLockEntry> source)
        {
            Dictionary<string, AudioLockEntry> byKey = new Dictionary<string, AudioLockEntry>(StringComparer.Ordinal);
            foreach (AudioLockEntry e in source) byKey[Key(e.packageId, e.name)] = e;
            List<AudioLockEntry> list = new List<AudioLockEntry>(byKey.Values);
            list.Sort((a, b) =>
            {
                int c = a.value.CompareTo(b.value);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.packageId, b.packageId);
                return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
            });
            return list;
        }

        // ── Generated file text ───────────────────────────────────────────────

        /// <summary>
        /// The text of one generated enum file. It has no timestamp and always uses "\n", so
        /// the same input gives the same bytes.
        /// </summary>
        public static string BuildEnumSource(List<AudioResolvedBlock> blocks, string ns,
            string enumName, string summary, string kind)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("// AUTO-GENERATED by the MidManStudio Audio Type Generator.\n");
            sb.Append("// DO NOT edit this file manually.\n");
            sb.Append("// Regenerate via: MidManStudio > Audio > Audio Type Generator\n");
            sb.Append("\n");
            sb.Append("namespace ").Append(ns).Append("\n");
            sb.Append("{\n");
            sb.Append("    /// <summary>").Append(summary).Append("</summary>\n");
            sb.Append("    [global::MidManStudio.Core.Audio.MID_AudioEnum(global::MidManStudio.Core.Audio.MID_AudioEnumKind.")
              .Append(kind).Append(")]\n");
            sb.Append("    public enum ").Append(enumName).Append("\n");
            sb.Append("    {\n");

            for (int b = 0; b < blocks.Count; b++)
            {
                AudioResolvedBlock blk = blocks[b];
                sb.Append("        // -- ").Append(OneLine(blk.DisplayName)).Append("  [").Append(OneLine(blk.PackageId))
                  .Append("]  (block ").Append(blk.BlockStart).Append("-").Append(blk.BlockStart + blk.BlockSize - 1)
                  .Append(")  priority=").Append(blk.Priority).Append("\n");

                if (blk.Entries.Count == 0)
                {
                    sb.Append("        // (no entries defined)\n");
                }
                else
                {
                    foreach (AudioResolvedEntry e in blk.Entries)
                    {
                        string comment = OneLine(e.Comment);
                        sb.Append("        ").Append(e.Name).Append(" = ").Append(e.Value).Append(",");
                        if (comment.Length > 0 || e.Pinned)
                        {
                            sb.Append(" //");
                            if (comment.Length > 0) sb.Append(" ").Append(comment);
                            if (e.Pinned) sb.Append(" [pinned]");
                        }
                        sb.Append("\n");
                    }
                }
                if (b < blocks.Count - 1) sb.Append("\n");
            }

            sb.Append("    }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // A comment must stay on one line, or its second line would become code.
        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
#endif
