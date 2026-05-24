using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class HistoryService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;

        public HistoryService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
        }

        /// <summary>
        /// History dispatch. <paramref name="partName"/> + <paramref name="snapshotToken"/>
        /// drive the edit-snapshot <c>restore</c> action: <c>snapshot=latest</c> or
        /// a timestamp substring resolves to <c>&lt;kbPath&gt;/.gx/snapshots/&lt;guid&gt;-&lt;part&gt;-*.bak</c>
        /// and the prior bytes are routed back through <see cref="WriteService.WriteObject(string, string, string, string, bool, bool, bool, bool)"/>.
        /// When <paramref name="discard"/> is <c>true</c> and no snapshot token is
        /// supplied, the most recent EditSnapshotStore entry is restored — IDE
        /// <i>History | Restore</i> / <i>Discard changes</i> parity. Missing
        /// snapshots return a <c>NoSnapshot</c> envelope rather than an error.
        /// </summary>
        public string Execute(string target, string action, int versionId = 0,
                              string partName = null, string snapshotToken = null,
                              bool discard = false, bool dryRun = false)
        {
            try
            {
                switch (action?.ToLower())
                {
                    case "list":
                        if (!string.IsNullOrWhiteSpace(snapshotToken) || !string.IsNullOrWhiteSpace(partName))
                            return ListEditSnapshots(target, partName);
                        return ListRevisions(target);
                    case "get_source":
                        return GetVersionSource(target, versionId);
                    case "save":
                        return SaveSnapshot(target);
                    case "restore":
                        // Item 21 (friction 2026-05-22): dryRun=true returns the diff
                        // (current vs snapshot) without writing through SDK.
                        if (dryRun)
                            return DryRunRestore(target, partName, snapshotToken, discard);
                        if (!string.IsNullOrWhiteSpace(snapshotToken))
                            return RestoreEditSnapshot(target, partName, snapshotToken);
                        if (discard)
                            return DiscardLatestEditSnapshot(target, partName);
                        return RestoreSnapshot(target);
                    default:
                        return Models.McpResponse.Error("Unknown history action", target, action, "Supported actions are list, get_source, save and restore.");
                }
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// Item 21 (friction 2026-05-22) — universal dryRun for genexus_history
        /// action=restore. Resolves the same snapshot the live restore would
        /// pick, reads the current persisted source, and returns a unified
        /// diff envelope — no SDK write.
        /// </summary>
        private string DryRunRestore(string target, string partName, string snapshotToken, bool discard)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Error("DryRun failed", target, partName, ex.Message); }

            string kbPath = null;
            try { kbPath = _objectService.GetKbService().GetKbPath(); } catch { }
            string root = EditSnapshotStore.ResolveRoot(kbPath);
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;

            string path;
            if (!string.IsNullOrWhiteSpace(snapshotToken))
            {
                path = EditSnapshotStore.ResolveByTimestamp(root, guid, part, snapshotToken);
            }
            else
            {
                var files = EditSnapshotStore.List(root, guid, part);
                path = files.Count > 0 ? files[0] : null;
            }
            if (string.IsNullOrEmpty(path))
            {
                return new JObject
                {
                    ["status"] = "NoSnapshot",
                    ["target"] = target,
                    ["part"] = part,
                    ["dryRun"] = true,
                    ["hint"] = "No snapshot to dry-run against. Edit this object first to capture a baseline."
                }.ToString();
            }

            string snapshotContent = EditSnapshotStore.ReadSnapshot(path);
            if (snapshotContent == null)
            {
                return Models.McpResponse.Error("Snapshot read failed", target, part, "File exists but could not be decoded: " + path);
            }

            string currentContent = string.Empty;
            try
            {
                string readJson = _objectService.ReadObjectSource(target, part, null, null, "mcp", true, null);
                if (!string.IsNullOrWhiteSpace(readJson))
                {
                    var parsed = JObject.Parse(readJson);
                    currentContent = parsed["source"]?.ToString() ?? parsed["content"]?.ToString() ?? string.Empty;
                }
            }
            catch { /* leave currentContent empty */ }

            string diff = GxMcp.Worker.Helpers.DiffBuilder.UnifiedDiff(currentContent, snapshotContent, 3);
            return new JObject
            {
                ["status"] = "DryRun",
                ["target"] = target,
                ["part"] = part,
                ["dryRun"] = true,
                ["discard"] = discard,
                ["restoreSource"] = System.IO.Path.GetFileName(path),
                ["restoreSourcePath"] = path,
                ["diff"] = diff,
                ["hint"] = "Re-run without dryRun to write these bytes through WriteService."
            }.ToString();
        }

        private string ListEditSnapshots(string target, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Error("Snapshot list failed", target, partName, ex.Message); }

            string kbPath = null;
            try { kbPath = _objectService.GetKbService().GetKbPath(); } catch { }
            string root = EditSnapshotStore.ResolveRoot(kbPath);
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            var files = EditSnapshotStore.List(root, guid, part);
            var arr = new JArray();
            foreach (var f in files)
            {
                arr.Add(new JObject
                {
                    ["path"] = f,
                    ["fileName"] = System.IO.Path.GetFileName(f)
                });
            }
            return new JObject
            {
                ["status"] = "Success",
                ["target"] = target,
                ["part"] = part,
                ["count"] = files.Count,
                ["snapshots"] = arr
            }.ToString();
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#28) — IDE "Discard changes" parity. Resolves
        /// the most recent pre-edit snapshot for (target, part) and restores
        /// it through WriteService (the same persistence boundary the IDE
        /// uses). Returns the snapshot token used so the caller has an
        /// audit trail. NoSnapshot is a soft outcome — the agent may ask
        /// for discard before any edit was captured and that should not be
        /// treated as an error.
        /// </summary>
        private string DiscardLatestEditSnapshot(string target, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Error("Discard failed", target, partName, ex.Message); }

            string kbPath = null;
            try { kbPath = _objectService.GetKbService().GetKbPath(); } catch { }

            return DiscardLatestEditSnapshotCore(
                target, partName, guid, kbPath,
                (t, p, content) => _writeService.WriteObject(t, p, content));
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#28) — pure helper, no SDK reads. Splits out the
        /// snapshot lookup + restoration so it can be unit-tested without a live
        /// KB. <paramref name="writer"/> is the persistence hook (WriteService
        /// in production; a recording delegate in tests).
        /// </summary>
        internal static string DiscardLatestEditSnapshotCore(
            string target,
            string partName,
            string objectGuid,
            string kbPath,
            Func<string, string, string, string> writer)
        {
            string root = EditSnapshotStore.ResolveRoot(kbPath);
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            var files = EditSnapshotStore.List(root, objectGuid, part);
            if (files.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "NoSnapshot",
                    ["target"] = target,
                    ["part"] = part,
                    ["hint"] = "Edit this object first to capture a baseline; discard restores the pre-edit state."
                }.ToString();
            }

            string path = files[0]; // newest
            string content = EditSnapshotStore.ReadSnapshot(path);
            if (content == null)
            {
                return Models.McpResponse.Error("Snapshot read failed", target, part, "File exists but could not be decoded: " + path);
            }

            string snapshotToken;
            try { snapshotToken = System.IO.Path.GetFileName(path); } catch { snapshotToken = path; }

            string writeResult = writer(target, part, content) ?? "{}";
            try
            {
                var json = JObject.Parse(writeResult);
                json["discarded"] = true;
                json["restoredFrom"] = path;
                json["restoredSnapshot"] = snapshotToken;
                return json.ToString();
            }
            catch
            {
                return writeResult;
            }
        }

        private string RestoreEditSnapshot(string target, string partName, string snapshotToken)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Error("Snapshot restore failed", target, partName, ex.Message); }

            string kbPath = null;
            try { kbPath = _objectService.GetKbService().GetKbPath(); } catch { }
            string root = EditSnapshotStore.ResolveRoot(kbPath);
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            string path = EditSnapshotStore.ResolveByTimestamp(root, guid, part, snapshotToken);
            if (string.IsNullOrEmpty(path))
            {
                return Models.McpResponse.Error(
                    "Snapshot not found",
                    target,
                    part,
                    "No snapshot matched token '" + snapshotToken + "'. Use action=list with part=" + part + " to enumerate available snapshots.");
            }

            string content = EditSnapshotStore.ReadSnapshot(path);
            if (content == null)
            {
                return Models.McpResponse.Error("Snapshot read failed", target, part, "File exists but could not be decoded: " + path);
            }

            string writeResult = _writeService.WriteObject(target, part, content);
            try
            {
                var json = JObject.Parse(writeResult);
                json["restoredFrom"] = path;
                json["restoredSnapshot"] = System.IO.Path.GetFileName(path);
                return json.ToString();
            }
            catch
            {
                return writeResult;
            }
        }

        private string GetVersionSource(string target, int versionId)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null)
            {
                return Models.McpResponse.Error(
                    "Object not found",
                    target,
                    "Source",
                    "The requested object is not available in the active Knowledge Base."
                );
            }

            try
            {
                var versions = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>().ToList();
                var targetVersion = versions.FirstOrDefault(v => v.VersionId == versionId);

                if (targetVersion != null)
                {
                    var sourcePart = targetVersion.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>()
                                        .FirstOrDefault(p => p is global::Artech.Architecture.Common.Objects.ISource) 
                                        as global::Artech.Architecture.Common.Objects.ISource;

                    if (sourcePart != null)
                    {
                        string content = sourcePart.Source ?? "";
                        var result = new JObject();
                        result["source"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
                        result["isBase64"] = true;
                        result["versionId"] = versionId;
                        return result.ToString();
                    }
                }
                return "{\"status\":\"Error\",\"error\": \"Version " + versionId + " not found or has no source code.\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read version source: " + ex.Message);
                return "{\"status\":\"Error\",\"error\": \"SDK Version access failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string ListRevisions(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null)
            {
                return Models.McpResponse.Error(
                    "Object not found",
                    target,
                    null,
                    "The requested object is not available in the active Knowledge Base."
                );
            }

            var history = new JArray();
            try
            {
                var versions = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>();
                foreach (var rev in versions)
                {
                    history.Add(new JObject
                    {
                        ["version"] = rev.VersionId,
                        ["date"] = rev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["user"] = rev.UserName,
                        ["comment"] = rev.Comment
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read revisions: " + ex.Message);
                return "{\"status\":\"Error\",\"error\": \"SDK History access failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }

            return new JObject { ["history"] = history }.ToString();
        }

        private string SaveSnapshot(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);

            string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".history");
            if (!Directory.Exists(histDir)) Directory.CreateDirectory(histDir);
            
            string sourceJson = _objectService.ReadObjectSource(target, "Source", client: "mcp");
            if (sourceJson.Contains("\"error\"")) return sourceJson;

            var json = JObject.Parse(sourceJson);
            string code = json["source"] != null ? json["source"].ToString() : "";

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // Use canonical name: Type_Name
            string safeName = $"{obj.TypeDescriptor.Name}_{obj.Name}".Replace(":", "_").Replace(" ", "_");
            string filePath = Path.Combine(histDir, string.Format("{0}_{1}.txt", safeName, ts));
            File.WriteAllText(filePath, code, Encoding.UTF8);

            return "{\"status\": \"Snapshot saved\", \"file\": \"" + CommandDispatcher.EscapeJsonString(Path.GetFileName(filePath)) + "\", \"timestamp\": \"" + ts + "\", \"canonicalName\": \"" + safeName + "\"}";
        }

        private string RestoreSnapshot(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target);

            string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".history");
            if (!Directory.Exists(histDir)) Directory.CreateDirectory(histDir);

            // Use canonical name: Type_Name
            string safeName = $"{obj.TypeDescriptor.Name}_{obj.Name}".Replace(":", "_").Replace(" ", "_");
            var files = Directory.GetFiles(histDir, $"{safeName}_*.txt")
                .OrderByDescending(f => f)
                .ToArray();

            if (files.Length == 0)
                return "{\"status\":\"Error\",\"error\": \"No snapshots found for " + CommandDispatcher.EscapeJsonString(safeName) + " (Target: " + target + ")\"}";

            string lastFile = files.First();
            string code = File.ReadAllText(lastFile, Encoding.UTF8);

            return _writeService.WriteObject(target, "Source", code);
        }
    }
}
