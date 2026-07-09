using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// GxServer (Team Development) sync-state surface for genexus_gxserver.
    ///
    /// Primary path is the GeneXus SDK: it resolves
    /// <c>ITeamDevClientService</c> (the model-level Team Development service)
    /// and answers connection / pending / conflict queries from the live KB —
    /// the same source the IDE's Team Development tab reads. This replaces the
    /// old filesystem-heuristic that produced false "not connected" results on
    /// KBs that ARE linked to a GeneXus Server (the link lives in the KB
    /// metadata DB, not in well-known files on disk).
    ///
    /// If the Team Development service isn't loaded in the worker session, or
    /// the SDK throws, it falls back to the legacy file-probe envelopes so the
    /// caller still gets a stable (if coarser) answer.
    ///
    /// commit/update/lock/resolve are WRITE actions and delegate to the
    /// sibling <see cref="GxServerWriteService"/> instead — kept separate so
    /// this read path stays a pure query surface.
    /// </summary>
    public class GxServerSyncService
    {
        private readonly KbService _kb;
        private readonly GxServerWriteService _write;

        public GxServerSyncService(KbService kb)
        {
            _kb = kb;
            _write = new GxServerWriteService(kb);
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "status";
            action = action.Trim().ToLowerInvariant();

            switch (action)
            {
                case "commit":
                case "update":
                case "lock":
                case "resolve":
                    return _write.Run(action, args ?? new JObject());
            }

            string kbPath = _kb?.GetKbPath();
            string kbAlias = Environment.GetEnvironmentVariable("GX_KB_ALIAS")
                             ?? (string.IsNullOrEmpty(kbPath) ? null : Path.GetFileName(kbPath.TrimEnd('\\', '/')));

            switch (action)
            {
                case "status":
                case "pending":
                case "conflicts":
                case "history":
                    break;
                default:
                    return McpResponse.Err(
                        code: "BadAction",
                        message: "Unknown action '" + action + "'. Expected one of: status, pending, conflicts, history, commit, update, lock, resolve.",
                        hint: "Pass action=status, action=pending, action=conflicts, action=history, action=commit, action=update, action=lock, or action=resolve.",
                        nextSteps: new JArray { McpResponse.NextStep("genexus_gxserver", new JObject { ["action"] = "status" }, "Query connection status.") });
            }

            // Primary: SDK-backed answer from the live KB. Returns null when the
            // Team Development service is unavailable or any SDK call throws, in
            // which case we drop to the legacy file-heuristic below.
            int limit = args?["limit"]?.ToObject<int?>() ?? 10;
            string sdk = TrySdkEnvelope(action, kbAlias, limit);
            if (sdk != null) return sdk;

            switch (action)
            {
                case "status": return StatusEnvelope(kbPath, kbAlias);
                case "pending": return PendingEnvelope(kbPath);
                case "conflicts": return ConflictsEnvelope(kbPath);
                default: return HistoryEnvelope(kbPath, limit);
            }
        }

        // ----- SDK-backed path (authoritative) -----

        /// <summary>
        /// Builds the response from <see cref="ITeamDevClientService"/> against
        /// the open KB. Returns null (caller falls back to the file-heuristic)
        /// when the service isn't registered in this worker or the SDK throws.
        /// </summary>
        private string TrySdkEnvelope(string action, string kbAlias, int limit)
        {
            KnowledgeBase kb;
            ITeamDevClientService svc;
            try
            {
                kb = _kb?.GetKB() as KnowledgeBase;
                if (kb == null) return null;
                svc = SdkServices.TryGetService<ITeamDevClientService>();
                if (svc == null) return null;
            }
            catch { return null; }

            try
            {
                bool linked = svc.IsLinkedKB(kb);
                var model = kb.DesignModel;

                switch (action)
                {
                    case "status":
                    {
                        if (!linked)
                        {
                            return McpResponse.Ok(
                                code: "GxServerStatusRetrieved",
                                result: new JObject
                                {
                                    ["connected"] = false,
                                    ["kbAlias"] = kbAlias ?? string.Empty,
                                    ["hint"] = "This KB is not linked to a GeneXus Server instance.",
                                    ["source"] = "sdk:ITeamDevClientService"
                                });
                        }
                        return McpResponse.Ok(
                            code: "GxServerStatusRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["kbAlias"] = kbAlias ?? string.Empty,
                                ["serverUrl"] = SafeStr(() => svc.GetServerUrl(kb)),
                                ["host"] = SafeStr(() => svc.GetGXserverHost(kb)),
                                ["remoteKbName"] = SafeStr(() => svc.GetRemoteKBName(kb)),
                                ["remoteVersionName"] = SafeStr(() => svc.RemoteVersionName(model)),
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }

                    case "pending":
                    {
                        if (!linked) return NotLinked();
                        var objects = new JArray();
                        foreach (var h in EnumLocalChanges(svc, model))
                        {
                            objects.Add(new JObject
                            {
                                ["name"] = SafeStr(() => (string)h.ObjectName) ?? SafeStr(() => h.GetName()),
                                ["operation"] = SafeStr(() => h.Operation.ToString()),
                                ["lastChange"] = SafeStr(() => h.LastChange.ToUniversalTime().ToString("o")),
                                ["user"] = SafeStr(() => (string)h.Username)
                            });
                        }
                        return McpResponse.Ok(
                            code: "GxServerPendingRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["count"] = objects.Count,
                                ["objects"] = objects,
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }

                    case "conflicts":
                    {
                        if (!linked) return NotLinked();
                        var conflicts = new JArray();
                        foreach (var ct in new[] { UpdateConflict.YesMustOverwrite, UpdateConflict.YesWithAutoMerge })
                        {
                            foreach (var e in EnumConflicts(svc, model, ct))
                            {
                                conflicts.Add(new JObject
                                {
                                    ["object"] = SafeStr(() => e.ToString()),
                                    ["conflictType"] = ct.ToString()
                                });
                            }
                        }
                        return McpResponse.Ok(
                            code: "GxServerConflictsRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["count"] = conflicts.Count,
                                ["conflicts"] = conflicts,
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }

                    default: // history — local change log (most-recent first). Remote
                             // revision history requires server credentials, which this
                             // read-only surface does not collect.
                    {
                        if (!linked) return NotLinked();
                        if (limit <= 0) limit = 10;
                        if (limit > 200) limit = 200;
                        var rows = new System.Collections.Generic.List<JObject>();
                        foreach (var h in EnumLocalChanges(svc, model))
                        {
                            rows.Add(new JObject
                            {
                                ["name"] = SafeStr(() => (string)h.ObjectName) ?? SafeStr(() => h.GetName()),
                                ["operation"] = SafeStr(() => h.Operation.ToString()),
                                ["lastChange"] = SafeStr(() => h.LastChange.ToUniversalTime().ToString("o")),
                                ["user"] = SafeStr(() => (string)h.Username)
                            });
                        }
                        rows.Sort((a, b) => string.CompareOrdinal((string)b["lastChange"], (string)a["lastChange"]));
                        var history = new JArray();
                        for (int i = 0; i < rows.Count && i < limit; i++) history.Add(rows[i]);
                        return McpResponse.Ok(
                            code: "GxServerHistoryRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["limit"] = limit,
                                ["history"] = history,
                                ["scope"] = "localChanges",
                                ["note"] = "Local (uncommitted) change log. Remote revision history requires server credentials.",
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }
                }
            }
            catch { return null; }
        }

        private static string NotLinked()
        {
            return McpResponse.Ok(
                code: "GxServerStatusRetrieved",
                result: new JObject
                {
                    ["connected"] = false,
                    ["hint"] = "This KB is not linked to a GeneXus Server instance.",
                    ["source"] = "sdk:ITeamDevClientService"
                });
        }

        private static IEnumerable<dynamic> EnumLocalChanges(ITeamDevClientService svc, KBModel model)
        {
            IEnumerable raw = svc.GetLocalChanges(model);
            if (raw == null) yield break;
            foreach (var h in raw) yield return h;
        }

        private static IEnumerable<dynamic> EnumConflicts(ITeamDevClientService svc, KBModel model, UpdateConflict ct)
        {
            IEnumerable raw = svc.GetConflictEntities(model, ct);
            if (raw == null) yield break;
            foreach (var e in raw) yield return e;
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        // ----- legacy file-heuristic fallback (also exercised by unit tests) -----

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

        internal static string StatusEnvelope(string kbPath, string kbAlias)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerStatusRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["kbAlias"] = kbAlias ?? string.Empty,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerStatusRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["kbAlias"] = kbAlias ?? string.Empty,
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath,
                    ["detectedVia"] = det.DetectedPath
                });
        }

        internal static string PendingEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerPendingRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerPendingRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["objects"] = new JArray(),
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }

        internal static string ConflictsEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerConflictsRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerConflictsRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["conflicts"] = new JArray(),
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }

        internal static string HistoryEnvelope(string kbPath, int limit)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerHistoryRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            if (limit <= 0) limit = 10;
            if (limit > 200) limit = 200;
            return McpResponse.Ok(
                code: "GxServerHistoryRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["history"] = new JArray(),
                    ["limit"] = limit,
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }
    }
}
