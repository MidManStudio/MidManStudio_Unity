// Minimal, dependency-free JSON parser producing a generic object graph
// (Dictionary<string, object> / List<object> / string / double / bool / null).
// Exists because JsonUtility can only deserialize into a pre-declared C# class
// with matching field names — fine for a 3-field pool entry, not workable for
// a 40+ field asset like ProjectileConfigScriptableObject where the whole
// point is applying arbitrary keys generically via SerializedProperty.
//
// Not a general-purpose/spec-complete JSON parser (no \uXXXX escapes, no
// exponent-form number edge cases beyond the common ones) — just enough to
// reliably parse hand-written or LLM-generated config JSON.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MidManStudio.Projectiles.Editor
{
    public static class MiniJsonParser
    {
        public static object Parse(string json)
        {
            int i = 0;
            SkipWhitespace(json, ref i);
            var value = ParseValue(json, ref i);
            SkipWhitespace(json, ref i);
            if (i != json.Length)
                throw new FormatException($"Unexpected trailing content at position {i}.");
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true");  return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null");  return null;
                default:  return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>();
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == '}') { i++; return dict; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != ':') throw new FormatException($"Expected ':' at {i}.");
                i++;
                dict[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == '}') { i++; break; }
                throw new FormatException($"Expected ',' or '}}' at {i}.");
            }
            return dict;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == ']') { i++; return list; }

            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == ']') { i++; break; }
                throw new FormatException($"Expected ',' or ']' at {i}.");
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            if (Peek(s, i) != '"') throw new FormatException($"Expected '\"' at {i}.");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string.");
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("Unterminated escape.");
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'n':  sb.Append('\n'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'u':
                            string hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            break;
                        default: throw new FormatException($"Unknown escape '\\{esc}'.");
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (Peek(s, i) == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (Peek(s, i) == '.') { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
            if (Peek(s, i) == 'e' || Peek(s, i) == 'E')
            {
                i++;
                if (Peek(s, i) == '+' || Peek(s, i) == '-') i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            string num = s.Substring(start, i - start);
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                throw new FormatException($"Invalid number '{num}' at {start}.");
            return result;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new FormatException($"Expected '{literal}' at {i}.");
            i += literal.Length;
        }

        private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
