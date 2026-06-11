using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class IndexCacheService
    {
        // PERFORMANCE (W-A3): bump the disk-flush throttle so write bursts during bulk index
        // don't thrash. The in-memory index is always current; the on-disk copy is for
        // cold-start only, so a slightly stale snapshot is acceptable.
        private const int FlushThrottleSeconds = 30;

        private SearchIndex _index;
        private string _indexPath;

        // ── Dirty-generation tracking (stale-index-forever fix) ─────────────────
        // Every in-memory mutation bumps _dirtyGeneration; a successful FlushToDisk
        // records the generation it captured at snapshot time into _flushedGeneration.
        // FlushNow() only reports success once the generation that was dirty at its
        // entry is confirmed on disk — so callers (KbService) can gate the meta
        // sidecar on a body that actually contains their changes. _flushedGeneration
        // starts at -1 so a clean index still gets one real write on FlushNow().
        private long _dirtyGeneration = 0;
        private long _flushedGeneration = -1;

        internal void MarkDirty() => System.Threading.Interlocked.Increment(ref _dirtyGeneration);
        internal long DirtyGeneration => System.Threading.Interlocked.Read(ref _dirtyGeneration);
        internal long FlushedGeneration => System.Threading.Interlocked.Read(ref _flushedGeneration);
        internal bool IsFullyFlushed => FlushedGeneration >= DirtyGeneration;
        // PERFORMANCE (W-A3): mirror path with .gz extension. Derived from _indexPath so
        // the two never drift; new flushes go here gzipped, legacy plain-JSON still readable.
        private string _indexPathGz => _indexPath + ".gz";
        private BuildService _buildService;
        private bool _initialized = false;
        private readonly object _lock = new object();
        private DateTime _lastFlushTime = DateTime.MinValue;
        private bool _savingInProgress = false;
        // PERFORMANCE (W-M2): track consecutive flush failures so a silently failing
        // disk (full / locked / permission) surfaces through whoami instead of going
        // unnoticed until the next cold start finds a stale snapshot.
        private static int _consecutiveFlushFailures = 0;
        private static DateTime _lastFlushSuccessUtc = DateTime.MinValue;
        private static string _lastFlushErrorMessage = null;
        public static int ConsecutiveFlushFailures => System.Threading.Volatile.Read(ref _consecutiveFlushFailures);
        public static DateTime LastFlushSuccessUtc => _lastFlushSuccessUtc;
        public static string LastFlushErrorMessage => _lastFlushErrorMessage;

        // Fase 0 instrumentation: per-sub-step enrichment timing accumulators (raw
        // Stopwatch ticks, Interlocked because the ref/textual scans run on the
        // background dispatcher thread). These decide whether Fase 3 parallelization
        // is worth it: typeExtract+embedding are CPU-only (parallelizable), refScan is
        // STA-bound (GetReferences). Snapshot via GetEnrichTimingSummary() at drain end.
        // NOTE: refScan/textualScan are enqueued on Program.EnqueueBackground and may
        // still be accumulating when [ENRICH-DONE] is logged — read as "work so far".
        private static long _enrichTypeExtractTicks = 0;
        private static long _enrichEmbeddingTicks = 0;
        private static long _enrichRefScanTicks = 0;
        private static long _enrichTextualScanTicks = 0;
        public static string GetEnrichTimingSummary()
        {
            double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long te = System.Threading.Interlocked.Read(ref _enrichTypeExtractTicks);
            long em = System.Threading.Interlocked.Read(ref _enrichEmbeddingTicks);
            long rs = System.Threading.Interlocked.Read(ref _enrichRefScanTicks);
            long ts = System.Threading.Interlocked.Read(ref _enrichTextualScanTicks);
            return $"typeExtractMs={(long)(te * tickToMs)} embeddingMs={(long)(em * tickToMs)} refScanMs={(long)(rs * tickToMs)} textualScanMs={(long)(ts * tickToMs)}";
        }

        private readonly VectorService _vectorService = new VectorService();

        // PERFORMANCE (W-M5): cache resolved hierarchy by object Guid to avoid re-walking
        // the parent chain on repeated UpdateEntry calls for the same object. Invalidated
        // on Clear/RemoveEntry. Bulk-index hits each Guid once so the win comes from
        // incremental re-indexing after saves.
        private readonly ConcurrentDictionary<Guid, (string ParentName, string ParentPath, string Path, string ModuleName)> _hierarchyCache
            = new ConcurrentDictionary<Guid, (string, string, string, string)>();

        // v2.3.8 (Task 1.1): unified IndexState surface so downstream services (whoami,
        // search, analyze) share a single source of truth for index readiness.
        private IndexState _state = new IndexState { Status = "Cold", TotalObjects = 0 };
        private readonly object _stateLock = new object();

        public IndexState GetState()
        {
            lock (_stateLock)
            {
                return new IndexState
                {
                    Status = _state.Status,
                    LastIndexedAt = _state.LastIndexedAt,
                    TotalObjects = _state.TotalObjects,
                    Progress = _state.Progress,
                    EtaMs = _state.EtaMs,
                    LitePassCompletedUtc = _state.LitePassCompletedUtc,
                    EnrichmentStartedUtc = _state.EnrichmentStartedUtc
                };
            }
        }

        public void MarkReindexStarted(int totalEstimated)
        {
            lock (_stateLock)
            {
                _state.Status = "Reindexing";
                _state.Progress = 0;
                _state.EtaMs = null;
                _state.LastIndexedAt = null;
                _state.TotalObjects = totalEstimated;
            }
            // Fase 0: re-arm the once-per-build [TIME-TO-USABLE] guards so a repeated
            // force-reindex in the same process logs the milestones again.
            System.Threading.Interlocked.Exchange(ref _loggedUltraLiteUsable, 0);
            System.Threading.Interlocked.Exchange(ref _loggedLiteUsable, 0);
            // Reset the enrichment-timing accumulators so [INDEX-SAVE]/[ENRICH-DONE] report
            // per-build cost, not a process-global cumulative total across multiple builds.
            System.Threading.Interlocked.Exchange(ref _enrichTypeExtractTicks, 0);
            System.Threading.Interlocked.Exchange(ref _enrichEmbeddingTicks, 0);
            System.Threading.Interlocked.Exchange(ref _enrichRefScanTicks, 0);
            System.Threading.Interlocked.Exchange(ref _enrichTextualScanTicks, 0);
        }

        public void MarkReindexProgress(double progress, int etaMs)
        {
            lock (_stateLock)
            {
                _state.Progress = progress;
                _state.EtaMs = etaMs;
            }
        }

        public void MarkIndexFailed()
        {
            lock (_stateLock)
            {
                _state.Status = "Cold";
                _state.Progress = null;
                _state.EtaMs = null;
            }
        }

        public void MarkIndexComplete(int totalObjects)
        {
            lock (_stateLock)
            {
                _state.Status = "Ready";
                _state.LastIndexedAt = DateTime.UtcNow;
                _state.TotalObjects = totalObjects;
                _state.Progress = null;
                _state.EtaMs = null;
            }
        }

        // Fase 0: log [TIME-TO-USABLE] once per usable milestone so the SM warmup
        // (captured in sdkReadyMs) is cleanly separated from the object walk
        // (sinceSdkReadyMs). processMs is absolute since process start.
        private static int _loggedUltraLiteUsable = 0;
        private static int _loggedLiteUsable = 0;

        public void MarkLitePassComplete(int totalObjects)
        {
            lock (_stateLock)
            {
                _state.Status = "LiteReady";
                _state.TotalObjects = totalObjects;
                _state.LitePassCompletedUtc = DateTime.UtcNow;
                _state.Progress = 1.0;
                _state.EtaMs = 0;
            }
            if (System.Threading.Interlocked.Exchange(ref _loggedLiteUsable, 1) == 0)
            {
                long p = Program.ProcessElapsedMs;
                Logger.Info($"[TIME-TO-USABLE] event=liteReady processMs={p} sdkReadyMs={Program.SdkReadyAtMs} sinceSdkReadyMs={Math.Max(0, p - Program.SdkReadyAtMs)} objects={totalObjects}");
            }
        }

        // v2.6.9 perf: signal that the lite walk has emitted enough entries to
        // make list_objects / query / inspect useful, even though it hasn't
        // walked the full catalogue yet. Status stays at "UltraLiteReady" until
        // MarkLitePassComplete promotes it to "LiteReady". Partial flag lets
        // ListService surface a `partial:true` hint so the agent knows the
        // catalogue is still growing.
        public void MarkUltraLiteReady(int objectsSoFar)
        {
            lock (_stateLock)
            {
                // Don't downgrade if we've already advanced past UltraLite.
                if (_state.Status == "LiteReady" || _state.Status == "Enriching" || _state.Status == "Ready")
                    return;
                _state.Status = "UltraLiteReady";
                _state.TotalObjects = objectsSoFar;
                _state.Progress = null;
                _state.EtaMs = null;
            }
            if (System.Threading.Interlocked.Exchange(ref _loggedUltraLiteUsable, 1) == 0)
            {
                long p = Program.ProcessElapsedMs;
                Logger.Info($"[TIME-TO-USABLE] event=ultraLiteReady processMs={p} sdkReadyMs={Program.SdkReadyAtMs} sinceSdkReadyMs={Math.Max(0, p - Program.SdkReadyAtMs)} objects={objectsSoFar}");
            }
        }

        public void MarkEnrichmentStarted()
        {
            lock (_stateLock)
            {
                _state.Status = "Enriching";
                _state.EnrichmentStartedUtc = DateTime.UtcNow;
            }
        }

        public IndexCacheService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        // v2.3.8 (Task 1.3): test-only seam. Bypasses disk load so unit tests for
        // graph services (CallerGraphService) can drive a deterministic in-memory
        // index without spinning up a KB. Not intended for production code paths.
        internal void LoadFromEntries(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            lock (_lock)
            {
                var idx = new SearchIndex();
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                        string type = string.IsNullOrEmpty(e.Type) ? "Object" : e.Type;
                        string key = type + ":" + e.Name;
                        idx.Objects[key] = e;
                    }
                }
                idx.LastUpdated = DateTime.UtcNow;
                _index = idx;
                _initialized = true;
                PrimeHierarchyCacheFromIndex(idx);
            }
            // v2.6.9 perf: flip state to Ready so the gateway-side / list-service
            // fast-fail path doesn't treat fixture-loaded indexes as still-building.
            MarkIndexComplete(_index.Objects.Count);
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public KbService KbService => _buildService?.KbService;

        // Name-keyed projection of the index, rebuilt lazily when the underlying
        // _index reference changes. Lookups are O(1); previously each call
        // iterated up to 38k entries (review-finding: build with 100 error lines
        // hammered the index ~3.8M times). _byNameIndex is invalidated by
        // setting _byNameIndexFor = null whenever _index is replaced.
        private Dictionary<string, SearchIndex.IndexEntry> _byNameIndex;
        private SearchIndex _byNameIndexFor;
        private readonly object _byNameLock = new object();

        private Dictionary<string, SearchIndex.IndexEntry> GetByNameIndex()
        {
            var idx = GetIndex();
            if (idx?.Objects == null) return null;
            lock (_byNameLock)
            {
                if (!ReferenceEquals(idx, _byNameIndexFor) || _byNameIndex == null)
                {
                    var built = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in idx.Objects.Values)
                    {
                        if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                        // Last-write-wins on name collision (rare; storage key
                        // is Type:Name so two objects can share a bare Name).
                        built[v.Name] = v;
                    }
                    _byNameIndex = built;
                    _byNameIndexFor = idx;
                }
                return _byNameIndex;
            }
        }

        // v2.6.6 Stream E (FR#7): index-aware verification used by the normalizer
        // (BuildService.NormalizeMissingObjectName) and by the orphan-sweep /
        // CS2001-demotion paths.
        public bool IsObjectKnown(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                var byName = GetByNameIndex();
                return byName != null && byName.ContainsKey(name);
            }
            catch (Exception ex)
            {
                Logger.Warn("[IsObjectKnown] lookup failed for '" + name + "': " + ex.Message);
                return false;
            }
        }

        // v2.6.6 Stream E (FR#3/#9): companion to IsObjectKnown — returns the
        // first index entry matching the case-insensitive Name, or null.
        public SearchIndex.IndexEntry TryGetEntryByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var byName = GetByNameIndex();
                if (byName == null) return null;
                byName.TryGetValue(name, out var entry);
                return entry;
            }
            catch (Exception ex)
            {
                Logger.Warn("[TryGetEntryByName] lookup failed for '" + name + "': " + ex.Message);
                return null;
            }
        }

         public bool IsIndexMissing
         {
             get
             {
                 try
                 {
                     if (string.IsNullOrEmpty(_indexPath)) return true;
                     // PERFORMANCE (W-A3): accept either the gzipped (new) or plain (legacy) snapshot.
                     if (!File.Exists(_indexPathGz) && !File.Exists(_indexPath)) return true;

                     var index = GetIndex();
                     return index == null || index.Objects.Count == 0;
                 }
                 catch { return true; }
             }
         }
 
         public bool IsScanning => KbService?.IsIndexing ?? false;

         public bool IsInitialized => _initialized;

        // Public so the index build (KbService.BulkIndex) can force the on-disk cache path
        // to be resolved BEFORE any read/write. Otherwise force=true (which never touches
        // GetIndex/IsIndexMissing) writes to the constructor's <BaseDir>\cache\search_index.*
        // fallback while the warm-start read path resolves the %LOCALAPPDATA%\…\index_{hash}.*
        // path — so the sidecar written by one is never found by the other (metaPresent=False,
        // delta never engages).
        // proactiveLoad=false resolves the on-disk cache path WITHOUT kicking the background
        // GetIndex() warm load. BulkIndex(force=true) needs the path resolved (so the right
        // %LOCALAPPDATA%\…\index_{hash}.* files get deleted) but must NOT start a background
        // load that would race Clear()/DeleteOnDiskSnapshot() and briefly republish the stale
        // snapshot as Ready.
        public void EnsureInitialized(bool proactiveLoad = true)
        {
            if (_initialized) return;
            try
            {
                string kbPath = _buildService.GetKBPath();
                if (!string.IsNullOrEmpty(kbPath)) Initialize(kbPath, proactiveLoad);
            }
            catch { }
        }

        public void Initialize(string kbPath, bool proactiveLoad = true)
        {
            if (string.IsNullOrEmpty(kbPath)) return;
            if (kbPath.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase)) kbPath = Path.GetDirectoryName(kbPath);

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheDir = Path.Combine(localAppData, "GxMcp", "Cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string hash = GetHash(kbPath);
                _indexPath = Path.Combine(cacheDir, string.Format("index_{0}.json", hash));
                _initialized = true;
                // Fase 1 diagnostic: log the resolved cache paths so a sidecar-not-found on warm
                // start (metaPresent=False) can be traced to a hash/path mismatch between runs.
                Logger.Info(string.Format("[INDEX-CACHE-PATHS] kbPathIn={0} hash={1} gz={2} meta={3}", kbPath, hash, _indexPathGz, _metaPath));

                // PERFORMANCE: Pro-active loading in background. Skipped when proactiveLoad=false
                // (force reindex) so it doesn't race the imminent Clear()/DeleteOnDiskSnapshot().
                if (proactiveLoad) Task.Run(() => GetIndex());
            }
            catch (Exception ex) { Logger.Error("IndexCache Init Error: " + ex.Message); }
        }

        private string GetHash(string input)
        {
            // Fase 1: canonicalize the KB path so the same KB always maps to the same cache
            // hash across worker runs — otherwise a sidecar written under one path-spelling
            // (trailing slash, casing, forward vs back slashes) isn't found under another and
            // the warm-start delta never engages (metaPresent=False).
            string canonical = input ?? string.Empty;
            try { canonical = Path.GetFullPath(canonical); } catch { /* keep raw on malformed paths */ }
            canonical = canonical.TrimEnd('\\', '/').ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 16);
            }
        }

        private void BuildParentIndex(SearchIndex index)
        {
            var byParent = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            // Fase 2: (re)build the Guid → storage-key map alongside the parent index — both
            // derive from a single full pass over Objects, so do them together.
            var guidToKey = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in index.Objects)
            {
                var entry = kv.Value;
                string parent = entry.ParentPath ?? entry.Parent ?? "";
                if (!byParent.TryGetValue(parent, out var list))
                {
                    list = new System.Collections.Generic.List<SearchIndex.IndexEntry>();
                    byParent[parent] = list;
                }

                // PERFORMANCE: Since we are iterating dictionary values, there are NO duplicates.
                // Using .Add() directly changes this from an O(N^2) operation to O(N).
                list.Add(entry);

                if (!string.IsNullOrEmpty(entry.Guid)) guidToKey[entry.Guid] = kv.Key;
            }
            index.ChildrenByParent = byParent;
            index.GuidToKey = guidToKey;
        }

        private string GetEntryStorageKey(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;
            // PERFORMANCE (W-B1): cache the computed key on the entry to avoid repeated
            // string.Format in AddOrUpdateEntryInParentIndex's List.Any lookup.
            if (!string.IsNullOrEmpty(entry.StorageKey)) return entry.StorageKey;

            string key;
            if (string.Equals(entry.Type, "Folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Type, "Module", StringComparison.OrdinalIgnoreCase))
            {
                string scopedPath = entry.Path ?? entry.Name ?? string.Empty;
                key = string.Format("{0}:{1}", entry.Type ?? string.Empty, scopedPath);
            }
            else
            {
                key = string.Format("{0}:{1}", entry.Type ?? string.Empty, entry.Name ?? string.Empty);
            }
            entry.StorageKey = key;
            return key;
        }

        private void NormalizeLegacyHierarchy(SearchIndex index)
        {
            if (index?.Objects == null) return;

            foreach (var entry in index.Objects.Values)
            {
                if (string.IsNullOrWhiteSpace(entry.ParentPath))
                {
                    entry.ParentPath = InferLegacyParentPath(entry);
                }

                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    entry.Path = InferLegacyPath(entry);
                }

                if (string.IsNullOrWhiteSpace(entry.ParentFolderPath))
                {
                    entry.ParentFolderPath = ComposeParentFolderPath(entry.ParentPath);
                }
            }
        }

        private string InferLegacyParentPath(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;

            if (string.IsNullOrWhiteSpace(entry.Parent) ||
                string.Equals(entry.Parent, "Root Module", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(entry.Module) &&
                string.Equals(entry.Parent, entry.Module, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Module;
            }

            // When an entry lives inside a Folder under a Module, qualify the
            // folder name with its parent module to preserve hierarchy. Without
            // this, folders sharing names across modules (e.g. "Procs") collapse
            // into a single bucket in the parent index.
            if (!string.IsNullOrWhiteSpace(entry.Module))
            {
                return string.Format("{0}/{1}", entry.Module, entry.Parent);
            }

            return entry.Parent;
        }

        // v2.3.8 (Task 2.2): full folder path always prefixed with the synthetic
        // "Root Module" so callers can pathPrefix-filter by the same string they
        // see in the GeneXus IDE. Empty hierarchy.ParentPath means object lives
        // directly under DesignModel -> we surface "Root Module" as the folder.
        private static string ComposeParentFolderPath(string hierarchyParentPath)
        {
            if (string.IsNullOrWhiteSpace(hierarchyParentPath))
                return "Root Module";
            // Already prefixed? Be tolerant — never double-prefix.
            if (hierarchyParentPath.Equals("Root Module", StringComparison.OrdinalIgnoreCase) ||
                hierarchyParentPath.StartsWith("Root Module/", StringComparison.OrdinalIgnoreCase))
                return hierarchyParentPath;
            return "Root Module/" + hierarchyParentPath;
        }

        private string InferLegacyPath(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;

            var parentPath = entry.ParentPath ?? InferLegacyParentPath(entry);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return entry.Name ?? string.Empty;
            }

            return string.Format("{0}/{1}", parentPath, entry.Name ?? string.Empty);
        }

        private void AddOrUpdateEntryInParentIndex(System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>> byParent, SearchIndex.IndexEntry entry)
        {
            string parent = entry.ParentPath ?? entry.Parent ?? "";
            var list = byParent.GetOrAdd(parent, _ => new System.Collections.Generic.List<SearchIndex.IndexEntry>());
            
            lock (list)
            {
                string entryKey = GetEntryStorageKey(entry);
                if (!list.Any(e => string.Equals(GetEntryStorageKey(e), entryKey, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(entry);
                }
            }
        }

        // Fase 2: drop an entry from its parent-children list (used by rename collapse and
        // RemoveEntryByGuid). Matches by storage key under the same per-list lock the add path uses.
        private void RemoveEntryFromParentIndex(System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>> byParent, SearchIndex.IndexEntry entry)
        {
            if (byParent == null || entry == null) return;
            string parent = entry.ParentPath ?? entry.Parent ?? "";
            if (!byParent.TryGetValue(parent, out var list)) return;
            string entryKey = GetEntryStorageKey(entry);
            lock (list)
            {
                list.RemoveAll(e => string.Equals(GetEntryStorageKey(e), entryKey, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Fase 2: remove an object by its (stable) Guid — used by the warm-start deletion
        // sweep when an object that was in the persisted index no longer exists in the KB.
        // Resolves the storage key via GuidToKey so renames don't strand it.
        public void RemoveEntryByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            var index = GetIndex();
            lock (_lock)
            {
                if (index.GuidToKey == null || !index.GuidToKey.TryGetValue(guid, out var key)) return;
                if (index.Objects.TryRemove(key, out var removed))
                {
                    if (index.ChildrenByParent != null) RemoveEntryFromParentIndex(index.ChildrenByParent, removed);
                    if (Guid.TryParse(guid, out var g)) _hierarchyCache.TryRemove(g, out _);
                }
                index.GuidToKey.TryRemove(guid, out _);
            }
            MarkDirty();
        }

        private (string ParentName, string ParentPath, string Path, string ModuleName) ResolveHierarchy(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            // PERFORMANCE (W-M5): fast-path for objects whose hierarchy has already been resolved.
            if (obj != null && _hierarchyCache.TryGetValue(obj.Guid, out var cached))
            {
                return cached;
            }

            string parentName = string.Empty;
            string moduleName = null;
            var parentSegments = new System.Collections.Generic.List<string>();

            try
            {
                dynamic currentParent = obj?.Parent;
                bool isImmediateParent = true;

                while (currentParent != null)
                {
                    try
                    {
                        if (currentParent.Guid == obj.Guid)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    string parentTypeName = null;
                    try { parentTypeName = currentParent.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isImmediateParent)
                        {
                            parentName = "Root Module";
                        }
                        break;
                    }

                    bool isContainer =
                        currentParent is global::Artech.Architecture.Common.Objects.Module ||
                        currentParent is global::Artech.Architecture.Common.Objects.Folder;

                    if (!isContainer)
                    {
                        currentParent = currentParent.Parent;
                        isImmediateParent = false;
                        continue;
                    }

                    string currentName = null;
                    try { currentName = currentParent.Name; } catch { }

                    if (!string.IsNullOrWhiteSpace(currentName))
                    {
                        parentSegments.Insert(0, currentName);
                        if (isImmediateParent)
                        {
                            parentName = currentName;
                        }

                        if (moduleName == null &&
                            currentParent is global::Artech.Architecture.Common.Objects.Module)
                        {
                            moduleName = currentName;
                        }
                    }

                    currentParent = currentParent.Parent;
                    isImmediateParent = false;
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(moduleName))
            {
                try
                {
                    if (obj?.Module != null && obj.Module.Guid != obj.Guid)
                    {
                        moduleName = obj.Module.Name;
                    }
                }
                catch
                {
                }
            }

            string parentPath = string.Join("/", parentSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
            string path = parentPath;
            if (!string.IsNullOrWhiteSpace(obj?.Name))
            {
                path = string.IsNullOrEmpty(parentPath) ? obj.Name : parentPath + "/" + obj.Name;
            }

            var result = (parentName, parentPath, path, moduleName);
            if (obj != null)
            {
                _hierarchyCache[obj.Guid] = result;
            }
            return result;
        }

        // v2.6.9 perf: non-blocking probe. Returns the in-memory index if it's
        // already loaded, otherwise returns null without taking the loader lock.
        // Use this from hot paths (list_objects, query) that prefer to fast-fail
        // with an "Indexing" envelope rather than block 30-60s on a cold load.
        // GetIndex() retains the blocking-load behaviour for callers that
        // genuinely need the index synchronously.
        public SearchIndex TryGetLoadedIndex()
        {
            return _index;
        }

        // Best-effort: kick off the background load if it hasn't started yet.
        // Idempotent — safe to call repeatedly. Caller still uses
        // TryGetLoadedIndex() to test readiness without blocking.
        public void EnsureLoadStarted()
        {
            if (_index != null) return;
            try { EnsureInitialized(); } catch { /* best-effort */ }
            try { System.Threading.Tasks.Task.Run(() => GetIndex()); } catch { /* best-effort */ }
        }

        public SearchIndex GetIndex()
        {
            if (_index != null) return _index;
            EnsureInitialized();

            lock (_lock)
            {
                if (_index != null) return _index;
                try
                {
                    // PERFORMANCE (W-A3): prefer the new gzipped snapshot; fall back to legacy
                    // plain JSON so existing installs keep working without re-indexing.
                    string json = null;
                    if (File.Exists(_indexPathGz))
                    {
                        Logger.Debug(string.Format("Loading gzipped index from disk: {0}", _indexPathGz));
                        json = ReadGzippedText(_indexPathGz);
                    }
                    else if (File.Exists(_indexPath))
                    {
                        Logger.Debug(string.Format("Loading legacy plain index from disk: {0}", _indexPath));
                        json = File.ReadAllText(_indexPath);
                    }

                    if (!string.IsNullOrEmpty(json))
                    {
                        _index = SearchIndex.FromJson(json);
                        NormalizeLegacyHierarchy(_index);
                        BuildParentIndex(_index);
                        PrimeHierarchyCacheFromIndex(_index);
                        Logger.Info(string.Format("Index loaded. Objects: {0}", _index.Objects.Count));
                        // v2.3.8 (post-Task 1.2 fix): when we hydrate the in-memory index
                        // from the on-disk cache (warm start), publish Ready to IndexState
                        // so whoami doesn't keep reporting Cold while list/search hit a
                        // fully-populated index. Without this the state machine only
                        // transitioned via BulkIndex's MarkIndexComplete, which is skipped
                        // on warm starts (AlreadyIndexed path in KbService.BulkIndex).
                        if (_index.Objects.Count > 0)
                        {
                            MarkIndexComplete(_index.Objects.Count);
                        }
                    }
                }
                catch (Exception ex) { Logger.Error("Load Index Error: " + ex.Message); }

                if (_index == null) _index = new SearchIndex();
                return _index;
            }
        }

        private static string ReadGzippedText(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        public bool LooksLikeAttributeName(string term)
        {
            if (string.IsNullOrEmpty(term)) return false;
            return GetIndex().Objects.ContainsKey($"Attribute:{term}");
        }

        public void UpdateIndex(SearchIndex index)
        {
            lock (_lock)
            {
                BuildParentIndex(index);
                _index = index;
            }
            MarkDirty();
            // Fire and forget save to disk with throttling (W-A3: 10s → 30s)
            ScheduleThrottledFlush();
        }

        // Fase 0.5: route per-object enrichment flushes through the 30s throttle so a
        // bulk enrichment pass doesn't re-serialize the whole (growing) index on every
        // object — measured 261 full-index serializations in one pass on a 38.6k KB,
        // serializeMs climbing 300→1800ms as the index swelled 12→45MB. The direct
        // Task.Run(FlushToDisk) calls bypassed the throttle that UpdateIndex already had.
        //
        // Trailing-edge debounce: writes landing INSIDE the throttle window used to be
        // dropped on the floor (nothing re-armed a flush), so the last burst of changes
        // before the process went idle never reached disk until exit. Now a one-shot
        // timer fires at the end of the window and flushes whatever is still dirty.
        private double _flushThrottleSeconds = FlushThrottleSeconds;
        internal void SetFlushThrottleForTest(double seconds) { _flushThrottleSeconds = seconds; }
        private System.Threading.Timer _trailingFlushTimer;
        private int _trailingFlushArmed = 0;
        private readonly object _trailingTimerLock = new object();

        internal void ScheduleThrottledFlush()
        {
            if (!_savingInProgress && (DateTime.Now - _lastFlushTime).TotalSeconds > _flushThrottleSeconds)
            {
                Task.Run(() => FlushToDisk());
                return;
            }
            ArmTrailingFlush();
        }

        private void ArmTrailingFlush()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _trailingFlushArmed, 1, 0) != 0) return;
            try
            {
                lock (_trailingTimerLock)
                {
                    if (_trailingFlushTimer == null)
                        _trailingFlushTimer = new System.Threading.Timer(OnTrailingFlush, null,
                            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    double elapsed = (DateTime.Now - _lastFlushTime).TotalSeconds;
                    int dueMs = (int)Math.Max(250, (_flushThrottleSeconds - elapsed) * 1000);
                    _trailingFlushTimer.Change(dueMs, System.Threading.Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _trailingFlushArmed, 0);
                Logger.Warn("ArmTrailingFlush failed: " + ex.Message);
            }
        }

        private void OnTrailingFlush(object state)
        {
            System.Threading.Interlocked.Exchange(ref _trailingFlushArmed, 0);
            try
            {
                if (IsFullyFlushed) return;
                bool flushed = FlushToDisk();
                // A concurrent flush was in flight (or this one failed) and changes are
                // still pending — re-arm so the trailing write isn't lost.
                if (!flushed && !IsFullyFlushed) ArmTrailingFlush();
            }
            catch (Exception ex) { Logger.Warn("Trailing flush failed: " + ex.Message); }
        }

        // Force a confirmed-current flush regardless of the throttle window. Call at the
        // end of a bulk enrichment drain / delta refresh BEFORE writing the meta sidecar.
        // Returns true only once every change that was dirty at the moment of this call
        // is confirmed on disk; returns false (never lies) when the deadline elapses —
        // in which case the caller MUST NOT stamp the sidecar, otherwise the persisted
        // high-water-mark would claim changes the body doesn't contain and the next warm
        // start's delta would skip them forever.
        public bool FlushNow(int timeoutMs = 30000)
        {
            if (_index == null) return false; // nothing loaded — nothing to certify
            long target = System.Threading.Interlocked.Read(ref _dirtyGeneration);
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
            while (true)
            {
                if (System.Threading.Interlocked.Read(ref _flushedGeneration) >= target) return true;
                FlushToDisk(); // no-ops (returns false) while another flush is mid-flight
                if (System.Threading.Interlocked.Read(ref _flushedGeneration) >= target) return true;
                if (DateTime.UtcNow >= deadline)
                {
                    Logger.Warn($"[FLUSH-NOW] timed out after {timeoutMs}ms (flushedGen={FlushedGeneration} targetGen={target}) — caller must not stamp the meta sidecar.");
                    return false;
                }
                System.Threading.Thread.Sleep(20);
            }
        }

        // ===== Fase 1: persistible incremental index (version stamp + high-water-mark + delta-on-open) =====

        // Bump whenever IndexEntry's serialized shape changes. A mismatch on warm start
        // forces a full cold rebuild (IntelliJ stub-index pattern) instead of silently
        // deserialising a 38k index into a different layout.
        public const int CurrentSchemaVersion = 1;

        // Sidecar holding the validation header (schema version, worker-DLL hash, high-water-mark).
        // Written ONLY when enrichment fully completes (PersistEnrichedComplete) or after a delta
        // refresh — never on the throttled mid-enrichment body flushes. So the sidecar's presence
        // means "the body on disk is fully enriched and this hwm is trustworthy"; a worker that
        // dies mid-enrichment leaves a body but no sidecar → next warm start does a full rebuild.
        private string _metaPath => string.IsNullOrEmpty(_indexPath) ? null : _indexPath.Replace(".json", ".meta.json");

        // High-water-mark: max KBObject.LastUpdate observed. Stored as ticks for lock-free CAS.
        // Instance field (per index / per KB) — not process-global.
        private long _hwmTicks = 0;

        /// <summary>Advance the high-water-mark if <paramref name="lastUpdate"/> is newer. Lock-free.</summary>
        public void ObserveLastUpdate(DateTime lastUpdate)
        {
            long t = lastUpdate.Ticks;
            long cur;
            while (true)
            {
                cur = System.Threading.Interlocked.Read(ref _hwmTicks);
                if (t <= cur) return;
                if (System.Threading.Interlocked.CompareExchange(ref _hwmTicks, t, cur) == cur) return;
            }
        }

        /// <summary>Reset the high-water-mark (called on a full reindex so a stale max doesn't persist).</summary>
        public void ResetHighWaterMark() => System.Threading.Interlocked.Exchange(ref _hwmTicks, 0);

        /// <summary>
        /// Snapshot of entries that were never fully enriched (no embedding) — used by the
        /// warm-start delta path to resume enrichment when the persisted body was lite-only
        /// or partially enriched (e.g. a worker idle-evicted before completing).
        /// Embedding==null/empty is the reliable signal: UpdateEntry always sets a 128-float
        /// embedding, lite stubs never carry one.
        /// </summary>
        public List<SearchIndex.IndexEntry> GetUnenrichedEntries()
        {
            var idx = GetIndex();
            var result = new List<SearchIndex.IndexEntry>();
            if (idx?.Objects == null) return result;
            foreach (var e in idx.Objects.Values)
            {
                if (e == null) continue;
                if (e.Embedding == null || e.Embedding.Length == 0) result.Add(e);
            }
            return result;
        }

        /// <summary>Current high-water-mark as a DateTime (MinValue if none observed).</summary>
        public DateTime CurrentHighWaterMark
        {
            // UTC: the ticks come from KBObject.LastUpdate, which the watcher and GetKeys()
            // treat as UTC (compared against DateTime.UtcNow). Labelling it Utc keeps the
            // persisted *Utc field honest and makes any future cross-comparison safe.
            get { long t = System.Threading.Interlocked.Read(ref _hwmTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); }
        }

        /// <summary>
        /// Persist the validation sidecar. Call AFTER a body flush, only when the body is in a
        /// trustworthy (fully-enriched or delta-merged) state. Atomic temp-then-move.
        /// </summary>
        public void WriteMetaSidecar(int objectCount)
        {
            string metaPath = _metaPath;
            if (string.IsNullOrEmpty(metaPath)) return;
            try
            {
                var meta = new WarmIndexSnapshotMetadata
                {
                    WorkerDllSha256 = WarmIndexSnapshot.ComputeWorkerDllSha256(),
                    KbPath = _buildService?.GetKBPath(),
                    CapturedAtUtc = DateTime.UtcNow.ToString("o"),
                    ObjectCount = objectCount,
                    SchemaVersion = CurrentSchemaVersion,
                    HighWaterMarkUtc = CurrentHighWaterMark == DateTime.MinValue ? null : CurrentHighWaterMark.ToString("o")
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(meta);
                string dir = Path.GetDirectoryName(metaPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = metaPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(metaPath)) File.Delete(metaPath);
                File.Move(tmp, metaPath);
                Logger.Info($"[INDEX-META] sidecar written: schema={CurrentSchemaVersion} hwm={meta.HighWaterMarkUtc ?? "<none>"} objects={objectCount}");
            }
            catch (Exception ex) { Logger.Warn("WriteMetaSidecar failed: " + ex.Message); }
        }

        /// <summary>Result of validating the on-disk cache against the current worker + schema.</summary>
        public sealed class OnDiskCacheValidation
        {
            public bool BodyPresent;
            public bool MetaPresent;
            public bool SchemaMatch;
            public bool DllMatch;
            public DateTime HighWaterMark = DateTime.MinValue;
            // Delta-on-open is only safe when the body is present AND a trustworthy sidecar
            // (matching schema + worker DLL) accompanies it. Anything else → full rebuild.
            public bool CanDelta => BodyPresent && MetaPresent && SchemaMatch && DllMatch && HighWaterMark != DateTime.MinValue;
        }

        /// <summary>
        /// Inspect the on-disk cache (cheap — reads the small sidecar, not the body) to decide
        /// whether a warm start can do a bounded delta refresh or must full-rebuild.
        /// </summary>
        public OnDiskCacheValidation ValidateOnDiskCache()
        {
            var v = new OnDiskCacheValidation();
            try
            {
                EnsureInitialized();
                v.BodyPresent = (!string.IsNullOrEmpty(_indexPathGz) && File.Exists(_indexPathGz))
                                 || (!string.IsNullOrEmpty(_indexPath) && File.Exists(_indexPath));
                string metaPath = _metaPath;
                v.MetaPresent = !string.IsNullOrEmpty(metaPath) && File.Exists(metaPath);
                Logger.Info(string.Format("[INDEX-CACHE-PATHS] validate: bodyPresent={0} metaPresent={1} gz={2} meta={3}", v.BodyPresent, v.MetaPresent, _indexPathGz, metaPath));
                if (v.MetaPresent)
                {
                    var meta = Newtonsoft.Json.JsonConvert.DeserializeObject<WarmIndexSnapshotMetadata>(File.ReadAllText(metaPath));
                    v.SchemaMatch = meta != null && meta.SchemaVersion == CurrentSchemaVersion;
                    string currentDll = WarmIndexSnapshot.ComputeWorkerDllSha256();
                    v.DllMatch = meta != null && string.Equals(meta.WorkerDllSha256, currentDll, StringComparison.OrdinalIgnoreCase);
                    if (meta != null && !string.IsNullOrEmpty(meta.HighWaterMarkUtc)
                        && DateTime.TryParse(meta.HighWaterMarkUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var hwm))
                    {
                        v.HighWaterMark = hwm;
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("ValidateOnDiskCache failed (treating as full-rebuild): " + ex.Message); }
            return v;
        }

        // Returns true when this call wrote a snapshot to disk that is at least as new
        // as the dirty generation captured before serialization started; false when it
        // skipped (flush already in flight / no index) or failed. Never throws.
        private bool FlushToDisk()
        {
            if (_savingInProgress) return false;

            SearchIndex snapshot = null;
            lock (_lock)
            {
                if (_savingInProgress) return false;
                if (_index == null) return false;

                _savingInProgress = true;
                // ELITE: Snapshot the index to serialize it OUTSIDE the lock.
                // ConcurrentDictionary iteration is thread-safe and provides a consistent snapshot view.
                snapshot = _index;
            }

            // Capture the generation BEFORE enumeration begins: every mutation that
            // happened-before this read is visible to the serializer below, so on
            // success the on-disk body provably contains generation `gen`.
            long gen = System.Threading.Interlocked.Read(ref _dirtyGeneration);

            try
            {
                string dir = Path.GetDirectoryName(_indexPathGz);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var settings = new Newtonsoft.Json.JsonSerializerSettings {
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
                    Formatting = Newtonsoft.Json.Formatting.None
                };

                var flushSw = System.Diagnostics.Stopwatch.StartNew();

                // PERFORMANCE (W-A3): write gzipped via a temp file + atomic move so partial
                // writes never leave a corrupt snapshot on disk.
                // LOH fix: stream the serializer straight through gzip instead of building
                // the whole index as one ~45MB JSON string first — the intermediate string
                // landed on the Large Object Heap on every flush.
                string tmpPath = _indexPathGz + ".tmp";
                // CompressionLevel.Fastest: file isn't transmitted, the ~5% ratio improvement
                // from Optimal isn't worth the extra CPU on every flush.
                var serializer = Newtonsoft.Json.JsonSerializer.Create(settings);
                using (var fs = File.Create(tmpPath))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                using (var jsonWriter = new Newtonsoft.Json.JsonTextWriter(writer))
                {
                    serializer.Serialize(jsonWriter, snapshot);
                }
                long serializeGzipMs = flushSw.ElapsedMilliseconds;
                long gzBytes = 0;
                try { gzBytes = new FileInfo(tmpPath).Length; } catch { }

                flushSw.Restart();
                if (File.Exists(_indexPathGz)) File.Delete(_indexPathGz);
                File.Move(tmpPath, _indexPathGz);
                long moveMs = flushSw.ElapsedMilliseconds;

                // Clean up the legacy plain-JSON file once we have a valid gzipped snapshot.
                try { if (File.Exists(_indexPath)) File.Delete(_indexPath); } catch { }

                long sizeKb = gzBytes / 1024;
                int entryCount = snapshot.Objects?.Count ?? 0;
                // Fase 3 measurement: piggyback the running enrichment sub-step split on the
                // throttled flush so the SDK-bound (refScan/typeExtract) vs CPU-only
                // (embedding/textualScan) proportion is observable without waiting for the
                // (pathologically slow) full drain to reach [ENRICH-DONE].
                Logger.Info($"[INDEX-SAVE] gzKB={sizeKb} serializeGzipMs={serializeGzipMs} moveMs={moveMs} totalMs={serializeGzipMs + moveMs} entries={entryCount} gen={gen} | {GetEnrichTimingSummary()}");
                System.Threading.Interlocked.Exchange(ref _consecutiveFlushFailures, 0);
                _lastFlushSuccessUtc = DateTime.UtcNow;
                _lastFlushErrorMessage = null;
                // Publish the confirmed-on-disk generation (monotonic max).
                long cur;
                while ((cur = System.Threading.Interlocked.Read(ref _flushedGeneration)) < gen
                       && System.Threading.Interlocked.CompareExchange(ref _flushedGeneration, gen, cur) != cur) { }
                return true;
            }
            catch (Exception ex) {
                int n = System.Threading.Interlocked.Increment(ref _consecutiveFlushFailures);
                _lastFlushErrorMessage = ex.Message;
                Logger.Error($"Flush Error (consecutive={n}): {ex.Message}");
                return false;
            }
            finally {
                _savingInProgress = false;
                _lastFlushTime = DateTime.Now;
            }
        }

        private string GetJsonHash(string json)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                return BitConverter.ToString(bytes).Replace("-", "");
            }
        }

        // v2.6.8: defensive reads for KBObject SDK accessors that can throw on
        // partially-loaded objects. Callers want a sentinel ("unknown") rather than
        // a crashed indexer.
        private static DateTime SafeReadDate(Func<DateTime> read)
        {
            try { return read(); } catch { return DateTime.MinValue; }
        }

        private static string SafeReadString(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }

        public void UpdateEntry(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var index = GetIndex();
            var hierarchy = ResolveHierarchy(obj);

            var entry = new SearchIndex.IndexEntry
            {
                Guid = obj.Guid.ToString(),
                Name = obj.Name,
                Type = obj.TypeDescriptor.Name,
                Description = obj.Description,
                Parent = hierarchy.ParentName,
                ParentPath = hierarchy.ParentPath,
                ParentFolderPath = ComposeParentFolderPath(hierarchy.ParentPath),
                Path = hierarchy.Path,
                Module = hierarchy.ModuleName,
                LastUpdate = SafeReadDate(() => obj.LastUpdate),
                CreatedAt = SafeReadDate(() => obj.VersionDate),
                LastModifiedBy = SafeReadString(() => obj.UserName),
                // This entry IS being enriched (type/embedding synchronously here; edges async
                // via EnrichEdges). Mark it so the LIVE index entry reflects enriched state —
                // otherwise AnalyzeService re-reads IsEnriched=false and re-promotes on every
                // call (the enricher only flips the orphaned lite stub, not this replacement).
                IsEnriched = true
            };

            // Fase 1: advance the high-water-mark so incremental updates keep the
            // persisted delta baseline current.
            ObserveLastUpdate(entry.LastUpdate);

            long teStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (obj is global::Artech.Genexus.Common.Objects.Attribute attr)
            {
                entry.DataType = attr.Type.ToString();
                entry.Length = attr.Length;
                entry.Decimals = attr.Decimals;
                entry.IsFormula = attr.Formula != null;
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Table tbl)
            {
                entry.RootTable = tbl.Name;
                try {
                    var children = new Newtonsoft.Json.Linq.JArray();
                    dynamic dStructure = ((dynamic)tbl).TableStructure;
                    if (dStructure != null && dStructure.Attributes != null) {
                        foreach (dynamic tableAttr in dStructure.Attributes) 
                            children.Add(VisualStructureMapper.MapAttribute(tableAttr));
                    }
                    entry.SourceSnippet = children.ToString(Newtonsoft.Json.Formatting.None);
                } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Transaction trn)
            {
                entry.RootTable = trn.Structure.Root.Name;
                try { entry.ParmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase)); } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.SDT sdt)
            {
                try {
                    entry.SourceSnippet = StructureParser.SerializeToText(obj);
                    entry.Complexity = entry.SourceSnippet?.Split('\n').Length ?? 0;
                } catch (Exception ex) {
                    Logger.Error($"SDT index snippet failed for {obj.Name}: {ex.Message}");
                }
            }

            // Calculate Complexity for Procedures/DataProviders
            if (obj is global::Artech.Genexus.Common.Objects.Procedure || obj is global::Artech.Genexus.Common.Objects.DataProvider)
            {
                try {
                    dynamic sourcePart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
                    if (sourcePart != null) {
                        string src = sourcePart.Source ?? "";
                        entry.Complexity = src.Split('\n').Length;
                    }
                } catch { }
            }

            System.Threading.Interlocked.Add(ref _enrichTypeExtractTicks, System.Diagnostics.Stopwatch.GetTimestamp() - teStart);

            string key = GetEntryStorageKey(entry);

            // Compute Embedding
            string semanticText = $"{entry.Name} {entry.Type} {entry.Description} {entry.RootTable} {entry.ParmRule}";
            long emStart = System.Diagnostics.Stopwatch.GetTimestamp();
            entry.Embedding = _vectorService.ComputeEmbedding(semanticText);
            System.Threading.Interlocked.Add(ref _enrichEmbeddingTicks, System.Diagnostics.Stopwatch.GetTimestamp() - emStart);

            // Fase 2: rename collapse. The Guid is stable across a rename but the Type:Name
            // storage key changes — without this the old key lingers as a duplicate/stale
            // entry. If this Guid was previously stored under a different key, drop the old one.
            if (index.GuidToKey != null && !string.IsNullOrEmpty(entry.Guid)
                && index.GuidToKey.TryGetValue(entry.Guid, out var oldKey)
                && !string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase))
            {
                if (index.Objects.TryRemove(oldKey, out var stale) && index.ChildrenByParent != null)
                    RemoveEntryFromParentIndex(index.ChildrenByParent, stale);
            }

            // Atomic update using ConcurrentDictionary
            index.Objects.AddOrUpdate(key, entry, (k, existing) => entry);
            if (index.ChildrenByParent != null)
            {
                AddOrUpdateEntryInParentIndex(index.ChildrenByParent, entry);
            }
            if (index.GuidToKey != null && !string.IsNullOrEmpty(entry.Guid))
            {
                index.GuidToKey[entry.Guid] = key;
            }

            // ENRICHMENT: Dependencies (Calls/Tables/CalledBy) via GetReferences + textual scan,
            // fire-and-forget on the background STA dispatcher. NOTE: this populates the target's
            // OUTGOING edges (Calls) and adds the target to its callees' CalledBy lists — it does
            // NOT populate the target's own CalledBy (incoming/callers), which only fill in as the
            // CALLERS get enriched. So in lazy mode impact analysis (callers) correctly relies on
            // the live SDK cross-check rather than the index — see AnalyzeService.
            Guid objGuid = obj.Guid;
            Program.EnqueueBackground(() => {
                try {
                    var kb = _buildService.KbService?.GetKB();
                    if (kb == null) return;
                    // SdkGate: GetKB/Get/GetReferences are SDK calls; the background
                    // dispatcher thread must not interleave them with other apartments.
                    using (SdkGate.Enter())
                    {
                        var bgObj = kb.DesignModel.Objects.Get(objGuid);
                        if (bgObj == null) return;
                        EnrichEdges((global::Artech.Architecture.Common.Objects.KBObject)bgObj, entry, index);
                    }
                } catch (Exception ex) { Logger.Error("Background Indexing Error: " + ex.Message); }
            });

            MarkDirty();
            // Fire and forget save to disk (throttled — see ScheduleThrottledFlush)
            ScheduleThrottledFlush();
        }

        // ── Copy-on-write edge lists ────────────────────────────────────────────
        // FlushToDisk serializes LIVE entries on a threadpool thread with no lock.
        // Mutating a published List<string> in place (Calls/Tables/CalledBy) raced
        // that serialization ("Collection was modified" flush failures / torn
        // snapshots). These helpers never mutate a published list: they replace the
        // property with a new list instance, so any reader/serializer holding the
        // old reference sees a stable snapshot. The per-entry lock only serializes
        // concurrent WRITERS against each other.
        internal static bool AddEdgeCow(
            SearchIndex.IndexEntry entry,
            Func<SearchIndex.IndexEntry, List<string>> get,
            Action<SearchIndex.IndexEntry, List<string>> set,
            string value)
        {
            if (entry == null || string.IsNullOrEmpty(value)) return false;
            lock (entry)
            {
                var current = get(entry) ?? new List<string>();
                if (current.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
                var next = new List<string>(current.Count + 1);
                next.AddRange(current);
                next.Add(value);
                set(entry, next);
                return true;
            }
        }

        internal static bool AddCallCow(SearchIndex.IndexEntry e, string name)
            => AddEdgeCow(e, x => x.Calls, (x, l) => x.Calls = l, name);
        internal static bool AddTableCow(SearchIndex.IndexEntry e, string name)
            => AddEdgeCow(e, x => x.Tables, (x, l) => x.Tables = l, name);
        internal static bool AddCalledByCow(SearchIndex.IndexEntry e, string name)
            => AddEdgeCow(e, x => x.CalledBy, (x, l) => x.CalledBy = l, name);

        // Dependency enrichment (extracted so the call path is readable): populate this entry's
        // Calls/Tables and the inverted CalledBy edges via the SDK reference graph + the FR#3
        // textual call-site scan. Runs on the background STA dispatcher.
        private void EnrichEdges(global::Artech.Architecture.Common.Objects.KBObject bgObj, SearchIndex.IndexEntry entry, SearchIndex index)
        {
            long rsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var references = ((dynamic)bgObj).GetReferences();
            bool changed = false;
            if (references != null)
            {
                foreach (dynamic reference in references)
                {
                    try
                    {
                        dynamic targetKey = reference.To;
                        if (targetKey == null) continue;
                        string targetName = targetKey.Name;
                        if (string.IsNullOrEmpty(targetName)) {
                            string keyStr = targetKey.ToString();
                            int colon = keyStr.IndexOf(':');
                            if (colon >= 0 && colon < keyStr.Length - 1)
                                targetName = keyStr.Substring(colon + 1);
                            else
                                targetName = keyStr;
                        }
                        if (string.IsNullOrEmpty(targetName)) continue;

                        string targetType = targetKey.TypeDescriptor.Name;
                        if (targetType == "Attribute" || targetType == "Table") {
                            if (AddTableCow(entry, targetName)) changed = true;
                        } else {
                            if (AddCallCow(entry, targetName)) changed = true;
                        }

                        // Inverted Index (CalledBy) — copy-on-write, see AddEdgeCow.
                        string targetIndexKey = $"{targetType}:{targetName}";
                        if (index.Objects.TryGetValue(targetIndexKey, out var targetEntry)) {
                            if (AddCalledByCow(targetEntry, entry.Name)) changed = true;
                        }
                    } catch { }
                }
            }
            System.Threading.Interlocked.Add(ref _enrichRefScanTicks, System.Diagnostics.Stopwatch.GetTimestamp() - rsStart);

            // FR#3 (friction-report 2026-05-14): GetReferences misses call sites in Event Start
            // blocks on some KB shapes. Augment with a textual scan over Source/Events/Rules:
            // any identifier that already exists as a non-Attribute object in the index is
            // treated as a call. The hard "must exist in index" filter eliminates false
            // positives from keywords / language built-ins.
            long tsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                bool textualChanged = EnrichCallsFromTextualScan(bgObj, entry, index);
                if (textualChanged) changed = true;
            }
            catch (Exception scanEx) { Logger.Debug("[FR#3] Textual call scan failed: " + scanEx.Message); }
            System.Threading.Interlocked.Add(ref _enrichTextualScanTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tsStart);

            if (changed) { MarkDirty(); ScheduleThrottledFlush(); }
        }

        // FR#3 (friction-report 2026-05-14): textual call-site scan that augments the SDK
        // reverse-dep index. Walks every ISource part on the object, harvests identifiers
        // that look like object calls (Name() / Name.Method() / Name(args)), and binds
        // them as Calls / CalledBy if the identifier matches a known object in the index.
        private static readonly System.Text.RegularExpressions.Regex _identifierCall =
            new System.Text.RegularExpressions.Regex(
                @"\b([A-Z][A-Za-z0-9_]{2,})(?:\s*\.\s*[A-Za-z0-9_]+)?\s*\(",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private bool EnrichCallsFromTextualScan(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            SearchIndex.IndexEntry entry,
            SearchIndex index)
        {
            if (obj == null || entry == null || index?.Objects == null) return false;

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in obj.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>())
            {
                string src = null;
                try { if (part is ISource sp) src = sp.Source; }
                catch { }
                if (string.IsNullOrEmpty(src)) continue;

                foreach (System.Text.RegularExpressions.Match m in _identifierCall.Matches(src))
                {
                    if (!m.Success) continue;
                    string name = m.Groups[1].Value;
                    if (name.Length < 3) continue;
                    candidates.Add(name);
                }
            }

            if (candidates.Count == 0) return false;
            bool changed = false;

            foreach (var name in candidates)
            {
                if (string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase)) continue;

                // Find any matching object in the index across object types we treat as callable.
                // Attributes/Tables don't qualify as "calls" — those go into Tables already.
                foreach (var typePrefix in _callableTypes)
                {
                    string key = typePrefix + ":" + name;
                    if (!index.Objects.TryGetValue(key, out var target) || target == null) continue;

                    if (AddCallCow(entry, target.Name)) changed = true;
                    if (AddCalledByCow(target, entry.Name)) changed = true;
                    break; // first matching object type wins
                }
            }

            return changed;
        }

        private static readonly string[] _callableTypes = new[]
        {
            "Procedure", "DataProvider", "WebPanel", "Transaction", "Menubar", "WorkPanel",
            "BusinessProcessDiagram", "SDT", "Domain", "ExternalObject"
        };

        public void RemoveEntry(string type, string name)
        {
            var index = GetIndex();
            bool removed = false;
            lock (_lock)
            {
                var keysToRemove = index.Objects
                    .Where(pair =>
                        string.Equals(pair.Value.Type, type, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var pair in keysToRemove)
                {
                    if (index.Objects.TryRemove(pair.Key, out var removedEntry))
                    {
                        removed = true;
                        // Surgical removal (mirrors RemoveEntryByGuid) — the previous
                        // implementation rebuilt the ENTIRE parent index via UpdateIndex
                        // per deletion, an O(N) rebuild for an O(1) operation.
                        if (index.ChildrenByParent != null && removedEntry != null)
                            RemoveEntryFromParentIndex(index.ChildrenByParent, removedEntry);
                        // PERFORMANCE (W-M5): invalidate hierarchy cache for removed objects.
                        if (!string.IsNullOrEmpty(removedEntry?.Guid) && Guid.TryParse(removedEntry.Guid, out var g))
                        {
                            _hierarchyCache.TryRemove(g, out _);
                        }
                        // Fase 2: keep the Guid→key map in sync.
                        if (index.GuidToKey != null && !string.IsNullOrEmpty(removedEntry?.Guid))
                            index.GuidToKey.TryRemove(removedEntry.Guid, out _);
                    }
                }
            }

            if (removed)
            {
                MarkDirty();
                ScheduleThrottledFlush();
            }
        }

        private void PrimeHierarchyCacheFromIndex(SearchIndex index)
        {
            if (index?.Objects == null) return;
            foreach (var entry in index.Objects.Values)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Guid)) continue;
                if (!Guid.TryParse(entry.Guid, out var g)) continue;
                _hierarchyCache[g] = (entry.Parent ?? string.Empty, entry.ParentPath ?? string.Empty, entry.Path ?? string.Empty, entry.Module);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _index = null;
                _hierarchyCache.Clear(); // PERFORMANCE (W-M5): drop stale hierarchy on KB unload.
            }
        }

        // SP6.T6 — fast-index lite pass uses this to bulk-replace the in-memory index with
        // stub entries (no SourceSnippet/Calls/CalledBy/Embedding). Keys follow the same
        // GetEntryStorageKey scheme so subsequent UpdateEntry calls AddOrUpdate cleanly.
        // Anti-demotion guard: an already-enriched live entry (concurrent UpdateEntry /
        // on-demand PromoteAsync during the walk) is NEVER overwritten by an incoming
        // lite stub — the enriched entry is carried over into the new index.
        public void ReplaceAll(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            lock (_lock)
            {
                var previous = _index;
                var idx = new SearchIndex();
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                        string key = GetEntryStorageKey(e);
                        if (!e.IsEnriched && previous != null
                            && previous.Objects.TryGetValue(key, out var existing)
                            && existing != null && existing.IsEnriched)
                        {
                            idx.Objects[key] = existing; // keep the enriched live entry
                            continue;
                        }
                        idx.Objects[key] = e;
                    }
                }
                idx.LastUpdated = DateTime.UtcNow;
                BuildParentIndex(idx);
                _index = idx;
                _initialized = true;
                PrimeHierarchyCacheFromIndex(idx);
            }
            MarkDirty();
        }

        // Streaming companion to ReplaceAll: incrementally AddOrUpdate the given batch
        // into the LIVE index instead of rebuilding a brand-new SearchIndex from the
        // full accumulated list (the lite walk used to do that every 500 objects —
        // O(N²) on a 38.6k KB and it clobbered concurrent UpdateEntry promotions by
        // demoting just-enriched entries back to stubs). Enriched entries are never
        // overwritten by stubs here either.
        public void AddOrUpdateBatch(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            if (entries == null) return;
            SearchIndex idx;
            lock (_lock)
            {
                if (_index == null) _index = new SearchIndex();
                idx = _index;
                if (idx.ChildrenByParent == null || idx.GuidToKey == null) BuildParentIndex(idx);
                _initialized = true;
            }

            bool any = false;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                string key = GetEntryStorageKey(e);
                if (!e.IsEnriched && idx.Objects.TryGetValue(key, out var existing)
                    && existing != null && existing.IsEnriched)
                {
                    continue; // never demote an enriched entry to a stub
                }
                idx.Objects[key] = e;
                if (idx.ChildrenByParent != null) AddOrUpdateEntryInParentIndex(idx.ChildrenByParent, e);
                if (idx.GuidToKey != null && !string.IsNullOrEmpty(e.Guid)) idx.GuidToKey[e.Guid] = key;
                any = true;
            }
            if (any)
            {
                idx.LastUpdated = DateTime.UtcNow;
                MarkDirty();
            }
        }

        // SP6.T6 — wires the EnrichmentQueue produced by KbService so callers (analyze, etc.)
        // can PromoteAsync individual entries on demand instead of waiting for the background
        // drain to reach them.
        private EnrichmentQueue _enrichmentQueue;

        public void SetEnrichmentQueue(EnrichmentQueue queue) { _enrichmentQueue = queue; }
        public EnrichmentQueue GetEnrichmentQueue() { return _enrichmentQueue; }

        // v2.3.8 (post-self-review) — companion to Clear() for force-reindex.
        // Removes the on-disk snapshot files so the next GetIndex() hydration
        // returns null and triggers a full SDK rebuild instead of re-loading
        // the stale gz/plain blob.
        public void DeleteOnDiskSnapshot()
        {
            lock (_lock)
            {
                try { if (!string.IsNullOrEmpty(_indexPathGz) && File.Exists(_indexPathGz)) File.Delete(_indexPathGz); } catch (Exception ex) { Logger.Warn("Delete gz snapshot failed: " + ex.Message); }
                try { if (!string.IsNullOrEmpty(_indexPath) && File.Exists(_indexPath)) File.Delete(_indexPath); } catch (Exception ex) { Logger.Warn("Delete plain snapshot failed: " + ex.Message); }
                // Fase 1: drop the validation sidecar + hwm so a forced rebuild starts clean.
                try { if (!string.IsNullOrEmpty(_metaPath) && File.Exists(_metaPath)) File.Delete(_metaPath); } catch (Exception ex) { Logger.Warn("Delete meta sidecar failed: " + ex.Message); }
                ResetHighWaterMark();
            }
        }
    }
}
