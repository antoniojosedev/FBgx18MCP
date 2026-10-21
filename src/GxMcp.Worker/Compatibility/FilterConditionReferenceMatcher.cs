using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Pure lexical matcher that decides whether a standard WorkWith filter condition
    /// references the variable of a filter attribute (issue #586).
    ///
    /// A filter attribute named <c>CustomerName</c> materializes as the variable
    /// <c>&amp;CustomerName</c>; the attribute written without <c>&amp;</c> in a
    /// condition (<c>CustomerName like &amp;CustomerName</c>) is the KB attribute, which
    /// removing the filter attribute does not touch. The matcher therefore reports a
    /// reference only for a whole <c>&amp;Identifier</c> token outside string literals
    /// and comments, and reports everything else it noticed (bare identifiers, member
    /// accesses such as <c>&amp;Customer.Name</c>, literal and comment text) as
    /// incidental mentions so a caller can still see them.
    ///
    /// It is conservative in the safe direction: text it cannot analyze (an
    /// unterminated literal or comment, a dangling <c>&amp;</c>, or XML entity text
    /// that suggests an unescaped value) is <see cref="ReferenceVerdict.Unanalyzable"/>,
    /// which callers must treat as a reference.
    /// </summary>
    internal static class FilterConditionReferenceMatcher
    {
        internal enum ReferenceVerdict
        {
            NotReferenced,
            Referenced,
            Unanalyzable
        }

        internal sealed class ReferenceAnalysis
        {
            public ReferenceVerdict Verdict;
            public string Reason;
            public List<string> IncidentalMentions = new List<string>();

            /// True when the condition must block an unlisted removal: a real
            /// reference or text the matcher could not analyze (fail-closed).
            public bool BlocksRemoval => Verdict != ReferenceVerdict.NotReferenced;
        }

        private static readonly string[] XmlEntityMarkers = { "&amp;", "&lt;", "&gt;", "&quot;", "&apos;" };

        /// Strips one leading `&` and surrounding whitespace so `&CustomerName` and
        /// `CustomerName` name the same filter attribute.
        internal static string NormalizeAttributeName(string attributeName)
        {
            string name = attributeName?.Trim() ?? string.Empty;
            return name.StartsWith("&", StringComparison.Ordinal) ? name.Substring(1) : name;
        }

        internal static ReferenceAnalysis Analyze(string condition, string attributeName)
        {
            string name = NormalizeAttributeName(attributeName);
            var analysis = new ReferenceAnalysis { Verdict = ReferenceVerdict.NotReferenced };

            if (string.IsNullOrWhiteSpace(name))
            {
                analysis.Verdict = ReferenceVerdict.Unanalyzable;
                analysis.Reason = "the filter attribute name is empty";
                return analysis;
            }

            if (string.IsNullOrWhiteSpace(condition))
                return analysis;

            foreach (var marker in XmlEntityMarkers)
            {
                if (condition.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    analysis.Verdict = ReferenceVerdict.Unanalyzable;
                    analysis.Reason = $"the condition text contains the XML entity '{marker}', so variable references cannot be identified reliably";
                    return analysis;
                }
            }

            int i = 0;
            int length = condition.Length;
            while (i < length)
            {
                char c = condition[i];

                if (c == '\'' || c == '"')
                {
                    int close = condition.IndexOf(c, i + 1);
                    if (close < 0)
                    {
                        analysis.Verdict = ReferenceVerdict.Unanalyzable;
                        analysis.Reason = $"unterminated string literal starting at position {i}";
                        return analysis;
                    }
                    string literal = condition.Substring(i + 1, close - i - 1);
                    if (literal.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        analysis.IncidentalMentions.Add($"'{name}' appears inside the string literal {c}{literal}{c} (not a variable reference)");
                    i = close + 1;
                    continue;
                }

                if (c == '/' && i + 1 < length && condition[i + 1] == '/')
                {
                    int end = condition.IndexOfAny(new[] { '\r', '\n' }, i);
                    string comment = end < 0 ? condition.Substring(i) : condition.Substring(i, end - i);
                    if (comment.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        analysis.IncidentalMentions.Add($"'{name}' appears inside the line comment \"{comment.Trim()}\" (not a variable reference)");
                    if (end < 0) break;
                    i = end;
                    continue;
                }

                if (c == '/' && i + 1 < length && condition[i + 1] == '*')
                {
                    int close = condition.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        analysis.Verdict = ReferenceVerdict.Unanalyzable;
                        analysis.Reason = $"unterminated block comment starting at position {i}";
                        return analysis;
                    }
                    string comment = condition.Substring(i, close + 2 - i);
                    if (comment.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        analysis.IncidentalMentions.Add($"'{name}' appears inside the comment \"{comment}\" (not a variable reference)");
                    i = close + 2;
                    continue;
                }

                if (c == '&')
                {
                    int start = i + 1;
                    int end = ReadIdentifier(condition, start);
                    if (end == start)
                    {
                        analysis.Verdict = ReferenceVerdict.Unanalyzable;
                        analysis.Reason = $"'&' at position {i} is not followed by an identifier";
                        return analysis;
                    }
                    string identifier = condition.Substring(start, end - start);
                    if (string.Equals(identifier, name, StringComparison.OrdinalIgnoreCase))
                    {
                        analysis.Verdict = ReferenceVerdict.Referenced;
                        analysis.Reason = $"references the variable &{identifier}";
                        return analysis;
                    }
                    i = end;
                    continue;
                }

                if (c == '.')
                {
                    int start = i + 1;
                    int end = ReadIdentifier(condition, start);
                    if (end > start)
                    {
                        string member = condition.Substring(start, end - start);
                        if (string.Equals(member, name, StringComparison.OrdinalIgnoreCase))
                            analysis.IncidentalMentions.Add($"'.{member}' is a member access, not the variable &{name}");
                        i = end;
                        continue;
                    }
                    i++;
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    int end = ReadIdentifier(condition, i);
                    string identifier = condition.Substring(i, end - i);
                    if (string.Equals(identifier, name, StringComparison.OrdinalIgnoreCase))
                        analysis.IncidentalMentions.Add($"'{identifier}' appears as a bare identifier (an attribute or object reference, not the variable &{name})");
                    i = end;
                    continue;
                }

                if (char.IsDigit(c))
                {
                    // A numeric literal may be followed by identifier characters in
                    // malformed text; consume the whole run so `1Name` is never read
                    // as the identifier `Name`.
                    i = ReadIdentifier(condition, i, allowLeadingDigit: true);
                    continue;
                }

                i++;
            }

            return analysis;
        }

        private static bool IsIdentifierStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdentifierPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static int ReadIdentifier(string text, int start, bool allowLeadingDigit = false)
        {
            int i = start;
            if (i < text.Length && (IsIdentifierStart(text[i]) || (allowLeadingDigit && char.IsDigit(text[i]))))
            {
                i++;
                while (i < text.Length && IsIdentifierPart(text[i])) i++;
            }
            return i;
        }
    }
}
