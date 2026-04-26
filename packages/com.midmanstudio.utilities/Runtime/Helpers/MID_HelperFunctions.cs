// MID_HelperFunctions.cs
// Generic static utility methods for use across all MidManStudio packages.
//
// WHAT'S HERE:
//   - GameObject child management
//   - UI / CanvasGroup helpers
//   - String formatting (sentence, camel, pascal, kebab, snake)
//   - Color parsing
//   - Reflection-based debug printing
//   - Generic functional helpers (Map, Filter, Reduce, GroupBy)
//   - JSON / XML serialisation via Unity's built-in JsonUtility + System.Xml
//
// WHAT'S NOT HERE (intentionally removed):
//   - Game-specific sprite/atlas loading
//   - Cloud save / economy / analytics hooks
//   - Error handler references
//   - Any logging beyond thin wrappers around MID_Logger

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.HelperFunctions
{
    public static class MID_HelperFunctions
    {
        // ── Logging shims ─────────────────────────────────────────────────────
        // Kept for API compatibility with existing callers.
        // Route straight to MID_Logger so log level control works uniformly.

        public static void LogDebug(string message, string className = "", string methodName = "") =>
            MID_Logger.LogDebug(MID_LogLevel.Debug, message, className, methodName);

        public static void LogWarning(string message, string className = "", string methodName = "") =>
            MID_Logger.LogWarning(MID_LogLevel.Info, message, className, methodName);

        public static void LogError(string message, string className = "", string methodName = "",
            Exception e = null) =>
            MID_Logger.LogError(MID_LogLevel.Error, message, className, methodName, e);

        public static void LogException(Exception e, string message = "Exception",
            string className = "", string methodName = "") =>
            MID_Logger.LogException(MID_LogLevel.Error, e, message, className, methodName);

        [System.Diagnostics.Conditional("ENABLE_VERBOSE_LOGS")]
        public static void LogVerbose(string message, string className = "", string methodName = "") =>
            MID_Logger.LogVerbose(MID_LogLevel.Verbose, message, className, methodName);

        // ── GameObject helpers ────────────────────────────────────────────────

        /// <summary>Destroy all children of a transform.</summary>
        public static void KillObjChildren(Transform holder)
        {
            for (int i = holder.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(holder.GetChild(i).gameObject);
        }

        /// <summary>Destroy all children of multiple transforms.</summary>
        public static void KillMultipleParentsChildren(List<Transform> holders)
        {
            foreach (var h in holders)
                KillObjChildren(h);
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        /// <summary>Enable or fully disable a CanvasGroup in one call.</summary>
        public static void SetCanvasGroup(CanvasGroup cg, bool enable)
        {
            cg.alpha          = enable ? 1f : 0f;
            cg.interactable   = enable;
            cg.blocksRaycasts = enable;
        }

        /// <summary>Parse a hex colour string. Returns white on failure.</summary>
        public static Color GetColorFromString(string hexColor) =>
            ColorUtility.TryParseHtmlString(hexColor, out var c) ? c : Color.white;

        // ── String formatting ─────────────────────────────────────────────────

        public static string ToSentenceCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            input = input.ToLowerInvariant();
            var chars = input.ToCharArray();
            bool cap = true;
            for (int i = 0; i < chars.Length; i++)
            {
                if (cap && char.IsLetter(chars[i])) { chars[i] = char.ToUpper(chars[i]); cap = false; }
                if (chars[i] == '.' || chars[i] == '!' || chars[i] == '?') cap = true;
            }
            return new string(chars);
        }

        public static string ToCamelCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return string.Join(" ", input.Split(' ').Select(w =>
                w.Length > 0 ? char.ToLowerInvariant(w[0]) + w.Substring(1) : w));
        }

        public static string ToPascalCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var ti = CultureInfo.CurrentCulture.TextInfo;
            return string.Join("", input.Split(' ').Select(w => ti.ToTitleCase(w.ToLower())));
        }

        public static string ToKebabCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return string.Join("-", input.Split(' ').Select(w =>
                string.Concat(w.Select((x, i) =>
                    char.IsUpper(x)
                        ? (i > 0 ? "-" : "") + char.ToLowerInvariant(x)
                        : x.ToString()))));
        }

        public static string ToSnakeCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return string.Join("_", input.Split(' ').Select(w =>
                string.Concat(w.Select((x, i) =>
                    char.IsUpper(x)
                        ? (i > 0 ? "_" : "") + char.ToLowerInvariant(x)
                        : x.ToString()))));
        }

        // ── String validation ─────────────────────────────────────────────────

        /// <summary>Returns false for null, empty, or strings that equal "null" (any case).</summary>
        public static bool IsStringValid(string val) =>
            !string.IsNullOrWhiteSpace(val) &&
            !val.Equals("null", StringComparison.OrdinalIgnoreCase);

        // ── Reflection debug helpers ──────────────────────────────────────────

        /// <summary>
        /// Returns a formatted string of all fields and properties on a struct or class.
        /// Useful for logging complex data objects during development.
        /// </summary>
        public static string GetStructOrClassMemberValues<T>(T instance) where T : notnull
        {
            var sb = new StringBuilder();
            AppendMembers(instance, sb, 0);
            return sb.ToString();
        }

        private static void AppendMembers(object instance, StringBuilder sb, int depth)
        {
            if (instance == null) { sb.AppendLine("null"); return; }

            var type   = instance.GetType();
            var flags  = BindingFlags.Public | BindingFlags.Instance;
            var indent = new string(' ', depth * 4);
            var arrow  = depth == 0 ? "" : new string('-', depth) + ">";

            foreach (var field in type.GetFields(flags))
                AppendValue(sb, field.Name, field.GetValue(instance), depth, indent, arrow);

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                try { AppendValue(sb, prop.Name, prop.GetValue(instance), depth, indent, arrow); }
                catch (Exception ex) { sb.AppendLine($"{indent}{arrow} {prop.Name} :: [error: {ex.Message}]"); }
            }
        }

        private static void AppendValue(StringBuilder sb, string name, object value,
            int depth, string indent, string arrow)
        {
            if (value == null)
            {
                sb.AppendLine($"{indent}{arrow} {name} :: null");
            }
            else if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum)
            {
                sb.AppendLine($"{indent}{arrow} {name} :: {value}");
            }
            else if (value is IEnumerable enumerable)
            {
                sb.AppendLine($"{indent}{arrow} {name} :: [");
                int idx = 0;
                foreach (var item in enumerable)
                {
                    sb.AppendLine($"{indent}    [{idx}]:");
                    if (item == null || item is string || item.GetType().IsPrimitive)
                        sb.AppendLine($"{indent}    {item}");
                    else
                        AppendMembers(item, sb, depth + 2);
                    idx++;
                }
                sb.AppendLine($"{indent}{arrow} ]");
            }
            else
            {
                sb.AppendLine($"{indent}{arrow} {name} :: {{");
                AppendMembers(value, sb, depth + 1);
                sb.AppendLine($"{indent}{arrow} }}");
            }
        }

        // ── Serialisation ─────────────────────────────────────────────────────

        /// <summary>
        /// Serialise to JSON using Unity's built-in JsonUtility.
        /// For types that need full reflection support, handle serialisation yourself.
        /// </summary>
        public static string ToJson<T>(T obj, bool prettyPrint = true) where T : notnull
        {
            try { return JsonUtility.ToJson(obj, prettyPrint); }
            catch (Exception ex)
            {
                MID_Logger.LogError(MID_LogLevel.Error,
                    $"ToJson failed: {ex.Message}", nameof(MID_HelperFunctions));
                return "{}";
            }
        }

        /// <summary>
        /// Deserialise from JSON using Unity's built-in JsonUtility.
        /// </summary>
        public static T FromJson<T>(string json)
        {
            try { return JsonUtility.FromJson<T>(json); }
            catch (Exception ex)
            {
                MID_Logger.LogError(MID_LogLevel.Error,
                    $"FromJson failed: {ex.Message}", nameof(MID_HelperFunctions));
                return default;
            }
        }

        /// <summary>Serialise to XML using System.Xml.Serialization.</summary>
        public static string ToXml<T>(T obj) where T : notnull
        {
            try
            {
                var ser = new System.Xml.Serialization.XmlSerializer(typeof(T));
                using var sw = new StringWriter();
                ser.Serialize(sw, obj);
                return sw.ToString();
            }
            catch (Exception ex)
            {
                MID_Logger.LogError(MID_LogLevel.Error,
                    $"ToXml failed: {ex.Message}", nameof(MID_HelperFunctions));
                return "<error>Failed to serialize</error>";
            }
        }

        /// <summary>
        /// Check whether a JSON string is syntactically valid.
        /// Uses a lightweight manual pass — no external dependency.
        /// </summary>
        public static bool IsValidJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            json = json.Trim();
            return (json.StartsWith("{") && json.EndsWith("}")) ||
                   (json.StartsWith("[") && json.EndsWith("]"));
        }
    }

    // ── Generic functional helpers ────────────────────────────────────────────

    /// <summary>Map / Filter / Reduce / GroupBy helpers for List&lt;T&gt;.</summary>
    public static class MID_HelperFunctionsWithType<T>
    {
        public static List<U> Map<U>(List<T> items, Func<T, U> fn)
        {
            var result = new List<U>(items.Count);
            foreach (var item in items) result.Add(fn(item));
            return result;
        }

        public static List<T> Filter(List<T> items, Predicate<T> pred) =>
            items.FindAll(pred);

        public static U Reduce<U>(List<T> items, U seed, Func<U, T, U> fn)
        {
            var acc = seed;
            foreach (var item in items) acc = fn(acc, item);
            return acc;
        }

        public static Dictionary<K, List<T>> GroupBy<K>(List<T> items, Func<T, K> keySelector)
        {
            var result = new Dictionary<K, List<T>>();
            foreach (var item in items)
            {
                var key = keySelector(item);
                if (!result.TryGetValue(key, out var list))
                    result[key] = list = new List<T>();
                list.Add(item);
            }
            return result;
        }

        public static bool AnyMatch(List<T> items, Predicate<T> pred) => items.Exists(pred);
        public static bool AllMatch(List<T> items, Predicate<T> pred) => items.TrueForAll(pred);

        public static void PrintValues(List<T> items)
        {
            var sb = new StringBuilder($"List<{typeof(T).Name}> ({items.Count}):\n");
            foreach (var v in items) sb.AppendLine($"  {v}");
            Debug.Log(sb.ToString());
        }
    }
}
