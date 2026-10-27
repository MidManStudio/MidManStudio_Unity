// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/enum-generators.md, section "EnumBlockLogic.cs"
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MidManStudio.Utilities.Tests.Editor")]

namespace MidManStudio.Core.EditorUtils.Generators
{
    /// <summary>One entry as read from a provider asset.</summary>
    internal sealed class EnumEntryData
    {
        public readonly string Name;
        public readonly string Comment;
        public readonly int Offset;

        public EnumEntryData(string name, string comment, int offset)
        {
            Name = name;
            Comment = comment;
            Offset = offset;
        }
    }

    /// <summary>One provider asset: an identity, a priority and a list of entries.</summary>
    internal sealed class EnumProviderData
    {
        public string PackageId;
        public string DisplayName;
        public int Priority;
        public List<EnumEntryData> Entries = new List<EnumEntryData>();
    }

    internal sealed class EnumResolvedEntry
    {
        public string Name;
        public int Value;
        public string Comment;
        public bool Pinned;
    }

    internal sealed class EnumResolvedBlock
    {
        public string PackageId;
        public string DisplayName;
        public int Priority;
        public int BlockStart;
        public int BlockSize;
        public List<EnumResolvedEntry> Entries = new List<EnumResolvedEntry>();
    }

    /// <summary>A remembered number. The field names match the lock files written before blocks were stored.</summary>
    [Serializable]
    internal sealed class EnumLockEntry
    {
        public string packageId;
        public string name;
        public int value;
    }

    /// <summary>The range of numbers a provider owns.</summary>
    [Serializable]
    internal sealed class EnumLockBlock
    {
        public string packageId;
        public int start;
        public int size;
    }

    /// <summary>
    /// Everything the lock file remembers about one enum: live entries, block ranges, and the
    /// numbers of entries that were removed. A lock file holds one of these per enum, stored as
    /// three lists with its own field names.
    /// </summary>
    internal sealed class EnumLockSection
    {
        public List<EnumLockBlock> blocks = new List<EnumLockBlock>();
        public List<EnumLockEntry> entries = new List<EnumLockEntry>();
        public List<EnumLockEntry> retired = new List<EnumLockEntry>();
    }

    /// <summary>
    /// The Unity-free part of the enum generators: validation, block placement and value
    /// assignment. The effect and pool generators both run their providers through
    /// <see cref="Assign"/>.
    /// </summary>
    internal static class EnumBlockLogic
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
            if (!(s[0] == '_' || char.IsLetter(s[0]))) return false;
            for (int i = 1; i < s.Length; i++)
                if (!(s[i] == '_' || char.IsLetterOrDigit(s[i]))) return false;
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

        // ── Block assignment ──────────────────────────────────────────────────

