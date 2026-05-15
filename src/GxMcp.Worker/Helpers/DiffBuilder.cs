using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Minimal unified-diff producer using LCS (Longest Common Subsequence).
    /// context = number of unchanged lines to show before/after each hunk.
    /// </summary>
    public static class DiffBuilder
    {
        public static string UnifiedDiff(string before, string after, int context = 3)
        {
            var a = (before ?? "").Replace("\r\n", "\n").Split('\n');
            var b = (after ?? "").Replace("\r\n", "\n").Split('\n');
            var lcs = ComputeLcsLengths(a, b);
            var ops = WalkBack(a, b, lcs);
            return FormatUnified(a, b, ops, context);
        }

        private static int[,] ComputeLcsLengths(string[] a, string[] b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    dp[i, j] = a[i - 1] == b[j - 1]
                        ? dp[i - 1, j - 1] + 1
                        : System.Math.Max(dp[i - 1, j], dp[i, j - 1]);
            return dp;
        }

        private enum Op { Keep, Add, Remove }

        private static List<(Op op, int i, int j)> WalkBack(string[] a, string[] b, int[,] dp)
        {
            var ops = new List<(Op, int, int)>();
            int i = a.Length, j = b.Length;
            while (i > 0 || j > 0)
            {
                if (i > 0 && j > 0 && a[i - 1] == b[j - 1]) { ops.Add((Op.Keep, i - 1, j - 1)); i--; j--; }
                else if (j > 0 && (i == 0 || dp[i, j - 1] >= dp[i - 1, j])) { ops.Add((Op.Add, -1, j - 1)); j--; }
                else { ops.Add((Op.Remove, i - 1, -1)); i--; }
            }
            ops.Reverse();
            return ops;
        }

        private static string FormatUnified(string[] a, string[] b, List<(Op op, int i, int j)> ops, int context)
        {
            // Group consecutive non-Keep ops into hunks, expanding ±context unchanged lines.
            var sb = new StringBuilder();
            int idx = 0;
            while (idx < ops.Count)
            {
                // skip Keep until next change
                while (idx < ops.Count && ops[idx].op == Op.Keep) idx++;
                if (idx >= ops.Count) break;
                int hunkStart = System.Math.Max(0, idx - context);
                int hunkEnd = idx;
                // extend through changes + trailing context
                while (hunkEnd < ops.Count)
                {
                    if (ops[hunkEnd].op != Op.Keep) hunkEnd++;
                    else
                    {
                        // peek: if next change within context, include
                        int peek = hunkEnd;
                        while (peek < ops.Count && ops[peek].op == Op.Keep) peek++;
                        if (peek < ops.Count && peek - hunkEnd <= context) hunkEnd = peek;
                        else { hunkEnd = System.Math.Min(ops.Count, hunkEnd + context); break; }
                    }
                }
                // counts for header
                int aStart = -1, bStart = -1, aCount = 0, bCount = 0;
                for (int k = hunkStart; k < hunkEnd; k++)
                {
                    var (op, ai, bj) = ops[k];
                    if (op == Op.Keep || op == Op.Remove) { if (aStart < 0) aStart = ai; aCount++; }
                    if (op == Op.Keep || op == Op.Add) { if (bStart < 0) bStart = bj; bCount++; }
                }
                sb.Append("@@ -").Append(aStart + 1).Append(',').Append(aCount)
                  .Append(" +").Append(bStart + 1).Append(',').Append(bCount).Append(" @@\n");
                for (int k = hunkStart; k < hunkEnd; k++)
                {
                    var (op, ai, bj) = ops[k];
                    switch (op)
                    {
                        case Op.Keep: sb.Append(' ').Append(a[ai]).Append('\n'); break;
                        case Op.Add: sb.Append('+').Append(b[bj]).Append('\n'); break;
                        case Op.Remove: sb.Append('-').Append(a[ai]).Append('\n'); break;
                    }
                }
                idx = hunkEnd;
            }
            return sb.ToString();
        }

        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.2 — Byte-level nearMatchHint (friction-report #4)
        // ----------------------------------------------------------------------
        // When edit fails to find an exact context but a near window exists,
        // surface a structured hint pin-pointing the first divergence (EOL,
        // Whitespace, or Content) so the agent can fix context in one turn.

        public static JObject ByteLevelDivergence(string sourceWindow, string context)
        {
            sourceWindow = sourceWindow ?? string.Empty;
            context = context ?? string.Empty;
            var normSource = WriteService.NormalizeForCompare(sourceWindow) ?? string.Empty;
            var normCtx = WriteService.NormalizeForCompare(context) ?? string.Empty;

            double sim = ComputeSimilarity(normSource, normCtx);
            string divKind = ClassifyDivergence(sourceWindow, context, normSource, normCtx);

            int firstLine = 1, firstCol = 1;
            int min = System.Math.Min(normSource.Length, normCtx.Length);
            int diffIdx = -1;
            for (int i = 0; i < min; i++)
            {
                if (normSource[i] != normCtx[i]) { diffIdx = i; break; }
            }
            if (diffIdx < 0 && normSource.Length != normCtx.Length) diffIdx = min;
            if (diffIdx >= 0)
            {
                string scan = normSource.Length >= normCtx.Length ? normSource : normCtx;
                int newlines = 0;
                int lastNL = -1;
                for (int i = 0; i < diffIdx && i < scan.Length; i++)
                {
                    if (scan[i] == '\n') { newlines++; lastNL = i; }
                }
                firstLine = newlines + 1;
                firstCol = diffIdx - lastNL; // 1-based column within line
                if (firstCol < 1) firstCol = 1;
            }

            return new JObject
            {
                ["similarity"] = System.Math.Round(sim, 4),
                ["topWindow"] = new JObject
                {
                    ["contextNormalized"] = normCtx,
                    ["sourceWindowNormalized"] = normSource,
                    ["firstDivergenceAt"] = new JObject { ["line"] = firstLine, ["column"] = firstCol },
                    ["divergenceKind"] = divKind
                }
            };
        }

        private static string ClassifyDivergence(string src, string ctx, string normSrc, string normCtx)
        {
            if (normSrc == normCtx)
            {
                // Same after EOL+trailing-ws normalization. If only line endings differ → EOL,
                // otherwise some other whitespace (e.g. trailing spaces) was the differentiator.
                if (src.Replace("\r\n", "\n") == ctx.Replace("\r\n", "\n"))
                    return "EOL";
                return "Whitespace";
            }
            string stripWs(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (stripWs(normSrc) == stripWs(normCtx))
                return "Whitespace";
            return "Content";
        }

        private static double ComputeSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            int dist = LevenshteinDistance(a, b);
            int max = System.Math.Max(a.Length, b.Length);
            return 1.0 - (double)dist / max;
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (a == b) return 0;
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int del = prev[j] + 1;
                    int ins = cur[j - 1] + 1;
                    int sub = prev[j - 1] + cost;
                    cur[j] = System.Math.Min(System.Math.Min(del, ins), sub);
                }
                var tmp = prev; prev = cur; cur = tmp;
            }
            return prev[m];
        }
    }
}
