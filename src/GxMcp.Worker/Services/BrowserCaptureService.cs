using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 browser-verify pipeline: opens the target object via the headless browser driver,
    /// then collects console / network / exception traces. Reuses <see cref="IBrowserDriverInvoker"/>
    /// so tests can mock the shell-out. Gracefully degrades to a typed
    /// <c>{ skipped, code: "BrowserDriverUnavailable" }</c> envelope when the driver isn't on PATH.
    /// Production wiring of the per-capture CLI sub-verbs is stubbed — the seam ships now, the
    /// underlying chrome-devtools-axi sub-commands (e.g. <c>console-tail</c> / <c>network-har</c>)
    /// can be filled in once finalised.
    /// </summary>
    public class BrowserCaptureService
    {
        private readonly ObjectService _objectService;
        private readonly IBrowserDriverInvoker _invoker;
        private const int DefaultTimeoutMs = 60000;

        public BrowserCaptureService(ObjectService objectService, IBrowserDriverInvoker invoker)
        {
            _objectService = objectService;
            _invoker = invoker ?? new DefaultBrowserDriverInvoker();
        }

        public JObject Capture(string target, JArray captureKinds)
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
                result["message"] = "target is required";
                return result;
            }

            var driverPath = _invoker.ResolveDriverPath();
            if (string.IsNullOrEmpty(driverPath))
            {
                result["skipped"] = true;
                result["code"] = "BrowserDriverUnavailable";
                result["hint"] = "Install chrome-devtools-axi globally (npm i -g chrome-devtools-axi) or place on PATH.";
                return result;
            }
            result["driverUsed"] = driverPath;

            // Default capture set when none requested.
            var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (captureKinds != null)
            {
                foreach (var k in captureKinds)
                {
                    var s = k?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) kinds.Add(s);
                }
            }
            if (kinds.Count == 0)
            {
                kinds.Add("console"); kinds.Add("network"); kinds.Add("exceptions");
            }

            // 1) Open target (best-effort; the existing PreviewService handles full launcher flow,
            //    here we just `open target.aspx` so the driver attaches a tab. If this fails, the
            //    subsequent eval calls return empty buffers.
            var openRes = _invoker.Invoke("open " + Quote(target + ".aspx"), DefaultTimeoutMs);
            if (openRes.ExitCode != 0 && !openRes.TimedOut)
            {
                // Stubbed sub-verb may not exist in older driver versions — surface as warning, continue.
                result["openWarning"] = openRes.StdErr;
            }

            // 2) Collect each requested stream via deterministic JS eval, parse JSON when possible.
            if (kinds.Contains("console"))
            {
                var r = _invoker.Invoke("eval " + Quote("JSON.stringify(window.__gxConsoleErrors||[])"), DefaultTimeoutMs);
                result["console"] = ParseJsonArrayOrEmpty(r.StdOut);
            }
            if (kinds.Contains("network"))
            {
                var r = _invoker.Invoke("eval " + Quote("JSON.stringify((performance.getEntriesByType('resource')||[]).map(function(e){return{name:e.name,duration:e.duration,status:e.responseStatus||0};}))"), DefaultTimeoutMs);
                result["network"] = ParseJsonArrayOrEmpty(r.StdOut);
            }
            if (kinds.Contains("exceptions"))
            {
                var r = _invoker.Invoke("eval " + Quote("JSON.stringify(window.__gxUnhandledErrors||[])"), DefaultTimeoutMs);
                result["exceptions"] = ParseJsonArrayOrEmpty(r.StdOut);
            }

            result["ok"] = true;
            return result;
        }

        internal static JArray ParseJsonArrayOrEmpty(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new JArray();
            try
            {
                var t = JToken.Parse(s.Trim());
                if (t is JArray a) return a;
                return new JArray();
            }
            catch { return new JArray(); }
        }

        private static string Quote(string s) =>
            "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
