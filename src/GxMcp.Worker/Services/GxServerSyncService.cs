using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Read-only v1 surface for GxServer sync state. Probes well-known
    /// metadata files under the active KB; does NOT call the GxServer
    /// SDK or perform any sync operation. When metadata is present but
    /// unparseable, returns {connected:true, note:"metadata parsing pending"}
    /// so callers still see a stable connection signal.
    /// </summary>
    public class GxServerSyncService
    {
        private readonly KbService _kb;

        public GxServerSyncService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "status";
            action = action.Trim().ToLowerInvariant();

            string kbPath = _kb?.GetKbPath();
            string kbAlias = Environment.GetEnvironmentVariable("GX_KB_ALIAS")
                             ?? (string.IsNullOrEmpty(kbPath) ? null : Path.GetFileName(kbPath.TrimEnd('\\', '/')));

            switch (action)
            {
                case "status":
                    return StatusEnvelope(kbPath, kbAlias);
                case "pending":
                    return PendingEnvelope(kbPath);
                case "conflicts":
                    return ConflictsEnvelope(kbPath);
                case "history":
                    int limit = args?["limit"]?.ToObject<int?>() ?? 10;
                    return HistoryEnvelope(kbPath, limit);
                default:
                    return new JObject
                    {
                        ["status"] = "Error",
                        ["code"] = "BadAction",
                        ["message"] = "Unknown action '" + action + "'. Expected one of: status, pending, conflicts, history."
                    }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        // ----- detection -----

        internal class DetectionResult
        {
            public bool Connected;
            public string DetectedPath;
        }

        internal static DetectionResult Detect(string kbPath)
        {
            var r = new DetectionResult();
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) return r;

            string p1 = Path.Combine(kbPath, "Repository", "Repository.gxs");
            if (File.Exists(p1)) { r.Connected = true; r.DetectedPath = p1; return r; }

            string p2 = Path.Combine(kbPath, ".gx", "gxserver-state.xml");
            if (File.Exists(p2)) { r.Connected = true; r.DetectedPath = p2; return r; }

            string p3 = Path.Combine(kbPath, ".gxserver", "state.xml");
            if (File.Exists(p3)) { r.Connected = true; r.DetectedPath = p3; return r; }

            return r;
        }

        // ----- envelope builders -----

        internal static string StatusEnvelope(string kbPath, string kbAlias)
        {
            var det = Detect(kbPath);
            var jo = new JObject
            {
                ["status"] = "Success",
                ["connected"] = det.Connected,
                ["kbAlias"] = kbAlias ?? string.Empty
            };
            if (!det.Connected)
            {
                jo["hint"] = "This KB is not connected to a GeneXus Server instance.";
                return jo.ToString(Newtonsoft.Json.Formatting.None);
            }
            jo["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath;
            jo["detectedVia"] = det.DetectedPath;
            return jo.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string PendingEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return new JObject
                {
                    ["status"] = "Success",
                    ["connected"] = false,
                    ["hint"] = "This KB is not connected to a GeneXus Server instance."
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            return new JObject
            {
                ["status"] = "Success",
                ["connected"] = true,
                ["objects"] = new JArray(),
                ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string ConflictsEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return new JObject
                {
                    ["status"] = "Success",
                    ["connected"] = false,
                    ["hint"] = "This KB is not connected to a GeneXus Server instance."
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            return new JObject
            {
                ["status"] = "Success",
                ["connected"] = true,
                ["conflicts"] = new JArray(),
                ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string HistoryEnvelope(string kbPath, int limit)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return new JObject
                {
                    ["status"] = "Success",
                    ["connected"] = false,
                    ["hint"] = "This KB is not connected to a GeneXus Server instance."
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            if (limit <= 0) limit = 10;
            if (limit > 200) limit = 200;
            return new JObject
            {
                ["status"] = "Success",
                ["connected"] = true,
                ["history"] = new JArray(),
                ["limit"] = limit,
                ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
