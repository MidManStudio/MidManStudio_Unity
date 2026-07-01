// Pure C# fuzzy name matcher — no external dependency.
// Composite of normalized Levenshtein similarity, camelCase/underscore token overlap
// (Jaccard), and a substring-containment bonus. Used to disambiguate a field when
// more than one candidate component/GameObject of the right type is found.

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MidManStudio.Core.AutoReference
{
    public static class MID_NameMatcher
    {
        private const float LevenshteinWeight = 0.45f;
        private const float TokenWeight       = 0.35f;
        private const float SubstringBonus    = 0.25f;

        /// <summary>Returns a 0–1 similarity score between a field name and a candidate's owning-object name.</summary>
        public static float Score(string fieldName, string candidateName)
        {
            if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(candidateName)) return 0f;

            string a = Normalize(fieldName);
            string b = Normalize(candidateName);
            if (a.Length == 0 || b.Length == 0) return 0f;
            if (a == b) return 1f;

            float levSim   = LevenshteinSimilarity(a, b);
            float tokenSim = TokenJaccard(fieldName, candidateName);
            float subBonus = (a.Contains(b) || b.Contains(a)) ? SubstringBonus : 0f;

            float score = (levSim * LevenshteinWeight) + (tokenSim * TokenWeight) + subBonus;
            return Mathf.Clamp01(score);
        }

        // Lowercase, letters/digits only — used for edit-distance + substring comparison.
        private static string Normalize(string s)
        {
            s = s.Replace("(Clone)", "");
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Splits on camelCase boundaries, underscores, spaces, dashes.
        // e.g. "titleText" -> ["title","text"], "hp_Bar" -> ["hp","bar"]
        private static List<string> Tokenize(string s)
        {
            s = s.Replace("(Clone)", "");
            var tokens  = new List<string>();
            var current = new StringBuilder();

            foreach (char c in s)
            {
                if (c == '_' || c == ' ' || c == '-')
                {
                    if (current.Length > 0) { tokens.Add(current.ToString().ToLowerInvariant()); current.Clear(); }
                    continue;
                }
                if (char.IsUpper(c) && current.Length > 0 && !char.IsUpper(current[current.Length - 1]))
                {
                    tokens.Add(current.ToString().ToLowerInvariant());
                    current.Clear();
                }
                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString().ToLowerInvariant());

            // Drop a lone leading Hungarian-style marker token, e.g. "m","Field" or "_","field"
            if (tokens.Count > 1 && (tokens[0] == "m" || tokens[0] == "_"))
                tokens.RemoveAt(0);

            return tokens;
        }

        private static float TokenJaccard(string a, string b)
        {
            var ta = new HashSet<string>(Tokenize(a));
            var tb = new HashSet<string>(Tokenize(b));
            if (ta.Count == 0 || tb.Count == 0) return 0f;

            int intersection = 0;
            foreach (var t in ta) if (tb.Contains(t)) intersection++;
            int union = ta.Count + tb.Count - intersection;
            return union == 0 ? 0f : (float)intersection / union;
        }

        private static float LevenshteinSimilarity(string a, string b)
        {
            int dist   = LevenshteinDistance(a, b);
            int maxLen = Mathf.Max(a.Length, b.Length);
            return maxLen == 0 ? 1f : 1f - ((float)dist / maxLen);
        }

        private static int LevenshteinDistance(string a, string b)
        {
            int n = a.Length, m = b.Length;
            var d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Mathf.Min(Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }
    }
}
