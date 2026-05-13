using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Applies a set of WebFormPropertyDelta items to a live WebFormPart by going through
    /// the canonical SDK path:
    ///   1. Enumerate IWebTag instances via WebFormHelper.EnumerateWebTag(part).
    ///   2. Match by tag.Node id / ControlName attribute.
    ///   3. Call WebFormEditable.SetTagProperty(tag, tag.Properties, null, propName, value, ref changed, null)
    ///      so the SDK runs the proper PropertyValueConverter and updates typed Properties.
    ///
    /// On save (later), WebFormPart.BeforeSaveKBObject iterates tags and calls
    /// tag.SaveProperties() — which writes the typed Properties back into the tag's XmlNode
    /// inside m_Document. Result: persisted XML matches the requested change WITHOUT us
    /// touching the raw Document.
    /// </summary>
    internal static class WebFormTypedPropertyWriter
    {
        private const string HelperTypeName = "Artech.Genexus.Common.Parts.WebForm.WebFormHelper";
        private const string EditableTypeName = "Artech.Genexus.Common.Parts.WebForm.WebFormEditable";

        public static bool TryApply(object webFormPart, IReadOnlyList<WebFormPropertyDelta> deltas, out string failure)
        {
            failure = null;
            if (webFormPart == null) { failure = "part is null"; return false; }
            if (deltas == null || deltas.Count == 0) { failure = "no deltas"; return false; }

            Type helperType = FindType(HelperTypeName);
            if (helperType == null) { failure = "WebFormHelper type not loaded"; return false; }

            Type editableType = FindType(EditableTypeName);
            if (editableType == null) { failure = "WebFormEditable type not loaded"; return false; }

            // Pick the EnumerateWebTag overload that takes (KBObject, XmlDocument) so tags are rooted
            // in part.Document — the SAME document the SDK's BeforeSaveKBObject iterates.
            var partDocForEnum = GetReadProperty(webFormPart, "Document") as XmlDocument;
            var partKbObj = GetReadProperty(webFormPart, "KBObject") ?? GetReadProperty(webFormPart, "ContainerObject") ?? GetReadProperty(webFormPart, "Parent") ?? GetReadProperty(webFormPart, "Container");
            MethodInfo enumerate = null;
            object[] enumArgs = null;
            if (partDocForEnum != null && partKbObj != null)
            {
                enumerate = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "EnumerateWebTag") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 2 && ps[1].ParameterType == typeof(XmlDocument) && ps[0].ParameterType.IsInstanceOfType(partKbObj);
                    });
                if (enumerate != null) enumArgs = new object[] { partKbObj, partDocForEnum };
            }
            if (enumerate == null)
            {
                enumerate = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "EnumerateWebTag") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(webFormPart);
                    });
                enumArgs = new object[] { webFormPart };
            }
            if (enumerate == null) { failure = "no EnumerateWebTag overload found"; return false; }
            Logger.Info("[TypedWriter] using " + enumerate.Name + "(" + string.Join(",", enumerate.GetParameters().Select(p => p.ParameterType.Name)) + ")");

            MethodInfo setTagProperty = editableType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SetTagProperty");
            if (setTagProperty == null) { failure = "WebFormEditable.SetTagProperty not found"; return false; }

            // Alternative path: IWebTag.SetProperties(IDictionary) — higher-level API exposed by the
            // interface itself. Used when SetTagProperty throws because of TypeDescriptorContext=null.
            Type webTagInterface = FindType("Artech.Genexus.Common.Parts.WebForm.IWebTag");
            MethodInfo setPropertiesDict = webTagInterface?.GetMethod("SetProperties", new[] { typeof(IDictionary) });

            IDictionary<string, object> byId = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            IDictionary<string, object> byControlName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            try
            {
                IEnumerable tags = (IEnumerable)enumerate.Invoke(null, enumArgs);
                foreach (var tag in tags)
                {
                    total++;
                    var node = GetReadProperty(tag, "Node") as XmlNode;
                    if (node?.Attributes == null) continue;
                    string id = node.Attributes["id"]?.Value;
                    string cn = node.Attributes["ControlName"]?.Value ?? node.Attributes["controlName"]?.Value;
                    if (!string.IsNullOrEmpty(id) && !byId.ContainsKey(id)) byId[id] = tag;
                    if (!string.IsNullOrEmpty(cn) && !byControlName.ContainsKey(cn)) byControlName[cn] = tag;
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                failure = "EnumerateWebTag threw: " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            Logger.Info("[TypedWriter] Indexed " + total + " IWebTag(s): " + byId.Count + " by id, " + byControlName.Count + " by ControlName.");

            // Group deltas by control so SetProperties is called once per tag with all changes.
            var byControl = new Dictionary<string, List<WebFormPropertyDelta>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in deltas)
            {
                if (!byControl.TryGetValue(d.ControlName ?? string.Empty, out var list)) { list = new List<WebFormPropertyDelta>(); byControl[d.ControlName ?? string.Empty] = list; }
                list.Add(d);
            }

            foreach (var kv in byControl)
            {
                string controlName = kv.Key;
                object tag = null;
                if (!string.IsNullOrEmpty(controlName))
                {
                    byId.TryGetValue(controlName, out tag);
                    if (tag == null) byControlName.TryGetValue(controlName, out tag);
                }
                if (tag == null) { failure = "control '" + controlName + "' not found in tag enumeration"; return false; }

                // Canonical-XML strategy:
                //  1) Mutate the tag's XmlNode attributes directly. The Node is shared with m_Document,
                //     so this updates the on-disk XML for free.
                //  2) Invalidate the typed Property cache on the tag (m_Props=null, m_PropertiesLoaded=false).
                //     This forces the SDK to re-load typed Properties FROM the now-updated XmlNode the next
                //     time tag.Properties is accessed — which happens during BeforeSaveKBObject/SaveProperties.
                //     Result: SaveProperties writes the SAME values back to m_Document (no-op clobber).
                // `EnumerateWebTag` returns tags whose Node lives in an INTERNAL XmlDocument, not the
                // live part.Document. Mutating tag.Node alone wouldn't propagate to what gets persisted.
                // Find the matching element in part.Document by id/ControlName and mutate THAT instead.
                var partDoc = GetReadProperty(webFormPart, "Document") as XmlDocument;
                if (partDoc == null) { failure = "part.Document is null"; return false; }
                XmlElement node = FindElementInPartDoc(partDoc, controlName);
                if (node == null) { failure = "no element id='" + controlName + "' (nor ControlName) in part.Document"; return false; }

                foreach (var d in kv.Value)
                {
                    if (d.Value == null)
                    {
                        node.Attributes.RemoveNamedItem(d.PropertyName);
                        Logger.Info("[TypedWriter] removed attr " + controlName + "." + d.PropertyName);
                        continue;
                    }
                    var attr = node.Attributes[d.PropertyName];
                    if (attr == null)
                    {
                        attr = node.OwnerDocument.CreateAttribute(d.PropertyName);
                        node.Attributes.Append(attr);
                    }
                    attr.Value = d.Value;
                    Logger.Info("[TypedWriter] node[" + controlName + "]." + d.PropertyName + " <- '" + Truncate(d.Value, 80) + "'");
                }

                // Invalidate the tag's cached typed Properties so the next read reloads from the new XML.
                InvalidateTagPropertyCache(tag, controlName);

                // Verify the mutation actually landed in part.Document by re-querying.
                var verify = FindElementInPartDoc(partDoc, controlName);
                foreach (var d in kv.Value)
                {
                    string after = verify?.Attributes?[d.PropertyName]?.Value;
                    Logger.Info("[TypedWriter] verify part.Document <" + node.Name + " id=" + controlName + ">." + d.PropertyName + " = '" + Truncate(after, 80) + "' (wanted '" + Truncate(d.Value, 80) + "', match=" + (after == d.Value) + ")");
                }
            }

            // Bump LastModification so the part is considered dirty and gets persisted on EnsureSave.
            try
            {
                var inv = webFormPart.GetType().GetMethod("InvalidateLastModification",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                inv?.Invoke(webFormPart, null);
            }
            catch { }

            // NOTE: NOT calling DeserializeDataFromDocument — that path itself invokes
            // tag.SaveProperties which can clobber our mutations if the typed model resolves
            // values differently than what we wrote.

            // Clear the editable-to-stored pending flag so EnsureSave doesn't replay old m_EditableContent.
            try
            {
                var flag = webFormPart.GetType().GetField("m_EditableToStoredNeeded",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (flag != null && flag.FieldType == typeof(bool))
                {
                    flag.SetValue(webFormPart, false);
                    Logger.Info("[TypedWriter] cleared m_EditableToStoredNeeded.");
                }
            }
            catch { }

            // Also rewrite m_EditableContent to match the new XML, so any latent path that goes
            // through "editable -> stored" produces the same result we wrote directly to m_Document.
            try
            {
                var docNow = GetReadProperty(webFormPart, "Document") as XmlDocument;
                if (docNow != null)
                {
                    var ecField = webFormPart.GetType().GetField("m_EditableContent",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (ecField != null && ecField.FieldType == typeof(string))
                    {
                        ecField.SetValue(webFormPart, docNow.OuterXml);
                        Logger.Info("[TypedWriter] synced m_EditableContent from m_Document (" + docNow.OuterXml.Length + " chars).");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[TypedWriter] failed to sync m_EditableContent: " + ex.Message);
            }

            return true;
        }

        private static XmlElement FindElementInPartDoc(XmlDocument doc, string controlName)
        {
            if (doc?.DocumentElement == null || string.IsNullOrEmpty(controlName)) return null;
            // Prefer id, fall back to ControlName / controlName.
            foreach (var attr in new[] { "id", "ControlName", "controlName", "InternalName" })
            {
                var xp = string.Format("//*[@{0}='{1}']", attr, controlName.Replace("'", "&apos;"));
                var node = doc.SelectSingleNode(xp) as XmlElement;
                if (node != null) return node;
            }
            return null;
        }

        private static void InvalidateTagPropertyCache(object tag, string controlName)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            int invalidated = 0;
            for (var t = tag.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                {
                    string n = f.Name;
                    if (string.Equals(n, "m_Props", StringComparison.Ordinal) ||
                        string.Equals(n, "m_Properties", StringComparison.Ordinal))
                    {
                        try { f.SetValue(tag, null); invalidated++; }
                        catch { }
                    }
                    else if (string.Equals(n, "m_PropertiesLoaded", StringComparison.Ordinal) ||
                             string.Equals(n, "m_PropsLoaded", StringComparison.Ordinal))
                    {
                        try { f.SetValue(tag, false); invalidated++; }
                        catch { }
                    }
                }
            }
            Logger.Info("[TypedWriter] invalidated " + invalidated + " cache field(s) on tag '" + controlName + "'.");
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static object GetReadProperty(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                var pi = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) return pi.GetValue(instance);
            }
            catch { }
            return null;
        }

        private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length > n ? s.Substring(0, n) + "…" : s);
    }
}
