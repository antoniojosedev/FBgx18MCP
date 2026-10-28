using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #711: a layout-form control can carry its Conditions (<c>ControlWhere</c>) twice.
    ///
    /// <list type="bullet">
    /// <item><c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c> holds the control's SDK property. For a
    /// built-in control type (Dynamic Combo Box) it is what the IDE's Conditions property and the
    /// control's typed property bag read, and the SDK parses and re-stores it on save.</item>
    /// <item><c>WebUserControlProperties</c> is a verbatim string: the user-control property bag the
    /// HTML-to-layout conversion fills with the HTML control's properties. The IDE reads it only for a
    /// user-control type that supports the web layout.</item>
    /// </list>
    ///
    /// A write that changes the expression in one of them and leaves the other holding a different one
    /// persists and verifies as written, yet the IDE, the generator and the XML then disagree on the
    /// control's Conditions. The write is refused before the save, naming both values.
    /// </summary>
    internal static class ControlConditionCarriers
    {
        internal const string WebUserControlPropertiesAttribute = "WebUserControlProperties";

        /// <summary>Expression properties carried by both bags. ControlWhere is the IDE's Conditions.</summary>
        internal static readonly string[] ExpressionProperties = { "ControlWhere" };

        internal sealed class Divergence
        {
            public string Element { get; set; }
            public string Control { get; set; }
            public string Property { get; set; }
            public string CustomPropertiesExpression { get; set; }
            public string WebUserControlExpression { get; set; }
            public bool CustomPropertiesChanged { get; set; }
            public bool WebUserControlChanged { get; set; }
        }

        /// <summary>
        /// The expression properties that differ between the two bags of one control in
        /// <paramref name="requestedXml"/> and that the request changed in either bag relative to
        /// <paramref name="currentXml"/>. A divergence the request leaves untouched is not reported, so
        /// an unrelated edit to a form the IDE already left inconsistent is not blocked. A control absent
        /// from the current XML counts as changed. Unparseable XML yields no divergence: the visual write
        /// reports its own parse error.
        /// </summary>
        internal static List<Divergence> FindIntroduced(string currentXml, string requestedXml)
        {
            var result = new List<Divergence>();
            XDocument requested = TryParse(requestedXml);
            if (requested?.Root == null) return result;
            XDocument current = TryParse(currentXml);

            var currentByKey = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            if (current?.Root != null)
            {
                foreach (var pair in Keyed(current.Root))
                    if (!currentByKey.ContainsKey(pair.Key)) currentByKey[pair.Key] = pair.Value;
            }

            foreach (var pair in Keyed(requested.Root))
            {
                XElement element = pair.Value;
                currentByKey.TryGetValue(pair.Key, out XElement before);
                foreach (string property in ExpressionProperties)
                {
                    if (!TryRead(element, CustomPropertiesPayload.AttributeName, property, out string custom)) continue;
                    if (!TryRead(element, WebUserControlPropertiesAttribute, property, out string userControl)) continue;
                    string customExpression = ControlExpressionText.Of(custom) ?? string.Empty;
                    string userControlExpression = ControlExpressionText.Of(userControl) ?? string.Empty;
                    if (string.Equals(customExpression, userControlExpression, StringComparison.Ordinal)) continue;

                    bool customChanged = before == null || !SameExpression(before, CustomPropertiesPayload.AttributeName, property, customExpression);
                    bool userControlChanged = before == null || !SameExpression(before, WebUserControlPropertiesAttribute, property, userControlExpression);
                    if (!customChanged && !userControlChanged) continue;

                    result.Add(new Divergence
                    {
                        Element = element.Name.LocalName,
                        Control = Describe(element),
                        Property = property,
                        CustomPropertiesExpression = customExpression,
                        WebUserControlExpression = userControlExpression,
                        CustomPropertiesChanged = customChanged,
                        WebUserControlChanged = userControlChanged
                    });
                }
            }
            return result;
        }

        private static bool SameExpression(XElement element, string carrier, string property, string expression)
        {
            if (!TryRead(element, carrier, property, out string value)) return false;
            return string.Equals(ControlExpressionText.Of(value) ?? string.Empty, expression, StringComparison.Ordinal);
        }

        /// <summary>The value of <paramref name="property"/> inside the <c>&lt;Properties&gt;</c> bag held
        /// by <paramref name="carrier"/>. False when the attribute, the bag or the property is absent.</summary>
        internal static bool TryRead(XElement element, string carrier, string property, out string value)
        {
            value = null;
            string payloadText = Attribute(element, carrier);
            if (string.IsNullOrWhiteSpace(payloadText)) return false;
            if (!CustomPropertiesPayload.TryParse(payloadText, out var payload) || payload == null) return false;
            return payload.TryGet(property, out value);
        }

        /// <summary>
        /// Every element that holds both bags, keyed by element name and control identity (controlName,
        /// else the bound attribute/variable, else the id), with an ordinal for repeats.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, XElement>> Keyed(XElement root)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in root.DescendantsAndSelf())
            {
                if (Attribute(element, CustomPropertiesPayload.AttributeName) == null) continue;
                if (Attribute(element, WebUserControlPropertiesAttribute) == null) continue;
                string baseKey = element.Name.LocalName + "|" + (Identity(element) ?? string.Empty);
                seen.TryGetValue(baseKey, out int ordinal);
                seen[baseKey] = ordinal + 1;
                yield return new KeyValuePair<string, XElement>(baseKey + "#" + ordinal, element);
            }
        }

        private static string Identity(XElement element)
            => Attribute(element, "controlName") ?? Attribute(element, "attribute") ?? Attribute(element, "id");

        private static string Describe(XElement element)
        {
            string name = Attribute(element, "controlName");
            string binding = Attribute(element, "attribute");
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(binding)) return name + " (" + binding + ")";
            return name ?? binding ?? Attribute(element, "id") ?? element.Name.LocalName;
        }

        private static string Attribute(XElement element, string localName)
            => element.Attributes()
                .FirstOrDefault(a => !a.IsNamespaceDeclaration
                    && string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                ?.Value;

        private static XDocument TryParse(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try { return XDocument.Parse(xml); }
            catch (XmlException) { return null; }
        }
    }
}
