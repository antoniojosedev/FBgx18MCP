using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
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
                    return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
                }
            }
        }

        public string BulkIndex()
        {
            Logger.Info("BulkIndex() requested.");
            if (_isIndexing) return "{\"status\":\"Already in progress\"}";

            // Skip when the on-disk cache already populated the in-memory index — avoids
            // a redundant full rebuild on every warm start. Callers wanting a forced
            // refresh use action='reorg' instead, which clears the index first.
            //
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
                if (!_indexCacheService.IsIndexMissing)
                {
                    var loaded = _indexCacheService.GetIndex();
                    if (loaded != null && loaded.Objects.Count > 0)
                    {
                        Logger.Info($"BulkIndex skipped — cache already populated ({loaded.Objects.Count} objects).");
                        return "{\"status\":\"AlreadyIndexed\",\"objects\":" + loaded.Objects.Count + "}";
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
                                    progressToken = "genexus-mcp-bulk-index",
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

        public string EnsureNotIndexing()
        {
            if (_isIndexing)
            {
                return "{\"error\": \"Knowledge Base is currently busy performing a background indexing task. Please wait a few seconds and try again.\", \"isBusy\": true}";
            }
            if (_isOpenInProgress)
            {
                return "{\"error\": \"Knowledge Base is currently opening. Please wait.\", \"isBusy\": true}";
            }
            return null;
        }
    }
}
