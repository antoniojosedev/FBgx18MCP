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
        // PERFORMANCE (W-A3): mirror path with .gz extension. Derived from _indexPath so
        // the two never drift; new flushes go here gzipped, legacy plain-JSON still readable.
        private string _indexPathGz => _indexPath + ".gz";
        private BuildService _buildService;
        private bool _initialized = false;
        private readonly object _lock = new object();
        private DateTime _lastFlushTime = DateTime.MinValue;
        private bool _savingInProgress = false;
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
                    EtaMs = _state.EtaMs
                };
            }
        }

        public void MarkReindexStarted(int totalEstimated)
        {
            lock (_stateLock)
            {
                _state.Status = "Reindexing";
                _state.Progress = 0;
                _state.TotalObjects = totalEstimated;
            }
        }

        public void MarkReindexProgress(double progress, int etaMs)
        {
            lock (_stateLock)
            {
                _state.Progress = progress;
                _state.EtaMs = etaMs;
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

        public IndexCacheService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public KbService KbService => _buildService?.KbService;

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

        private void EnsureInitialized()
        {
            if (_initialized) return;
            try
            {
                string kbPath = _buildService.GetKBPath();
                if (!string.IsNullOrEmpty(kbPath)) Initialize(kbPath);
            }
            catch { }
        }

        public void Initialize(string kbPath)
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
                Logger.Info(string.Format("IndexCache initialized: {0}", _indexPath));

                // PERFORMANCE: Pro-active loading in background
                Task.Run(() => GetIndex());
            }
            catch (Exception ex) { Logger.Error("IndexCache Init Error: " + ex.Message); }
        }

        private string GetHash(string input)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input.ToLower().Trim()));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 16);
            }
        }

        private void BuildParentIndex(SearchIndex index)
        {
            var byParent = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var entry in index.Objects.Values)
            {
                string parent = entry.ParentPath ?? entry.Parent ?? "";
                if (!byParent.TryGetValue(parent, out var list))
                {
                    list = new System.Collections.Generic.List<SearchIndex.IndexEntry>();
                    byParent[parent] = list;
                }
                
                // PERFORMANCE: Since we are iterating dictionary values, there are NO duplicates. 
                // Using .Add() directly changes this from an O(N^2) operation to O(N).
                list.Add(entry);
            }
            index.ChildrenByParent = byParent;
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
                        Logger.Info(string.Format("Index loaded. Objects: {0}", _index.Objects.Count));
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
            // Fire and forget save to disk with throttling (W-A3: 10s → 30s)
            if (!_savingInProgress && (DateTime.Now - _lastFlushTime).TotalSeconds > FlushThrottleSeconds)
            {
                Task.Run(() => FlushToDisk());
            }
        }

        private void FlushToDisk()
        {
            if (_savingInProgress) return;
            
            SearchIndex snapshot = null;
            lock (_lock)
            {
                if (_savingInProgress) return;
                if (_index == null) return;
                
                _savingInProgress = true;
                // ELITE: Snapshot the index to serialize it OUTSIDE the lock.
                // ConcurrentDictionary iteration is thread-safe and provides a consistent snapshot view.
                snapshot = _index; 
            }

            try
            {
                string dir = Path.GetDirectoryName(_indexPathGz);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var settings = new Newtonsoft.Json.JsonSerializerSettings {
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
                    Formatting = Newtonsoft.Json.Formatting.None
                };

                // Perform heavy serialization OUTSIDE the lock
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, settings);

                // PERFORMANCE (W-A3): write gzipped via a temp file + atomic move so partial
                // writes never leave a corrupt snapshot on disk.
                string tmpPath = _indexPathGz + ".tmp";
                // CompressionLevel.Fastest: file isn't transmitted, the ~5% ratio improvement
                // from Optimal isn't worth the extra CPU on every flush.
                using (var fs = File.Create(tmpPath))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                }
                if (File.Exists(_indexPathGz)) File.Delete(_indexPathGz);
                File.Move(tmpPath, _indexPathGz);

                // Clean up the legacy plain-JSON file once we have a valid gzipped snapshot.
                try { if (File.Exists(_indexPath)) File.Delete(_indexPath); } catch { }

                long sizeKb = new FileInfo(_indexPathGz).Length / 1024;
                Logger.Info($"[INDEX-SAVE] Index flushed (gz): {sizeKb} KB on disk, {json.Length / 1024} KB raw.");
            }
            catch (Exception ex) { Logger.Error("Flush Error: " + ex.Message); }
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
                Path = hierarchy.Path,
                Module = hierarchy.ModuleName
            };

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

            string key = GetEntryStorageKey(entry);
            
            // Compute Embedding
            string semanticText = $"{entry.Name} {entry.Type} {entry.Description} {entry.RootTable} {entry.ParmRule}";
            entry.Embedding = _vectorService.ComputeEmbedding(semanticText);

            // Atomic update using ConcurrentDictionary
            index.Objects.AddOrUpdate(key, entry, (k, existing) => entry);
            if (index.ChildrenByParent != null)
            {
                AddOrUpdateEntryInParentIndex(index.ChildrenByParent, entry);
            }

            // ENRICHMENT: Dependencies (Calls and Tables) - ASYNCHRONOUS BACKGROUND
            Guid objGuid = obj.Guid;
            Program.EnqueueBackground(() => {
                try {
                    var kb = _buildService.KbService?.GetKB();
                    if (kb == null) return;
                    var bgObj = kb.DesignModel.Objects.Get(objGuid);
                    if (bgObj == null) return;

                    var references = bgObj.GetReferences();
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
                                    targetName = keyStr.Contains(":") ? keyStr.Split(':')[1] : keyStr;
                                }
                                if (string.IsNullOrEmpty(targetName)) continue;

                                string targetType = targetKey.TypeDescriptor.Name;
                                if (targetType == "Attribute" || targetType == "Table") {
                                    if (!entry.Tables.Contains(targetName)) { entry.Tables.Add(targetName); changed = true; }
                                } else {
                                    if (!entry.Calls.Contains(targetName)) { entry.Calls.Add(targetName); changed = true; }
                                }

                                // Phase 1: Inverted Index (CalledBy)
                                string targetIndexKey = $"{targetType}:{targetName}";
                                if (index.Objects.TryGetValue(targetIndexKey, out var targetEntry)) {
                                    lock (targetEntry.CalledBy) {
                                        if (!targetEntry.CalledBy.Contains(entry.Name)) {
                                            targetEntry.CalledBy.Add(entry.Name);
                                            changed = true;
                                        }
                                    }
                                }
                            } catch { }
                        }
                    }

                    // FR#3 (friction-report 2026-05-14): GetReferences misses call sites in
                    // Event Start blocks on some KB shapes (the impact analysis returned
                    // totalAffected=0 with literal callers in the source). Augment with a
                    // textual scan over Source/Events/Rules: any identifier that already
                    // exists as a non-Attribute object in the index is treated as a call.
                    // The hard "must exist in index" filter eliminates false positives from
                    // keywords / language built-ins.
                    try
                    {
                        bool textualChanged = EnrichCallsFromTextualScan(bgObj, entry, index);
                        if (textualChanged) changed = true;
                    }
                    catch (Exception scanEx) { Logger.Debug("[FR#3] Textual call scan failed: " + scanEx.Message); }

                    if (changed) Task.Run(() => FlushToDisk());
                } catch (Exception ex) { Logger.Error("Background Indexing Error: " + ex.Message); }
            });
            
            // Fire and forget save to disk
            Task.Run(() => FlushToDisk());
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

                    if (!entry.Calls.Contains(target.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.Calls.Add(target.Name);
                        changed = true;
                    }
                    if (target.CalledBy != null)
                    {
                        lock (target.CalledBy)
                        {
                            if (!target.CalledBy.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                target.CalledBy.Add(entry.Name);
                                changed = true;
                            }
                        }
                    }
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
            lock (_lock)
            {
                var keysToRemove = index.Objects
                    .Where(pair =>
                        string.Equals(pair.Value.Type, type, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var removed = false;
                foreach (var pair in keysToRemove)
                {
                    if (index.Objects.TryRemove(pair.Key, out var removedEntry))
                    {
                        removed = true;
                        // PERFORMANCE (W-M5): invalidate hierarchy cache for removed objects.
                        if (!string.IsNullOrEmpty(removedEntry?.Guid) && Guid.TryParse(removedEntry.Guid, out var g))
                        {
                            _hierarchyCache.TryRemove(g, out _);
                        }
                    }
                }

                if (removed) UpdateIndex(index);
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
    }
}
