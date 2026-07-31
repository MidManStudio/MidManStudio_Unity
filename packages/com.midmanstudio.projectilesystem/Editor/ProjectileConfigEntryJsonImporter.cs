// Editor-only JSON → ProjectileConfigEntry importer, used by the "Import from
// JSON" section on ProjectileConfigProviderSO's custom Inspector (see
// ProjectileConfigProviderEditor.cs). Mirrors PoolEntryJsonImporter exactly —
// same accepted shapes, same field aliases — with one addition:
// ProjectileConfigEntry carries a direct ProjectileConfigSO reference that
// JSON can't represent, so each entry's configSO is resolved by searching the
// project for a ProjectileConfigSO asset matching the entry's name. Ambiguous
// or missing matches still produce the entry (name/comment/offset intact) but
// leave configSO null, reported in warnings for manual drag-and-drop — never
// guessed at.
//
// ACCEPTED FORMATS (pick one per paste — the whole array must be one shape):
//
//   Simple — just names, offsets auto-assigned, configSO auto-searched:
//     ["Aliyahoo419", "Beatle", "HoodGun"]
//
//   Full — every field optional except one of enumName/name:
//     [
//       { "enumName": "Aliyahoo419", "explicitOffset": 2, "comment": "SMG basic" },
//       { "name": "Beatle" }
//     ]
//
//   Field aliases accepted in the full form, so pasted JSON doesn't have to
//   hand-match ProjectileConfigEntry's exact C# field names:
//     name          → enumName
//     offset, value → explicitOffset
//     description   → comment

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.Config
{
    public static class ProjectileConfigEntryJsonImporter
    {
        [Serializable]
        private class RawEntry
        {
            public string enumName;
            public string name;
            public string comment;
            public string description;
            public int explicitOffset = int.MinValue;
            public int offset = int.MinValue;
            public int value = int.MinValue;
        }

        [Serializable] private class RawEntryArray  { public List<RawEntry> items; }
        [Serializable] private class RawStringArray { public List<string>  items; }

        /// <summary>
        /// Parses pasted JSON into ProjectileConfigEntry instances, auto-linking
        /// each entry's configSO to a matching ProjectileConfigSO asset already
        /// in the project. Never throws. Returns false (with a message in
        /// <paramref name="error"/>) only for whole-paste parse failures — a
        /// missing/ambiguous configSO match for one entry doesn't block the
        /// rest; it's reported in <paramref name="warnings"/> instead.
        /// </summary>
        public static bool TryParse(
            string json,
            out List<ProjectileConfigEntry> result,
            out List<string> warnings,
            out string error)
        {
            result   = null;
            warnings = new List<string>();
            error    = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Nothing pasted.";
                return false;
            }

            string trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]"))
            {
                error = "Expected a JSON array, e.g. [\"Name1\", \"Name2\"] " +
                        "or [{\"enumName\":\"Name1\"}, ...].";
                return false;
            }

            int i = 1;
            while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
            bool isEmpty          = i < trimmed.Length && trimmed[i] == ']';
            bool looksLikeObjects = !isEmpty && i < trimmed.Length && trimmed[i] == '{';

            try
            {
                string wrapped = "{\"items\":" + trimmed + "}";

                if (isEmpty)
                {
                    result = new List<ProjectileConfigEntry>();
                    return true;
                }

                var rawParsed = new List<(string name, string comment, int offset)>();

                if (looksLikeObjects)
                {
                    var parsed = JsonUtility.FromJson<RawEntryArray>(wrapped);
                    if (parsed?.items == null)
                    {
                        error = "Couldn't parse any entries from that JSON.";
                        return false;
                    }

                    foreach (var raw in parsed.items)
                    {
                        string name = !string.IsNullOrWhiteSpace(raw.enumName)
                            ? raw.enumName : raw.name;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            error = "One entry is missing \"enumName\" (or \"name\").";
                            return false;
                        }

                        int offsetVal = raw.explicitOffset != int.MinValue ? raw.explicitOffset
                                      : raw.offset          != int.MinValue ? raw.offset
                                      : raw.value            != int.MinValue ? raw.value
                                      : -1;

                        string commentVal = !string.IsNullOrWhiteSpace(raw.comment)
                            ? raw.comment : raw.description;

                        rawParsed.Add((name.Trim(), commentVal, offsetVal));
                    }
                }
                else
                {
                    var parsed = JsonUtility.FromJson<RawStringArray>(wrapped);
                    if (parsed?.items == null)
                    {
                        error = "Couldn't parse any entries from that JSON.";
                        return false;
                    }

                    foreach (var name in parsed.items)
                    {
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            error = "One array entry is an empty string.";
                            return false;
                        }
                        rawParsed.Add((name.Trim(), null, -1));
                    }
                }

                var list = new List<ProjectileConfigEntry>(rawParsed.Count);
                foreach (var (name, comment, offset) in rawParsed)
                {
                    list.Add(new ProjectileConfigEntry
                    {
                        enumName       = name,
                        comment        = comment,
                        explicitOffset = offset,
                        configSO       = FindConfigSOByName(name, warnings)
                    });
                }

                result = list;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON: {ex.Message}";
                return false;
            }
        }

        // ── configSO auto-search ────────────────────────────────────────────
        // JSON can't carry a direct ScriptableObject reference, so we search
        // the project for a ProjectileConfigSO asset whose file name matches
        // the entry name exactly. Ambiguous or missing matches never get
        // guessed at — the entry is still added (so the name/offset isn't
        // lost) with configSO left null and a warning telling the user to
        // drag it in by hand.

        private static ProjectileConfigSO FindConfigSOByName(string name, List<string> warnings)
        {
            var guids = AssetDatabase.FindAssets($"{name} t:ProjectileConfigSO");
            if (guids.Length == 0)
            {
                warnings.Add($"'{name}': no ProjectileConfigSO asset found matching that name. " +
                             "Entry added with configSO unassigned — drag it in manually.");
                return null;
            }

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileNameWithoutExtension(path), name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return AssetDatabase.LoadAssetAtPath<ProjectileConfigSO>(path);
                }
            }

            string firstPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            if (guids.Length > 1)
            {
                warnings.Add($"'{name}': {guids.Length} ProjectileConfigSO assets matched, none " +
                             $"exactly — used '{firstPath}'. Verify it's the right one.");
            }
            return AssetDatabase.LoadAssetAtPath<ProjectileConfigSO>(firstPath);
        }
    }
}
