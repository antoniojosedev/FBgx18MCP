using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The pattern specification as the attribute check needs it: which element type a child
    /// element name stands for, and which attribute names an element type declares.
    /// </summary>
    internal interface IPatternSpecView
    {
        string RootType();
        /// <summary>The type of child element <paramref name="childName"/> under <paramref name="parentType"/>, or null when the specification does not say.</summary>
        string ChildType(string parentType, string childName);
        /// <summary>The attribute names the type declares, or null when the type is unknown.</summary>
        IReadOnlyCollection<string> AttributeNames(string typeName);
    }

    internal sealed class DiscardedAttribute
    {
        internal string ElementPath;
        internal string Element;
        internal string Attribute;
        internal string Value;
        internal string DidYouMean;
    }

    /// <summary>
    /// Finds the XML attributes of a requested PatternInstance that the pattern specification
    /// does not declare. <c>PatternInstanceElementAttributes.FromXml</c> reads only the
    /// specification's attributes, so any other attribute is discarded on save and the write
    /// ends in <c>PatternVerificationMismatch</c> after the part has already been saved.
    /// </summary>
    internal static class PatternSdkAttributeCheck
    {
        internal const string CustomPropertiesAttribute = "PATTERN_ELEMENT_CUSTOM_PROPERTIES";

        /// <param name="requested">The instance XML the caller wants stored.</param>
        /// <param name="spec">The pattern instance specification.</param>
        /// <param name="extraSuggestion">Pattern-specific alias lookup (key to the specification name it stands for), or null.</param>
        internal static List<DiscardedAttribute> Find(XDocument requested, IPatternSpecView spec, Func<string, string> extraSuggestion = null)
        {
            var result = new List<DiscardedAttribute>();
            if (requested?.Root == null || spec == null) return result;
            string rootType = spec.RootType();
            if (string.IsNullOrEmpty(rootType)) return result;
            Visit(requested.Root, rootType, "/" + requested.Root.Name.LocalName, spec, extraSuggestion, result);
            return result;
        }

        private static void Visit(XElement element, string type, string path, IPatternSpecView spec,
            Func<string, string> extraSuggestion, List<DiscardedAttribute> result)
        {
            IReadOnlyCollection<string> declared = spec.AttributeNames(type);
            if (declared != null)
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None) continue;
                    string name = attribute.Name.LocalName;
                    if (name == CustomPropertiesAttribute || declared.Contains(name)) continue;
                    result.Add(new DiscardedAttribute
                    {
                        ElementPath = path,
                        Element = element.Name.LocalName,
                        Attribute = name,
                        Value = attribute.Value,
                        DidYouMean = Suggest(name, declared, extraSuggestion)
                    });
                }
            }

            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XElement child in element.Elements())
            {
                string childName = child.Name.LocalName;
                occurrences.TryGetValue(childName, out int seen);
                occurrences[childName] = ++seen;
                string childType = spec.ChildType(type, childName);
                if (string.IsNullOrEmpty(childType)) continue;
                Visit(child, childType, path + "/" + childName + "[" + seen + "]", spec, extraSuggestion, result);
            }
        }

        /// <summary>The declared name a rejected name most likely stands for: the same name in another case, a pattern-specific alias, or a near miss.</summary>
        internal static string Suggest(string name, IReadOnlyCollection<string> declared, Func<string, string> extraSuggestion)
        {
            string sameLetters = declared.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
            if (sameLetters != null) return sameLetters;
            string alias = extraSuggestion?.Invoke(name);
            if (!string.IsNullOrEmpty(alias) && declared.Contains(alias, StringComparer.Ordinal)) return alias;
            string lower = name.ToLowerInvariant();
            string best = null;
            int bestDistance = int.MaxValue;
            foreach (string candidate in declared)
            {
                if (candidate.StartsWith("default", StringComparison.Ordinal)) continue;
                int distance = EditDistance(lower, candidate.ToLowerInvariant());
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            return best != null && bestDistance <= Math.Max(3, name.Length / 4) && bestDistance < name.Length ? best : null;
        }

        private static int EditDistance(string a, string b)
        {
            var row = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) row[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }

        /// <summary>The refusal text and the first element's <c>verifyDiff</c>, in the shape a failed verification uses.</summary>
        internal static JObject Describe(IReadOnlyList<DiscardedAttribute> discarded, string patternName)
        {
            var lines = discarded.Take(8).Select(d => d.ElementPath + " @" + d.Attribute
                + (d.DidYouMean == null ? string.Empty : " (the specification declares '" + d.DidYouMean + "')"));
            string message = "The " + patternName + " specification does not declare " + discarded.Count + " attribute(s) in this edit: "
                + string.Join("; ", lines) + (discarded.Count > 8 ? "; ..." : string.Empty)
                + ". The SDK keeps only declared attributes when the instance is saved, so they would be discarded and the write would end in PatternVerificationMismatch. No change was applied.";
            DiscardedAttribute first = discarded[0];
            var rejected = new JArray(discarded.Where(d => d.ElementPath == first.ElementPath).Select(d => d.Attribute));
            return new JObject
            {
                ["message"] = message,
                ["verifyDiff"] = new JObject
                {
                    ["element"] = first.Element,
                    ["path"] = first.ElementPath,
                    ["rejectedAttributes"] = rejected
                },
                ["count"] = discarded.Count
            };
        }
    }
}
