using System;
using System.Collections.Generic;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 86 — genexus_what_if. Simulates a typed change (e.g. attribute type
    /// change) without mutating anything. Combines the AnalyzeService callers
    /// graph for the target attribute with a substring scan of each caller's
    /// source to categorise the impact as breaks / probably_safe / unknown.
    /// Strictly read-only.
    /// </summary>
    public class WhatIfService
    {
        private readonly AnalyzeService _analyzeService;
        private readonly ObjectService _objectService;

        public WhatIfService(AnalyzeService analyzeService, ObjectService objectService)
        {
            _analyzeService = analyzeService;
            _objectService = objectService;
        }

        public string Simulate(JObject change)
        {
            try
            {
                if (change == null)
                    return Error("MissingChange", "change object is required.");
                string kind = change["kind"]?.ToString();
                string target = change["target"]?.ToString();
                string attribute = change["attribute"]?.ToString();
                string oldType = change["oldType"]?.ToString();
                string newType = change["newType"]?.ToString();
                if (string.IsNullOrWhiteSpace(target))
                    return Error("MissingTarget", "change.target is required.");

                string impactName = !string.IsNullOrEmpty(attribute) ? attribute : target;

                // 1) Walk callers via ImpactAnalysis (already integrated with the index).
                JObject impact;
                try
                {
                    string raw = _analyzeService?.ImpactAnalysis(impactName);
                    impact = string.IsNullOrEmpty(raw) ? new JObject() : JObject.Parse(raw);
                }
                catch (Exception ex)
                {
                    // Impact analysis failed — use empty callers so WhatIf still returns a result.
                    impact = new JObject { ["_impactError"] = ex.Message };
                }

                var callers = impact["callers"] as JArray ?? new JArray();

                // 2) Categorise each caller by scanning its source for usage of
                //    the attribute name. Type changes that narrow precision /
                //    change semantics (Numeric → Character, etc.) are flagged
                //    as 'breaks' — every numeric op on the attribute may now
                //    fail. Same-family transitions (Numeric(10) → Numeric(12))
                //    are 'probably_safe'.
                var breaks = new JArray();
                var probablySafe = new JArray();
                var unknown = new JArray();
                bool semanticBreak = IsSemanticBreak(oldType, newType);
                foreach (var c in callers)
                {
                    string callerName = c?.ToString();
                    if (string.IsNullOrEmpty(callerName)) continue;
                    string usage = TryGetSource(callerName);
                    bool referenced = !string.IsNullOrEmpty(usage)
                        && !string.IsNullOrEmpty(attribute)
                        && usage.IndexOf(attribute, StringComparison.OrdinalIgnoreCase) >= 0;

                    var entry = new JObject
                    {
                        ["name"] = callerName,
                        ["referencesAttribute"] = referenced
                    };
                    if (!referenced && !string.IsNullOrEmpty(attribute))
                    {
                        unknown.Add(entry);
                        continue;
                    }
                    if (semanticBreak)
                    {
                        entry["reason"] = "type family change (" + (oldType ?? "?") + " → " + (newType ?? "?") + ") on referenced attribute";
                        breaks.Add(entry);
                    }
                    else if (referenced)
                    {
                        entry["reason"] = "references attribute; same-family type change is usually safe";
                        probablySafe.Add(entry);
                    }
                    else
                    {
                        unknown.Add(entry);
                    }
                }

                var whatIfResult = new JObject
                {
                    ["change"] = change,
                    ["kind"] = kind ?? "type_change",
                    ["impactedCount"] = callers.Count,
                    ["breaks"] = breaks,
                    ["probably_safe"] = probablySafe,
                    ["unknown"] = unknown,
                    ["note"] = "Read-only simulation. No mutation performed. Heuristic: attribute name substring + type-family compatibility."
                };
                // issue #25 follow-up (P0): impact analysis can report that it could
                // NOT confirm the caller set (index edges not enriched, no SDK cross-check)
                // — propagate that uncertainty instead of presenting impactedCount:0 as a
                // confident "this change breaks nothing".
                if (impact["indexEdgesMissing"] != null)
                    whatIfResult["indexEdgesMissing"] = impact["indexEdgesMissing"];
                var upstreamRisk = impact["riskLevel"]?.ToString();
                if (!string.Equals(upstreamRisk, "None", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(upstreamRisk, "Unknown", StringComparison.OrdinalIgnoreCase)
                        || (impact["verifiedZero"] != null && !(impact["verifiedZero"].ToObject<bool>()))))
                {
                    whatIfResult["impactUnconfirmed"] = true;
                    whatIfResult["note"] = "Read-only simulation, but the caller set could NOT be confirmed (the index isn't enriched for this attribute and no SDK cross-check was available). An empty breaks/impacted list here does NOT mean the change is safe — re-run genexus_analyze(mode=impact) once the index is enriched.";
                }
                if (impact["_impactError"] != null)
                    whatIfResult["impactError"] = impact["_impactError"];
                return McpResponse.Ok(code: "WhatIfComputed", result: whatIfResult);
            }
            catch (Exception ex)
            {
                return Error("SimulationFailed", ex.Message);
            }
        }

        private string TryGetSource(string callerName)
        {
            try
            {
                // Pull all parts; cheaper than knowing the exact source part.
                string raw = _objectService?.ReadObject(callerName);
                return raw ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool IsSemanticBreak(string oldType, string newType)
        {
            if (string.IsNullOrEmpty(oldType) || string.IsNullOrEmpty(newType)) return false;
            if (string.Equals(oldType, newType, StringComparison.OrdinalIgnoreCase)) return false;
            string a = Family(oldType);
            string b = Family(newType);
            return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Coarse type families. Same family ⇒ probably_safe; different ⇒ break.
        private static string Family(string t)
        {
            string s = (t ?? string.Empty).Trim().ToLowerInvariant();
            // Strip length/decimals: "numeric(10.2)" → "numeric"
            int lp = s.IndexOf('(');
            if (lp >= 0) s = s.Substring(0, lp);
            switch (s)
            {
                case "numeric":
                case "number":
                case "int":
                case "integer":
                case "long":
                case "decimal":
                case "double":
                case "float":
                    return "numeric";
                case "character":
                case "varchar":
                case "longvarchar":
                case "char":
                case "string":
                    return "string";
                case "date":
                case "datetime":
                case "time":
                    return "date";
                case "boolean":
                case "bool":
                    return "boolean";
                default:
                    return s;
            }
        }

        private static string Error(string code, string message) =>
            McpResponse.Err(code: code, message: message);
    }
}
