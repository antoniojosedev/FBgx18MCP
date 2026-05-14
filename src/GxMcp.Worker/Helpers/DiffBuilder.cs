using System.Collections.Generic;
using System.Linq;
using System.Text;

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
    }
}
