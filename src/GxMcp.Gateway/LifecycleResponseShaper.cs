using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // v2.3.8 (Task 6.1) — friction report 2026-05-15 #9: a failed build's
    // BuildService.GetStatus payload (full Errors[], Warnings[], Output) blows
    // the assistant's tool-result budget. Compact mode replaces it with counts,
    // top-10 errors, and warning dedup (one entry per message + sampleLocations).
    // Triggered when the lifecycle tool args carry compact != false; default true.
    public static class LifecycleResponseShaper
    {
        public const int ErrorCap = 10;
        public const int WarningSampleCap = 3;

        public static string Compact(string rawJson, bool compact)
        {
            if (!compact || string.IsNullOrWhiteSpace(rawJson)) return rawJson;

            JObject obj;
            try { obj = JObject.Parse(rawJson); }
            catch { return rawJson; }

            // Only reshape worker BuildTaskStatus shapes (have ErrorCount or Errors array).
            // Anything else (job envelopes, KB-side payloads, etc.) passes through verbatim.
            if (obj["Errors"] == null && obj["Warnings"] == null && obj["ErrorCount"] == null)
                return rawJson;

            var errors = obj["Errors"] as JArray ?? new JArray();
            var warnings = obj["Warnings"] as JArray ?? new JArray();
            int errCount = obj["ErrorCount"]?.Value<int?>() ?? errors.Count;
            int warnCount = obj["WarningCount"]?.Value<int?>() ?? warnings.Count;

            var dedupedWarnings = new JArray();
            var groups = warnings
                .GroupBy(w => (w is JObject jo ? jo["message"]?.ToString() : w?.ToString()) ?? "")
                .ToList();
            foreach (var g in groups)
            {
                var sample = g.Take(WarningSampleCap)
                    .Select(w => w is JObject jo ? (jo["location"]?.ToString() ?? jo.ToString(Newtonsoft.Json.Formatting.None)) : w?.ToString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
                dedupedWarnings.Add(new JObject
                {
                    ["message"] = g.Key,
                    ["count"] = g.Count(),
                    ["sampleLocations"] = new JArray(sample)
                });
            }

            var compactObj = new JObject
            {
                ["Status"] = obj["Status"] ?? obj["status"],
                ["Phase"] = obj["Phase"],
                ["TaskId"] = obj["TaskId"],
                ["ExitCode"] = obj["ExitCode"],
                ["errorCount"] = errCount,
                ["warningCount"] = warnCount,
                ["errors"] = new JArray(errors.Take(ErrorCap)),
                ["warnings"] = dedupedWarnings,
                ["summary"] = $"{errCount} errors / {warnCount} warnings",
                ["truncated"] = errCount > ErrorCap,
                ["compact"] = true
            };

            // FR (2026-05-21): when CS0246/CS2001 fired, BuildService already extracted
            // the missing object names into SuggestedRebuildTargets. Surface them as a
            // ready-to-fire retry hint so the agent doesn't have to scrape the paths
            // out of error[] by hand.
            var suggested = obj["SuggestedRebuildTargets"] as JArray;
            if (suggested != null && suggested.Count > 0)
            {
                var names = suggested.Select(t => t?.ToString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (names.Length > 0)
                {
                    compactObj["suggested_retry"] = new JObject
                    {
                        ["action"] = "build",
                        ["target"] = string.Join(",", names),
                        ["includeCallees"] = "direct",
                        ["hint"] = "CS0246/CS2001 referenced these objects — rebuild them (and direct callees) before retrying the full build."
                    };
                }
            }

            // Surface taskId/jobId for callers that want to fetch the raw payload later via action=result&compact=false.
            if (obj["jobId"] != null) compactObj["jobId"] = obj["jobId"];
            if (obj["ElapsedSeconds"] != null) compactObj["ElapsedSeconds"] = obj["ElapsedSeconds"];
            if (obj["_meta"] != null) compactObj["_meta"] = obj["_meta"];

            return compactObj.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static bool ShouldCompact(JObject toolArgs)
        {
            if (toolArgs == null) return true;
            var v = toolArgs["compact"];
            if (v == null) return true;
            if (v.Type == JTokenType.Boolean) return v.Value<bool>();
            if (v.Type == JTokenType.String)
            {
                var s = v.ToString();
                return !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(s, "0", StringComparison.Ordinal);
            }
            return true;
        }
    }
}
