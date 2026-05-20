using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Compares the KB state produced by genexus_apply_pattern against the state produced
    /// by the IDE's "Right-click → Apply Pattern" on an equivalent target. The comparison
    /// is dimension-scoped (not byte-by-byte) because GeneXus serializers introduce
    /// nondeterministic attribute ordering and whitespace.
    ///
    /// Five dimensions, each PASS/FAIL independently:
    ///   1. Generated family (set of object names + types) — must match 1:1.
    ///   2. PatternInstance XML — XCanonical normalize then deep-equal.
    ///   3. WebForm projected onto the parent — same normalization.
    ///   4. Variables — set-equal on (name, type).
    ///   5. Rules — line-set equal after trim, normalized EOL, blank-line collapse.
    ///
    /// Used by the LiveKbFact integration test and by the future parity_report CLI.
    /// </summary>
    public static class PatternParityHarness
    {
        public sealed class ParityReport
        {
            public string McpRoot { get; set; }
            public string IdeRoot { get; set; }
            public List<DimensionResult> Dimensions { get; } = new List<DimensionResult>();

            public bool AllPass => Dimensions.All(d => d.Pass);

            public string ToMarkdown()
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Pattern parity report");
                sb.AppendLine();
                sb.AppendLine("- MCP root: `" + McpRoot + "`");
                sb.AppendLine("- IDE root: `" + IdeRoot + "`");
                sb.AppendLine("- Overall: " + (AllPass ? "PASS" : "FAIL"));
                sb.AppendLine();
                foreach (var d in Dimensions)
                {
                    sb.AppendLine("## " + d.Name + " — " + (d.Pass ? "PASS" : "FAIL"));
                    if (!string.IsNullOrEmpty(d.Detail))
                    {
                        sb.AppendLine();
                        sb.AppendLine("```");
                        sb.AppendLine(d.Detail.TrimEnd());
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        public sealed class DimensionResult
        {
            public string Name { get; set; }
            public bool Pass { get; set; }
            public string Detail { get; set; }
        }

        public sealed class FamilySnapshot
        {
            public List<(string Name, string Type)> Members { get; set; } = new List<(string, string)>();
            public string PatternInstanceXml { get; set; }
            public string WebFormXml { get; set; }
            public List<(string Name, string Type)> Variables { get; set; } = new List<(string, string)>();
            public string RulesSource { get; set; }
        }

        public static ParityReport Compare(FamilySnapshot mcp, FamilySnapshot ide, string mcpRoot, string ideRoot)
        {
            var report = new ParityReport { McpRoot = mcpRoot, IdeRoot = ideRoot };
            report.Dimensions.Add(CompareFamily(mcp.Members, ide.Members));
            report.Dimensions.Add(CompareXml("PatternInstance", mcp.PatternInstanceXml, ide.PatternInstanceXml));
            report.Dimensions.Add(CompareXml("WebForm", mcp.WebFormXml, ide.WebFormXml));
            report.Dimensions.Add(CompareVariables(mcp.Variables, ide.Variables));
            report.Dimensions.Add(CompareRules(mcp.RulesSource, ide.RulesSource));
            return report;
        }

        internal static DimensionResult CompareFamily(IList<(string, string)> mcpMembers, IList<(string, string)> ideMembers)
        {
            var mcpSet = new HashSet<string>(mcpMembers.Select(m => Key(m.Item1, m.Item2)), StringComparer.OrdinalIgnoreCase);
            var ideSet = new HashSet<string>(ideMembers.Select(m => Key(m.Item1, m.Item2)), StringComparer.OrdinalIgnoreCase);
            var onlyMcp = mcpSet.Except(ideSet, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyIde = ideSet.Except(mcpSet, StringComparer.OrdinalIgnoreCase).ToList();
            bool pass = onlyMcp.Count == 0 && onlyIde.Count == 0;
            string detail = pass
                ? "Both sides generated " + mcpSet.Count + " objects."
                : "Only in MCP: " + (onlyMcp.Count == 0 ? "(none)" : string.Join(", ", onlyMcp)) +
                  "\nOnly in IDE: " + (onlyIde.Count == 0 ? "(none)" : string.Join(", ", onlyIde));
            return new DimensionResult { Name = "Generated family", Pass = pass, Detail = detail };
        }

        internal static DimensionResult CompareXml(string label, string mcpXml, string ideXml)
        {
            string mcpN = NormalizeXml(mcpXml);
            string ideN = NormalizeXml(ideXml);
            bool pass = string.Equals(mcpN, ideN, StringComparison.Ordinal);
            string detail;
            if (pass)
            {
                detail = "Canonical XML matches (" + (mcpN?.Length ?? 0) + " chars).";
            }
            else if (string.IsNullOrEmpty(mcpN) || string.IsNullOrEmpty(ideN))
            {
                detail = "One side is empty. MCP len=" + (mcpN?.Length ?? 0) + ", IDE len=" + (ideN?.Length ?? 0);
            }
            else
            {
                detail = "Canonical XML diverges. First differing index: " + FirstDifferenceIndex(mcpN, ideN) +
                         "\nMCP excerpt: " + Excerpt(mcpN, 240) +
                         "\nIDE excerpt: " + Excerpt(ideN, 240);
            }
            return new DimensionResult { Name = label + " XML", Pass = pass, Detail = detail };
        }

        internal static DimensionResult CompareVariables(IList<(string, string)> mcpVars, IList<(string, string)> ideVars)
        {
            var mcpSet = new HashSet<string>(mcpVars.Select(v => Key(v.Item1, v.Item2)), StringComparer.OrdinalIgnoreCase);
            var ideSet = new HashSet<string>(ideVars.Select(v => Key(v.Item1, v.Item2)), StringComparer.OrdinalIgnoreCase);
            var onlyMcp = mcpSet.Except(ideSet, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyIde = ideSet.Except(mcpSet, StringComparer.OrdinalIgnoreCase).ToList();
            bool pass = onlyMcp.Count == 0 && onlyIde.Count == 0;
            string detail = pass
                ? "Both sides declare " + mcpSet.Count + " variables."
                : "Only in MCP: " + (onlyMcp.Count == 0 ? "(none)" : string.Join(", ", onlyMcp)) +
                  "\nOnly in IDE: " + (onlyIde.Count == 0 ? "(none)" : string.Join(", ", onlyIde));
            return new DimensionResult { Name = "Variables", Pass = pass, Detail = detail };
        }

        internal static DimensionResult CompareRules(string mcpRules, string ideRules)
        {
            var mcpLines = NormalizeRulesLines(mcpRules);
            var ideLines = NormalizeRulesLines(ideRules);
            var onlyMcp = mcpLines.Except(ideLines, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyIde = ideLines.Except(mcpLines, StringComparer.OrdinalIgnoreCase).ToList();
            bool pass = onlyMcp.Count == 0 && onlyIde.Count == 0;
            string detail = pass
                ? "Both sides declare " + mcpLines.Count + " rule lines."
                : "Only in MCP:\n  " + (onlyMcp.Count == 0 ? "(none)" : string.Join("\n  ", onlyMcp)) +
                  "\nOnly in IDE:\n  " + (onlyIde.Count == 0 ? "(none)" : string.Join("\n  ", onlyIde));
            return new DimensionResult { Name = "Rules", Pass = pass, Detail = detail };
        }

        private static string Key(string name, string type) => (type ?? "") + ":" + (name ?? "");

        // Canonicalize: parse, sort attributes alphabetically, strip insignificant whitespace.
        // Empty input → empty string. Bad XML → return the original trimmed string.
        internal static string NormalizeXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return "";
            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.None);
                CanonicalizeNode(doc.Root);
                return doc.ToString(SaveOptions.DisableFormatting);
            }
            catch
            {
                return xml.Trim();
            }
        }

        private static void CanonicalizeNode(XElement node)
        {
            if (node == null) return;
            var sortedAttrs = node.Attributes()
                .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
                .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
                .ToList();
            node.RemoveAttributes();
            foreach (var a in sortedAttrs) node.Add(a);
            foreach (var child in node.Elements()) CanonicalizeNode(child);
        }

        private static int FirstDifferenceIndex(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
            return n;
        }

        private static string Excerpt(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static HashSet<string> NormalizeRulesLines(string rules)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rules)) return set;
            foreach (var raw in rules.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                set.Add(line);
            }
            return set;
        }
    }
}
