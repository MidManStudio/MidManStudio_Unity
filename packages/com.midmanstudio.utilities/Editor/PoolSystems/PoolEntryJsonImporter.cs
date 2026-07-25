// Editor-only JSON → PoolEntryDefinition importer, used by the "Import from
// JSON" section on ObjectPoolTypeProviderSO / ParticlePoolTypeProviderSO's
// custom Inspector (see PoolTypeProviderEditor.cs). Pure parsing logic, no
// UI — kept separate so it's easy to read/extend on its own.
//
// ACCEPTED FORMATS (pick one per paste — the whole array must be one shape):
//
//   Simple — just names, offsets auto-assigned by the generator:
//     ["BulletShell", "MuzzleFlash", "GrenadeBlueprint"]
//
//   Full — every field optional except one of entryName/name:
//     [
//       { "entryName": "BulletShell", "explicitOffset": 2, "comment": "9mm casing" },
//       { "name": "MuzzleFlash" }
//     ]
//
//   Field aliases accepted in the full form, so pasted JSON doesn't have to
//   hand-match PoolEntryDefinition's exact C# field names:
//     name          → entryName
//     offset, value → explicitOffset
//     description   → comment

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Core.Pools.Generator
{
    public static class PoolEntryJsonImporter
    {
        [Serializable]
        private class RawEntry
        {
            public string entryName;
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
        /// Parses pasted JSON into PoolEntryDefinition instances. Returns false
        /// (with a human-readable message in <paramref name="error"/>) for
        /// anything that isn't valid JSON or isn't one of the accepted shapes.
        /// Never throws.
        /// </summary>
        public static bool TryParse(
            string json, out List<PoolEntryDefinition> result, out string error)
        {
            result = null;
            error  = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Nothing pasted.";
                return false;
            }

            string trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]"))
            {
                error = "Expected a JSON array, e.g. [\"Name1\", \"Name2\"] " +
                        "or [{\"entryName\":\"Name1\"}, ...].";
                return false;
            }

            // JsonUtility can't deserialize a bare top-level array, so wrap it
            // in an object first ({"items": <array>}) — the standard
            // JsonUtility workaround. Element shape is sniffed from the first
            // non-whitespace character after the opening bracket; an empty
            // array ("[]" or "[ ]") is valid and always parses as zero entries.
            int i = 1;
            while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
            bool isEmpty         = i < trimmed.Length && trimmed[i] == ']';
            bool looksLikeObjects = !isEmpty && i < trimmed.Length && trimmed[i] == '{';

            try
            {
                string wrapped = "{\"items\":" + trimmed + "}";

                if (isEmpty)
                {
                    result = new List<PoolEntryDefinition>();
                    return true;
                }

                if (looksLikeObjects)
                {
                    var parsed = JsonUtility.FromJson<RawEntryArray>(wrapped);
                    if (parsed?.items == null)
                    {
                        error = "Couldn't parse any entries from that JSON.";
                        return false;
                    }

                    var list = new List<PoolEntryDefinition>(parsed.items.Count);
                    foreach (var raw in parsed.items)
                    {
                        string name = !string.IsNullOrWhiteSpace(raw.entryName)
                            ? raw.entryName : raw.name;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            error = "One entry is missing \"entryName\" (or \"name\").";
                            return false;
                        }

                        int offsetVal = raw.explicitOffset != int.MinValue ? raw.explicitOffset
                                      : raw.offset          != int.MinValue ? raw.offset
                                      : raw.value           != int.MinValue ? raw.value
                                      : -1;

                        string commentVal = !string.IsNullOrWhiteSpace(raw.comment)
                            ? raw.comment : raw.description;

                        list.Add(new PoolEntryDefinition
                        {
                            entryName      = name.Trim(),
                            comment        = commentVal,
                            explicitOffset = offsetVal
                        });
                    }
                    result = list;
                    return true;
                }
                else
                {
                    var parsed = JsonUtility.FromJson<RawStringArray>(wrapped);
                    if (parsed?.items == null)
                    {
                        error = "Couldn't parse any entries from that JSON.";
                        return false;
                    }

                    var list = new List<PoolEntryDefinition>(parsed.items.Count);
                    foreach (var name in parsed.items)
                    {
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            error = "One array entry is an empty string.";
                            return false;
                        }
                        list.Add(new PoolEntryDefinition
                        {
                            entryName      = name.Trim(),
                            comment        = null,
                            explicitOffset = -1
                        });
                    }
                    result = list;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON: {ex.Message}";
                return false;
            }
        }
    }
}
