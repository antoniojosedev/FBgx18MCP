using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class CommandDispatcher
    {
        private static CommandDispatcher _instance;
        private static readonly object _lock = new object();

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;
        private readonly BuildService _buildService;
        private readonly WriteService _writeService;
        private readonly UIService _uiService;
        private readonly AnalyzeService _analyzeService;
        private readonly RefactorService _refactorService;
        private readonly BatchService _batchService;
        private readonly ForgeService _forgeService;
        private readonly ValidationService _validationService;
        private readonly TestService _testService;
        private readonly SearchService _searchService;
        private readonly SourceSearchService _sourceSearchService;
        private readonly WikiService _wikiService;
        private readonly HistoryService _historyService;
        private readonly VisualizerService _visualizerService;
        private readonly HealthService _healthService;
        private readonly NavigationService _navigationService;
        private readonly NavigationSqlService _navigationSqlService;
        private readonly LinterService _linterService;
        private readonly PatternService _patternService;
        private readonly PatchService _patchService;
        private readonly SDTService _sdtService;
        private readonly StructureService _structureService;
        private readonly FormatService _formatService;
        private readonly PropertyService _propertyService;
        private readonly AssetService _assetService;
        private readonly VersionControlService _versionControlService;
        private readonly ConversionService _conversionService;
        private readonly SelfTestService _selfTestService;
        private readonly PatternAnalysisService _patternAnalysisService;
        private readonly DataInsightService _dataInsightService;
        private readonly SummarizeService _summarizeService;
        private readonly InjectionService _injectionService;
        private readonly ListService _listService;
        private readonly LayoutService _layoutService;
        private readonly KbValidationService _kbValidationService;
        private readonly ValidatePayloadService _validatePayloadService;
        private readonly ExportObjectService _exportObjectService;
        private readonly DiffService _diffService;
        private readonly ApplyTemplateService _applyTemplateService;

        private CommandDispatcher()
        {
            // Phase 1: Creation
            _indexCacheService = new IndexCacheService();
            _buildService = new BuildService();
            _kbService = new KbService(_indexCacheService);
            _visualizerService = new VisualizerService();
            _healthService = new HealthService();
            _formatService = new FormatService();
            _objectService = new ObjectService(_kbService, _buildService);
            _assetService = new AssetService(_buildService);
            _navigationService = new NavigationService(_kbService);
            _navigationSqlService = new NavigationSqlService(_navigationService);
            _listService = new ListService(_kbService, _indexCacheService);
            _uiService = new UIService(_kbService, _objectService);
            var callerGraphService = new CallerGraphService(_indexCacheService, _objectService);
            _analyzeService = new AnalyzeService(_kbService, _objectService, _indexCacheService, _navigationService, _uiService, callerGraphService);
            _buildService.SetCallerGraphService(callerGraphService);
            _summarizeService = new SummarizeService(_kbService, _objectService);
            _injectionService = new InjectionService(_kbService, _objectService, _analyzeService);
            _patternAnalysisService = new PatternAnalysisService(_objectService);
            _layoutService = new LayoutService(_objectService);
            _validationService = new ValidationService(_kbService);
            _searchService = new SearchService(_indexCacheService, _objectService);
            _sourceSearchService = new SourceSearchService(_indexCacheService, _objectService);
            _versionControlService = new VersionControlService(_kbService);
            _dataInsightService = new DataInsightService(_kbService, _objectService, _navigationService, _patternAnalysisService);
            _writeService = new WriteService(_objectService);
            _refactorService = new RefactorService(_kbService, _objectService, _indexCacheService, _writeService, _patternAnalysisService);
            _patchService = new PatchService(_objectService, _writeService);
            _batchService = new BatchService(_kbService, _writeService, _patchService, _objectService);
            _forgeService = new ForgeService(_kbService);
            _testService = new TestService(_kbService, _buildService);
            _wikiService = new WikiService(_objectService, _searchService);
            _historyService = new HistoryService(_objectService, _writeService);
            _linterService = new LinterService(_objectService, _navigationService);
            _patternService = new PatternService(_indexCacheService, _objectService);
            _sdtService = new SDTService(_objectService);
            _structureService = new StructureService(_objectService);
            _propertyService = new PropertyService(_objectService);
            _conversionService = new ConversionService(_objectService);
            _selfTestService = new SelfTestService(_kbService, _searchService, _linterService);
            _kbValidationService = new KbValidationService(_indexCacheService, _objectService, _patternAnalysisService);
            _validatePayloadService = new ValidatePayloadService(_objectService);
            _exportObjectService = new ExportObjectService(_objectService);
            _diffService = new DiffService(_objectService);
            _applyTemplateService = new ApplyTemplateService(_writeService);

            // Phase 2: Late Linking
            _kbService.SetBuildService(_buildService);
            _buildService.SetKbService(_kbService);
            _buildService.SetIndexCacheService(_indexCacheService);
            _indexCacheService.SetBuildService(_buildService);
            _validationService.SetObjectService(_objectService);
            _writeService.SetValidationService(_validationService);
            _objectService.SetWriteService(_writeService);
            _objectService.SetDataInsightService(_dataInsightService);
            _objectService.SetUIService(_uiService);
            _objectService.SetPatternAnalysisService(_patternAnalysisService);
            _linterService.SetWriteService(_writeService);
        }

        public static CommandDispatcher Instance
        {
            get { lock (_lock) { return _instance ?? (_instance = new CommandDispatcher()); } }
        }

        public KbService GetKbService() { return _kbService; }

        public bool IsThreadSafe(string line)
        {
            try
            {
                var request = JObject.Parse(line);
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();

                if (string.IsNullOrEmpty(method)) return false;
                method = method.ToLower();

                // Only allow strictly non-SDK or pure read-cache operations to bypass STA thread
                if (method == "ping" || method == "search" || method == "health")
                    return true;

                if (method == "list")
                    return true;

                // v2.3.8 (post-Task 7.2 fix): Control:Cancel must run on the parallel
                // dispatch path so it can interleave with an in-flight SDK call and
                // trip the registered CTS. Pure in-memory dictionary lookup, no SDK.
                if (method == "control" && action == "Cancel")
                    return true;

                // GetIndexStatus only reads static volatile fields – no SDK access
                if (method == "kb" && action == "GetIndexStatus")
                    return true;
                // v2.3.8 Task 1.2: GetIndexState reads in-memory IndexCacheService snapshot only – no SDK access
                if (method == "kb" && action == "GetIndexState")
                    return true;

                
                // Any operation interacting with GeneXus SDK (COM objects) MUST run in the STA thread to prevent corruption
                return false;
            }
            catch { return false; }
        }

        public string Dispatch(string line)
        {
            try
            {
                var request = JObject.Parse(line);
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();
                string target = request["target"]?.ToString();
                var payload = request["payload"]?.ToString();
                var args = request["params"] as JObject;

                Logger.Info(string.Format("[DISPATCHER] Method: {0}, Action: {1}, Target: {2}", method, action, target));

                switch (method?.ToLower())
                {
                    case "ping": return "{\"status\":\"pong\"}";
                    case "control":
                        // v2.3.8 (post-Task 7.2 fix): IPC cancellation side-channel.
                        // Gateway sends this on the thread-safe path so it can interleave
                        // with a sibling SDK call. Signals the registered CTS keyed by
                        // cancelToken (typically the BackgroundJobRegistry job_id).
                        if (string.Equals(action, "Cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            string ct = args?["cancelToken"]?.ToString() ?? target;
                            bool ok = GxMcp.Worker.Helpers.WorkerCancellationRegistry.Cancel(ct);
                            return JsonConvert.SerializeObject(new
                            {
                                status = ok ? "Cancelled" : "NotFound",
                                cancelToken = ct,
                                message = ok
                                    ? "Cancellation signalled to in-flight command. The handler may still take a few iterations to terminate."
                                    : "No active command with that cancelToken (already finished or never started)."
                            });
                        }
                        break;
                    case "kb":
                        if (action == "Open")
                        {
                            string result = _kbService.OpenKB(target);
                            try
                            {
                                var openResult = JObject.Parse(result);
                                if (string.Equals(openResult["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                                {
                                    Environment.SetEnvironmentVariable("GX_KB_PATH", target);
                                }
                            }
                            catch
                            {
                            }

                            return result;
                        }
                        if (action == "BulkIndex") return _kbService.BulkIndex();
                        if (action == "SelfTest") return _selfTestService.RunAllTests();
                        if (action == "GetIndexStatus") return _kbService.GetIndexStatus();
                        if (action == "GetIndexState")
                        {
                            // v2.3.8 Task 1.2: surface unified IndexState from IndexCacheService.
                            // Gateway uses this to populate the `index` block in whoami.
                            var st = _indexCacheService.GetState();
                            var j = new JObject
                            {
                                ["status"] = st.Status ?? "Cold",
                                ["totalObjects"] = st.TotalObjects,
                                ["lastIndexedAt"] = st.LastIndexedAt.HasValue
                                    ? (JToken)st.LastIndexedAt.Value.ToUniversalTime().ToString("o")
                                    : JValue.CreateNull(),
                                ["progress"] = st.Progress.HasValue ? (JToken)st.Progress.Value : JValue.CreateNull(),
                                ["etaMs"] = st.EtaMs.HasValue ? (JToken)st.EtaMs.Value : JValue.CreateNull()
                            };
                            return j.ToString();
                        }
                        if (action == "ValidateConditions") return _kbValidationService.ValidateConditions(args?["limit"]?.ToObject<int?>() ?? 0);
                        if (action == "ListPatternSnapshots") return _kbValidationService.ListPatternSnapshots(target);
                        if (action == "RestorePatternSnapshot") return _kbValidationService.RestorePatternSnapshot(target, args?["snapshotPath"]?.ToString(), _writeService);
                        break;
                    case "batch":
                        if (action == "BatchRead") return _batchService.BatchRead(args?["items"] as JArray);
                        if (action == "BatchEdit") return _batchService.BatchEdit(target, args?["changes"] as JArray);
                        if (action == "MultiEdit") return _batchService.MultiEdit(args?["items"] as JArray);
                        if (action == "Process") return _batchService.ProcessBatch(args?["batchAction"]?.ToString(), target, payload);
                        break;
                    case "search":
                        if (action == "Query")
                        {
                            string searchResult = _searchService.Search(
                                target,
                                args?["typeFilter"]?.ToString(),
                                args?["domainFilter"]?.ToString(),
                                args?["limit"]?.ToObject<int?>() ?? 50,
                                args?["exactMatch"]?.ToObject<bool?>() ?? false
                            );
                            int inlineTopSearch = Math.Min(3, args?["inline_read_top"]?.ToObject<int?>() ?? 0);
                            return inlineTopSearch > 0
                                ? AppendInlineReads(searchResult, inlineTopSearch)
                                : searchResult;
                        }
                        if (action == "SearchSource")
                        {
                            var criteria = new SourceSearchCriteria
                            {
                                Callee = args?["callee"]?.ToString(),
                                Pattern = args?["pattern"]?.ToString(),
                                TypeFilter = args?["typeFilter"]?.ToString(),
                                CaseSensitive = args?["caseSensitive"]?.ToObject<bool?>() ?? false,
                                IncludeComments = args?["includeComments"]?.ToObject<bool?>() ?? false,
                                MaxResults = args?["maxResults"]?.ToObject<int?>() ?? 50
                            };
                            if (args?["scope"] is JArray scopeArr)
                                criteria.Scope = scopeArr.Select(t => t.ToString()).ToList();
                            if (args?["argMatches"] is JObject am)
                            {
                                criteria.ArgMatches = new Dictionary<int, string>();
                                foreach (var prop in am.Properties())
                                {
                                    if (int.TryParse(prop.Name, out int idx))
                                        criteria.ArgMatches[idx] = prop.Value?.ToString();
                                }
                            }
                            // v2.3.8 (post-Task 7.2 fix): register cancellation if the caller
                            // supplied a cancelToken so a sibling Control:Cancel can stop the
                            // scan mid-loop. Token typically matches the gateway's job_id.
                            string ssCancelToken = args?["cancelToken"]?.ToString();
                            string sourceSearchResult;
                            using (GxMcp.Worker.Helpers.WorkerCancellationRegistry.Register(ssCancelToken, out var ssCt))
                            {
                                sourceSearchResult = _sourceSearchService.SearchAsJson(criteria, ssCt);
                            }
                            int inlineTopSearchSource = Math.Min(3, args?["inline_read_top"]?.ToObject<int?>() ?? 0);
                            return inlineTopSearchSource > 0
                                ? AppendInlineReadsForSourceSearch(sourceSearchResult, inlineTopSearchSource)
                                : sourceSearchResult;
                        }
                        break;
                    case "list":
                        if (action == "Objects")
                        {
                            string listResult = _listService.ListObjects(
                                target,
                                args?["limit"]?.ToObject<int?>() ?? 5000,
                                args?["offset"]?.ToObject<int?>() ?? 0,
                                args?["parent"]?.ToString(),
                                args?["typeFilter"]?.ToString(),
                                args?["parentPath"]?.ToString(),
                                args?["verbose"]?.ToObject<bool?>() ?? false,
                                args?["nameFilter"]?.ToString(),
                                args?["descriptionFilter"]?.ToString(),
                                args?["pathPrefix"]?.ToString()
                            );
                            int inlineTopList = Math.Min(3, args?["inline_read_top"]?.ToObject<int?>() ?? 0);
                            return inlineTopList > 0
                                ? AppendInlineReads(listResult, inlineTopList)
                                : listResult;
                        }
                        break;
                    case "read":
                        if (action == "ExtractSource") return _objectService.ReadObjectSource(target, args?["part"]?.ToString(), args?["offset"]?.ToObject<int?>(), args?["limit"]?.ToObject<int?>(), "mcp", false, args?["type"]?.ToString());
                        if (action == "ExtractParts")
                        {
                            var partsTok = args?["parts"] as JArray;
                            var requestedParts = partsTok?.Select(p => p.ToString()) ?? Enumerable.Empty<string>();
                            return _objectService.ReadObjectSourceParts(target, requestedParts, args?["type"]?.ToString());
                        }
                        if (action == "GetVariables") return _analyzeService.GetVariables(target);
                        if (action == "GetAttribute") return _analyzeService.GetAttributeMetadata(target);
                        break;
                    case "object":
                        if (action == "Read") return _objectService.ReadObject(target, args?["type"]?.ToString());
                        if (action == "Create") return _objectService.CreateObject(args?["type"]?.ToString(), target);
                        if (action == "Delete") return _objectService.DeleteObject(target, args?["type"]?.ToString(), args?["confirm"]?.ToObject<bool?>() ?? false);
                        if (action == "WorkerReload") return _objectService.WorkerReload(args?["sourceDir"]?.ToString());
                        if (action == "ReadLogs") return _objectService.ReadLogs(
                            args?["lines"]?.ToObject<int?>() ?? 50,
                            args?["filterCorrelation"]?.ToString(),
                            args?["grep"]?.ToString());
                        if (action == "ExportText")
                        {
                            return _objectService.ExportObjectToText(
                                target,
                                args?["outputPath"]?.ToString() ?? args?["path"]?.ToString(),
                                args?["part"]?.ToString(),
                                args?["type"]?.ToString(),
                                args?["overwrite"]?.ToObject<bool?>() ?? false);
                        }
                        if (action == "ImportText") return _objectService.ImportObjectFromText(target, args?["inputPath"]?.ToString() ?? args?["path"]?.ToString(), args?["part"]?.ToString(), args?["type"]?.ToString());
                        break;
                    case "write":
                        if (action == "AddVariable")
                        {
                            return _writeService.AddVariable(
                                target,
                                args?["varName"]?.ToString(),
                                args?["typeName"]?.ToString());
                        }
                        if (action == "DeleteVariable")
                        {
                            return _writeService.DeleteVariable(
                                target,
                                args?["varName"]?.ToString());
                        }
                        if (action == "ModifyVariable")
                        {
                            return _writeService.ModifyVariable(
                                target,
                                args?["varName"]?.ToString(),
                                args?["typeName"]?.ToString(),
                                args?["basedOn"]?.ToString());
                        }
                        if (action == "ValidatePayload")
                        {
                            return _validatePayloadService.Validate(
                                target,
                                args?["part"]?.ToString(),
                                payload);
                        }
                        if (action == "Bulk")
                        {
                            return _writeService.BulkWrite(args ?? request);
                        }
                        if (action == "ApplyTemplate")
                        {
                            return _applyTemplateService.Apply(
                                args?["template"]?.ToString(),
                                target,
                                args,
                                args?["dryRun"]?.ToObject<bool?>() ?? false);
                        }
                        if (action == "ListTemplates")
                        {
                            return _applyTemplateService.ListTemplates();
                        }
                        return _writeService.WriteObject(
                            target,
                            action,
                            payload,
                            args?["type"]?.ToString(),
                            true,
                            false,
                            true,
                            args?["dryRun"]?.ToObject<bool?>() ?? false);
                    case "semanticops":
                        if (action == "Apply") return _writeService.ApplySemanticOps(args ?? request);
                        break;
                    case "jsonpatch":
                        if (action == "Apply") return _writeService.ApplyJsonPatch(args ?? request);
                        break;
                    case "patch":
                        if (action == "Apply") return _patchService.ApplyPatch(
                            target,
                            args?["part"]?.ToString(),
                            args?["operation"]?.ToString(),
                            payload,
                            args?["context"]?.ToString(),
                            args?["expectedCount"]?.ToObject<int?>() ?? 1,
                            args?["type"]?.ToString(),
                            args?["dryRun"]?.ToObject<bool?>() ?? false,
                            args?["verifyRollback"]?.ToObject<bool?>() ?? false,
                            args?["return_post_state"]?.ToObject<bool?>() ?? true,
                            args?["verbose"]?.ToObject<bool?>() ?? false);
                        break;
                    case "analyze":
                        var analyzeType = args?["type"]?.ToString();
                        if (action == "GetNavigation") return _navigationService.GetNavigation(target);
                        if (action == "GetSqlForNavigation")
                        {
                            int? levelNumber = args?["levelNumber"]?.ToObject<int?>();
                            return _navigationSqlService.Generate(target, levelNumber);
                        }
                        if (action == "GetParameters") return _analyzeService.GetSignature(target, analyzeType);
                        if (action == "GetHierarchy") return _analyzeService.GetHierarchy(target, analyzeType);
                        if (action == "GetDataContext") return _dataInsightService.GetDataContext(target);
                        if (action == "GetConversionContext") return _analyzeService.GetConversionContext(target, args?["include"] as JArray, analyzeType);
                        if (action == "GetPatternMetadata") return _patternAnalysisService.GetWWPStructure(target);
                        if (action == "Summarize") return _summarizeService.Summarize(target, analyzeType);
                        if (action == "GetSQL")
                        {
                            bool includeSub = args?["includeSubordinated"]?.ToObject<bool?>() ?? false;
                            return _dataInsightService.GetTableDDL(target, includeSub);
                        }
                        if (action == "ExplainCode") return _analyzeService.ExplainCode(target, payload);
                        if (action == "ImpactAnalysis")
                        {
                            // v2.3.8 (Task 1.4): index-aware impact with optional wait-for-index.
                            // v2.3.8 (post-Task 7.2): honour cancelToken via WorkerCancellationRegistry
                            // so a parallel Control:Cancel stops the BFS mid-walk.
                            bool waitForIndex = args?["waitForIndex"]?.ToObject<bool?>() ?? true;
                            int waitTimeoutMs = args?["waitTimeoutMs"]?.ToObject<int?>() ?? 30000;
                            string ctToken = args?["cancelToken"]?.ToString();
                            using (GxMcp.Worker.Helpers.WorkerCancellationRegistry.Register(ctToken, out var iaCt))
                            {
                                return _analyzeService.ImpactAnalysis(target, waitForIndex, waitTimeoutMs, iaCt);
                            }
                        }
                        if (action == "InjectContext")
                        {
                            bool recursive = args?["recursive"]?.ToObject<bool>() ?? false;
                            return _injectionService.InjectContext(target, recursive, analyzeType);
                        }
                        return _analyzeService.Analyze(target, analyzeType);
                    case "linter":
                        bool linterFix = args?["fix"]?.ToObject<bool?>() ?? false;
                        if (linterFix) return _linterService.LintAndFix(target);
                        return _linterService.Lint(target);
                    case "diff":
                        return _diffService.Diff(
                            args?["mode"]?.ToString() ?? action,
                            target,
                            args?["part"]?.ToString(),
                            args?["left"]?.ToString(),
                            args?["right"]?.ToString(),
                            args?["context"]?.ToObject<int?>() ?? 3);
                    case "export":
                        if (action == "Unified" || action == "Full")
                            return _exportObjectService.Export(target, args?["type"]?.ToString());
                        break;
                    case "forge":
                        if (action == "Scaffold")
                        {
                            var properties = new JObject();
                            if (!string.IsNullOrEmpty(args?["description"]?.ToString())) properties["description"] = args["description"]?.ToString();
                            if (!string.IsNullOrEmpty(args?["code"]?.ToString())) properties["code"] = args["code"]?.ToString();

                            string scaffoldType = args?["type"]?.ToString() ?? target;
                            string scaffoldName = args?["name"]?.ToString() ?? payload;
                            return _forgeService.Scaffold(scaffoldType, scaffoldName, properties);
                        }
                        break;
                    case "conversion":
                        if (action == "TranslateTo") return _conversionService.TranslateTo(target, args?["language"]?.ToString());
                        break;
                    case "pattern":
                        if (action == "GetSample") return _patternService.GetSample(target);
                        break;
                    case "ui":
                        if (action == "GetUIContext") return _uiService.GetUIContext(target);
                        break;
                    case "layout":
                        if (action == "GetTree")
                        {
                            return _layoutService.GetTree(
                                target,
                                args?["control"]?.ToString(),
                                args?["limit"]?.ToObject<int?>() ?? 500);
                        }
                        if (action == "FindControls")
                        {
                            return _layoutService.FindControls(
                                target,
                                args?["propertyName"]?.ToString(),
                                args?["query"]?.ToString(),
                                args?["limit"]?.ToObject<int?>() ?? 200);
                        }
                        if (action == "SetProperty")
                        {
                            return _layoutService.SetProperty(
                                target,
                                args?["control"]?.ToString(),
                                args?["propertyName"]?.ToString(),
                                args?["value"]?.ToString());
                        }
                        if (action == "SetProperties")
                        {
                            return _layoutService.SetProperties(
                                target,
                                args?["changes"] as JArray);
                        }
                        if (action == "InspectSurface")
                        {
                            return _layoutService.InspectSurface(target, args?["limit"]?.ToObject<int?>() ?? 50);
                        }
                        if (action == "GetVisualPreview")
                        {
                            return _layoutService.GetVisualPreview(target);
                        }
                        if (action == "ScanMutators")
                        {
                            return _layoutService.ScanMutators(target, args?["limit"]?.ToObject<int?>() ?? 100);
                        }
                        if (action == "RenamePrintBlock")
                        {
                            return _layoutService.RenamePrintBlock(
                                target,
                                args?["currentName"]?.ToString(),
                                args?["newName"]?.ToString());
                        }
                        if (action == "AddPrintBlock")
                        {
                            return _layoutService.AddPrintBlock(
                                target,
                                args?["printBlockName"]?.ToString(),
                                args?["height"]?.ToObject<int?>());
                        }
                        break;
                    case "structure":
                        if (action == "GetVisualStructure") return _structureService.GetVisualStructure(target);
                        if (action == "UpdateVisualStructure") return _structureService.UpdateVisualStructure(target, payload);
                        if (action == "GetVisualIndexes") return _structureService.GetVisualIndexes(target);
                        if (action == "GetLogicStructure") return _structureService.GetLogicStructure(target);
                        break;
                    case "build":
                        if (action == "Status") return _buildService.GetStatus(
                            target,
                            args?["page"]?.ToObject<int?>() ?? 1,
                            args?["pageSize"]?.ToObject<int?>() ?? 50);
                        if (action == "Result") return _buildService.GetResult(
                            target,
                            args?["page"]?.ToObject<int?>() ?? 1,
                            args?["pageSize"]?.ToObject<int?>() ?? 50);
                        if (action == "Cancel") return _buildService.Cancel(target);
                        {
                            // v2.3.8 (Task 5.2): forward includeCallees + buildPlanCap from gateway.
                            var includeCallees = args?["includeCallees"]?.ToString();
                            var cap = args?["buildPlanCap"]?.ToObject<int?>() ?? 200;
                            if (string.IsNullOrWhiteSpace(includeCallees)) includeCallees = "transitive";
                            return _buildService.Build(action, target, includeCallees, cap);
                        }
                    case "validation":
                        return _validationService.ValidateCode(target, action, payload);
                    case "test":
                        return _testService.RunTest(target);
                    case "wiki":
                        return _wikiService.Generate(target);
                    case "visualizer":
                        return _visualizerService.GenerateGraph(payload ?? target);
                    case "health":
                        return _healthService.GetHealthReport();
                    case "history":
                        int verId = args?["versionId"]?.ToObject<int?>() ?? 0;
                        return _historyService.Execute(target, action, verId);
                    case "property":
                        var propType = args?["type"]?.ToString();
                        if (action == "Set")
                        {
                            return _propertyService.SetProperty(
                                target,
                                args?["propertyName"]?.ToString(),
                                args?["value"]?.ToString(),
                                args?["control"]?.ToString(),
                                propType);
                        }
                        return _propertyService.GetProperties(target, args?["control"]?.ToString(), propType);
                    case "asset":
                        if (action == "Find")
                        {
                            return _assetService.Find(
                                args?["pattern"]?.ToString(),
                                args?["relativeRoot"]?.ToString(),
                                args?["limit"]?.ToObject<int?>() ?? 20);
                        }

                        if (action == "Read")
                        {
                            return _assetService.Read(
                                target,
                                args?["includeContent"]?.ToObject<bool?>() ?? false,
                                args?["maxBytes"]?.ToObject<int?>());
                        }

                        if (action == "Write")
                        {
                            return _assetService.Write(
                                target,
                                args?["contentBase64"]?.ToString());
                        }

                        break;
                    case "formatting":
                        if (action == "Format") return _formatService.Format(payload);
                        break;
                    case "refactor":
                        return _refactorService.Refactor(target, action, payload);
                }

                return Models.McpResponse.Error(
                    "Method or Action not found",
                    target,
                    action,
                    string.Format("Unsupported dispatch combination. Method='{0}', Action='{1}'.", method ?? "", action ?? "")
                );
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>
        /// Post-process a list/query JSON response by appending inline_reads for the top N results.
        /// Reads are capped at 3. ResponseSizeGuard remains the ceiling on total response size.
        /// </summary>
        private string AppendInlineReads(string responseJson, int n)
        {
            return AppendInlineReadsCore(responseJson, n,
                (name, type) => _objectService.ReadObjectSourceParts(name, null, type));
        }

        private string AppendInlineReadsForSourceSearch(string responseJson, int n) =>
            AppendInlineReadsCore(responseJson, n,
                (name, type) => _objectService.ReadObjectSourceParts(name, null, type),
                arrayKey: "hits", nameField: "objectName", dedupe: true);

        /// <summary>
        /// Testable core: merges inline_reads into a response JSON given a reader delegate.
        /// arrayKey/nameField/dedupe let search_source (hits/objectName, dedup repeats) share
        /// this with query/list_objects (results/name, no dedup).
        /// </summary>
        public static string AppendInlineReadsCore(string responseJson, int n, Func<string, string, string> reader,
            string arrayKey = "results", string nameField = "name", bool dedupe = false)
        {
            if (string.IsNullOrEmpty(responseJson) || n <= 0) return responseJson;
            try
            {
                var responseObj = JObject.Parse(responseJson);
                var items = responseObj[arrayKey] as JArray;
                if (items == null || items.Count == 0) return responseJson;

                var reads = new JArray();
                var seen = dedupe ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                foreach (var item in items.OfType<JObject>())
                {
                    if (reads.Count >= n) break;
                    string name = item[nameField]?.ToString();
                    string type = item["type"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (seen != null && !seen.Add(name)) continue;
                    try
                    {
                        string content = reader(name, type);
                        reads.Add(new JObject
                        {
                            ["name"] = name,
                            ["type"] = type,
                            ["content"] = JToken.Parse(content)
                        });
                    }
                    catch { /* skip objects that fail to read */ }
                }

                if (reads.Count > 0) responseObj["inline_reads"] = reads;
                return responseObj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return responseJson; }
        }
    }
}

