using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 16 — genexus_undo last=N.
    /// Scans .gx/snapshots/ for all snapshot files, picks the N
    /// most-recently-modified snapshots (newest first by filename timestamp),
    /// and restores them through WriteService.
    /// Best-effort: if a restore fails the already-restored items stay and the
    /// response surfaces both the succeeded and failed lists.
    /// </summary>
    public class UndoService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly IndexCacheService _indexCacheService;

        public UndoService(ObjectService objectService, WriteService writeService, IndexCacheService indexCacheService = null)
        {
            _objectService = objectService;
            _writeService = writeService;
            _indexCacheService = indexCacheService;
        }

        public string Undo(int last) => Undo(last, false);

        public string Undo(int last, bool dryRun)
        {
            int requestedLast = last;
            if (last < 1) last = 1;
            if (last > 20) last = 20;
            bool capped = requestedLast > 20;

            string kbPath = null;
            try { kbPath = _objectService.GetKbService()?.GetKbPath(); } catch { }
            string root = EditSnapshotStore.ResolveRoot(kbPath);

            if (!Directory.Exists(root))
            {
                return new JObject
                {
                    ["status"] = "NoSnapshots",
                    ["restored"] = new JArray(),
                    ["failed"] = new JArray(),
                    ["hint"] = "No snapshot directory at " + root + ". Edit an object first to capture a baseline."
                }.ToString();
            }

            // Enumerate all .bak / .bak.gz files, sort newest first by filename
            // (filename encodes UTC timestamp: <guidSanitized>-<part>-<yyyyMMddTHHmmssfffZ>.bak)
            List<string> allFiles;
            try
            {
                // Sort newest-first by the timestamp embedded in the filename
                // (<guidSanitized>-<part>-<yyyyMMddTHHmmssfffZ>.bak) — ordinal sort
                // of full paths is dominated by the leading GUID and would pick
                // arbitrary GUID-buckets instead of the actually-most-recent edits.
                allFiles = Directory.EnumerateFiles(root)
                    .Where(p =>
                    {
                        string fn = Path.GetFileName(p);
                        return fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                            || fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(p => ExtractSnapshotTimestamp(p), StringComparer.Ordinal)
                    .Take(last)
                    .ToList();
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["error"] = "Failed to enumerate snapshots: " + ex.Message
                }.ToString();
            }

            if (allFiles.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "NoSnapshots",
                    ["restored"] = new JArray(),
                    ["failed"] = new JArray(),
                    ["hint"] = "No snapshots found in " + root
                }.ToString();
            }

            var restored = new JArray();
            var failed = new JArray();

            foreach (var path in allFiles)
            {
                var meta = ParseSnapshotFileName(path);
                if (meta == null)
                {
                    failed.Add(new JObject
                    {
                        ["snapshotPath"] = path,
                        ["error"] = "Could not parse snapshot filename"
                    });
                    continue;
                }

                // Resolve object name from the sanitized GUID. dryRun skips this:
                // the name is only required to call WriteObject; for a dryRun envelope
                // we surface the raw guid + part + bytes preview without any SDK round-trip.
                // Live restore path still resolves, but with a hard cap to avoid the
                // full-index iteration that was costing ~60s on a 38k-object KB.
                string objectName = dryRun ? meta.RawGuid : (ResolveObjectNameFromGuid(meta.RawGuid) ?? meta.RawGuid);

                string content = EditSnapshotStore.ReadSnapshot(path);
                if (content == null)
                {
                    failed.Add(new JObject
                    {
                        ["object"] = objectName,
                        ["part"] = meta.Part,
                        ["snapshotTimestamp"] = meta.Timestamp,
                        ["error"] = "Could not read snapshot file"
                    });
                    continue;
                }

                try
                {
                    // Item 21 (friction 2026-05-22): dryRun skips the WriteObject call;
                    // we report what WOULD be restored so the agent can preview.
                    if (dryRun)
                    {
                        restored.Add(new JObject
                        {
                            ["object"] = objectName,
                            ["part"] = meta.Part,
                            ["snapshotTimestamp"] = meta.Timestamp,
                            ["restoreSource"] = path,
                            ["bytes"] = content?.Length ?? 0,
                            ["dryRun"] = true
                        });
                        continue;
                    }
                    string writeResult = _writeService.WriteObject(objectName, meta.Part, content);
                    var writeJson = TryParseJson(writeResult);
                    bool success = writeJson != null
                        && (string.Equals(writeJson["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase)
                            || writeJson["error"] == null);

                    if (success)
                    {
                        restored.Add(new JObject
                        {
                            ["object"] = objectName,
                            ["part"] = meta.Part,
                            ["snapshotTimestamp"] = meta.Timestamp,
                            ["restoredFrom"] = path
                        });
                    }
                    else
                    {
                        failed.Add(new JObject
                        {
                            ["object"] = objectName,
                            ["part"] = meta.Part,
                            ["snapshotTimestamp"] = meta.Timestamp,
                            ["error"] = writeJson?["error"]?.ToString() ?? writeResult
                        });
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject
                    {
                        ["object"] = objectName,
                        ["part"] = meta.Part,
                        ["snapshotTimestamp"] = meta.Timestamp,
                        ["error"] = ex.Message
                    });
                }
            }

            string status = dryRun ? "DryRun"
                          : failed.Count == 0 ? "Success"
                          : restored.Count == 0 ? "Failed"
                          : "PartialSuccess";

            var resp = new JObject
            {
                ["status"] = status,
                ["dryRun"] = dryRun,
                ["restoredCount"] = restored.Count,
                ["failedCount"] = failed.Count,
                ["restored"] = restored,
                ["failed"] = failed
            };
            if (capped)
            {
                resp["capped"] = true;
                resp["requestedLast"] = requestedLast;
                resp["effectiveLast"] = 20;
                resp["hint"] = $"Requested last={requestedLast} clamped to 20 (per-call hard cap). Call again to revert older snapshots.";
            }
            return resp.ToString();
        }

        /// Pulls the ISO-8601 timestamp segment out of a snapshot filename.
        /// Filename shape: <c>&lt;guidSanitized&gt;-&lt;part&gt;-&lt;yyyyMMddTHHmmssfffZ&gt;.bak[.gz]</c>.
        /// Returns the timestamp portion (sortable lexically); falls back to
        /// the full file name when the shape doesn't match so degraded sort
        /// is still deterministic instead of throwing.
        private static string ExtractSnapshotTimestamp(string path)
        {
            string fn = Path.GetFileName(path) ?? string.Empty;
            // Drop extension(s)
            if (fn.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                fn = fn.Substring(0, fn.Length - 3);
            if (fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                fn = fn.Substring(0, fn.Length - 4);
            int lastDash = fn.LastIndexOf('-');
            if (lastDash >= 0 && lastDash + 1 < fn.Length)
                return fn.Substring(lastDash + 1);
            return fn;
        }

        /// <summary>
        /// Try to resolve a KB object name from its sanitized guid string.
        /// First consults the IndexCacheService (entries carry the real guid as a field);
        /// falls back to null when the index is unavailable or the guid is unknown.
        /// </summary>
        private string ResolveObjectNameFromGuid(string sanitizedGuid)
        {
            if (string.IsNullOrEmpty(sanitizedGuid)) return null;
            if (_indexCacheService == null) return null;
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index?.Objects == null) return null;
                // IndexEntry carries Guid as a snapshot field already — no SDK round-trip
                // needed. Prior implementation called FindObject per entry, which on a
                // 38k-object KB cost ~60 s per snapshot. Now O(N) string comparisons
                // against the in-memory snapshot, completes in milliseconds.
                foreach (var kv in index.Objects)
                {
                    var entry = kv.Value;
                    if (entry == null || string.IsNullOrEmpty(entry.Guid)) continue;
                    string sanitized = SanitizeGuid(entry.Guid);
                    if (string.Equals(sanitized, sanitizedGuid, StringComparison.OrdinalIgnoreCase))
                        return entry.Name;
                }
            }
            catch { }
            return null;
        }

        private static string SanitizeGuid(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var sb = new System.Text.StringBuilder(s.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in s)
            {
                if (c == '-' || c == ' ') { sb.Append('_'); continue; }
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        private sealed class SnapshotMeta
        {
            public string RawGuid;
            public string Part;
            public string Timestamp;
        }

        // Filename format: <guid_sanitized>-<part_sanitized>-<timestamp>.bak[.gz]
        // The sanitized guid replaces '-' with '_', so the only '-' separators remaining
        // are between the three logical segments. We split from the right:
        // last segment = timestamp, second-to-last = part, rest = guid.
        private static SnapshotMeta ParseSnapshotFileName(string path)
        {
            try
            {
                string fn = Path.GetFileName(path);
                if (fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase))
                    fn = fn.Substring(0, fn.Length - 7);
                else if (fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    fn = fn.Substring(0, fn.Length - 4);

                var segments = fn.Split('-');
                if (segments.Length < 3) return null;

                string timestamp = segments[segments.Length - 1];
                string part = segments[segments.Length - 2];
                string guid = string.Join("-", segments, 0, segments.Length - 2);

                return new SnapshotMeta { RawGuid = guid, Part = part, Timestamp = timestamp };
            }
            catch { return null; }
        }

        private static JObject TryParseJson(string s)
        {
            try { return JObject.Parse(s); } catch { return null; }
        }
    }
}
