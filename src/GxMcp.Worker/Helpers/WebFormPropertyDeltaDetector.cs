using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    public sealed class WebFormPropertyDelta
    {
        public string ControlName { get; set; }   // id (preferred) or ControlName from XML
        public string PropertyName { get; set; }  // XML attribute name (= SDK property name)
        public string Value { get; set; }         // raw XML attribute value
    }

    public sealed class WebFormPropertyDeltaResult
    {
        public bool IsSupported { get; set; }
        public IReadOnlyList<WebFormPropertyDelta> Deltas { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Compares two GxMultiForm XML documents structurally. If they differ ONLY in attribute
    /// values on existing controls (no add/remove/move of elements, no text-node changes),
    /// returns IsSupported=true plus the per-attribute deltas. Otherwise IsSupported=false
    /// with a Reason indicating the structural divergence — caller falls back to the raw
    /// XML rewrite path (which currently still fails verification for those cases, but at
    /// least we know why we're falling back).
    /// </summary>
    public static class WebFormPropertyDeltaDetector
    {
        public static WebFormPropertyDeltaResult DetectSupportedPropertyDeltas(string currentXml, string updatedXml)
        {
            var empty = new WebFormPropertyDeltaResult
            {
                IsSupported = false,
                Deltas = new List<WebFormPropertyDelta>(),
                Reason = "empty input"
            };
            if (string.IsNullOrWhiteSpace(currentXml) || string.IsNullOrWhiteSpace(updatedXml)) return empty;

            XDocument current, updated;
            try
            {
                current = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                updated = XDocument.Parse(updatedXml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                return new WebFormPropertyDeltaResult { IsSupported = false, Deltas = new List<WebFormPropertyDelta>(), Reason = "parse: " + ex.Message };
            }

            var deltas = new List<WebFormPropertyDelta>();
            string failure = null;
            bool ok = CompareElements(current.Root, updated.Root, deltas, ref failure);
            return new WebFormPropertyDeltaResult
            {
                IsSupported = ok,
                Deltas = ok ? deltas : new List<WebFormPropertyDelta>(),
                Reason = ok ? "ok" : failure
            };
        }

        private static bool CompareElements(XElement current, XElement updated, List<WebFormPropertyDelta> deltas, ref string failure)
        {
            if (current == null || updated == null)
            {
                if (current != updated) { failure = "one side null"; return false; }
                return true;
            }
            if (!string.Equals(current.Name.LocalName, updated.Name.LocalName, StringComparison.Ordinal))
            {
                failure = "element name differs: " + current.Name.LocalName + " vs " + updated.Name.LocalName;
                return false;
            }

            var currentChildren = current.Elements().ToList();
            var updatedChildren = updated.Elements().ToList();
            if (currentChildren.Count != updatedChildren.Count)
            {
                failure = "child count differs at <" + current.Name.LocalName + "> (" + currentChildren.Count + " vs " + updatedChildren.Count + ")";
                return false;
            }

            string controlName = GetControlName(current);
            string updatedControlName = GetControlName(updated);
            if (!string.Equals(controlName, updatedControlName, StringComparison.OrdinalIgnoreCase))
            {
                failure = "control identity differs: '" + (controlName ?? "?") + "' vs '" + (updatedControlName ?? "?") + "'";
                return false;
            }

            if (!CompareAttributes(current, updated, controlName, deltas, ref failure)) return false;

            string currentText = string.Concat(current.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Value));
            string updatedText = string.Concat(updated.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Value));
            if (!string.Equals(currentText, updatedText, StringComparison.Ordinal))
            {
                failure = "text content differs at <" + current.Name.LocalName + ">";
                return false;
            }

            for (int i = 0; i < currentChildren.Count; i++)
            {
                if (!CompareElements(currentChildren[i], updatedChildren[i], deltas, ref failure)) return false;
            }
            return true;
        }

        private static bool CompareAttributes(XElement current, XElement updated, string controlName, List<WebFormPropertyDelta> deltas, ref string failure)
        {
            var currentAttrs = current.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);
            var updatedAttrs = updated.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);

            // Newly added attribute
            foreach (var item in updatedAttrs)
            {
                if (!currentAttrs.ContainsKey(item.Key))
                {
                    if (string.IsNullOrWhiteSpace(controlName))
                    {
                        failure = "new attribute '" + item.Key + "' on unnamed <" + current.Name.LocalName + ">";
                        return false;
                    }
                    deltas.Add(new WebFormPropertyDelta { ControlName = controlName, PropertyName = item.Key, Value = item.Value });
                }
            }

            // Removed / changed attribute
            foreach (var item in currentAttrs)
            {
                string updatedValue;
                if (!updatedAttrs.TryGetValue(item.Key, out updatedValue))
                {
                    if (string.IsNullOrWhiteSpace(controlName))
                    {
                        failure = "removed attribute '" + item.Key + "' on unnamed <" + current.Name.LocalName + ">";
                        return false;
                    }
                    deltas.Add(new WebFormPropertyDelta { ControlName = controlName, PropertyName = item.Key, Value = null });
                    continue;
                }
                if (string.Equals(item.Value, updatedValue, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(controlName))
                {
                    failure = "attribute '" + item.Key + "' changed on unnamed <" + current.Name.LocalName + ">";
                    return false;
                }
                deltas.Add(new WebFormPropertyDelta { ControlName = controlName, PropertyName = item.Key, Value = updatedValue });
            }

            return true;
        }

        private static string GetControlName(XElement element)
        {
            return Attr(element, "id") ?? Attr(element, "ControlName") ?? Attr(element, "controlName") ?? Attr(element, "InternalName");
        }

        private static string Attr(XElement element, string name)
        {
            var attr = element.Attribute(name);
            return attr == null ? null : attr.Value;
        }
    }
}
