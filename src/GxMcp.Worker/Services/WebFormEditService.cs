using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
    ///
    /// Action-based mutations on the WebForm XML tree, layered over the existing
    /// WriteService visual-write path so descriptor-name auto-routing (gxButton
    /// OnClickEvent → Event etc.) still applies. Three high-frequency actions are
    /// implemented in this batch:
    ///
    ///   - add_textblock   { name, target, parent?, caption, format?, position? }
    ///   - add_button      { name, target, parent?, caption, event?, controlId? }
    ///   - set_visibility  { name, controlId, visible }
    ///   - remove_control  { name, controlId }
    ///   - wrap_in_fieldset{ name, controlIds[], legend? }
    ///
    /// XML mutation is pure (testable without SDK). Persistence routes through
    /// the existing WebForm write pipeline (LayoutGotchaScanner / typed-property
    /// fixup) so all the guard-rails apply.
    /// </summary>
    public class WebFormEditService
    {
        public interface IWebFormBackend
        {
            string ReadWebFormXml(string target);
            string WriteWebFormXml(string target, string xml, bool dryRun);
        }

        private sealed class DefaultBackend : IWebFormBackend
        {
            private readonly ObjectService _obj;
            private readonly WriteService _write;
            public DefaultBackend(ObjectService obj, WriteService write)
            {
                _obj = obj;
                _write = write;
            }
            public string ReadWebFormXml(string target) =>
                _obj.ReadObjectSource(target, "WebForm", null, null, "mcp", false, null);
            public string WriteWebFormXml(string target, string xml, bool dryRun) =>
                _write.WriteObject(target, "WebForm", xml, null, true, false, true, dryRun);
        }

        private readonly IWebFormBackend _backend;

        public WebFormEditService(ObjectService objectService, WriteService writeService)
            : this(new DefaultBackend(objectService, writeService)) { }

        public WebFormEditService(IWebFormBackend backend)
        {
            _backend = backend;
        }

        public string Execute(string action, JObject args)
        {
            if (string.IsNullOrWhiteSpace(action))
                return Err("MissingAction", "action is required.");
            if (args == null) args = new JObject();

            string target = args["name"]?.ToString() ?? args["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(target))
                return Err("MissingName", "name (or target) is required.");

            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;

            var t0 = DateTime.UtcNow;
            string xmlBefore;
            try
            {
                string read = _backend.ReadWebFormXml(target);
                xmlBefore = ExtractXmlFromReadResponse(read);
            }
            catch (Exception ex)
            {
                return Err("ReadFailed", "Failed to read WebForm: " + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(xmlBefore))
                return Err("EmptyWebForm", "The object does not expose a WebForm part, or it is empty.");

            string xmlAfter;
            var warnings = new List<string>();
            var controlsAdded = new List<string>();
            try
            {
                xmlAfter = ApplyAction(xmlBefore, action, args, warnings, controlsAdded);
            }
            catch (ArgumentException aex)
            {
                return Err("InvalidArgs", aex.Message);
            }
            catch (InvalidOperationException iex)
            {
                return Err("ActionFailed", iex.Message);
            }
            catch (Exception ex)
            {
                return Err("MutationFailed", ex.Message);
            }

            if (string.Equals(xmlBefore, xmlAfter, StringComparison.Ordinal))
            {
                return McpResponse.Ok(target: target, code: "NoChange", result: new JObject
                {
                    ["action"] = action,
                    ["warnings"] = JArray.FromObject(warnings)
                });
            }

            if (!dryRun && WriteService.WasTargetWrittenSince(target, t0))
            {
                return Err("StaleWrite", "The WebForm was modified by another write between this read and write. Re-read and retry.");
            }

            string writeResult;
            try
            {
                writeResult = _backend.WriteWebFormXml(target, xmlAfter, dryRun);
            }
            catch (Exception ex)
            {
                return Err("WriteFailed", "WebForm write failed: " + ex.Message);
            }

            JObject writeObj;
            try { writeObj = JObject.Parse(writeResult ?? "{}"); }
            catch { writeObj = new JObject { ["raw"] = writeResult }; }

            string writeStatus = writeObj["status"]?.ToString() ?? string.Empty;
            bool writeOk = string.Equals(writeStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "Success", StringComparison.OrdinalIgnoreCase)
                        || dryRun;

            var resultPayload = new JObject
            {
                ["action"] = action,
                ["controlsAdded"] = JArray.FromObject(controlsAdded),
                ["warnings"] = JArray.FromObject(warnings),
                ["xmlBeforeAfterDiff"] = BuildShortDiff(xmlBefore, xmlAfter),
                ["write"] = writeObj
            };

            if (!writeOk)
            {
                string writeMsg = writeObj["error"]?["message"]?.ToString() ?? writeObj["message"]?.ToString() ?? "WebForm write failed.";
                return McpResponse.Err(code: "WebFormEditFailed", message: writeMsg, target: target, extra: resultPayload);
            }

            string editCode = dryRun ? "DryRun" : "WebFormEdited";
            return McpResponse.Ok(target: target, code: editCode, result: resultPayload);
        }

        // --- Pure XML mutation core (testable) -------------------------------------------------

        public static string ApplyAction(string xml, string action, JObject args,
            List<string> warnings = null, List<string> controlsAdded = null)
        {
            warnings ??= new List<string>();
            controlsAdded ??= new List<string>();

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("WebForm XML failed to parse: " + ex.Message);
            }
            if (doc.Root == null)
                throw new InvalidOperationException("WebForm XML has no root element.");

            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "add_textblock":
                    AddTextBlock(doc, args, controlsAdded);
                    break;
                case "add_button":
                    AddButton(doc, args, controlsAdded);
                    break;
                case "set_visibility":
                    SetVisibility(doc, args);
                    break;
                case "remove_control":
                    RemoveControl(doc, args);
                    break;
                case "wrap_in_fieldset":
                    WrapInFieldset(doc, args, controlsAdded, warnings);
                    break;
                default:
                    throw new ArgumentException("Unknown action: " + action);
            }

            return doc.Declaration != null
                ? doc.Declaration + Environment.NewLine + doc.Root.ToString(SaveOptions.None)
                : doc.Root.ToString(SaveOptions.None);
        }

        private static void AddTextBlock(XDocument doc, JObject args, List<string> controlsAdded)
        {
            string caption = args["caption"]?.ToString() ?? string.Empty;
            string format = args["format"]?.ToString() ?? "Text";
            string controlId = args["controlId"]?.ToString() ?? GenerateId(doc, "TextBlock");
            string position = args["position"]?.ToString() ?? "last";
            string parentId = args["parent"]?.ToString();

            var tb = new XElement("gxTextBlock",
                new XAttribute("ControlName", controlId),
                new XAttribute("Caption", caption),
                new XAttribute("Format", format));

            InsertControl(doc, tb, parentId, position);
            controlsAdded.Add(controlId);
        }

        private static void AddButton(XDocument doc, JObject args, List<string> controlsAdded)
        {
            string caption = args["caption"]?.ToString() ?? "Button";
            string eventName = args["event"]?.ToString();
            string controlId = args["controlId"]?.ToString() ?? GenerateId(doc, "Btn");
            string parentId = args["parent"]?.ToString();
            string position = args["position"]?.ToString() ?? "last";

            var btn = new XElement("gxButton",
                new XAttribute("ControlName", controlId),
                new XAttribute("Caption", caption));
            // Use descriptor-name 'OnClickEvent' — WebFormTypedPropertyWriter routes it
            // to the canonical 'Event' attribute on save.
            if (!string.IsNullOrWhiteSpace(eventName))
                btn.SetAttributeValue("OnClickEvent", "'" + eventName + "'");

            InsertControl(doc, btn, parentId, position);
            controlsAdded.Add(controlId);
        }

        private static void SetVisibility(XDocument doc, JObject args)
        {
            string controlId = args["controlId"]?.ToString();
            if (string.IsNullOrWhiteSpace(controlId))
                throw new ArgumentException("controlId is required.");
            bool? visibleNullable = args["visible"]?.ToObject<bool?>();
            if (!visibleNullable.HasValue)
                throw new ArgumentException("visible (bool) is required.");

            var ctl = FindControl(doc, controlId);
            if (ctl == null)
                throw new InvalidOperationException("Control not found: " + controlId);
            ctl.SetAttributeValue("Visible", visibleNullable.Value ? "True" : "False");
        }

        private static void RemoveControl(XDocument doc, JObject args)
        {
            string controlId = args["controlId"]?.ToString();
            if (string.IsNullOrWhiteSpace(controlId))
                throw new ArgumentException("controlId is required.");
            var ctl = FindControl(doc, controlId);
            if (ctl == null)
                throw new InvalidOperationException("Control not found: " + controlId);
            ctl.Remove();
        }

        private static void WrapInFieldset(XDocument doc, JObject args, List<string> controlsAdded, List<string> warnings)
        {
            var idsToken = args["controlIds"] as JArray;
            if (idsToken == null || idsToken.Count == 0)
                throw new ArgumentException("controlIds[] is required.");
            string legend = args["legend"]?.ToString();
            string groupId = args["controlId"]?.ToString() ?? GenerateId(doc, "Grp");

            var ids = idsToken.Select(t => t.ToString()).ToList();
            var firstControl = FindControl(doc, ids[0]);
            if (firstControl == null)
                throw new InvalidOperationException("Control not found: " + ids[0]);

            var fieldset = new XElement("gxFieldSet",
                new XAttribute("ControlName", groupId));
            if (!string.IsNullOrEmpty(legend))
                fieldset.SetAttributeValue("Caption", legend);

            // Insert fieldset where the first control was; then move the named controls into it.
            firstControl.AddBeforeSelf(fieldset);
            foreach (var id in ids)
            {
                var ctl = FindControl(doc, id);
                if (ctl == null)
                {
                    warnings.Add("control_not_found:" + id);
                    continue;
                }
                ctl.Remove();
                fieldset.Add(ctl);
            }
            controlsAdded.Add(groupId);
        }

        // --- Helpers ---------------------------------------------------------------------------

        private static XElement FindControl(XDocument doc, string controlId)
        {
            if (string.IsNullOrWhiteSpace(controlId)) return null;
            return doc.Descendants()
                .FirstOrDefault(e =>
                {
                    string n = (string)e.Attribute("ControlName");
                    if (string.Equals(n, controlId, StringComparison.OrdinalIgnoreCase)) return true;
                    string id = (string)e.Attribute("id") ?? (string)e.Attribute("Id");
                    return string.Equals(id, controlId, StringComparison.OrdinalIgnoreCase);
                });
        }

        private static void InsertControl(XDocument doc, XElement newElement, string parentId, string position)
        {
            XElement parent = null;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                parent = FindControl(doc, parentId);
                if (parent == null)
                    throw new InvalidOperationException("Parent control not found: " + parentId);
            }
            else
            {
                // Find the Form/Body container — fall back to root.
                parent = doc.Descendants("Form").FirstOrDefault()
                         ?? doc.Descendants("Body").FirstOrDefault()
                         ?? doc.Descendants("Table").FirstOrDefault()
                         ?? doc.Root;
            }

            position = (position ?? "last").Trim();
            if (position.Equals("first", StringComparison.OrdinalIgnoreCase))
            {
                parent.AddFirst(newElement);
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                string anchorId = position.Substring("after:".Length);
                var anchor = FindControl(doc, anchorId);
                if (anchor == null)
                    throw new InvalidOperationException("Anchor control not found for position: " + anchorId);
                anchor.AddAfterSelf(newElement);
            }
            else
            {
                parent.Add(newElement);
            }
        }

        private static string GenerateId(XDocument doc, string prefix)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Descendants())
            {
                string n = (string)e.Attribute("ControlName");
                if (!string.IsNullOrEmpty(n)) used.Add(n);
            }
            for (int i = 1; i < 10000; i++)
            {
                string candidate = prefix + i;
                if (!used.Contains(candidate)) return candidate;
            }
            return prefix + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string BuildShortDiff(string before, string after)
        {
            // Cheap line-count delta; full diff would bloat the response.
            int beforeLen = before?.Length ?? 0;
            int afterLen = after?.Length ?? 0;
            return string.Format("{0} chars → {1} chars (Δ {2:+0;-0;0})", beforeLen, afterLen, afterLen - beforeLen);
        }

        private static string ExtractXmlFromReadResponse(string read)
        {
            // ObjectService.ReadObjectSource returns either raw XML or a JSON envelope
            // (depending on call site). Detect JSON and extract the content field;
            // otherwise return the raw string.
            if (string.IsNullOrEmpty(read)) return read;
            string trimmed = read.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                try
                {
                    var jo = JObject.Parse(read);
                    return jo["content"]?.ToString()
                        ?? jo["source"]?.ToString()
                        ?? jo["xml"]?.ToString()
                        ?? read;
                }
                catch { return read; }
            }
            return read;
        }

        private static string Err(string code, string message)
        {
            return McpResponse.Err(code: code, message: message);
        }
    }
}
