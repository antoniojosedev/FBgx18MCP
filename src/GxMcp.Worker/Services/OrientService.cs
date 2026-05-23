using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 65 — welcome card for a new session.
    ///
    /// Returns a cheap-to-build snapshot the agent can read once at session
    /// start: KB name/path, last 5 edited objects (from .gx/snapshots/),
    /// top 3 KB-applicable gotcha hints, and a top-tools placeholder
    /// (gateway-side stats live in OperationTracker; the worker exposes
    /// only what it can compute locally).
    /// </summary>
    public class OrientService
    {
        private readonly KbService _kbService;

        public OrientService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Welcome()
        {
            string kbPath = null;
            string kbName = null;
            try
            {
                kbPath = _kbService?.GetKbPath();
                var kb = _kbService?.GetKB();
                kbName = kb?.Name;
            }
            catch { }

            var resp = new JObject
            {
                ["status"] = "Success",
                ["kb"] = new JObject
                {
                    ["name"] = kbName ?? "(no KB open)",
                    ["path"] = kbPath ?? ""
                },
                ["recentEdits"] = BuildRecentEdits(kbPath),
                ["gotchas"] = BuildGotchas(),
                ["topTools"] = new JArray
                {
                    "genexus_inspect", "genexus_edit", "genexus_read",
                    "genexus_lifecycle", "genexus_analyze"
                },
                ["topToolsNote"] = "Static default — live per-session stats live in genexus_whoami.stats.tools."
            };
            return resp.ToString();
        }

        private static JArray BuildRecentEdits(string kbPath)
        {
            var arr = new JArray();
            if (string.IsNullOrEmpty(kbPath)) return arr;
            string snapDir = Path.Combine(kbPath, ".gx", "snapshots");
            if (!Directory.Exists(snapDir)) return arr;
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = Directory.EnumerateFiles(snapDir)
                    .Where(p =>
                    {
                        string fn = Path.GetFileName(p);
                        return fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                            || fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(p => p, StringComparer.Ordinal);

                foreach (var f in files)
                {
                    string fn = Path.GetFileName(f);
                    // Filename shape: <guidSanitized>-<part>-<yyyyMMddTHHmmssfffZ>.bak
                    var parts = fn.Split('-');
                    if (parts.Length < 3) continue;
                    string guidKey = parts[0];
                    if (!seen.Add(guidKey)) continue;
                    arr.Add(new JObject
                    {
                        ["objectGuid"] = guidKey,
                        ["part"] = parts.Length >= 2 ? parts[1] : "",
                        ["snapshotPath"] = f
                    });
                    if (arr.Count >= 5) break;
                }
            }
            catch { }
            return arr;
        }

        private static JArray BuildGotchas()
        {
            // Curated KB-agnostic baseline; gateway-side per-KB customization
            // happens in BuildPlaybooksBlock. Top 3 most useful for a new agent.
            return new JArray
            {
                "html_form_inline_js: <script> inside Format=\"HTML\" gxTextBlock is escaped. Use <body onmousedown> + addEventListener for runtime JS.",
                "popup_call_async: .Popup() returns immediately — out-params are EMPTY on the next line. Handle them in Refresh, gated by AUTO_REFRESH=VARS_CHANGE.",
                "verify_in_browser: chrome-devtools-axi is the right tool to drive aspx flows after a build."
            };
        }
    }
}
