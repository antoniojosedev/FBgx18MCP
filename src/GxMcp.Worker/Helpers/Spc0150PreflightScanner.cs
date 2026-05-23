using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Detects the spc0150 build-time error pattern: attribute writes inside a
    /// <c>For each … endfor</c> block in a WebPanel's Events source.
    ///
    /// spc0150 fires at build time ("Attribute cannot be assigned in this context")
    /// when GeneXus compiles a WebPanel event that writes to a transaction attribute
    /// (no leading <c>&amp;</c>) inside a For each/endfor loop.  The write succeeds at
    /// MCP time (the SDK accepts the text) but the KB build will fail.
    ///
    /// Detection heuristic (line-by-line, case-insensitive):
    ///   • Track nesting depth by counting <c>for each</c> / <c>endfor</c> lines.
    ///   • Inside any depth &gt; 0, flag lines that match <c>AttrName = …</c> where the
    ///     left-hand side has no leading <c>&amp;</c> (i.e., not a variable).
    ///   • Variable assignments (<c>&amp;Foo = …</c>) are intentionally excluded.
    ///   • False-positive risk is low: attribute names without &amp; inside For each
    ///     bodies are almost always transaction attributes.
    /// </summary>
    public static class Spc0150PreflightScanner
    {
        public sealed class Finding
        {
            public int Line;        // 1-based
            public string Text;     // trimmed source line
        }

        // Matches "For each" at the start of a line (ignoring leading whitespace).
        private static readonly Regex _rxForEach = new Regex(
            @"^\s*for\s+each\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches "endfor" at the start of a line (ignoring leading whitespace).
        private static readonly Regex _rxEndFor = new Regex(
            @"^\s*endfor\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches a bare attribute assignment: identifier (no &) followed by = (not ==).
        // Excludes lines that start with & (variable assignments) and // (comments).
        private static readonly Regex _rxAttrAssign = new Regex(
            @"^\s*(?!&)(?!//)([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)",
            RegexOptions.Compiled);

        /// <summary>
        /// Scans GeneXus Events source text for attribute assignments inside
        /// <c>For each … endfor</c> blocks. Returns all detected findings.
        /// </summary>
        public static List<Finding> Scan(string source)
        {
            var findings = new List<Finding>();
            if (string.IsNullOrEmpty(source)) return findings;

            int depth = 0;
            int lineNum = 0;
            foreach (var rawLine in source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                lineNum++;
                string line = rawLine;

                if (_rxForEach.IsMatch(line))
                {
                    depth++;
                    continue;
                }

                if (_rxEndFor.IsMatch(line))
                {
                    if (depth > 0) depth--;
                    continue;
                }

                if (depth > 0 && _rxAttrAssign.IsMatch(line))
                {
                    findings.Add(new Finding { Line = lineNum, Text = line.Trim() });
                }
            }

            return findings;
        }
    }
}
