using System;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>One side of a revision comparison: its metadata and rendered content.</summary>
    internal sealed class RevisionDiffSide
    {
        public RevisionDiffSide(JObject info, string content)
        {
            Info = info ?? new JObject();
            Content = content ?? string.Empty;
        }

        public JObject Info { get; }
        public string Content { get; }
        public string Representation => Info["representation"]?.ToString();
    }

    /// <summary>
    /// Issue #710 — the pure part of <c>history_diff</c>: normalises both sides the same
    /// way, computes the unified diff and builds the envelope. No SDK access.
    /// </summary>
    internal static class RevisionDiffEnvelope
    {
        internal const string PartXml = "part-xml";
        internal const string VisualXml = "visual-xml";
        internal const string MaskedGuidId = "{guid}";

        /// <summary>Upper bound for the LCS table of <see cref="DiffBuilder.UnifiedDiff"/> (cells).</summary>
        internal const long MaxCells = 16_000_000;

        /// <summary>
        /// Serialized XML is re-indented so a one-line SDK serialization still produces a
        /// readable line diff; when either side is not well-formed XML both sides are compared
        /// verbatim. The SDK rebuilds the editable WebForm document on every read and assigns
        /// fresh GUID ids to layout/table/row/cell nodes, so two reads of the same revision
        /// differ only in those ids: they are masked on both sides of a visual diff.
        /// </summary>
        internal static string Build(string target, string part, RevisionDiffSide from, RevisionDiffSide to, int contextLines)
        {
            if (contextLines <= 0) contextLines = 3;
            string before = from.Content;
            string after = to.Content;
            string normalization = "none";
            int maskedIds = 0;
            bool maskGuidIds = from.Representation == VisualXml && to.Representation == VisualXml;
            if (IsXml(from.Representation) && IsXml(to.Representation)
                && TryIndentXml(before, maskGuidIds, out string indentedBefore, out int maskedBefore)
                && TryIndentXml(after, maskGuidIds, out string indentedAfter, out int maskedAfter))
            {
                before = indentedBefore;
                after = indentedAfter;
                maskedIds = maskedBefore + maskedAfter;
                normalization = maskGuidIds ? "xml-indented,guid-ids-masked" : "xml-indented";
            }

            string normalizedBefore = before.Replace("\r\n", "\n");
            string normalizedAfter = after.Replace("\r\n", "\n");
            int beforeLines = normalizedBefore.Split('\n').Length;
            int afterLines = normalizedAfter.Split('\n').Length;
            bool identical = string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal);

            var result = new JObject
            {
                ["part"] = part,
                ["from"] = from.Info,
                ["to"] = to.Info,
                ["identical"] = identical,
                ["normalization"] = normalization,
                ["maskedGuidIds"] = maskedIds,
                ["beforeLines"] = beforeLines,
                ["afterLines"] = afterLines,
                ["context"] = contextLines
            };

            if (!identical && (long)(beforeLines + 1) * (afterLines + 1) > MaxCells)
                return TooLarge(target, part, from, result, beforeLines, afterLines);

            result["diff"] = identical ? string.Empty : DiffBuilder.UnifiedDiff(before, after, contextLines);
            return Models.McpResponse.Ok(target: target, code: "VersionDiffComputed", result: result);
        }

        private static string TooLarge(string target, string part, RevisionDiffSide from, JObject result, int beforeLines, int afterLines)
        {
            return Models.McpResponse.Err(
                code: "VersionDiffTooLarge",
                message: "Part '" + part + "' is too large for an in-process line diff (" + beforeLines + " x " + afterLines + " lines).",
                hint: "Read each side with history_get and compare them outside the MCP.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_versioning",
                    args: new JObject { ["action"] = "history_get", ["name"] = target, ["part"] = part, ["versionId"] = from.Info["versionId"] },
                    why: "Returns the full content of the base revision.")),
                target: target,
                extra: result);
        }

        private static bool IsXml(string representation)
            => representation == PartXml || representation == VisualXml;

        private static bool TryIndentXml(string xml, bool maskGuidIds, out string indented, out int maskedCount)
        {
            indented = null;
            maskedCount = 0;
            if (string.IsNullOrWhiteSpace(xml)) return false;
            try
            {
                var document = XDocument.Parse(xml, LoadOptions.None);
                if (maskGuidIds)
                    maskedCount = MaskGuidIds(document);
                indented = document.ToString();
                return true;
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }
        }

        private static int MaskGuidIds(XDocument document)
        {
            int masked = 0;
            foreach (var attribute in document.Descendants().SelectMany(e => e.Attributes())
                .Where(a => string.Equals(a.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase)))
            {
                if (!Guid.TryParse(attribute.Value, out _)) continue;
                attribute.Value = MaskedGuidId;
                masked++;
            }
            return masked;
        }
    }
}
