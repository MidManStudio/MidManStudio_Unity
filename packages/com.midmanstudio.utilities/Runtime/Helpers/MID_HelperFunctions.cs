// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "MID_HelperFunctions.cs"
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.HelperFunctions
{
    /// <summary>
    /// Static helpers shared by the MidManStudio packages: log shims, child destruction, CanvasGroup toggling,
    /// hex colour parsing, string case conversion, reflection-based debug printing, and JSON / XML wrappers.
    /// The list helpers (Map, Filter, Reduce, GroupBy) are in <see cref="MID_HelperFunctionsWithType{T}"/>.
    /// </summary>
    public  static partial class MID_HelperFunctions
    {
        // ── Logging shims ─────────────────────────────────────────────────────
        // Kept for API compatibility with existing callers. Each shim forwards to MID_Logger with a FIXED
        // caller level (Debug, Info, Error, Error, Verbose), so every call clears its own level gate and is
        // always written. A MID_LogLevel cannot silence them. Guard the call yourself, or call MID_Logger
        // directly with your own level field.

        /// <summary>Writes a [DEBUG] line through <see cref="MID_Logger"/>. Always written, see the note above.</summary>
        public static void LogDebug(string message, string className = "", string methodName = "") =>
            MID_Logger.LogDebug(MID_LogLevel.Debug, message, className, methodName);

        /// <summary>Writes a console warning through <see cref="MID_Logger"/>. Always written, see the note above.</summary>
        public static void LogWarning(string message, string className = "", string methodName = "") =>
            MID_Logger.LogWarning(MID_LogLevel.Info, message, className, methodName);

        /// <summary>Writes a console error through <see cref="MID_Logger"/>, with the exception if one is given.</summary>
        public static void LogError(string message, string className = "", string methodName = "",
            Exception e = null) =>
            MID_Logger.LogError(MID_LogLevel.Error, message, className, methodName, e);

        /// <summary>Writes an exception through <see cref="MID_Logger"/>.</summary>
        public static void LogException(Exception e, string message = "Exception",
            string className = "", string methodName = "") =>
            MID_Logger.LogException(MID_LogLevel.Error, e, message, className, methodName);

        /// <summary>
        /// Writes a verbose line through <see cref="MID_Logger"/>. The call is removed at compile time unless the
        /// <c>ENABLE_VERBOSE_LOGS</c> symbol is defined in the assembly that makes the call.
        /// </summary>
        [System.Diagnostics.Conditional("ENABLE_VERBOSE_LOGS")]
        public static void LogVerbose(string message, string className = "", string methodName = "") =>
            MID_Logger.LogVerbose(MID_LogLevel.Verbose, message, className, methodName);

        // ── GameObject helpers ────────────────────────────────────────────────

        /// <summary>
        /// Destroys all children of a transform with <c>Object.Destroy</c>. The children are removed at the end of
        /// the frame, so <c>childCount</c> still counts them until then.
        /// </summary>
        public static void DestroyObjChildren(Transform holder)
        {
            for (int i = holder.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(holder.GetChild(i).gameObject);
        }

        /// <summary>Destroy all children of multiple transforms.</summary>
        public static void DestroyMultipleParentsChildren(List<Transform> holders)
        {
            foreach (var h in holders)
                DestroyObjChildren(h);
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        /// <summary>Shows a CanvasGroup (alpha 1, interactable, blocks raycasts) or hides it (alpha 0, not interactable, no raycasts).</summary>
        public static void SetCanvasGroup(CanvasGroup cg, bool enable)
        {
            cg.alpha          = enable ? 1f : 0f;
            cg.interactable   = enable;
            cg.blocksRaycasts = enable;
        }

        /// <summary>Parses a colour string with Unity's <c>ColorUtility.TryParseHtmlString</c> (for example "#FF8800" or "red"). Returns white when it cannot be parsed.</summary>
        public static Color GetColorFromString(string hexColor) =>
            ColorUtility.TryParseHtmlString(hexColor, out var c) ? c : Color.white;

        // ── String formatting ─────────────────────────────────────────────────

        /// <summary>
        /// Lower-cases the whole string, then capitalises the first letter and the first letter after each
        /// '.', '!' or '?'. Everything else becomes lower case, so proper nouns and acronyms are lost, and a decimal
        /// point counts as a sentence end ("a 3.5 kg bag" becomes "A 3.5 Kg bag").
        /// </summary>
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

        /// <summary>
        /// Lower-cases the first letter of each space-separated word and keeps the spaces ("Hello World" becomes
        /// "hello world"). The spaces are not removed, so the result is not true camelCase.
        /// </summary>
        public static string ToCamelCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return string.Join(" ", input.Split(' ').Select(w =>
                w.Length > 0 ? char.ToLowerInvariant(w[0]) + w.Substring(1) : w));
        }

        /// <summary>
        /// Title-cases each space-separated word and joins them with no separator ("hello world" becomes
        /// "HelloWorld"). It uses the current culture, so the result can differ on devices set to some languages.
        /// </summary>
        public static string ToPascalCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var ti = CultureInfo.CurrentCulture.TextInfo;
            return string.Join("", input.Split(' ').Select(w => ti.ToTitleCase(w.ToLower())));
        }

        /// <summary>
        /// Lower-cases the string and puts '-' between words. A space starts a new word, and so does every
        /// upper-case letter that is not first in its word ("HelloWorld" becomes "hello-world", and "XMLParser"
        /// becomes "x-m-l-parser").
        /// </summary>
        public static string ToKebabCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return string.Join("-", input.Split(' ').Select(w =>
                string.Concat(w.Select((x, i) =>
                    char.IsUpper(x)
                        ? (i > 0 ? "-" : "") + char.ToLowerInvariant(x)
                        : x.ToString()))));
        }

        /// <summary>The same as <see cref="ToKebabCase"/> but with '_' between words.</summary>
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

        /// <summary>Returns false for null, empty, whitespace-only, or the exact text "null" in any case.</summary>
        public static bool IsStringValid(string val) =>
            !string.IsNullOrWhiteSpace(val) &&
            !val.Equals("null", StringComparison.OrdinalIgnoreCase);

        // ── Reflection debug helpers ──────────────────────────────────────────

        /// <summary>
        /// Returns a formatted string of all public instance fields and readable public properties of a struct or
        /// class, following nested objects and collections. Meant for logging plain data objects (primitives,
        /// strings, enums, nested plain classes and lists of them).
        /// There is no depth or cycle guard. Do not pass objects that reach Unity types such as Vector3, Color,
        /// Quaternion, Transform or GameObject, or a DateTime: their properties return values of their own type
        /// (for example <c>Vector3.normalized</c>), so the walk can recurse without end.
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
        /// Serialise to JSON using Unity's built-in JsonUtility, so its limits apply. Returns "{}" and logs an
        /// error when serialisation throws. For types that need full reflection support, handle serialisation
        /// yourself.
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
        /// Deserialise from JSON using Unity's built-in JsonUtility. Returns <c>default</c> and logs an error when
        /// parsing throws.
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

        /// <summary>
        /// Serialise to XML using System.Xml.Serialization. On failure it logs an error and returns the text
        /// "&lt;error&gt;Failed to serialize&lt;/error&gt;", so check for that before using the result.
        /// </summary>
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
        /// Cheap shape check: true when the trimmed text starts with '{' and ends with '}', or starts with '['
        /// and ends with ']'. It does not parse the JSON, so "{abc}" passes. Use it only as a quick filter before
        /// a real parse.
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

    /// <summary>
    /// Map / Filter / Reduce / GroupBy helpers for List&lt;T&gt;. The item type is on the class, so call it as
    /// <c>MID_HelperFunctionsWithType&lt;int&gt;.Map(list, x =&gt; x * 2)</c>.
    /// </summary>
    public static partial class MID_HelperFunctionsWithType<T>
    {
        /// <summary>Returns a new list holding <paramref name="fn"/> applied to every item.</summary>
        public static List<U> Map<U>(List<T> items, Func<T, U> fn)
        {
            var result = new List<U>(items.Count);
            foreach (var item in items) result.Add(fn(item));
            return result;
        }

        /// <summary>Returns a new list of the items for which <paramref name="pred"/> is true.</summary>
        public static List<T> Filter(List<T> items, Predicate<T> pred) =>
            items.FindAll(pred);

        /// <summary>Folds the list into one value, starting from <paramref name="seed"/>.</summary>
        public static U Reduce<U>(List<T> items, U seed, Func<U, T, U> fn)
        {
            var acc = seed;
            foreach (var item in items) acc = fn(acc, item);
            return acc;
        }

        /// <summary>Groups the items by key. Items keep their order inside each group. A null key throws, because it is a Dictionary key.</summary>
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

        /// <summary>True when at least one item matches. False for an empty list.</summary>
        public static bool AnyMatch(List<T> items, Predicate<T> pred) => items.Exists(pred);

        /// <summary>True when every item matches. True for an empty list.</summary>
        public static bool AllMatch(List<T> items, Predicate<T> pred) => items.TrueForAll(pred);

        /// <summary>Writes the list to the console with <c>Debug.Log</c> (not through MID_Logger, so no level gate applies).</summary>
        public static void PrintValues(List<T> items)
        {
            var sb = new StringBuilder($"List<{typeof(T).Name}> ({items.Count}):\n");
            foreach (var v in items) sb.AppendLine($"  {v}");
            Debug.Log(sb.ToString());
        }
    }
}
