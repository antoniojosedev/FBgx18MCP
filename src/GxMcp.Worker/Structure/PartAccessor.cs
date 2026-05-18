using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Structure
{
    public static class PartAccessor
    {
        // v2.3.8 Task 4.4 — Known part-type-names that wrap variable collections across
        // GeneXus object kinds. The typed `obj.Parts.Get<VariablesPart>()` lookup covers
        // Procedure / DataProvider cleanly, but some kinds (WebPanel, Transaction,
        // WorkPanel) historically returned null from that call, triggering the friction
        // "Part 'DeleteVariable' not found in WebPanel" failure mode. This name list
        // is consulted as the second-pass fallback before reflection.
        private static readonly string[] VariablesPartTypeNameCandidates = new[]
        {
            "VariablesPart",
            "Variables",
            "ProcedureVariables",
            "WebFormVariables",
            "TransactionVariables",
            "WorkPanelVariables",
        };

        /// <summary>
        /// v2.3.8 Task 4.4 — Kind-aware <see cref="VariablesPart"/> accessor.
        ///
        /// Resolution order:
        ///   1. <c>obj.Parts.Get&lt;VariablesPart&gt;()</c>           — typed SDK lookup.
        ///   2. Match by concrete type name (e.g. "VariablesPart", "ProcedureVariables").
        ///   3. Reflective fallback — first part exposing a public <c>Variables</c> property.
        ///
        /// Returns <c>null</c> only when the object truly has no variables-bearing part.
        /// </summary>
        public static VariablesPart GetVariablesPart(KBObject obj)
        {
            if (obj == null) return null;

            // 1. Typed SDK accessor — fast path; covers Procedure, DataProvider and any
            // kind whose variables part is the canonical VariablesPart concrete type.
            try
            {
                var typed = obj.Parts.Get<VariablesPart>();
                if (typed != null) return typed;
            }
            catch { /* SDK may throw for kinds that don't expose VariablesPart — try next */ }

            // 2. Name-based dispatch across known kind-specific part type names.
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p == null) continue;
                var typeName = p.GetType().Name;
                var descriptorName = p.TypeDescriptor?.Name;
                foreach (var candidate in VariablesPartTypeNameCandidates)
                {
                    if (string.Equals(typeName, candidate, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(descriptorName, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p is VariablesPart vp) return vp;
                    }
                }
            }

            // 3. Reflective fallback — any part exposing a `Variables` collection property
            // is considered variables-bearing. Returned as VariablesPart only when it
            // actually inherits from that concrete type (preserves type safety for callers).
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p == null) continue;
                if (p.GetType().GetProperty("Variables", BindingFlags.Public | BindingFlags.Instance) != null)
                {
                    if (p is VariablesPart vp) return vp;
                }
            }

            return null;
        }
        public static bool MatchesSourcePart(string requestedPartName, string sourcePartName)
        {
            if (string.IsNullOrWhiteSpace(requestedPartName))
            {
                return false;
            }

            bool isEventsRequest = requestedPartName.Equals("Events", StringComparison.OrdinalIgnoreCase);
            bool isSourceAliasRequest =
                requestedPartName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                requestedPartName.Equals("Code", StringComparison.OrdinalIgnoreCase);

            if (isEventsRequest)
            {
                return string.Equals(sourcePartName, "Events", StringComparison.OrdinalIgnoreCase);
            }

            if (!isSourceAliasRequest)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourcePartName))
            {
                return true;
            }

            return !string.Equals(sourcePartName, "Events", StringComparison.OrdinalIgnoreCase);
        }

        public static Guid GetPartGuid(string objType, string partName)
        {
            string p = partName.ToLower();

            if (objType.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
                if (p == "layout") return Guid.Parse("c414ed00-8cc4-4f44-8820-4baf93547173");
            }
            
            if (objType.Equals("WebPanel", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout") return Guid.Parse("ad3ca970-19d0-44e1-a7b7-db05556e820c");
                if (p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure") return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout" || p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("DataProvider", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("91705646-6086-4f32-8871-08149817e754");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
            }

            if (objType.Equals("SDT", StringComparison.OrdinalIgnoreCase) || objType.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure" || p == "source") return Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");
            }

            // Defaults fallback
            switch (p)
            {
                case "source": return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                case "rules": return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                case "events": return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                case "variables": return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                case "structure": return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                case "layout": return Guid.Parse("c414ed00-8cc4-4f44-8820-4baf93547173");
                case "webform": return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
                case "patterninstance": return Guid.Parse("a51ced48-7bee-0001-ab12-04e9e32123d1");
                case "help": return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
                case "documentation": return Guid.Parse("babf62c5-0111-49e9-a1c3-cc004d90900a");
                default: return Guid.Empty;
            }
        }

        public static KBObjectPart GetPart(KBObject obj, string partName)
        {
            Guid partGuid = GetPartGuid(obj.TypeDescriptor.Name, partName);
            
            if (partGuid != Guid.Empty)
            {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.Type == partGuid);
                if (part != null) return part;
            }

            // Dynamic Discovery Fallback
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p.TypeDescriptor != null && p.TypeDescriptor.Name.Equals(partName, StringComparison.OrdinalIgnoreCase)) return p;
                if (p is ISource && MatchesSourcePart(partName, p.TypeDescriptor?.Name)) return p;
                if (p.GetType().Name.Equals("VariablesPart") && partName.Equals("Variables", StringComparison.OrdinalIgnoreCase)) return p;
            }

            if (partName.Equals("Source", StringComparison.OrdinalIgnoreCase) || partName.Equals("Code", StringComparison.OrdinalIgnoreCase))
            {
                return obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
            }

            return null;
        }

        public static string[] GetAvailableParts(KBObject obj)
        {
            if (obj == null)
            {
                return new string[0];
            }

            var names = obj.Parts
                .Cast<KBObjectPart>()
                .Select(GetDisplayPartName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                // PatternVirtual has no readable SDK path through the MCP — hide until one exists.
                .Where(name => !string.Equals(name, "PatternVirtual", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // "Source" and "Events" both resolve to the same ISource part on WebPanels/Transactions —
            // keep only the canonical "Events" name; FindPart still accepts "Source" as alias.
            bool hasEvents = names.Any(n => string.Equals(n, "Events", StringComparison.OrdinalIgnoreCase));
            if (hasEvents)
                names.RemoveAll(n => string.Equals(n, "Source", StringComparison.OrdinalIgnoreCase));

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        /// First ISource part's text, or empty when the object exposes none.
        public static string GetFirstSourceText(KBObject obj)
        {
            if (obj == null) return string.Empty;
            foreach (var p in obj.Parts)
                if (p is ISource s) return s.Source ?? string.Empty;
            return string.Empty;
        }

        private static string GetDisplayPartName(KBObjectPart part)
        {
            if (part == null)
            {
                return null;
            }

            if (part is ISource)
            {
                var sourceName = part.TypeDescriptor?.Name;
                if (string.Equals(sourceName, "Events", StringComparison.OrdinalIgnoreCase))
                {
                    return "Events";
                }

                return "Source";
            }

            if (part.GetType().Name.Equals("VariablesPart", StringComparison.OrdinalIgnoreCase))
            {
                return "Variables";
            }

            if (!string.IsNullOrWhiteSpace(part.TypeDescriptor?.Name))
            {
                return part.TypeDescriptor.Name;
            }

            return part.GetType().Name.Replace("Part", "");
        }
    }
}
