using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public class TypeResolution
    {
        public bool Recognized { get; set; }
        public string CanonicalType { get; set; }
        public int? Length { get; set; }
        public int? Decimals { get; set; }
        public string DomainName { get; set; }
        public string Suggestion { get; set; }
        public List<string> AcceptedList { get; set; }
    }

    public static class VariableTypeResolver
    {
        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Character", "Character" }, { "Char", "Character" }, { "String", "Character" }, { "VarChar", "Character" },
            { "Numeric", "Numeric" }, { "Number", "Numeric" }, { "Decimal", "Numeric" }, { "Int", "Numeric" }, { "Integer", "Numeric" },
            { "Boolean", "Boolean" }, { "Bool", "Boolean" },
            { "Date", "Date" },
            { "DateTime", "DateTime" }, { "Timestamp", "DateTime" },
            { "Time", "Time" },
            { "LongVarChar", "LongVarChar" }, { "Text", "LongVarChar" },
            { "Blob", "Blob" }, { "Binary", "Blob" },
            { "Image", "Image" },
            { "GUID", "GUID" }, { "Uuid", "GUID" }
        };

        private static readonly Regex TypeRegex = new Regex(@"^([A-Za-z]+)(?:\((\d+)(?:,(\d+))?\))?$", RegexOptions.Compiled);

        public static TypeResolution Resolve(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new TypeResolution { Recognized = false, Suggestion = "Character(40)", AcceptedList = GetAccepted() };

            input = input.Trim();
            if (input.StartsWith("&"))
                return new TypeResolution { Recognized = true, CanonicalType = "DomainReference", DomainName = input.Substring(1) };

            var m = TypeRegex.Match(input);
            if (!m.Success)
                return new TypeResolution { Recognized = false, Suggestion = SuggestClosest(input), AcceptedList = GetAccepted() };

            var typeWord = m.Groups[1].Value;
            string canonical;
            if (!Synonyms.TryGetValue(typeWord, out canonical))
                return new TypeResolution { Recognized = false, Suggestion = SuggestClosest(typeWord), AcceptedList = GetAccepted() };

            int? len = m.Groups[2].Success ? (int?)int.Parse(m.Groups[2].Value) : null;
            int? dec = m.Groups[3].Success ? (int?)int.Parse(m.Groups[3].Value) : null;
            return new TypeResolution { Recognized = true, CanonicalType = canonical, Length = len, Decimals = dec };
        }

        private static List<string> GetAccepted()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var v in Synonyms.Values)
            {
                if (seen.Add(v)) list.Add(v);
            }
            return list;
        }

        private static string SuggestClosest(string input)
        {
            string best = null;
            int bestDist = int.MaxValue;
            var lowered = input.ToLowerInvariant();
            foreach (var k in Synonyms.Keys)
            {
                int d = Levenshtein(lowered, k.ToLowerInvariant());
                if (d < bestDist)
                {
                    bestDist = d;
                    best = k;
                }
            }
            return best;
        }

        private static int Levenshtein(string a, string b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return dp[a.Length, b.Length];
        }
    }
}
