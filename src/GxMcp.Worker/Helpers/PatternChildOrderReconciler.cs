using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    // Auto-reconciles the WorkWithPlus `childrenOrderedList` attribute that
    // every container element carries. The IDE renders children in the order
    // listed in this attribute (and HIDES children that are missing from it),
    // so when an LLM caller adds/removes/moves XML children but forgets to
    // sync the list, the new layout would not show up correctly in the IDE.
    //
    // Format of each entry: "{level};{typeCode};{identifier...}"
    //   level     — area code shared by all siblings of a given parent
    //               (2 = transaction subtree, 4 = selection subtree, 3 =
    //               direct level children). Inferred from existing entries
    //               on the same parent; falls back to a context default.
    //   typeCode  — numeric code per element kind (errorViewer=28,
    //               textBlock=27, attribute=22, …). A handful of kinds
    //               change between transaction and selection contexts
    //               (table 01⇄02, standardAction 18⇄17), handled below.
    //   identifier — per-kind: name / controlName / Name / BlockName, or
    //               the field-name suffix of the `attribute` GUID. Can be
    //               composite (e.g. `AcaoDes;=AcaoDes.ContextualTitle` for
    //               an attribute reference inside an <order>).
    //
    // Strategy:
    //   1. Only touch parents that ALREADY have `childrenOrderedList` —
    //      we never invent a list on parents that intentionally don't have
    //      one (operator nodes, gridAttribute leaf nodes, etc).
    //   2. Map existing entries by identifier so we preserve historical
    //      typeCodes (handles edge cases this code might not know about).
    //   3. Walk children in XML document order; rebuild the list in that
    //      order. This makes "where you put the element in the XML" the
    //      single source of truth for IDE display order.
    //   4. If we can't infer typeCode OR identifier for ANY child, bail on
    //      that parent rather than emit a half-broken list — surface the
    //      skip in the report so callers can fix the XML.
    //   5. Report what changed (and what was skipped) so LLM callers see
    //      *why* a layout might not have updated.
    public static class PatternChildOrderReconciler
    {
        private static readonly Dictionary<string, string> StaticTypeCodes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "errorViewer", "28" },
                { "textBlock", "27" },
                { "attribute", "22" },
                { "gridAttribute", "23" },
                { "filterAttribute", "12" },
                { "order", "13" },
                { "orders", "30" },
                { "rule", "56" },
                { "grid", "31" },
                { "descriptionAttribute", "25" },
                { "selection", "39" },
                { "level", "40" },
                { "transaction", "37" },
                { "eventBlock", "75" },
            };

        // Friction 2026-05-25 — element kinds that WWP intentionally OMITS
        // from childrenOrderedList in its own emit. Variables, web components,
        // images, and user controls are addressed by `name`/`controlName`
        // (not by ordered slot). Observed via parent-screen PatternInstance
        // in AcademicoHomolog1 where <table name="TableMain"> with mixed
        // children (textBlock + variable + webComponent) had a list that
        // enumerated ONLY textBlock/table, not the variables/webComponents.
        // Reconciler now treats them as "skip-from-order" instead of bailing
        // on the whole parent — the remaining orderable children still get
        // an updated childrenOrderedList, and the IDE renders them correctly.
        // Without this, every PatternInstance edit involving variables
        // emitted a misleading "may not render in the IDE" skip note.
        private static readonly HashSet<string> NonOrderedKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "variable",
                "webComponent",
                "image",
                "userControl",
            };

        // Element kinds whose entry in childrenOrderedList uses an EMPTY identifier
        // (`{level};{typeCode};`) because there is at most one of them per parent.
        // The IDE relies on type-code uniqueness to address them.
        private static readonly HashSet<string> SingletonKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "orders",
                "grid",
            };

        public sealed class Report
        {
            public int ParentsUpdated { get; internal set; }
            public List<string> Changes { get; } = new List<string>();
            public List<string> Skips { get; } = new List<string>();

            public bool HasContent => ParentsUpdated > 0 || Skips.Count > 0;
        }

        public static Report Reconcile(XDocument doc)
        {
            var report = new Report();
            if (doc?.Root == null) return report;

            // Snapshot parents so we don't iterate while mutating attributes.
            // Cover two cases:
            //  (1) parent ALREADY has childrenOrderedList — rebuild it
            //  (2) parent has NO list but has children that all resolve to
            //      known type codes — INVENT a list. Callers shouldn't need
            //      to know about this attribute at all; the MCP makes the IDE
            //      render their children even when they only describe the XML
            //      structure. Container-like elements only — pure leaves are
            //      excluded by the "has elements at all" guard inside
            //      ReconcileParent.
            var candidates = doc.Descendants()
                .Where(e => e.HasElements)
                .Where(e => !IsBlocklistedContainer(e))
                .ToList();

            foreach (var parent in candidates)
            {
                ReconcileParent(parent, report);
            }

            return report;
        }

        // Elements whose children are not part of the `childrenOrderedList`
        // ordering scheme (their layout is dictated by the SDK structurally,
        // not by this attribute). Stay conservative — only mark as blocked
        // when we've seen the SDK reject a list, OR when the element clearly
        // sits outside the rendered tree.
        private static bool IsBlocklistedContainer(XElement element)
        {
            string n = element.Name.LocalName;
            return n.Equals("instance", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("transaction", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("level", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("selection", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("WPRoot", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("rules", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("events", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("steps", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("parameters", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("filterAttribute", StringComparison.OrdinalIgnoreCase); // operator child is positional, not listed
        }

        private static void ReconcileParent(XElement parent, Report report)
        {
            bool hadList = parent.Attribute("childrenOrderedList") != null;
            string oldList = (string)parent.Attribute("childrenOrderedList") ?? string.Empty;
            string context = GetContext(parent);

            // Determine shared "level" code. Prefer an existing entry on this
            // parent; otherwise look upward at the nearest ancestor with a
            // list so a brand-new container "inherits" the right area code
            // (2 inside transaction, 4 inside selection, etc) without
            // hard-coding context defaults that may be wrong for nested cases.
            string sharedLevel = ExtractLevel(oldList) ?? InheritLevel(parent) ?? DefaultLevel(context);

            // Build identifier -> raw entry segments map for replay/preservation.
            var oldByIdentifier = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in SplitEntries(oldList))
            {
                if (entry.Length < 3) continue;
                string idKey = string.Join(";", entry.Skip(2));
                oldByIdentifier[idKey] = entry;
            }

            var newEntries = new List<string>();
            bool incomplete = false;

            foreach (var child in parent.Elements())
            {
                // Friction 2026-05-25 — WWP omits certain kinds from
                // childrenOrderedList by convention (variable, webComponent,
                // image, userControl — addressed by name/controlName instead).
                // Skip without bailing the whole parent.
                if (NonOrderedKinds.Contains(child.Name.LocalName)) continue;

                string identifier = GetIdentifier(child, parent);
                // Distinguish "missing" (null) from "intentionally empty" (singletons like
                // <orders>/<grid> use an empty identifier slot — `{level};{typeCode};`).
                // string.IsNullOrWhiteSpace lumps them together, breaking the singleton path.
                if (identifier == null)
                {
                    // issue #36.3 — group tables (`<table isGroup="True" title="…">`) carry
                    // no name/controlName/Name/BlockName. WWP addresses them in the parent's
                    // childrenOrderedList by their title/caption. We REUSE that identifier ONLY
                    // when it already appears in the existing list (i.e. the IDE itself wrote
                    // it): matching a known entry lets us preserve it and rebuild the list for
                    // the siblings that DID change. We never *invent* a title-based entry —
                    // that could overwrite a good list with a wrong slot — so if the title is
                    // absent from the existing list we still bail (now as a hard render warning).
                    string weakId = GetWeakTableIdentifier(child);
                    if (weakId != null && oldByIdentifier.TryGetValue(weakId, out var weakExisting))
                    {
                        newEntries.Add(string.Join(";", weakExisting));
                        continue;
                    }

                    incomplete = true;
                    string detail = weakId != null
                        ? " (title=\"" + weakId + "\" is not in the existing childrenOrderedList; give this container a name in the IDE or fix the list by hand — otherwise the edit will NOT render)"
                        : " (container has no name/title to address it by — the edit will NOT render until it is named or the list is fixed in the IDE)";
                    report.Skips.Add(GetPath(parent) + " : cannot derive identifier for <" + child.Name.LocalName + ">" + detail);
                    break;
                }

                if (oldByIdentifier.TryGetValue(identifier, out var existing))
                {
                    newEntries.Add(string.Join(";", existing));
                    continue;
                }

                string typeCode = GetTypeCode(child, context);
                if (string.IsNullOrEmpty(typeCode))
                {
                    incomplete = true;
                    report.Skips.Add(GetPath(parent) + " : no typeCode known for <" + child.Name.LocalName + ">");
                    break;
                }

                newEntries.Add(sharedLevel + ";" + typeCode + ";" + identifier);
            }

            if (incomplete) return;

            string newList = string.Join("-", newEntries);
            if (!string.Equals(oldList, newList, StringComparison.Ordinal))
            {
                parent.SetAttributeValue("childrenOrderedList", newList);
                report.ParentsUpdated++;
                string prefix = hadList ? "" : "(created) ";
                report.Changes.Add(prefix + GetPath(parent) + " : '" + oldList + "' → '" + newList + "'");
            }
        }

        private static string InheritLevel(XElement element)
        {
            for (var a = element.Parent; a != null; a = a.Parent)
            {
                string l = ExtractLevel((string)a.Attribute("childrenOrderedList"));
                if (!string.IsNullOrEmpty(l)) return l;
            }
            return null;
        }

        private static IEnumerable<string[]> SplitEntries(string list)
        {
            if (string.IsNullOrEmpty(list)) yield break;
            foreach (var raw in list.Split('-'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                yield return raw.Split(';');
            }
        }

        private static string ExtractLevel(string list)
        {
            foreach (var segs in SplitEntries(list))
            {
                if (segs.Length >= 1 && !string.IsNullOrWhiteSpace(segs[0]))
                    return segs[0];
            }
            return null;
        }

        private static string DefaultLevel(string context)
        {
            // Inside selection: 4. Everywhere else inside transaction: 2.
            return string.Equals(context, "selection", StringComparison.OrdinalIgnoreCase) ? "4" : "2";
        }

        private static string GetContext(XElement element)
        {
            for (var a = element; a != null; a = a.Parent)
            {
                string ln = a.Name.LocalName;
                if (ln.Equals("selection", StringComparison.OrdinalIgnoreCase)) return "selection";
                if (ln.Equals("transaction", StringComparison.OrdinalIgnoreCase)) return "transaction";
                if (ln.Equals("level", StringComparison.OrdinalIgnoreCase)) return "level";
            }
            return "transaction";
        }

        private static string GetTypeCode(XElement child, string context)
        {
            string name = child.Name.LocalName;

            if (name.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(context, "selection", StringComparison.OrdinalIgnoreCase) ? "02" : "01";
            }
            // <userAction> is a peer of <standardAction>: same row in TableActions,
            // same childrenOrderedList slot, same context-sensitive typeCode.
            if (name.Equals("standardAction", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("userAction", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(context, "selection", StringComparison.OrdinalIgnoreCase) ? "17" : "18";
            }
            return StaticTypeCodes.TryGetValue(name, out var c) ? c : null;
        }

        private static string GetIdentifier(XElement child, XElement parent)
        {
            string name = child.Name.LocalName;

            // Singleton containers (orders, grid, …) use an EMPTY identifier in
            // their parent's list — the entry is "{level};{typeCode};" with the
            // id slot left blank.
            if (SingletonKinds.Contains(name)) return string.Empty;

            // attribute / gridAttribute / descriptionAttribute: identifier is the
            // field-name suffix of the GUID-prefixed `attribute` value. Inside
            // <order>, the format is composite: "{fieldName};{orderName}".
            if (name.Equals("attribute", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("gridAttribute", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("descriptionAttribute", StringComparison.OrdinalIgnoreCase))
            {
                string attr = (string)child.Attribute("attribute");
                if (string.IsNullOrEmpty(attr)) return null;
                int idx = attr.LastIndexOf('-');
                string field = idx >= 0 ? attr.Substring(idx + 1) : attr;

                if (parent != null && parent.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase))
                {
                    string orderName = (string)parent.Attribute("name");
                    if (!string.IsNullOrEmpty(orderName))
                    {
                        return field + ";" + orderName;
                    }
                }
                return field;
            }

            // errorViewer has no name attribute — the literal "ErrorViewer" is used by convention.
            if (name.Equals("errorViewer", StringComparison.OrdinalIgnoreCase))
            {
                return "ErrorViewer";
            }

            return (string)child.Attribute("name")
                ?? (string)child.Attribute("controlName")
                ?? (string)child.Attribute("Name")
                ?? (string)child.Attribute("BlockName");
        }

        // issue #36.3 — a group table (`<table isGroup="True" title="…">`) has no
        // name-style attribute; WWP addresses it in the parent's childrenOrderedList by
        // its title/caption. Returned as a WEAK identifier: the caller only reuses it when
        // it already matches an existing list entry (never invents a new slot from it).
        private static string GetWeakTableIdentifier(XElement child)
        {
            if (!child.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))
                return null;
            string t = (string)child.Attribute("title") ?? (string)child.Attribute("caption");
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }

        private static string GetPath(XElement element)
        {
            var parts = new List<string>();
            for (var c = element; c != null && c.Parent != null; c = c.Parent)
            {
                string id = (string)c.Attribute("name")
                            ?? (string)c.Attribute("controlName")
                            ?? (string)c.Attribute("Name");
                parts.Insert(0, c.Name.LocalName + (string.IsNullOrEmpty(id) ? string.Empty : "[" + id + "]"));
            }
            parts.Insert(0, "instance");
            return "/" + string.Join("/", parts);
        }
    }
}
