using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Compares a PatternInstance re-read with what a combo-property write was meant to do:
    /// the requested properties hold their predicted values on the target element, and the
    /// rest of the document is untouched.
    /// </summary>
    internal static class WwpComboVerifier
    {
        private const int MaxDifferences = 10;

        /// <param name="before">The whole instance before the write.</param>
        /// <param name="after">The whole instance re-read after the save.</param>
        /// <param name="reference">The target attribute reference.</param>
        /// <param name="ordinal">Position of the target among the elements with that reference in <paramref name="before"/>.</param>
        /// <param name="expected">SDK property name to the value the SDK was predicted to store.</param>
        /// <param name="nativeEffective">
        /// Effective values read back from the native element after the save, or null. The XML
        /// omits a property that equals its default, so an omitted attribute is confirmed only by
        /// this map (or when the expected value is empty).
        /// </param>
        internal static JObject Verify(XDocument before, XDocument after, string reference, int ordinal,
            IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> nativeEffective)
        {
            var result = new JObject { ["confirmed"] = false };
            List<XElement> targetsBefore = WwpComboPlanner.FindAttributeElements(before, reference);
            List<XElement> targetsAfter = WwpComboPlanner.FindAttributeElements(after, reference);
            if (ordinal < 0 || ordinal >= targetsBefore.Count || targetsAfter.Count != targetsBefore.Count)
            {
                result["message"] = "The target attribute '" + reference + "' is not on the re-read instance the same number of times as before the write ("
                    + targetsBefore.Count + " before, " + targetsAfter.Count + " after).";
                result["elementFound"] = false;
                return result;
            }
            result["elementFound"] = true;
            XElement target = targetsAfter[ordinal];

            var properties = new JObject();
            bool propertiesConfirmed = true;
            foreach (var pair in expected)
            {
                string persisted = (string)target.Attribute(pair.Key);
                string effective = null;
                nativeEffective?.TryGetValue(pair.Key, out effective);
                bool confirmed = persisted != null
                    ? string.Equals(persisted, pair.Value, StringComparison.Ordinal)
                    : effective != null ? string.Equals(effective, pair.Value, StringComparison.Ordinal) : pair.Value.Length == 0;
                propertiesConfirmed &= confirmed;
                properties[pair.Key] = new JObject
                {
                    ["expected"] = pair.Value,
                    ["persistedXml"] = persisted,
                    ["persistedEffective"] = effective,
                    ["confirmed"] = confirmed
                };
            }
            result["properties"] = properties;
            result["propertiesConfirmed"] = propertiesConfirmed;

            XDocument maskedBefore = Mask(before, targetsBefore[ordinal], expected.Keys);
            XDocument maskedAfter = Mask(after, target, expected.Keys);
            var differences = new List<string>();
            Diff(maskedBefore.Root, maskedAfter.Root, "/" + maskedBefore.Root?.Name.LocalName, differences);
            result["unexpectedChanges"] = new JArray(differences);
            result["restOfInstanceUnchanged"] = differences.Count == 0;
            result["confirmed"] = propertiesConfirmed && differences.Count == 0;
            if (!(bool)result["confirmed"])
                result["message"] = !propertiesConfirmed
                    ? "The re-read instance does not hold the requested combo properties on '" + reference + "'."
                    : "The save changed the instance beyond the requested properties: " + string.Join("; ", differences);
            return result;
        }

        private static XDocument Mask(XDocument source, XElement target, IEnumerable<string> properties)
        {
            XDocument copy = new XDocument(source);
            int index = source.Descendants().ToList().IndexOf(target);
            XElement maskedTarget = copy.Descendants().ElementAt(index);
            foreach (string name in properties) maskedTarget.Attribute(name)?.Remove();
            // SDK bookkeeping that the save may renumber without any user-visible change.
            foreach (XElement element in copy.Descendants()) element.Attribute("childrenOrderedList")?.Remove();
            return copy;
        }

        private static void Diff(XElement left, XElement right, string path, List<string> differences)
        {
            if (differences.Count >= MaxDifferences) return;
            if (left == null || right == null || left.Name != right.Name)
            {
                differences.Add(path + ": element differs");
                return;
            }
            var leftAttributes = left.Attributes().ToDictionary(a => a.Name.ToString(), a => a.Value);
            var rightAttributes = right.Attributes().ToDictionary(a => a.Name.ToString(), a => a.Value);
            foreach (string name in leftAttributes.Keys.Union(rightAttributes.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                leftAttributes.TryGetValue(name, out string a);
                rightAttributes.TryGetValue(name, out string b);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    differences.Add(path + "/@" + name + ": " + (a ?? "<absent>") + " -> " + (b ?? "<absent>"));
                if (differences.Count >= MaxDifferences) return;
            }
            string leftText = string.Concat(left.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            string rightText = string.Concat(right.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (!string.Equals(leftText, rightText, StringComparison.Ordinal)) differences.Add(path + ": text differs");

            List<XElement> leftChildren = left.Elements().ToList();
            List<XElement> rightChildren = right.Elements().ToList();
            if (leftChildren.Count != rightChildren.Count)
            {
                differences.Add(path + ": " + leftChildren.Count + " children -> " + rightChildren.Count);
                return;
            }
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < leftChildren.Count; i++)
            {
                string name = leftChildren[i].Name.LocalName;
                occurrences.TryGetValue(name, out int seen);
                occurrences[name] = ++seen;
                Diff(leftChildren[i], rightChildren[i], path + "/" + name + "[" + seen + "]", differences);
            }
        }
    }
}
