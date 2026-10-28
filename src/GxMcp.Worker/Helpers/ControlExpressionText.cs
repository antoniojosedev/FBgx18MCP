using System;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #711: the SDK parses a control's expression property (ControlWhere, shown by the IDE
    /// as Conditions) and stores it as a <c>&lt;TokenDataList&gt;</c>, so a plain-text request reads
    /// back as tokens. This reads either form as the expression it spells.
    /// </summary>
    internal static class ControlExpressionText
    {
        /// <summary>
        /// The expression text a <c>&lt;TokenDataList&gt;</c> value spells: the concatenation of
        /// its <c>TokenData/Word</c> values. False when the value is not a token list.
        /// </summary>
        internal static bool TryDecodeTokenList(string value, out string text)
        {
            text = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string trimmed = value.Trim();
            if (!trimmed.StartsWith("<", StringComparison.Ordinal)) return false;
            XElement root;
            // PreserveWhitespace is load-bearing: a spacing token is a whitespace-only Word,
            // which a default parse drops, gluing "A = 1" into "A=1".
            try { root = XDocument.Parse(trimmed, LoadOptions.PreserveWhitespace).Root; }
            catch (XmlException) { return false; }
            if (root == null || !string.Equals(root.Name.LocalName, "TokenDataList", StringComparison.Ordinal)) return false;
            var words = new StringBuilder();
            foreach (var token in root.Elements("TokenData"))
                words.Append(token.Element("Word")?.Value ?? string.Empty);
            text = words.ToString();
            return true;
        }

        /// <summary>
        /// An expression with each whitespace run outside a string literal collapsed to one
        /// space, then trimmed: the SDK keeps the caller's spacing tokens but appends a line
        /// break. Spacing inside a quoted literal is content and is kept as written.
        /// </summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var output = new StringBuilder(text.Length);
            char quote = '\0';
            bool pendingSpace = false;
            foreach (char c in text)
            {
                if (quote == '\0' && char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && output.Length > 0) output.Append(' ');
                pendingSpace = false;
                output.Append(c);
                if (quote == '\0' && (c == '"' || c == '\'')) quote = c;
                else if (quote != '\0' && c == quote) quote = '\0';
            }
            return output.ToString();
        }

        /// <summary>
        /// The expression a property value spells, whether stored as a token list or as plain
        /// text, normalized for comparison. Null for a null or empty value.
        /// </summary>
        internal static string Of(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return Normalize(TryDecodeTokenList(value, out string text) ? text : value);
        }

        /// <summary>
        /// True when at least one value is a token list and both spell the same expression. Two
        /// plain values are not compared here: they keep the caller's strict comparison.
        /// </summary>
        internal static bool SameTokenExpression(string a, string b)
        {
            if (!TryDecodeTokenList(a, out _) && !TryDecodeTokenList(b, out _)) return false;
            return string.Equals(Of(a) ?? string.Empty, Of(b) ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