        /// <summary>
        /// Places every provider's block and gives every entry a number. Returns null when there
        /// are errors; they are added to <paramref name="errors"/>. The lock section passed in is
        /// not changed. The state to save is returned in <paramref name="updated"/>.
        /// </summary>
        public static List<EnumResolvedBlock> Assign(
            List<EnumProviderData> providers, int minBlock, EnumLockSection old,
            List<string> errors, List<string> warnings, string kind, out EnumLockSection updated)
        {
            updated = null;
            if (minBlock < 1) minBlock = 1;
            if (old == null) old = new EnumLockSection();
            int errorsBefore = errors.Count;

            List<EnumProviderData> sorted = new List<EnumProviderData>(providers);
            sorted.Sort(CompareProviders);

            // ── Validation ────────────────────────────────────────────────────
            List<EnumProviderData> present = new List<EnumProviderData>();
            Dictionary<string, List<EnumEntryData>> cleaned =
                new Dictionary<string, List<EnumEntryData>>(StringComparer.Ordinal);
            Dictionary<string, string> nameOwner = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (EnumProviderData p in sorted)
            {
                if (string.IsNullOrWhiteSpace(p.PackageId))
                {
                    errors.Add("[" + kind + "] A provider named '" + p.DisplayName + "' has an empty packageId.");
                    continue;
                }
                if (cleaned.ContainsKey(p.PackageId))
                {
                    errors.Add("[" + kind + "] Duplicate packageId '" + p.PackageId + "'.");
                    continue;
                }

                List<EnumEntryData> list = new List<EnumEntryData>();
                foreach (EnumEntryData e in p.Entries)
                {
                    string n = e.Name == null ? "" : e.Name.Trim();
                    if (n.Length == 0)
                    {
                        warnings.Add("[" + kind + "] Provider '" + p.PackageId + "' has an entry with no name. It was ignored.");
                        continue;
                    }
                    if (!IsValidIdentifier(n))
                    {
                        errors.Add("[" + kind + "] '" + n + "' in '" + p.PackageId + "' is not a valid C# member name.");
                        continue;
                    }
                    string owner;
                    if (nameOwner.TryGetValue(n, out owner))
                    {
                        errors.Add("[" + kind + "] Duplicate entry name '" + n + "' (in '" + owner + "' and '" + p.PackageId + "').");
                        continue;
                    }
                    nameOwner[n] = p.PackageId;
                    list.Add(new EnumEntryData(n, e.Comment, e.Offset));
                }
                cleaned[p.PackageId] = list;
                present.Add(p);
            }
            if (errors.Count > errorsBefore) return null;

            // ── Block sizes ───────────────────────────────────────────────────
            Dictionary<string, int> need = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (EnumProviderData p in present)
            {
                int span = 0;
                foreach (EnumEntryData e in cleaned[p.PackageId])
                    span = Math.Max(span, e.Offset >= 0 ? e.Offset + 1 : 0);
                span = Math.Max(span, cleaned[p.PackageId].Count);
                need[p.PackageId] = Math.Max(minBlock, RoundUp(span, minBlock));
            }

            // ── Block placement (a block keeps the place the lock gave it) ─────
            // A lock file from before blocks were stored has entries only. The blocks are worked
            // out from where those entries sit, so nothing moves on the first run.
            List<EnumLockBlock> lockBlocks = old.blocks;
            if (lockBlocks.Count == 0 && old.entries.Count > 0)
                lockBlocks = InferBlocks(old.entries, minBlock);

            Dictionary<string, int[]> claim = ReadLockBlocks(lockBlocks, warnings, kind);
            HashSet<string> presentIds = new HashSet<string>(cleaned.Keys, StringComparer.Ordinal);

            List<EnumProviderData> lockedPresent = new List<EnumProviderData>();
            foreach (EnumProviderData p in present)
                if (claim.ContainsKey(p.PackageId)) lockedPresent.Add(p);
            lockedPresent.Sort((a, b) => claim[a.PackageId][0].CompareTo(claim[b.PackageId][0]));

            foreach (EnumProviderData p in lockedPresent)
            {
                int[] c = claim[p.PackageId];
                int newSize = Math.Max(c[1], need[p.PackageId]);
                if (newSize == c[1]) continue;
                if (OverlapsOthers(claim, p.PackageId, c[0], newSize))
                {
                    claim.Remove(p.PackageId);
                    warnings.Add("[" + kind + "] '" + p.PackageId + "' now needs " + newSize +
                        " slots but the space after its block is taken. It was moved to a new block, so its values changed.");
                }
                else
                {
                    c[1] = newSize;
                }
            }

            // New providers go after every block that exists, including blocks of providers
            // that are not present right now.
            int cursor = 0;
            foreach (int[] c in claim.Values) cursor = Math.Max(cursor, c[0] + c[1]);
            foreach (EnumProviderData p in present)
            {
                if (claim.ContainsKey(p.PackageId)) continue;
                claim[p.PackageId] = new int[] { cursor, need[p.PackageId] };
                cursor += need[p.PackageId];
            }

            // ── Lock lookups ──────────────────────────────────────────────────
            Dictionary<string, int> liveLock = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (EnumLockEntry le in old.entries)
                if (le != null && le.packageId != null && le.name != null) liveLock[Key(le.packageId, le.name)] = le.value;
            Dictionary<string, int> retiredLock = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (EnumLockEntry le in old.retired)
                if (le != null && le.packageId != null && le.name != null) retiredLock[Key(le.packageId, le.name)] = le.value;

            // ── Entry values ──────────────────────────────────────────────────
            List<EnumResolvedBlock> blocks = new List<EnumResolvedBlock>();
            List<EnumLockEntry> newEntries = new List<EnumLockEntry>();
            List<EnumLockEntry> newRetired = new List<EnumLockEntry>();

            foreach (EnumProviderData p in present)
            {
                int[] c = claim[p.PackageId];
                List<EnumEntryData> entries = cleaned[p.PackageId];
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                foreach (EnumEntryData e in entries) names.Add(e.Name);

                // Numbers of removed entries stay reserved so a new entry never takes one over.
                HashSet<int> retiredHere = new HashSet<int>();
                List<EnumLockEntry> retiredNow = new List<EnumLockEntry>();
                foreach (EnumLockEntry le in old.retired)
                {
                    if (le == null || le.packageId != p.PackageId) continue;
                    if (InRange(le.value, c)) retiredHere.Add(le.value);
                }
                foreach (EnumLockEntry le in old.entries)
                {
                    if (le == null || le.packageId != p.PackageId || names.Contains(le.name)) continue;
                    retiredNow.Add(le);
                    if (InRange(le.value, c)) retiredHere.Add(le.value);
                }

                List<EnumResolvedEntry> resolved = ResolveEntries(
                    p.PackageId, entries, c[0], c[1], liveLock, retiredLock, retiredHere, errors, warnings, kind);
                if (resolved == null) return null;

                HashSet<int> used = new HashSet<int>();
                foreach (EnumResolvedEntry r in resolved)
                {
                    used.Add(r.Value);
                    newEntries.Add(new EnumLockEntry { packageId = p.PackageId, name = r.Name, value = r.Value });
                }

                foreach (EnumLockEntry le in old.retired)
                {
                    if (le == null || le.packageId != p.PackageId) continue;
                    if (names.Contains(le.name) || used.Contains(le.value)) continue;
                    newRetired.Add(le);
                }
                foreach (EnumLockEntry le in retiredNow)
                    if (!used.Contains(le.value)) newRetired.Add(le);

                blocks.Add(new EnumResolvedBlock
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
            foreach (EnumLockEntry le in old.entries)
                if (le != null && le.packageId != null && !presentIds.Contains(le.packageId)) newEntries.Add(le);
            foreach (EnumLockEntry le in old.retired)
                if (le != null && le.packageId != null && !presentIds.Contains(le.packageId)) newRetired.Add(le);

            blocks.Sort((a, b) => a.BlockStart.CompareTo(b.BlockStart));

            updated = new EnumLockSection();
            foreach (KeyValuePair<string, int[]> kv in claim)
                updated.blocks.Add(new EnumLockBlock { packageId = kv.Key, start = kv.Value[0], size = kv.Value[1] });
            updated.blocks.Sort((a, b) => a.start.CompareTo(b.start));
            updated.entries = DistinctSorted(newEntries);
            updated.retired = DistinctSorted(newRetired);
            return blocks;
        }

        private static List<EnumResolvedEntry> ResolveEntries(
            string pkg, List<EnumEntryData> entries, int start, int size,
            Dictionary<string, int> liveLock, Dictionary<string, int> retiredLock,
            HashSet<int> retiredHere, List<string> errors, List<string> warnings, string kind)
        {
            List<EnumResolvedEntry> resolved = new List<EnumResolvedEntry>();
            Dictionary<int, string> taken = new Dictionary<int, string>();
            bool failed = false;

            // Pass 1: pinned entries.
            foreach (EnumEntryData e in entries)
            {
                if (e.Offset < 0) continue;
                int abs = start + e.Offset;
                string other;
                if (taken.TryGetValue(abs, out other))
                {
                    errors.Add("[" + kind + "] '" + e.Name + "' and '" + other + "' in '" + pkg + "' pin to the same offset " + e.Offset + ".");
                    failed = true;
                    continue;
                }
                taken[abs] = e.Name;
                resolved.Add(new EnumResolvedEntry { Name = e.Name, Value = abs, Comment = e.Comment, Pinned = true });

                int before;
                if (liveLock.TryGetValue(Key(pkg, e.Name), out before) && before != abs)
                    warnings.Add("[" + kind + "] '" + e.Name + "' in '" + pkg + "' changed " + before + " -> " + abs + " (pinned).");
            }
            if (failed) return null;

            // Pass 2: entries that already had a number keep it, whatever their position in the list.
            List<EnumEntryData> pending = new List<EnumEntryData>();
            foreach (EnumEntryData e in entries)
            {
                if (e.Offset >= 0) continue;
                int remembered;
                if (TryRemembered(pkg, e.Name, liveLock, retiredLock, out remembered) &&
                    remembered >= start && remembered < start + size && !taken.ContainsKey(remembered))
                {
                    taken[remembered] = e.Name;
                    resolved.Add(new EnumResolvedEntry { Name = e.Name, Value = remembered, Comment = e.Comment, Pinned = false });
                }
                else
                {
                    pending.Add(e);
                }
            }

            // Pass 3: new entries take the lowest free number that no removed entry used.
            int scan = start;
            foreach (EnumEntryData e in pending)
            {
                int slot = -1;
                for (; scan < start + size; scan++)
                {
                    if (taken.ContainsKey(scan) || retiredHere.Contains(scan)) continue;
                    slot = scan++;
                    break;
                }

                // Every free number was used by a removed entry once. Reuse is the last resort.
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
                    errors.Add("[" + kind + "] Block overflow for '" + pkg + "'.");
                    return null;
                }

                taken[slot] = e.Name;
                resolved.Add(new EnumResolvedEntry { Name = e.Name, Value = slot, Comment = e.Comment, Pinned = false });
                if (reused)
                    warnings.Add("[" + kind + "] '" + e.Name + "' in '" + pkg + "' took " + slot + ", which a removed entry used before.");

                int was;
                if (TryRemembered(pkg, e.Name, liveLock, retiredLock, out was) && was != slot)
                    warnings.Add("[" + kind + "] '" + e.Name + "' in '" + pkg + "' changed " + was + " -> " + slot + ".");
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

        // Works out one block per package from the numbers its entries already have.
        private static List<EnumLockBlock> InferBlocks(List<EnumLockEntry> entries, int minBlock)
        {
            Dictionary<string, int[]> range = new Dictionary<string, int[]>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            foreach (EnumLockEntry le in entries)
            {
                if (le == null || string.IsNullOrEmpty(le.packageId) || le.value < 0) continue;
                int[] r;
                if (!range.TryGetValue(le.packageId, out r))
                {
                    range[le.packageId] = new int[] { le.value, le.value };
                    order.Add(le.packageId);
                }
                else
                {
                    r[0] = Math.Min(r[0], le.value);
                    r[1] = Math.Max(r[1], le.value);
                }
            }

            List<EnumLockBlock> blocks = new List<EnumLockBlock>();
            foreach (string id in order)
            {
                int[] r = range[id];
                int start = (r[0] / minBlock) * minBlock;
                blocks.Add(new EnumLockBlock { packageId = id, start = start, size = RoundUp(r[1] - start + 1, minBlock) });
            }
            return blocks;
        }

        private static Dictionary<string, int[]> ReadLockBlocks(List<EnumLockBlock> source, List<string> warnings, string kind)
        {
            List<EnumLockBlock> valid = new List<EnumLockBlock>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (EnumLockBlock b in source)
            {
                if (b == null || string.IsNullOrEmpty(b.packageId)) continue;
                if (b.start < 0 || b.size < 1)
                {
                    warnings.Add("[" + kind + "] The lock file has an invalid block for '" + b.packageId + "'. It was ignored.");
                    continue;
                }
                if (!seen.Add(b.packageId)) continue;
                valid.Add(b);
            }
            valid.Sort((a, b) => a.start.CompareTo(b.start));

            Dictionary<string, int[]> claim = new Dictionary<string, int[]>(StringComparer.Ordinal);
            int end = 0;
            foreach (EnumLockBlock b in valid)
            {
                if (b.start < end)
                {
                    warnings.Add("[" + kind + "] The lock file blocks for '" + b.packageId + "' overlap an earlier block. It will be placed again.");
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

        private static int CompareProviders(EnumProviderData a, EnumProviderData b)
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : string.CompareOrdinal(a.PackageId, b.PackageId);
        }

        private static List<EnumLockEntry> DistinctSorted(List<EnumLockEntry> source)
        {
            Dictionary<string, EnumLockEntry> byKey = new Dictionary<string, EnumLockEntry>(StringComparer.Ordinal);
            foreach (EnumLockEntry e in source) byKey[Key(e.packageId, e.name)] = e;
            List<EnumLockEntry> list = new List<EnumLockEntry>(byKey.Values);
            list.Sort((a, b) =>
            {
                int c = a.value.CompareTo(b.value);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.packageId, b.packageId);
                return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
            });
            return list;
        }

        /// <summary>Keeps a comment on one line, or its second line would become code.</summary>
        public static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
#endif
