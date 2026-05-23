using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 a11y audit. Drives chrome-devtools-axi's a11y subcommand when present, else falls
    /// back to <c>npx playwright axe</c>. Both paths funnel through <see cref="IBrowserDriverInvoker"/>.
    /// Output normalised to {violations: [{rule, impact, helpUrl, nodes}], score, capturedAtUtc}.
    /// When neither driver is available, returns
    /// <c>{ skipped: true, code: "A11yDriverUnavailable", hint }</c>.
    /// The sub-driver wire format is stubbed — actual axe-core JSON parsing ships in the seam-only
    /// form; real chrome-devtools-axi a11y output schema can be filled in once finalised.
    /// </summary>
    public class A11yAuditService
    {
        private readonly IBrowserDriverInvoker _invoker;
        private readonly Func<string, DriverResult> _playwrightFallback;
        private const int DefaultTimeoutMs = 90000;

        public A11yAuditService(IBrowserDriverInvoker invoker, Func<string, DriverResult> playwrightFallback = null)
        {
            _invoker = invoker ?? new DefaultBrowserDriverInvoker();
            _playwrightFallback = playwrightFallback; // null in production = no fallback path
        }

        public JObject Audit(string target)
        {
            var result = new JObject
            {
                ["target"] = target,
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("o")
            };

            if (string.IsNullOrWhiteSpace(target))
            {
                result["ok"] = false;
                result["code"] = "invalid_request";
                return result;
            }

            // Try chrome-devtools-axi first.
            var driverPath = _invoker.ResolveDriverPath();
            DriverResult run = null;
            string driverUsed = null;

            if (!string.IsNullOrEmpty(driverPath))
            {
                run = _invoker.Invoke("a11y " + Quote(target + ".aspx"), DefaultTimeoutMs);
                if (run.ExitCode == 0 && !string.IsNullOrWhiteSpace(run.StdOut))
                    driverUsed = "chrome-devtools-axi";
            }

            // Fallback: playwright axe (only when caller supplied it; production wiring deferred).
            if (driverUsed == null && _playwrightFallback != null)
            {
                run = _playwrightFallback(target);
                if (run != null && run.ExitCode == 0 && !string.IsNullOrWhiteSpace(run.StdOut))
                    driverUsed = "playwright-axe";
            }

            if (driverUsed == null)
            {
                result["skipped"] = true;
                result["code"] = "A11yDriverUnavailable";
                result["hint"] = "Install chrome-devtools-axi or wire a playwright-axe fallback. Neither produced parseable output.";
                return result;
            }

            result["driverUsed"] = driverUsed;
            var (violations, score) = NormaliseAxeOutput(run.StdOut);
            result["violations"] = violations;
            result["score"] = score;
            result["ok"] = true;
            return result;
        }

        /// <summary>Best-effort axe-core JSON parser. Accepts either the axe top-level
        /// <c>{violations:[...]}</c> shape or a plain array. Unknown shape → empty list.</summary>
        internal static (JArray Violations, double Score) NormaliseAxeOutput(string raw)
        {
            var violations = new JArray();
            double score = 1.0;
            if (string.IsNullOrWhiteSpace(raw)) return (violations, score);
            try
            {
                var t = JToken.Parse(raw.Trim());
                JArray source = null;
                if (t is JObject obj && obj["violations"] is JArray va) source = va;
                else if (t is JArray ar) source = ar;

                if (source != null)
                {
                    foreach (var v in source)
                    {
                        var item = new JObject
                        {
                            ["rule"] = v["id"]?.ToString() ?? v["rule"]?.ToString() ?? "unknown",
                            ["impact"] = v["impact"]?.ToString() ?? "minor",
                            ["helpUrl"] = v["helpUrl"]?.ToString() ?? "",
                            ["nodes"] = (v["nodes"] is JArray na) ? na.Count : 0
                        };
                        violations.Add(item);
                    }
                }
            }
            catch { /* leave empty */ }

            // Crude score: 1.0 minus 0.1 per violation, capped at 0.
            score = Math.Max(0.0, 1.0 - 0.1 * violations.Count);
            return (violations, score);
        }

        private static string Quote(string s) =>
            "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
