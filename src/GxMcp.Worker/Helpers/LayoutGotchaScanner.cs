using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Static analysis for WebForm layouts that catches patterns the GeneXus HTML
    /// generator silently breaks (or runtime renders read-only) — issues that compile
    /// cleanly but fail at user-facing behavior. Driven by friction-report 2026-05-19
    /// findings: gxButton custom OnClickEvent ignored in &lt;Form type="html"&gt;; gxAttribute
    /// Radio/Combo rendered disabled when local var shadows a transaction attribute.
    ///
    /// Returns warnings the caller can attach to inspect/edit responses so the agent
    /// learns at the failing call instead of after a build + browser smoke cycle.
    /// </summary>
    public static class LayoutGotchaScanner
    {
        public sealed class Gotcha
        {
            public string Code;       // stable identifier for grep/dedup
            public string Severity;   // "Warning" — these compile clean
            public string Element;
            public string ControlId;
            public string Message;
            public string Workaround;
        }

        /// <summary>
        /// Scans a layout XML and returns the list of detected gotchas. Empty list if
        /// none / on parse failure. KB-aware overload — uses the live model to resolve
        /// var:N bindings for descriptive messages.
        /// </summary>
        public static List<Gotcha> Scan(string layoutXml, KBObject obj)
        {
            return ScanInternal(layoutXml, attId => ResolveVarName(obj, attId));
        }

        /// <summary>
        /// Testable overload: accepts a delegate for var:N → variable name. The tests
        /// assembly does not reference Artech.Genexus.Common, so the KBObject overload
        /// can't run in unit tests.
        /// </summary>
        public static List<Gotcha> Scan(string layoutXml, Func<string, string> varNameResolver)
        {
            return ScanInternal(layoutXml, varNameResolver ?? (_ => null));
        }

        // Kept for backwards compatibility with callers that passed an attribute-existence
        // predicate (FR#2 originally hypothesized that shadowing was the cause). The predicate
        // is now ignored — see the comment on GotchaGxAttributeHtmlFormDiscreteReadOnly.
        public static List<Gotcha> Scan(string layoutXml, Func<string, string> varNameResolver, Func<string, bool> attributeExistsIgnored)
        {
            return ScanInternal(layoutXml, varNameResolver ?? (_ => null));
        }

        private static List<Gotcha> ScanInternal(string layoutXml, Func<string, string> varNameResolver)
        {
            var hits = new List<Gotcha>();
            if (string.IsNullOrWhiteSpace(layoutXml)) return hits;

            XDocument doc;
            try { doc = XDocument.Parse(layoutXml); }
            catch { return hits; }

            bool isHtmlForm = doc.Descendants("Form")
                .Any(f => string.Equals((string)f.Attribute("type"), "html", StringComparison.OrdinalIgnoreCase));

            // Reserved — kept as a no-op placeholder so existing helpers that referenced an
            // attribute-existence cache (in the v2.5.1 shadow-hypothesis design) still compile
            // without runtime cost. The current detection does not require KB attribute lookup.

            foreach (var el in doc.Descendants())
            {
                string elName = el.Name.LocalName;

                // FR#1 — gxButton in Form type="html" only fires the Enter event; any custom
                // OnClickEvent value is dropped by the generator (renders data-gx-evt="5"). The
                // SDK accepts the XML attribute (build passes) but the wired event at runtime
                // is always Enter. ListaAti-style buttons use <action onClickEvent="'X'" /> in
                // Form type="layout"; gxBitmap eventGX="'X'" also works for custom events.
                if (isHtmlForm && elName.Equals("gxButton", StringComparison.OrdinalIgnoreCase))
                {
                    string ev = AttrAny(el, "OnClickEvent", "onClickEvent", "eventGX", "event");
                    if (!string.IsNullOrWhiteSpace(ev))
                    {
                        string normalized = ev.Trim().Trim('\'').Trim();
                        if (!normalized.Equals("Enter", StringComparison.OrdinalIgnoreCase)
                            && !normalized.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                            && !normalized.Equals("Refresh", StringComparison.OrdinalIgnoreCase)
                            && normalized.Length > 0)
                        {
                            hits.Add(new Gotcha
                            {
                                Code = "GotchaGxButtonHtmlFormCustomEvent",
                                Severity = "Warning",
                                Element = elName,
                                ControlId = (string)el.Attribute("id"),
                                Message = $"gxButton OnClickEvent=\"'{normalized}'\" will compile but the HTML generator " +
                                          "wires data-gx-evt=5 (Enter) regardless. Custom events are not supported on " +
                                          "gxButton inside <Form type=\"html\">.",
                                Workaround = "Use a <gxBitmap eventGX=\"'" + normalized + "'\" /> styled as a button, or " +
                                             "move the control to a <Form type=\"layout\"> table and use <action onClickEvent=\"'" + normalized + "'\" />."
                            });
                        }
                    }
                }

                // FR#2 (revised 2026-05-19) — gxAttribute with ControlType="Radio Button" or
                // "Combo Box" in <Form type="html"> renders disabled in the HTML output (the
                // <input> gets disabled="" class="gx-disabled" data-gx-readonly=""), regardless
                // of the variable name or ReadOnly="False" / Enabled="True" attributes. Default
                // text-input ControlType is unaffected. Confirmed empirically: renaming
                // &Alu2RegProf → &RespRegProf (no attribute shadow) did NOT fix the disabled
                // render. The hypothesis that variable-name shadowing was the cause was WRONG —
                // the html-form generator simply never emits an editable radio/combo input.
                if (isHtmlForm && elName.Equals("gxAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    string ctrlType = (string)el.Attribute("ControlType");
                    if (!string.IsNullOrWhiteSpace(ctrlType)
                        && (ctrlType.Equals("Radio Button", StringComparison.OrdinalIgnoreCase)
                            || ctrlType.Equals("Combo Box", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Resolve var:N for the message (best-effort — the scanner still emits
                        // even if the binding can't be named).
                        string attId = (string)el.Attribute("AttID");
                        string varName = !string.IsNullOrWhiteSpace(attId) && attId.StartsWith("var:", StringComparison.OrdinalIgnoreCase)
                            ? varNameResolver(attId)
                            : null;
                        string bindingDesc = !string.IsNullOrEmpty(varName) ? "&" + varName : (attId ?? "(unbound)");

                        hits.Add(new Gotcha
                        {
                            Code = "GotchaGxAttributeHtmlFormDiscreteReadOnly",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? attId,
                            Message = $"gxAttribute ControlType=\"{ctrlType}\" bound to {bindingDesc} will render " +
                                      "disabled in the HTML output (the <input> gets disabled=\"\" + class=\"gx-disabled\"). " +
                                      "The html-form generator does not produce editable radio/combo widgets — " +
                                      "ReadOnly=\"False\" / Enabled=\"True\" are ignored here.",
                            Workaround = "Move the control to a <Form type=\"layout\"> and use the WWP table " +
                                         "pattern, OR render the radio/combo via a User Control, OR replace with " +
                                         "raw HTML <input type=\"radio\"> inside a gxTextBlock Format=\"HTML\" + JS " +
                                         "wiring back to a hidden gxAttribute (default ControlType, which IS editable)."
                        });
                    }
                }

                // W5 phase 2 rules — derived from W5 SDK probe (friction-report roadmap 2026-05-19).
                // These catch generator-silent breakages that compile clean and pass save-time
                // validation but render wrong / broken at runtime.

                // GotchaGxAttributeMissingDataField: gxAttribute with no AttID nor DataField →
                // SDK leaves a phantom control; generator emits markup but never binds it.
                if (elName.Equals("gxAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    var attId = (string)el.Attribute("AttID");
                    var dataField = (string)el.Attribute("DataField");
                    if (string.IsNullOrWhiteSpace(attId) && string.IsNullOrWhiteSpace(dataField))
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaGxAttributeMissingDataField",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? "(no-id)",
                            Message = "gxAttribute has neither AttID nor DataField — it will render but bind to nothing. " +
                                      "FixWebFormData keeps the element silently, masking the problem.",
                            Workaround = "Add AttID=\"var:N\" or DataField=\"<attributeName>\" so the control binds to a value."
                        });
                    }
                }

                // GotchaUnknownControlType: gxAttribute ControlType="..." with a misspelled value
                // (e.g. "RadioButton" without the space, "Combobox" all-lowercase) → SDK silently
                // falls back to default Edit. Known valid values:
                //   Edit, Text Box, Combo Box, Radio Button, Check Box, Calendar, Image,
                //   Picture, Hyperlink, Button, Static (legacy), Description, Embedded Page
                if (elName.Equals("gxAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    var ctrlType = (string)el.Attribute("ControlType");
                    if (!string.IsNullOrWhiteSpace(ctrlType) && !_validControlTypes.Contains(ctrlType))
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaUnknownControlType",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? (string)el.Attribute("AttID") ?? "(no-id)",
                            Message = $"gxAttribute ControlType=\"{ctrlType}\" is not a recognized SDK value. " +
                                      "Generator falls back to Edit; your intended control type is lost.",
                            Workaround = "Use one of: " + string.Join(", ", _validControlTypes.OrderBy(s => s)) + "."
                        });
                    }
                }

                // GotchaWebComponentMissingObjectCall: gxEmbeddedPage / gxWebComponent with no
                // ObjectCall — runtime renders an empty <div>, no embedded object loads.
                if (elName.Equals("gxEmbeddedPage", StringComparison.OrdinalIgnoreCase)
                    || elName.Equals("gxWebComponent", StringComparison.OrdinalIgnoreCase))
                {
                    var objCall = (string)el.Attribute("ObjectCall");
                    if (string.IsNullOrWhiteSpace(objCall))
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaWebComponentMissingObjectCall",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? "(no-id)",
                            Message = elName + " has no ObjectCall attribute — runtime renders an empty <div>.",
                            Workaround = "Add ObjectCall=\"<ComponentName>.Create()\" (or equivalent factory call)."
                        });
                    }
                }
            }

            // Structural rules — run after element loop so we have the doc available.

            // GotchaCellOutsideTable / GotchaRowOutsideTable: <cell> or <row> with no <table>
            // ancestor → generator wraps silently OR drops the element.
            foreach (var el in doc.Descendants())
            {
                string elName = el.Name.LocalName;
                if (elName.Equals("cell", StringComparison.OrdinalIgnoreCase) || elName.Equals("row", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasTableAncestor = el.Ancestors().Any(a =>
                        a.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase));
                    if (!hasTableAncestor)
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaCellOutsideTable",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? "(no-id)",
                            Message = "<" + elName + "> has no <table> ancestor — generator wraps silently or drops the element. Layout structure may be malformed.",
                            Workaround = "Wrap " + elName + " in a <table>...<tbody>...</tbody></table> hierarchy."
                        });
                    }
                }
            }

            // GotchaDuplicateControlName: two elements with same id/Name. SDK auto-renames
            // via GetUniqueName during save, but the caller loses the reference they wrote.
            // Pre-empt by reporting which id was duplicated.
            var idGroups = doc.Descendants()
                .Where(e => !string.IsNullOrWhiteSpace((string)e.Attribute("id")))
                .GroupBy(e => (string)e.Attribute("id"), StringComparer.OrdinalIgnoreCase);
            foreach (var grp in idGroups.Where(g => g.Count() > 1))
            {
                hits.Add(new Gotcha
                {
                    Code = "GotchaDuplicateControlName",
                    Severity = "Warning",
                    Element = grp.First().Name.LocalName,
                    ControlId = grp.Key,
                    Message = $"id=\"{grp.Key}\" appears on {grp.Count()} elements. SDK auto-renames via GetUniqueName on save, so caller references may break silently.",
                    Workaround = "Make each id unique. If you intended several controls with the same logical role, suffix them (Btn1, Btn2, ...)."
                });
            }

            return hits;
        }

        // Valid ControlType values for gxAttribute, per SDK PropertyDescriptor.Converter.GetStandardValues().
        // Conservative set — extend if a legitimate type appears that's missing here.
        private static readonly HashSet<string> _validControlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Edit", "Text Box", "Combo Box", "Radio Button", "Check Box", "Calendar", "Image",
            "Picture", "Hyperlink", "Button", "Static", "Description", "Embedded Page",
            "Dynamic Combo Box", "List Box", "Multi Selection List Box", "Textarea", "Password"
        };

        private static string AttrAny(XElement el, params string[] names)
        {
            foreach (var n in names)
            {
                var a = el.Attribute(n);
                if (a != null && !string.IsNullOrWhiteSpace(a.Value)) return a.Value;
            }
            return null;
        }

        // Resolve var:N to the variable name using the object's VariablesPart. Empty/null if
        // not resolvable. NOTE: GetVariableInternalId falls back to enumeration position when
        // SDK doesn't expose a real Id, so this may map the wrong variable in objects where
        // var:N != position. Best-effort.
        private static string ResolveVarName(KBObject obj, string attId)
        {
            if (obj == null || string.IsNullOrEmpty(attId)) return null;
            if (!attId.StartsWith("var:", StringComparison.OrdinalIgnoreCase)) return null;
            if (!int.TryParse(attId.Substring(4), out var id)) return null;
            return WebFormSchemaHints.LookupVarNameById(obj, id);
        }

    }
}
