using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// mode=cross_platform_impact — buckets a Transaction/SDT/Domain's callers by
    /// surface (Web vs SmartDevices) and surfaces points where the two sides
    /// drift (required-field mismatches, surface-gated Rules, …).
    ///
    /// Heuristic-grade by design: we read the existing in-memory SearchIndex —
    /// no SDK round trips. The agent gets a `_meta.confidence` hint so it knows
    /// which detectors were reachable on this invocation.
    ///
    /// Detectors implemented in v1:
    ///   - required_field_mismatch        (heuristic on caller-side source scan)
    ///   - validation_rule_only_on_one_side (scans the target Transaction's Rules
    ///     part for `if &surface = "Web"` / SmartDevices gates)
    /// Stubbed (envelope-shape stable, detector="pending"):
    ///   - type_coercion_only_on_one_side
    ///   - null_handling_divergence
    /// </summary>
    internal static class CrossPlatformImpactAnalyzer
    {
        // Type-buckets. A caller object's TypeDescriptor.Name (or index entry .Type)
        // is matched to a platform; types listed in `Both` can reach either
        // surface and rely on transitive caller-walk for disambiguation.
        private static readonly HashSet<string> WebTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WebPanel", "WebComponent", "MasterPage"
        };

        private static readonly HashSet<string> SdTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SDPanel", "Panel", "Menu", "WorkWithDevices", "Dashboard"
        };

        /// <summary>
        /// Classifies a caller by surface. Returns ("Web"|"SmartDevices"|"Both"|"Unknown").
        /// Procedures (and other shared types) walk up to depth=3 callers via the
        /// index and return "Both" if reachable from both, single-platform otherwise.
        /// </summary>
        internal static string ClassifyCallerPlatform(string callerName, SearchIndex index, int depth = 3)
        {
            if (index?.Objects == null || string.IsNullOrEmpty(callerName)) return "Unknown";
            if (!TryFindEntry(index, callerName, out var entry)) return "Unknown";

            string t = entry.Type ?? "";
            if (WebTypes.Contains(t)) return "Web";
            if (SdTypes.Contains(t)) return "SmartDevices";

            // Shared types (Procedure, DataProvider, Transaction-BC) → walk callers.
            bool web = false, sd = false;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Name };
            var frontier = new Queue<(string name, int d)>();
            frontier.Enqueue((entry.Name, 0));
            while (frontier.Count > 0)
            {
                var (cur, d) = frontier.Dequeue();
                if (d >= depth) continue;
                if (!TryFindEntry(index, cur, out var curEntry)) continue;
                foreach (var up in curEntry.CalledBy ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(up) || !visited.Add(up)) continue;
                    if (!TryFindEntry(index, up, out var upEntry)) continue;
                    string ut = upEntry.Type ?? "";
                    if (WebTypes.Contains(ut)) web = true;
                    else if (SdTypes.Contains(ut)) sd = true;
                    else frontier.Enqueue((up, d + 1));

                    if (web && sd) return "Both";
                }
            }
            if (web && sd) return "Both";
            if (web) return "Web";
            if (sd) return "SmartDevices";
            return "Unknown";
        }

        private static bool TryFindEntry(SearchIndex index, string bareName, out SearchIndex.IndexEntry entry)
        {
            entry = null;
            if (index?.Objects == null || string.IsNullOrEmpty(bareName)) return false;
            // Try common keys first; fall back to a value scan.
            foreach (var prefix in new[] { "Procedure", "Transaction", "WebPanel", "SDPanel", "DataProvider", "WebComponent", "MasterPage", "SDT", "Domain", "Object", "Menu", "Panel", "WorkWithDevices", "Dashboard" })
            {
                if (index.Objects.TryGetValue(prefix + ":" + bareName, out var hit) && hit != null) { entry = hit; return true; }
            }
            foreach (var kv in index.Objects)
            {
                if (kv.Value != null && string.Equals(kv.Value.Name, bareName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = kv.Value;
                    return true;
                }
            }
            return false;
        }

        public sealed class Result
        {
            public string TargetName;
            public string TargetType;
            public List<JObject> WebCallers = new List<JObject>();
            public List<JObject> SdCallers = new List<JObject>();
            public List<JObject> Divergence = new List<JObject>();
            public List<string> DetectorsRun = new List<string>();
            public List<string> DetectorsPending = new List<string>();
        }

        /// <summary>
        /// Build the analysis. `targetRulesSource` is the Rules part text of the
        /// target Transaction (when applicable) — pass null/empty for non-Trn
        /// targets or when the source isn't available.
        ///
        /// `callerSourceResolver` is the same shape as ParentContext: given a
        /// (callerName, partName) tuple returns the part text, or null. Pass
        /// null in unit tests to skip the source-driven detectors.
        /// </summary>
        public static Result Analyze(
            string targetName,
            string targetType,
            SearchIndex index,
            string targetRulesSource,
            Func<string, string, string> callerSourceResolver)
        {
            var r = new Result { TargetName = targetName, TargetType = targetType };

            if (index?.Objects == null) return r;
            if (!TryFindEntry(index, targetName, out var targetEntry)) return r;

            // 1) Bucket each caller by surface.
            foreach (var caller in (targetEntry.CalledBy ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(caller)) continue;
                string callerType = TryFindEntry(index, caller, out var ce) ? (ce.Type ?? "Object") : "Object";
                string platform = ClassifyCallerPlatform(caller, index);
                var node = new JObject
                {
                    ["name"] = caller,
                    ["type"] = callerType,
                    ["platform"] = platform
                };
                if (platform == "Web" || platform == "Both") r.WebCallers.Add(node);
                if (platform == "SmartDevices" || platform == "Both") r.SdCallers.Add(node);
            }

            // 2) Detector: validation_rule_only_on_one_side.
            //    Scans target's Rules part for surface-gated branches.
            r.DetectorsRun.Add("validation_rule_only_on_one_side");
            if (!string.IsNullOrEmpty(targetRulesSource))
            {
                // `if &surface = "Web"` / `if Platform.IsSmartDevice()` style gates.
                // Cheap textual scan — surface-conditional rules are uncommon enough
                // that a precise grammar isn't worth it here.
                var reWebGate = new System.Text.RegularExpressions.Regex(
                    "\\bif\\b[^\\n]*\\b(surface|platform)\\b[^\\n]*=\\s*[\"']Web[\"']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var reSdGate = new System.Text.RegularExpressions.Regex(
                    "\\b(if\\b[^\\n]*\\b(surface|platform)\\b[^\\n]*=\\s*[\"'](SmartDevices|Android|iOS)[\"']|Platform\\.IsSmartDevice\\s*\\(\\s*\\))",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                bool webGated = reWebGate.IsMatch(targetRulesSource);
                bool sdGated = reSdGate.IsMatch(targetRulesSource);
                if (webGated || sdGated)
                {
                    r.Divergence.Add(new JObject
                    {
                        ["kind"] = "validation_rule_only_on_one_side",
                        ["Web"] = webGated ? "gated" : "ungated",
                        ["SmartDevices"] = sdGated ? "gated" : "ungated",
                        ["severity"] = "warning",
                        ["remediation"] = "Surface-gated Rule detected on the target. Confirm whether the gate is intentional; agents calling from the un-gated surface will hit the alternative branch."
                    });
                }
            }

            // 3) Detector: required_field_mismatch.
            //    For each attribute mentioned in the target's Rules `Error(... if X.IsEmpty())`
            //    pattern, check whether callers on each side guard the assignment.
            r.DetectorsRun.Add("required_field_mismatch");
            if (callerSourceResolver != null && (r.WebCallers.Count > 0 || r.SdCallers.Count > 0))
            {
                var requiredAttrs = ExtractRequiredAttributes(targetRulesSource);
                foreach (var attr in requiredAttrs)
                {
                    var webShape = ProbeCallerSetShape(r.WebCallers, attr, callerSourceResolver);
                    var sdShape = ProbeCallerSetShape(r.SdCallers, attr, callerSourceResolver);
                    if (webShape != sdShape && webShape != "absent" && sdShape != "absent")
                    {
                        r.Divergence.Add(new JObject
                        {
                            ["kind"] = "required_field_mismatch",
                            ["field"] = attr,
                            ["Web"] = webShape,
                            ["SmartDevices"] = sdShape,
                            ["severity"] = "warning",
                            ["remediation"] = "Field '" + attr + "' is treated as " + webShape + " on Web callers but " + sdShape + " on SmartDevices. Tighten the weaker side or relax the Rule."
                        });
                    }
                }
            }

            // 4) Pending detectors — placeholder envelopes so callers see a stable shape.
            r.DetectorsPending.Add("type_coercion_only_on_one_side");
            r.DetectorsPending.Add("null_handling_divergence");

            return r;
        }

        private static List<string> ExtractRequiredAttributes(string rulesSource)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(rulesSource)) return list;
            // Patterns:
            //   Error('msg') if &x.IsEmpty();
            //   Error('msg') if AluCod.IsEmpty();
            //   Msg('msg', error) if AluCod = nullvalue(AluCod);
            var re = new System.Text.RegularExpressions.Regex(
                "(?:Error|Msg)\\s*\\([^)]*\\)\\s+if\\s+&?([A-Za-z_][A-Za-z0-9_]*)\\s*(?:\\.IsEmpty\\s*\\(\\s*\\)|=\\s*nullvalue)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in re.Matches(rulesSource))
            {
                string name = m.Groups[1].Value;
                if (seen.Add(name)) list.Add(name);
            }
            return list;
        }

        // Returns "required" if every caller writes the attribute without a guard,
        // "optional" if any caller guards (or skips) the write, "absent" if none mention it.
        private static string ProbeCallerSetShape(List<JObject> callers, string attr, Func<string, string, string> resolver)
        {
            bool mentioned = false;
            bool anyGuarded = false;
            bool anyUnguarded = false;
            var reAssign = new System.Text.RegularExpressions.Regex(
                "(?<!\\.)\\b" + System.Text.RegularExpressions.Regex.Escape(attr) + "\\s*=",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var reGuard = new System.Text.RegularExpressions.Regex(
                "if\\s+(?:not\\s+)?&?" + System.Text.RegularExpressions.Regex.Escape(attr) + "\\.IsEmpty\\s*\\(\\s*\\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var c in callers)
            {
                string name = c["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var partName in new[] { "Source", "Events", "Rules" })
                {
                    string src = null;
                    try { src = resolver(name, partName); } catch { }
                    if (string.IsNullOrEmpty(src)) continue;
                    if (!reAssign.IsMatch(src)) continue;
                    mentioned = true;
                    if (reGuard.IsMatch(src)) anyGuarded = true;
                    else anyUnguarded = true;
                }
            }
            if (!mentioned) return "absent";
            if (anyUnguarded && !anyGuarded) return "required";
            if (anyGuarded && !anyUnguarded) return "optional";
            return "mixed";
        }
    }
}
