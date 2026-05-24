using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Services
{
    public class KbService
    {
        private BuildService _buildService;
        private readonly IndexCacheService _indexCacheService;
        private readonly VectorService _vectorService = new VectorService();

        // Progress Tracking
        private static volatile int _processedCount = 0;
        private static volatile int _totalCount = 0;
        private static volatile bool _isIndexing = false;
        private static volatile string _currentStatus = "";

        private dynamic _kb;
        private bool _isOpenInProgress = false;
        private readonly object _kbLock = new object();

        public KbService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public IndexCacheService GetIndexCache() { return _indexCacheService; }
        public bool IsInitializing => _isOpenInProgress;
        public bool IsIndexing => _isIndexing;
        public int IndexProcessed => _processedCount;
        public int IndexTotal => _totalCount;
        public string IndexStatus => _currentStatus;
        public bool IsOpen { get { lock (_kbLock) { return _kb != null; } } }

        // v2.6.6 Stream D — expose the open KB handle + lock so BuildService can
        // run GeneXus MSBuild tasks in-process against the same KB instance
        // instead of spawning MSBuild.exe + reopening the KB out-of-process.
        // Callers MUST hold KbLock for the duration of any task.Execute() call.
        public object KbObject { get { lock (_kbLock) { return (object)_kb; } } }
        public object KbLock => _kbLock;

        public string GetKbPath()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                try { return (string)_kb.Location; } catch { return null; }
            }
        }

        public dynamic GetKB()
        {
            lock (_kbLock) 
            { 
                if (_kb == null && !_isOpenInProgress)
                {
                    string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
                    if (!string.IsNullOrEmpty(kbPath))
                    {
                        Logger.Info($"Auto-opening KB in background: {kbPath}");
                        System.Threading.Tasks.Task.Run(() => OpenKB(kbPath));
                    }
                }
                return _kb; 
            }
        }

        public string OpenKB(string path)
        {
            lock (_kbLock)
            {
                if (_isOpenInProgress) return "{\"status\":\"In Progress\"}";
                _isOpenInProgress = true;
                
                if (_kb != null)
                {
                    try { if (string.Equals(_kb.Location, path, StringComparison.OrdinalIgnoreCase)) { _isOpenInProgress = false; return "{\"status\":\"Success\"}"; } } catch { }
                    try { _kb.Close(); } catch { }
                }

                // PERFORMANCE (instrumentation): time the KB.Open call so cold-start regressions
                // are visible in logs. Previously this critical SDK call had no timing data.
                var sw = Stopwatch.StartNew();
                try {
                    Logger.Info($"Opening KB: {path}");
                    string oldDir = Directory.GetCurrentDirectory();
                    try {
                        string kbDir = Path.GetDirectoryName(path);
                        Directory.SetCurrentDirectory(kbDir);

                        var options = new KnowledgeBase.OpenOptions(path);
                        _kb = KnowledgeBase.Open(options);
                        NormalizeGxwVersionMetadata(path);

                        sw.Stop();
                        Logger.Info($"[KB-OPEN] elapsedMs={sw.ElapsedMilliseconds} path={path}");
                        return "{\"status\":\"Success\"}";
                    } finally { Directory.SetCurrentDirectory(oldDir); _isOpenInProgress = false; }
                } catch (Exception ex) {
                    sw.Stop();
                    Logger.Error($"[KB-OPEN-FAIL] elapsedMs={sw.ElapsedMilliseconds} path={path} error={ex.Message}"); 
                    Logger.Error($"ERROR opening KB: {ex.Message}");
                    _kb = null;
                    _isOpenInProgress = false;
                    return "{\"status\":\"Error\",\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
                }
            }
        }

        private static void NormalizeGxwVersionMetadata(string kbPath)
        {
            try
            {
                string kbDir = ResolveKbDirectory(kbPath);
                if (string.IsNullOrWhiteSpace(kbDir) || !Directory.Exists(kbDir)) return;

                string gxwPath = Directory.GetFiles(kbDir, "*.gxw", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(gxwPath) || !File.Exists(gxwPath)) return;

                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? string.Empty;
                string gxVersion = DetectGeneXusVersion(gxPath);
                if (string.IsNullOrWhiteSpace(gxVersion)) return;

                XDocument doc = XDocument.Load(gxwPath, LoadOptions.PreserveWhitespace);
                XElement root = doc.Root;
                if (root == null || !string.Equals(root.Name.LocalName, "GeneXusInformation", StringComparison.OrdinalIgnoreCase)) return;

                bool changed = false;
                changed |= UpsertElementValue(root, "InstallationPath", gxPath);
                changed |= UpsertElementValue(root, "ProductVersion", gxVersion);
                changed |= UpsertElementValue(root, "FriendlyVersion", gxVersion);
                changed |= UpsertElementValue(root, "VersionNumber", gxVersion);

                if (!changed) return;

                doc.Save(gxwPath, SaveOptions.DisableFormatting);
                Logger.Info($"[KB-METADATA] Updated '{Path.GetFileName(gxwPath)}' to version '{gxVersion}'.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[KB-METADATA] Failed to normalize .gxw version metadata: {ex.Message}");
            }
        }

        private static string ResolveKbDirectory(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath)) return null;
            if (Directory.Exists(kbPath)) return kbPath;
            if (File.Exists(kbPath)) return Path.GetDirectoryName(kbPath);
            string ext = Path.GetExtension(kbPath);
            if (!string.IsNullOrEmpty(ext) && ext.Equals(".gxw", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(kbPath);
            }
            return kbPath;
        }

        private static bool UpsertElementValue(XElement parent, string elementName, string value)
        {
            if (parent == null || string.IsNullOrWhiteSpace(elementName)) return false;
            XElement child = parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                parent.Add(new XElement(elementName, value ?? string.Empty));
                return true;
            }

            string current = (child.Value ?? string.Empty).Trim();
            string next = (value ?? string.Empty).Trim();
            if (string.Equals(current, next, StringComparison.Ordinal)) return false;

            child.Value = next;
            return true;
        }

        private static string DetectGeneXusVersion(string gxPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gxPath) || !Directory.Exists(gxPath)) return null;

                string[] candidates = new[]
                {
                    Path.Combine(gxPath, "version.txt"),
                    Path.Combine(gxPath, "Version.txt"),
                    Path.Combine(gxPath, "GeneXus.version")
                };

                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;
                    string raw = File.ReadAllText(candidate)?.Trim();
                    if (!string.IsNullOrWhiteSpace(raw)) return raw;
                }

                string exePath = Path.Combine(gxPath, "GeneXus.exe");
                if (!File.Exists(exePath)) return null;
                var info = FileVersionInfo.GetVersionInfo(exePath);

                // Reproduce the canonical version string the IDE itself writes to .gxw
                // (e.g. "18.0.187794 U14"). Reading ProductVersion as a string returns
                // "18.0.14.187794+<git-sha>" because .NET appends the InformationalVersion
                // suffix, which doesn't match what the IDE re-detects on its next open and
                // triggers the "different GeneXus installation than last time" dialog.
                if (info.FileMajorPart != 0 || info.FilePrivatePart != 0)
                {
                    return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FilePrivatePart} U{info.FileBuildPart}";
                }

                string version = info.ProductVersion ?? info.FileVersion;
                if (!string.IsNullOrWhiteSpace(version)) return version.Trim();
            }
            catch { }
            return null;
        }

        public string BulkIndex() => BulkIndex(force: false);

        // SP6.T6 — public entry point. When Indexing.UseLitePass is true (default) we run
        // the new fast lite + lazy-enrichment pipeline; otherwise we fall back to the
        // preserved monolithic path (BulkIndexLegacy) for one release.
        public string BulkIndex(bool force)
        {
            if (!Configuration.UseLitePass)
            {
                return BulkIndexLegacy(force);
            }

            Logger.Info($"BulkIndex(force={force}) requested — fast index path (lite + lazy enrichment).");
            if (_isIndexing) return "{\"status\":\"Already in progress\"}";

            // Wait briefly for the KB to open — same warm-up window as the legacy path.
            try
            {
                int waitMs = 0;
                while (waitMs < 15000 && !_indexCacheService.IsInitialized)
                {
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                if (force)
                {
                    Logger.Info("BulkIndex(fast): force=true — clearing in-memory + on-disk snapshot.");
                    try
                    {
                        _indexCacheService.Clear();
                        _indexCacheService.DeleteOnDiskSnapshot();
                        _indexCacheService.MarkReindexStarted(0);
                    }
                    catch (Exception ex) { Logger.Warn("BulkIndex(fast) force-clear failed: " + ex.Message); }
                }
                else if (!_indexCacheService.IsIndexMissing)
                {
                    var loaded = _indexCacheService.GetIndex();
                    if (loaded != null && loaded.Objects.Count > 0)
                    {
                        try { _indexCacheService.MarkIndexComplete(loaded.Objects.Count); } catch { }
                        Logger.Info($"BulkIndex(fast) skipped — cache already populated ({loaded.Objects.Count} objects).");
                        return "{\"status\":\"AlreadyIndexed\",\"objects\":" + loaded.Objects.Count + ",\"path\":\"fast\"}";
                    }
                }
            }
            catch { /* fall through and rebuild */ }

            _isIndexing = true;
            _processedCount = 0;
            _totalCount = 0;
            _currentStatus = "Lite-index pass starting...";

            var bulkSw = Stopwatch.StartNew();

            var liteThread = new Thread(() =>
            {
                try
                {
                    var liteSw = Stopwatch.StartNew();
                    dynamic kb = GetKB();
                    if (kb == null)
                    {
                        _isIndexing = false;
                        _currentStatus = "Error: KB not open";
                        return;
                    }

                    _currentStatus = "Lite-index pass: walking KB objects...";
                    Logger.Info(_currentStatus);

                    var objectList = (System.Collections.IEnumerable)kb.DesignModel.Objects;
                    var liteEntries = new List<SearchIndex.IndexEntry>();

                    foreach (global::Artech.Architecture.Common.Objects.KBObject obj in objectList)
                    {
                        _totalCount++;
                        string typeName = null;
                        try { typeName = obj.TypeDescriptor?.Name; } catch { }
                        if (string.IsNullOrEmpty(typeName)) typeName = obj.GetType().Name;

                        string description = null;
                        try { description = obj.Description; } catch { }

                        // v2.6.8: lite pass also captures lifecycle metadata. Reads
                        // are cheap on the KBObject handle (no part load), so we
                        // pay the cost once during the lite walk instead of
                        // forcing the user to wait for enrichment.
                        DateTime lu = DateTime.MinValue, ca = DateTime.MinValue;
                        string lub = null;
                        try { lu = obj.LastUpdate; } catch { }
                        try { ca = obj.VersionDate; } catch { }
                        try { lub = obj.UserName; } catch { }

                        liteEntries.Add(new SearchIndex.IndexEntry
                        {
                            Guid = obj.Guid.ToString(),
                            Name = obj.Name,
                            Type = typeName,
                            Description = description,
                            LastUpdate = lu,
                            CreatedAt = ca,
                            LastModifiedBy = lub,
                            IsEnriched = false
                        });

                        if (_totalCount % 500 == 0) Thread.Sleep(1);

                        if (_totalCount % 1000 == 0)
                        {
                            try
                            {
                                GxMcp.Worker.Helpers.ProgressEmitter.Emit(
                                    _totalCount,
                                    Math.Max(_totalCount, 50000),
                                    "Lite-index pass: " + _totalCount + " objects captured");
                            }
                            catch { }
                        }
                    }

                    _indexCacheService.ReplaceAll(liteEntries);
                    _indexCacheService.MarkLitePassComplete(_totalCount);

                    // Wire the enrichment queue BEFORE starting the background drain, so callers
                    // that hit ImpactAnalysis the moment LiteReady is published can promote
                    // their target on demand without a race window.
                    var enricher = new IndexEntryEnricher(e =>
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(e?.Guid)) return;
                            if (!Guid.TryParse(e.Guid, out var g)) return;
                            var fullObj = kb.DesignModel.Objects.Get(g);
                            if (fullObj == null) return;
                            _indexCacheService.UpdateEntry(fullObj);
                        }
                        catch (Exception ex) { Logger.Warn("Enrich " + (e != null ? e.Name : "?") + " failed: " + ex.Message); }
                    });

                    var queue = new EnrichmentQueue(enricher);
                    foreach (var entry in liteEntries) queue.Enqueue(entry);
                    _indexCacheService.SetEnrichmentQueue(queue);

                    liteSw.Stop();
                    _currentStatus = $"Lite pass complete: {_totalCount} objects. Enriching in background...";
                    Logger.Info($"[BULK-INDEX-LITE] elapsedMs={liteSw.ElapsedMilliseconds} objects={_totalCount}");

                    var enrichThread = new Thread(() =>
                    {
                        try
                        {
                            _indexCacheService.MarkEnrichmentStarted();

                            queue.DrainAsync().GetAwaiter().GetResult();
                            _processedCount = _totalCount;
                            _indexCacheService.MarkIndexComplete(_totalCount);
                            bulkSw.Stop();
                            _currentStatus = "Complete";
                            Logger.Info($"[BULK-INDEX-FULL] elapsedMs={bulkSw.ElapsedMilliseconds} processed={_totalCount}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("[BULK-INDEX-ENRICH-FAIL] error=" + ex.Message);
                            try { _indexCacheService.MarkIndexFailed(); } catch { }
                            _currentStatus = "Error: " + ex.Message;
                        }
                        finally { _isIndexing = false; }
                    }) {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "GxMcp-Enrich"
                    };
                    enrichThread.SetApartmentState(ApartmentState.STA);
                    enrichThread.Start();
                }
                catch (Exception ex)
                {
                    Logger.Error("[BULK-INDEX-LITE-FAIL] error=" + ex.Message);
                    try { _indexCacheService.MarkIndexFailed(); } catch { }
                    _currentStatus = "Error: " + ex.Message;
                    _isIndexing = false;
                }
            }) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "GxMcp-Lite"
            };
            liteThread.SetApartmentState(ApartmentState.STA);
            liteThread.Start();

            return "{\"status\":\"LiteStarted\",\"hint\":\"list_objects is usable after a few seconds; analyze impact uses on-demand enrichment.\"}";
        }

        // v2.3.8 (post-self-review) — force flag closes the "stale snapshot" gap.
        // Without force, the warm-start cache shortcuts to AlreadyIndexed even when
        // entries are missing edges (Calls/CalledBy) or new objects exist in the KB.
        // With force=true the in-memory + on-disk caches are cleared and the SDK
        // walk re-runs from scratch. SP6.T6 — preserved verbatim as the legacy path
        // behind Indexing.UseLitePass=false for one release before removal.
        private string BulkIndexLegacy(bool force)
        {
            Logger.Info($"BulkIndex(force={force}) requested.");
            if (_isIndexing) return "{\"status\":\"Already in progress\"}";

            // Wait briefly for the KB to open. The Gateway fires BulkIndex from the
            // initialize hook before the worker has opened the KB, so IsIndexMissing
            // would read true (cache path unknown) and trigger a redundant rebuild.
            try
            {
                int waitMs = 0;
                while (waitMs < 15000 && !_indexCacheService.IsInitialized)
                {
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                if (force)
                {
                    Logger.Info("BulkIndex: force=true — clearing in-memory + on-disk snapshot before full rebuild.");
                    try
                    {
                        _indexCacheService.Clear();
                        _indexCacheService.DeleteOnDiskSnapshot();
                        _indexCacheService.MarkReindexStarted(0);
                    }
                    catch (Exception ex) { Logger.Warn("BulkIndex force-clear failed (continuing with rebuild anyway): " + ex.Message); }
                }
                else if (!_indexCacheService.IsIndexMissing)
                {
                    // Skip when the on-disk cache already populated the in-memory index — avoids
                    // a redundant full rebuild on every warm start. Callers wanting a real refresh
                    // must pass force=true.
                    var loaded = _indexCacheService.GetIndex();
                    if (loaded != null && loaded.Objects.Count > 0)
                    {
                        // v2.3.8 (post-Task 1.2 fix): warm-start path — publish Ready to
                        // IndexState so whoami stops reporting Cold while the in-memory
                        // index is in fact populated. GetIndex() also calls MarkIndexComplete
                        // on first hydration; this is the second safety net for the case
                        // where the index was already in memory before the BulkIndex call.
                        try { _indexCacheService.MarkIndexComplete(loaded.Objects.Count); } catch { }
                        Logger.Info($"BulkIndex skipped — cache already populated ({loaded.Objects.Count} objects). Pass force=true to rebuild.");
                        return "{\"status\":\"AlreadyIndexed\",\"objects\":" + loaded.Objects.Count + ",\"hint\":\"Pass force=true to force a full SDK rescan when entries are missing edges or new objects exist.\"}";
                    }
                }
            }
            catch { /* fall through and rebuild */ }

            _isIndexing = true;
            _processedCount = 0;
            _totalCount = 0;
            _currentStatus = "Scanning objects...";

            // Start indexing in a dedicated STA thread to prevent blocking the command consumer
            var indexThread = new Thread(() => {
                // PERFORMANCE (instrumentation): measure end-to-end cold-start indexing time.
                var bulkSw = Stopwatch.StartNew();
                try {
                    dynamic kb = GetKB();
                    if (kb == null) {
                        _isIndexing = false;
                        _currentStatus = "Error: KB not open";
                        return;
                    }

                    _currentStatus = "Capturing KB objects snapshot...";
                    Logger.Info(_currentStatus);

                    var objectList = (System.Collections.IEnumerable)kb.DesignModel.Objects;
                    var objectSnapshot = new List<KeyValuePair<Guid, string>>();
                    foreach (global::Artech.Architecture.Common.Objects.KBObject obj in objectList)
                    {
                        objectSnapshot.Add(new KeyValuePair<Guid, string>(obj.Guid, obj.Name));
                        _totalCount++;
                        if (_totalCount % 500 == 0) Thread.Sleep(1); // Small breather during capture
                    }

                    _currentStatus = $"Indexing {_totalCount} objects using snapshot...";
                    Logger.Info(_currentStatus);

                    // v2.3.8 (Task 1.1): publish reindex start so downstream services (whoami,
                    // search, analyze) can detect Reindexing state from IndexCacheService.GetState().
                    try { _indexCacheService?.MarkReindexStarted(_totalCount); } catch { }

                    var indexSw = Stopwatch.StartNew();
                    foreach (var snapshotEntry in objectSnapshot)
                    {
                        try {
                            // Fetch object safely by stable identity. Name-based dynamic dispatch
                            // can bind to the wrong GeneXus SDK overload during bulk indexing.
                            var obj = kb.DesignModel.Objects.Get(snapshotEntry.Key);
                            if (obj == null) continue;

                            _indexCacheService.UpdateEntry(obj);
                            _processedCount++;
                            
                            int notifyInterval = Math.Max(500, _totalCount / 100);
                            if (_processedCount % notifyInterval == 0 || _processedCount == _totalCount) {
                                _currentStatus = $"Indexing KB: {_processedCount}/{_totalCount} objects";
                                Logger.Info(_currentStatus);

                                // MCP spec notifications/progress — clients render this natively
                                // as a progress bar. progressToken is a stable identifier for the
                                // background operation so clients can correlate repeated updates.
                                Program.SendNotification("notifications/progress", new {
                                    progressToken = GxMcp.Worker.Helpers.ProgressContext.CurrentToken ?? "genexus-mcp-bulk-index",
                                    progress = _processedCount,
                                    total = _totalCount,
                                    message = _currentStatus
                                });

                                // v2.3.8 (Task 1.1): mirror progress into IndexState so callers
                                // polling whoami can render the same ETA without piggy-backing on
                                // MCP notifications.
                                try {
                                    double frac = _totalCount > 0 ? (double)_processedCount / _totalCount : 0;
                                    long elapsedMs = indexSw.ElapsedMilliseconds;
                                    int etaMs = (frac > 0.0001)
                                        ? (int)Math.Max(0, (elapsedMs / frac) - elapsedMs)
                                        : 0;
                                    _indexCacheService?.MarkReindexProgress(frac, etaMs);
                                } catch { }
                            }
                            
                            // Ironclad Throttling: Give the system a breather every 50 objects
                            if (_processedCount % 50 == 0) Thread.Sleep(10);
                        } catch (Exception ex) {
                            Logger.Error($"Error indexing object {snapshotEntry.Value}: {ex.Message}");
                        }
                    }

                    _currentStatus = "Complete";
                    _isIndexing = false;
                    bulkSw.Stop();
                    // v2.3.8 (Task 1.1): publish completion to IndexState.
                    try { _indexCacheService?.MarkIndexComplete(_processedCount); } catch { }
                    Logger.Info($"[BULK-INDEX] elapsedMs={bulkSw.ElapsedMilliseconds} processed={_processedCount} total={_totalCount}");
                } catch (Exception ex) {
                    bulkSw.Stop();
                    Logger.Error($"[BULK-INDEX-FAIL] elapsedMs={bulkSw.ElapsedMilliseconds} error={ex.Message}");
                    _isIndexing = false;
                    _currentStatus = "Error: " + ex.Message;
                    // v2.3.8 (Task 1.1 review): reset IndexState on failure so callers don't
                    // see a permanent "Reindexing" status when bulk indexing throws after
                    // MarkReindexStarted. Wrapped in try/catch for resilience.
                    try { _indexCacheService?.MarkIndexFailed(); } catch { }
                }
            }) { 
                IsBackground = true, 
                Name = "AsyncIndexer", 
                Priority = ThreadPriority.BelowNormal 
            };
            indexThread.SetApartmentState(ApartmentState.STA);
            indexThread.Start();

            return "{\"status\":\"Started\"}";
        }

        public string GetIndexStatus()
        {
            var json = new Newtonsoft.Json.Linq.JObject();
            json["isIndexing"] = _isIndexing;
            json["total"] = _totalCount;
            json["processed"] = _processedCount;
            json["status"] = _currentStatus;
            json["isBusy"] = _isIndexing || _isOpenInProgress;
            return json.ToString();
        }

        // -----------------------------------------------------------------
        // v2.6.6 Stream H (FR#26) — active-environment cache surface.
        //
        // Worker-side resolver. The gateway calls this on cache miss; the
        // KbHandle holds the value for 60s and serves subsequent reads in
        // O(1). KbWatcherService.OnEnvironmentChanged invalidates the
        // gateway cache when the SDK fires its environment-changed event.
        // -----------------------------------------------------------------
        public string GetActiveEnvironment()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                // SDK exposes multiple shapes across major versions; probe in order
                // and swallow individually so a missing property on one branch
                // doesn't strand the whole call.
                try { var v = _kb.Environment?.Name; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.UserInterface?.ActiveEnvironment?.Name; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.DesignModel?.Environment?.Name; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.ActiveModel?.Name; if (v != null) return v.ToString(); } catch { }
                return null;
            }
        }

        public string GetActiveEnvironmentVersion()
        {
            lock (_kbLock)
            {
                if (_kb == null) return null;
                try { var v = _kb.Environment?.Version; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.UserInterface?.ActiveEnvironment?.Version; if (v != null) return v.ToString(); } catch { }
                try { var v = _kb.ActiveModel?.Version; if (v != null) return v.ToString(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#25) — F5 launcher resolver.
        ///
        /// Mirrors the IDE's F5/Run behaviour: pick the KB's configured
        /// startup/main object. Probes the SDK first; falls back to the
        /// in-memory index for the first object whose Tags contain "Main"
        /// (the same heuristic <c>HealthService.IsMainObject</c> uses).
        /// Returns <c>null</c> when no candidate exists — callers surface
        /// a <c>NoLauncher</c> envelope rather than guessing wrong.
        /// </summary>
        public string GetLauncherObjectName()
        {
            lock (_kbLock)
            {
                if (_kb != null)
                {
                    try { var v = _kb.DefaultStartupObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                    try { var v = _kb.UserInterface?.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                    try { var v = _kb.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                }
            }

            // Fall back to the index — first "Main"-tagged WebPanel/SDPanel
            // wins (matches HealthService.IsMainObject heuristic). Reads the
            // index outside the kb lock to keep startup-thread contention low.
            try
            {
                var idx = _indexCacheService?.GetIndex();
                if (idx == null) return null;
                foreach (var entry in idx.Objects.Values)
                {
                    if (entry == null) continue;
                    bool isMain = (entry.Tags != null && entry.Tags.Any(t => t.Equals("Main", StringComparison.OrdinalIgnoreCase)))
                               || (entry.Description != null && entry.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isMain) continue;
                    string type = entry.Type ?? string.Empty;
                    if (type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("SDPanel", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Name;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("GetLauncherObjectName fallback failed: " + ex.Message); }
            return null;
        }

        public string EnsureNotIndexing()
        {
            if (_isIndexing)
            {
                return "{\"status\":\"Error\",\"error\": \"Knowledge Base is currently busy performing a background indexing task. Please wait a few seconds and try again.\", \"isBusy\": true}";
            }
            if (_isOpenInProgress)
            {
                return "{\"status\":\"Error\",\"error\": \"Knowledge Base is currently opening. Please wait.\", \"isBusy\": true}";
            }
            return null;
        }
    }
}
