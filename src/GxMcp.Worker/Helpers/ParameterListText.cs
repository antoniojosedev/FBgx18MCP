using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>One <c>att:&lt;id&gt;</c> that an expression named and the KB did not resolve.</summary>
    internal sealed class UnresolvedAttributeReference
    {
        public int Index { get; set; }
        public int AttributeId { get; set; }
        public string Expression { get; set; }
        /// <summary>Why the lookup itself failed; null when it ran and found no such attribute.</summary>
        public string LookupFailure { get; set; }

        public string Message => LookupFailure == null
            ? "att:" + AttributeId + " does not resolve in this KB."
            : "att:" + AttributeId + " could not be looked up: " + LookupFailure;
    }

    /// <summary>
    /// The text form of a parameter list (a Data Provider's parameters, a call's arguments):
    /// <c>McpFilterA, &amp;Var, "text", Fn(a, b)</c>. The SDK's language engine owns the
    /// grammar, so this class only does what is needed to hand the engine a list and to give
    /// a caller one expression per entry: split at the top-level commas, join back, and turn
    /// <c>att:&lt;id&gt;</c> into the attribute name the engine tokenizes.
    /// </summary>
    internal static class ParameterListText
    {
        private static readonly Regex AttributeReference = new Regex(
            @"\batt:(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        /// <summary>
        /// The expressions of <paramref name="text"/>, trimmed, in order. A comma inside
        /// parentheses, brackets, braces or a quoted literal does not split. An empty text is
        /// an empty list; an empty entry between commas is kept as an empty string so the
        /// caller can refuse it instead of silently dropping a parameter.
        /// </summary>
        internal static List<string> Split(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var current = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    current.Append(c);
                    continue;
                }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == ']' || c == '}') && depth > 0) depth--;

                if (c == ',' && depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
                current.Append(c);
            }
            result.Add(current.ToString().Trim());
            return result;
        }

        internal static string Join(IEnumerable<string> expressions)
            => string.Join(", ", (expressions ?? Enumerable.Empty<string>()).Select(e => (e ?? string.Empty).Trim()));

        /// <summary>True when <paramref name="expression"/> is exactly one non-empty list entry.</summary>
        internal static bool IsSingleExpression(string expression)
        {
            var parts = Split(expression);
            return parts.Count == 1 && parts[0].Length > 0;
        }

        /// <summary>
        /// Replaces each <c>att:&lt;id&gt;</c> outside a quoted literal with the attribute's
        /// name, so the engine (which spells attributes by name) can tokenize the list. An id the
        /// KB does not know is reported, never guessed.
        /// </summary>
        internal static List<string> ResolveAttributeReferences(
            IReadOnlyList<string> expressions,
            Func<int, string> attributeNameById,
            out List<UnresolvedAttributeReference> unresolved)
        {
            unresolved = new List<UnresolvedAttributeReference>();
            var resolved = new List<string>(expressions.Count);
            for (int index = 0; index < expressions.Count; index++)
            {
                string expression = expressions[index] ?? string.Empty;
                var rebuilt = new StringBuilder();
                int cursor = 0;
                foreach (var segment in QuotedSegments(expression))
                {
                    rebuilt.Append(ReplaceOutsideQuotes(expression.Substring(cursor, segment.Start - cursor), index, expression, attributeNameById, unresolved));
                    rebuilt.Append(expression, segment.Start, segment.Length);
                    cursor = segment.Start + segment.Length;
                }
                rebuilt.Append(ReplaceOutsideQuotes(expression.Substring(cursor), index, expression, attributeNameById, unresolved));
                resolved.Add(rebuilt.ToString().Trim());
            }
            return resolved;
        }

        private static string ReplaceOutsideQuotes(
            string text, int index, string expression, Func<int, string> attributeNameById, List<UnresolvedAttributeReference> unresolved)
        {
            if (text.Length == 0) return text;
            return AttributeReference.Replace(text, match =>
            {
                int id;
                if (!int.TryParse(match.Groups[1].Value, out id))
                {
                    unresolved.Add(new UnresolvedAttributeReference { Index = index, AttributeId = -1, Expression = expression });
                    return match.Value;
                }
                string name = null;
                string lookupFailure = null;
                try { name = attributeNameById(id); }
                catch (Exception ex) { lookupFailure = (ex.InnerException ?? ex).Message; }
                if (string.IsNullOrWhiteSpace(name))
                {
                    unresolved.Add(new UnresolvedAttributeReference
                    {
                        Index = index, AttributeId = id, Expression = expression, LookupFailure = lookupFailure
                    });
                    return match.Value;
                }
                return name;
            });
        }

        private static readonly Regex VariableReference = new Regex(
            @"&([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        /// <summary>The <c>&amp;Variable</c> names an expression uses, outside quoted literals, in order and distinct.</summary>
        internal static List<string> ExtractVariableNames(string expression)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(expression)) return names;
            int cursor = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> scan = text =>
            {
                foreach (Match match in VariableReference.Matches(text))
                    if (seen.Add(match.Groups[1].Value)) names.Add(match.Groups[1].Value);
            };
            foreach (var segment in QuotedSegments(expression))
            {
                scan(expression.Substring(cursor, segment.Start - cursor));
                cursor = segment.Start + segment.Length;
            }
            scan(expression.Substring(cursor));
            return names;
        }

        private struct Segment
        {
            public int Start;
            public int Length;
        }

        private static IEnumerable<Segment> QuotedSegments(string text)
        {
            int start = -1;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote == '\0')
                {
                    if (c == '"' || c == '\'') { quote = c; start = i; }
                }
                else if (c == quote)
                {
                    yield return new Segment { Start = start, Length = i - start + 1 };
                    quote = '\0';
                }
            }
            if (quote != '\0') yield return new Segment { Start = start, Length = text.Length - start };
        }
    }
}
