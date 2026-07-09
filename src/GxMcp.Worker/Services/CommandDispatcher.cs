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
        // Wave-3 items 42 + 92: sample-data generator + translations CSV importer.
        private readonly SampleDataService _sampleDataService;
        private readonly TranslationsService _translationsService;
        private readonly LinterService _linterService;
        private readonly PatternService _patternService;
        private readonly PatternApplyService _patternApplyService;
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
        private readonly DatabaseInfoService _databaseInfoService;
        private readonly SummarizeService _summarizeService;
        private readonly InjectionService _injectionService;
        private readonly ListService _listService;
        private readonly LayoutService _layoutService;
        private readonly KbValidationService _kbValidationService;
        private readonly ValidatePayloadService _validatePayloadService;
        private readonly ExportObjectService _exportObjectService;
        private readonly DiffService _diffService;
        private readonly ApplyTemplateService _applyTemplateService;
        private readonly EditAndBuildOrchestrator _editAndBuildOrchestrator;
        private readonly PreviewService _previewService;
        private readonly PopupTemplateService _popupTemplateService;
        private readonly UndoService _undoService;
        private readonly SecurityAuditService _securityAuditService;
        private readonly OrientService _orientService;
        private readonly DbDriftService _dbDriftService;
        // SOTA tool — static "missing index" advisor for the KB.
        private readonly DbOptimizeService _dbOptimizeService;
        private readonly WebFormEditService _webFormEditService;
        private readonly RunObjectService _runObjectService;
        private readonly ExplainService _explainService;
        private readonly GeneratedDiffService _generatedDiffService;
        private readonly KbReadmeService _kbReadmeService;
        private readonly SaveAsService _saveAsService;
        private readonly OcrScreenshotService _ocrScreenshotService;
        private readonly PrDescriptionService _prDescriptionService;
        private readonly ScreenshotPublishService _screenshotPublishService;
        private readonly FrictionLogService _frictionLogService;
        private readonly WcagCheckService _wcagCheckService;
        // Wave-3 (items 76 / 78 / 84 / 86): friction-report aggregation, SDPanel proxy,
        // multi-agent file lock, what-if typed-change simulator.
        private readonly LearningReportService _learningReportService;
        // genexus_gxserver — read-only sync-state surface.
        private readonly GxServerSyncService _gxServerSyncService;
        // genexus_compare — read-only IComparerService surface ("Compare Objects" parity).
        private readonly CompareService _compareService;
        // genexus_module — GeneXus Module Manager (install/update) over IModuleManagerService.
        private readonly ModuleService _moduleService;
        // genexus_gam — GAM / integrated-security provisioning over IIntegratedSecurityService.
        private readonly GamService _gamService;
        // genexus_merge — WRITE IMergeService surface (2-way/3-way object merge).
        private readonly MergeToolService _mergeToolService;
        private readonly SdPanelService _sdPanelService;
        private readonly MultiAgentLockService _multiAgentLockService;
        private readonly WhatIfService _whatIfService;
        private readonly TutorialService _tutorialService;
        private readonly PlaybookService _playbookService;
        private readonly GithubService _githubService;
        private readonly AiCompleteService _aiCompleteService;
        private readonly TimeTravelService _timeTravelService;
        private readonly VoiceIntentService _voiceIntentService;
        private readonly AutoTestService _autoTestService;
        private readonly ReversePatternService _reversePatternService;
        private readonly CrossBrowserService _crossBrowserService;
        private readonly BlameService _blameService;
        private readonly KbExplorerService _kbExplorerService;
        private readonly NavigationViewService _navigationViewService;
        private readonly KbStartupService _kbStartupService;
        // Wave-3 browser-verify pipeline. Shared invoker → 3 services.
        private readonly IBrowserDriverInvoker _browserDriverInvoker;
        private readonly BrowserCaptureService _browserCaptureService;
        private readonly SmokeTestService _smokeTestService;
        private readonly A11yAuditService _a11yAuditService;
        // Wave-3 items 5 + 37: post-edit visual verification (screenshot + pixel-diff).
        // Lazy because it shells out to chrome-devtools-axi / playwright and is only
        // engaged when callers opt in via `visualVerify=true`.
        private readonly VisualVerifyService _visualVerifyService;
        // Wave-3 item 30: per-target build-plan with per-tool p95-derived estimates.
        private readonly BuildPlanService _buildPlanService;
        // genexus_doctor: one-call health/triage envelope.
        private readonly DoctorService _doctorService;
        // genexus_types: Domain + SDT type introspection + value validation.
        private readonly TypeIntrospectService _typeIntrospectService;
        // genexus_api: REST endpoint introspection + breaking-change diff.
        private readonly ApiIntrospectService _apiIntrospectService;
        // genexus_profile: runtime profiler XML bridge (file-only ingest v1).
        private readonly ProfileService _profileService;

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
            _navigationSqlService = new NavigationSqlService(_navigationService, _kbService, _objectService);
            _sampleDataService = new SampleDataService(_objectService);
            _translationsService = new TranslationsService(null); // writeService linked in Phase 2
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
            _databaseInfoService = new DatabaseInfoService(_kbService);
            _writeService = new WriteService(_objectService);
            _refactorService = new RefactorService(_kbService, _objectService, _indexCacheService, _writeService, _patternAnalysisService);
            _patchService = new PatchService(_objectService, _writeService, _patternAnalysisService);
            _batchService = new BatchService(_kbService, _writeService, _patchService, _objectService);
            _forgeService = new ForgeService(_kbService);
            _testService = new TestService(_kbService, _buildService);
            _wikiService = new WikiService(_objectService, _searchService);
            _historyService = new HistoryService(_objectService, _writeService);
            _linterService = new LinterService(_objectService, _navigationService);
            _patternService = new PatternService(_indexCacheService, _objectService);
            _patternApplyService = new PatternApplyService(_objectService);
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
            _editAndBuildOrchestrator = new EditAndBuildOrchestrator(_writeService, _analyzeService, _buildService);
            _previewService = new PreviewService(_objectService, _buildService);
            _popupTemplateService = new PopupTemplateService(_objectService, _writeService);
            _undoService = new UndoService(_objectService, _writeService, _indexCacheService);
            _securityAuditService = new SecurityAuditService(_kbService);
            _orientService = new OrientService(_kbService);
            _dbDriftService = new DbDriftService(_buildService);
            _dbOptimizeService = new DbOptimizeService(_kbService, _objectService, _indexCacheService);
            _webFormEditService = new WebFormEditService(_objectService, _writeService);
            _runObjectService = new RunObjectService(_objectService, _kbService, _previewService);
            _explainService = new ExplainService(_kbService, _objectService);
            _generatedDiffService = new GeneratedDiffService(_kbService);
            _kbReadmeService = new KbReadmeService(_kbService, _indexCacheService);
            _saveAsService = new SaveAsService(new SdkObjectCloner(_objectService, _writeService, _patternApplyService));
            _ocrScreenshotService = new OcrScreenshotService();
            _prDescriptionService = new PrDescriptionService(_kbService);
            _screenshotPublishService = new ScreenshotPublishService(_kbService);
            _frictionLogService = new FrictionLogService(_kbService);
            _wcagCheckService = new WcagCheckService(_objectService);
            _learningReportService = new LearningReportService(_kbService);
            _gxServerSyncService = new GxServerSyncService(_kbService);
            _compareService = new CompareService(_kbService, _objectService);
            _moduleService = new ModuleService(_kbService, _objectService);
            _gamService = new GamService(_kbService);
            _mergeToolService = new MergeToolService(_kbService, _objectService);
            _sdPanelService = new SdPanelService(_objectService, _writeService);
            _multiAgentLockService = new MultiAgentLockService(_kbService);
            _whatIfService = new WhatIfService(_analyzeService, _objectService);
            _tutorialService = new TutorialService();
            _playbookService = new PlaybookService();
            _githubService = new GithubService(_kbService);
            _aiCompleteService = new AiCompleteService();
            _timeTravelService = new TimeTravelService(_kbService, _objectService);
            _voiceIntentService = new VoiceIntentService();
            _autoTestService = new AutoTestService();
            _reversePatternService = new ReversePatternService(_objectService, _uiService);
            _crossBrowserService = new CrossBrowserService(_runObjectService);
            _blameService = new BlameService(_kbService, _objectService);
            _kbExplorerService = new KbExplorerService(_objectService, _indexCacheService);
            _navigationViewService = new NavigationViewService(_navigationService, _kbService);
            _kbStartupService = new KbStartupService(_kbService, _objectService);
            // Wave-3 browser-verify pipeline wiring.
            _browserDriverInvoker = new DefaultBrowserDriverInvoker();
            _browserCaptureService = new BrowserCaptureService(_objectService, _browserDriverInvoker);
            _smokeTestService = new SmokeTestService(_browserCaptureService);
            _a11yAuditService = new A11yAuditService(_browserDriverInvoker);
            _visualVerifyService = new VisualVerifyService(_kbService, _objectService);
            _buildPlanService = new BuildPlanService(_indexCacheService, _objectService, callerGraphService);
            _doctorService = new DoctorService(_kbService, _indexCacheService, null);
            _apiIntrospectService = new ApiIntrospectService(_kbService, _objectService, _indexCacheService);
            _typeIntrospectService = new TypeIntrospectService(_kbService, _objectService);
            _profileService = new ProfileService();

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
        public IndexCacheService GetIndexCacheService() { return _indexCacheService; }

        // Item 51 (Tier-S, EXPERIMENTAL) — capture IndexCacheService state to disk
        // before a warm reload. Returns a small JObject result the dispatcher
        // stitches into the soft-reload ack. Never throws — failures are surfaced
        // as { saved:false, error:... } so the soft reload still proceeds.
        private Newtonsoft.Json.Linq.JObject TryCaptureWarmSnapshot()
        {
            var result = new Newtonsoft.Json.Linq.JObject();
            try
            {
                string kbPath = _kbService?.GetKbPath();
                if (string.IsNullOrEmpty(kbPath))
                {
                    result["saved"] = false;
                    result["error"] = "no-kb-path";
                    return result;
                }
                string path = WarmIndexSnapshot.DefaultPath(kbPath);
                var index = _indexCacheService?.GetIndex();
                if (index == null)
                {
                    result["saved"] = false;
                    result["error"] = "index-not-initialised";
                    return result;
                }
                int objectCount = index.Objects?.Count ?? 0;
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(index.ToJson());
                WarmIndexSnapshot.Save(path, payload, kbPath, objectCount);
                result["saved"] = true;
                result["path"] = path;
                result["objectCount"] = objectCount;
                result["experimental"] = true;
                return result;
            }
            catch (Exception ex)
            {
                result["saved"] = false;
                result["error"] = ex.Message;
                return result;
            }
        }

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
                if (method == "ping" || method == "health")
                    return true;

                // issue #26 P2: 'search' is index-only (in-memory) EXCEPT SearchSource,
                // which reads object Source/WebForm XML via the GeneXus SDK and MUST run
                // on the STA thread. Routing SearchSource to the MTA thread-pool path
                // caused a native COM access-violation that crashed the worker 100% of the time.
                if (method == "search" && !string.Equals(action, "SearchSource", StringComparison.OrdinalIgnoreCase))
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
            // v2.8.0 — idempotency. When the caller threads a `clientRequestId`
            // through the RPC params, this dispatcher serves a cached response
            // for the same id within a 5-minute TTL. Lets LLM clients retry
            // safely after a socket drop / gateway timeout without double-
            // applying the underlying mutation. Excluded methods (ping, control)
            // skip the cache because they're meta operations.
            string requestId = null;
            string method0 = null;
            try
            {
                var req0 = JObject.Parse(line);
                method0 = req0["method"]?.ToString()?.ToLowerInvariant();
                requestId = req0["params"]?["clientRequestId"]?.ToString()
                    ?? (req0["params"]?["params"] as JObject)?["clientRequestId"]?.ToString();
            }
            catch { /* fall through to normal dispatch */ }

            bool cacheable = !string.IsNullOrEmpty(requestId)
                && method0 != "ping" && method0 != "control";
            if (cacheable)
            {
                // v2.8.0 (#37) — TryServe now waits for any in-flight call
                // with the same id, so a fast LLM retry within the original
                // call's window blocks on the first and gets the same result.
                string replay = GxMcp.Worker.Helpers.IdempotencyCache.TryServe(requestId);
                if (replay != null) return replay;
                // Mark this call as in-flight BEFORE executing so siblings
                // that race in mid-flight block instead of double-applying.
                GxMcp.Worker.Helpers.IdempotencyCache.BeginInflight(requestId);
            }

            string result;
            try
            {
                result = DispatchInternal(line);
            }
            catch
            {
                if (cacheable) GxMcp.Worker.Helpers.IdempotencyCache.AbortInflight(requestId);
                throw;
            }

            if (cacheable && !string.IsNullOrEmpty(result))
            {
                GxMcp.Worker.Helpers.IdempotencyCache.Store(requestId, result);
            }
            else if (cacheable)
            {
                GxMcp.Worker.Helpers.IdempotencyCache.AbortInflight(requestId);
            }
            return result;
        }

        private string DispatchInternal(string line)
        {
            try
            {
                var request = JObject.Parse(line);
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();
                string target = request["target"]?.ToString();
                var payload = request["payload"]?.ToString();
                var args = request["params"] as JObject;
                // OperationsRouter cases that pass-through original tool args via
                // `@params = args` (apply_pattern, apply_template, bulk_edit, diff)
                // produce a doubly-nested params shape on the RPC envelope:
                //   rpc.params = { module, action, target, params: { <original tool args> } }
                // Action handlers below read tool args off `args` directly (e.g.
                // args["pattern"]), so unwrap once when the inner params object exists.
                // Inner keys win on collision; outer routing keys (module/action/target)
                // are preserved as top-level fallback.
                if (args != null && args["params"] is JObject innerArgs)
                {
                    var merged = (JObject)innerArgs.DeepClone();
                    foreach (var prop in args.Properties())
                    {
                        if (prop.Name == "params") continue;
                        if (merged[prop.Name] == null) merged[prop.Name] = prop.Value?.DeepClone();
                    }
                    args = merged;
                }

                string progressToken = request["_meta"] != null
                    ? request["_meta"]["progressToken"]?.ToString()
                    : null;

                Logger.Info(string.Format("[DISPATCHER] Method: {0}, Action: {1}, Target: {2}", method, action, target));

                // v2.6.2 (Item B): blanket-register the cancel token so ANY handler that
                // observes WorkerCancellationRegistry sees a single registration for the whole
                // command. Inner handlers that also Register the same token share the CTS via
                // refcount and only the outermost scope removes the entry. Without this, async
                // builds/edits started by the gateway never registered their job_id and a
                // matching lifecycle action=cancel returned NotFound on the worker side.
                string commandCancelToken = args?["cancelToken"]?.ToString();
                using (GxMcp.Worker.Helpers.WorkerCancellationRegistry.Register(commandCancelToken, out _))
                using (GxMcp.Worker.Helpers.ProgressContext.Use(progressToken))
                {
                switch (method?.ToLower())
                {
                    case "ping": return Models.McpResponse.Ok(code: "Pong", result: new JObject { ["message"] = "pong" });
                    case "control":
                        // v2.3.8 (post-Task 7.2 fix): IPC cancellation side-channel.
                        // Gateway sends this on the thread-safe path so it can interleave
                        // with a sibling SDK call. Signals the registered CTS keyed by
                        // cancelToken (typically the BackgroundJobRegistry job_id).
                        if (string.Equals(action, "Cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            string ct = args?["cancelToken"]?.ToString() ?? target;
                            bool ok = GxMcp.Worker.Helpers.WorkerCancellationRegistry.Cancel(ct);
                            // Item 11: wrap in canonical McpResponse envelope.
                            return ok
                                ? Models.McpResponse.Ok(code: "Cancelled", result: new JObject
                                    {
                                        ["cancelToken"] = ct,
                                        ["message"] = "Cancellation signalled to in-flight command. The handler may still take a few iterations to terminate."
                                    })
                                : Models.McpResponse.Err(code: "NotFound",
                                    message: "No active command with that cancelToken (already finished or never started).",
                                    hint: "The cancelToken may have already expired or the command completed.");
                        }
                        break;
                    case "kb":
                        if (action == "Open")
                        {
                            string result = _kbService.OpenKB(target);
                            try
                            {
                                var openResult = JObject.Parse(result);
                                // v2.8.0 — KbService.OpenKB now emits the canonical envelope
                                // (status:"ok"). Recognize only the canonical shape; legacy
                                // emissions were removed in this release.
                                if (string.Equals(openResult["status"]?.ToString(), "ok", StringComparison.Ordinal))
                                {
                                    Environment.SetEnvironmentVariable("GX_KB_PATH", target);
                                }
                            }
                            catch
                            {
                            }

                            return result;
                        }
                        if (action == "BulkIndex")
                        {
                            bool force = args?["force"]?.ToObject<bool?>() ?? false;
                            bool indexDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                            if (indexDryRun)
                            {
                                string kbPathForDry = null;
                                try { kbPathForDry = _kbService.GetKbPath(); } catch { }
                                return Models.McpResponse.Ok(
                                    code: "DryRun",
                                    result: new JObject
                                    {
                                        ["preview"] = new JObject
                                        {
                                            ["action"] = "index",
                                            ["force"] = force,
                                            ["kbPath"] = kbPathForDry,
                                            ["note"] = "dryRun=true: index rebuild not executed."
                                        }
                                    });
                            }
                            return _kbService.BulkIndex(force);
                        }
                        if (action == "SelfTest") return _selfTestService.RunAllTests();
                        if (action == "GetDatabaseInfo")
                        {
                            return _databaseInfoService.GetInfo();
                        }
                        // v2.6.6 Stream H (FR#26) — surface active-environment metadata
                        // so the gateway can populate KbHandle.ActiveEnvironment on
                        // cache miss. Pure SDK read, no side effects.
                        if (action == "GetActiveEnvironment")
                        {
                            string env = _kbService.GetActiveEnvironment();
                            string ver = _kbService.GetActiveEnvironmentVersion();
                            return new JObject
                            {
                                ["environment"] = env,
                                ["version"] = ver
                            }.ToString(Newtonsoft.Json.Formatting.None);
                        }
                        // v2.6.6 Stream H (FR#25) — F5 launcher resolver. Returns the KB's
                        // configured startup object (or first IsMain-tagged WebPanel/SDPanel/Procedure).
                        if (action == "GetLauncherObject")
                        {
                            string launcherObj = _kbService.GetLauncherObjectName();
                            return new JObject { ["name"] = launcherObj }.ToString(Newtonsoft.Json.Formatting.None);
                        }
                        // Wave-3: IDE "Set As Startup Object" parity. Read goes
                        // through the same SDK shapes as GetLauncherObjectName so
                        // get/set agree on the field name the IDE writes to .gxw.
                        if (action == "GetStartupObject") return _kbStartupService.GetStartup();
                        if (action == "SetStartupObject")
                        {
                            string startupName = target ?? args?["name"]?.ToString();
                            return _kbStartupService.SetStartup(startupName);
                        }
                        if (action == "GetIndexStatus")
                        {
                            // issue #25 #1: event-driven wait. When `wait` is given, block
                            // until the index state transitions away from `since` (or a walk
                            // progress tick lands, or the timeout fires) and return early —
                            // no more polling loops. Runs on the non-SDK parallel path so
                            // blocking here never stalls the STA thread.
                            int waitSec = args?["wait"]?.ToObject<int?>() ?? 0;
                            string since = args?["since"]?.ToString();
                            if (waitSec > 0)
                            {
                                // Issue #27 item 3 (DX): two block modes.
                                //  - since given  → return the moment the state LEAVES `since`
                                //    (event-driven progress poll; legacy behaviour).
                                //  - since absent → block until the index reaches "Ready"
                                //    (or timeout), so an agent can just say "wait until usable"
                                //    without hand-rolling a since-chained poll loop. A Cold+idle
                                //    index simply times out at its current state — the caller
                                //    then knows to trigger an index build.
                                bool waitForReady = string.IsNullOrEmpty(since);
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                long budgetMs = waitSec * 1000L;
                                while (true)
                                {
                                    _indexCacheService.ArmStateSignal();
                                    string cur = _indexCacheService.GetState()?.Status ?? "Cold";
                                    bool done = waitForReady
                                        ? string.Equals(cur, "Ready", StringComparison.OrdinalIgnoreCase)
                                        : !string.Equals(cur, since, StringComparison.OrdinalIgnoreCase);
                                    if (done) break;
                                    long remaining = budgetMs - sw.ElapsedMilliseconds;
                                    if (remaining <= 0) break;
                                    // Cap each wait so a missed signal still re-checks promptly.
                                    _indexCacheService.WaitStateSignal((int)Math.Min(remaining, 2000));
                                }
                            }
                            // Merge the state-machine status so callers have a stable field
                            // to pass back as `since` (the legacy `status` string is descriptive
                            // prose, not a stable enum).
                            var statusJson = Newtonsoft.Json.Linq.JObject.Parse(_kbService.GetIndexStatus());
                            statusJson["indexStatus"] = _indexCacheService.GetState()?.Status ?? "Cold";
                            // Issue #27 item 1: attach the most-recent terminal build outcome so
                            // this plain status call answers "did my last build pass?" without a jobId.
                            var lastBuild = BuildService.GetLatestBuildSummary();
                            if (lastBuild != null) statusJson["lastBuild"] = lastBuild;
                            return statusJson.ToString();
                        }
                        if (action == "GetIndexState")
                        {
                            // v2.3.8 Task 1.2: surface unified IndexState from IndexCacheService.
                            // Gateway uses this to populate the `index` block in whoami.
                            var st = _indexCacheService.GetState();
                            // v2.8.0 — index state shape is the tool's payload (not an envelope).
                            // Renamed top-level "status" to "indexStatus" to avoid colliding with
                            // the canonical envelope's "status" once wrapped in McpResponse.Ok.
                            var j = new JObject
                            {
                                ["indexStatus"] = st.Status ?? "Cold",
                                ["totalObjects"] = st.TotalObjects,
                                ["lastIndexedAt"] = st.LastIndexedAt.HasValue
                                    ? (JToken)st.LastIndexedAt.Value.ToUniversalTime().ToString("o")
                                    : JValue.CreateNull(),
                                ["progress"] = st.Progress.HasValue ? (JToken)st.Progress.Value : JValue.CreateNull(),
                                ["etaMs"] = st.EtaMs.HasValue ? (JToken)st.EtaMs.Value : JValue.CreateNull(),
                                // PERFORMANCE (W-M2): expose flush-failure telemetry so a silently
                                // failing snapshot (disk full / permission) is visible via whoami.
                                ["flushFailuresConsecutive"] = IndexCacheService.ConsecutiveFlushFailures,
                                ["flushLastSuccessUtc"] = IndexCacheService.LastFlushSuccessUtc == DateTime.MinValue
                                    ? JValue.CreateNull()
                                    : (JToken)IndexCacheService.LastFlushSuccessUtc.ToString("o"),
                                ["flushLastError"] = IndexCacheService.LastFlushErrorMessage != null
                                    ? (JToken)IndexCacheService.LastFlushErrorMessage
                                    : JValue.CreateNull()
                            };

                            // v2.6.8: top-5 recently-changed projection. Cheap O(n) scan
                            // over the in-memory index; gateway forwards this into the
                            // `whoami.index.recentlyChanged` block so the agent gets a
                            // "what's hot" hint on the first call.
                            try
                            {
                                var idx = _indexCacheService.GetIndex();
                                if (idx != null && idx.Objects.Count > 0)
                                {
                                    var top = idx.Objects.Values
                                        .Where(e => e.LastUpdate > DateTime.MinValue)
                                        .OrderByDescending(e => e.LastUpdate)
                                        .Take(5)
                                        .ToList();
                                    if (top.Count > 0)
                                    {
                                        var arr = new JArray();
                                        foreach (var e in top)
                                        {
                                            arr.Add(new JObject
                                            {
                                                ["name"] = e.Name,
                                                ["type"] = e.Type,
                                                ["lastUpdate"] = e.LastUpdate.ToUniversalTime().ToString("o"),
                                                ["lastModifiedBy"] = e.LastModifiedBy ?? string.Empty
                                            });
                                        }
                                        j["recentlyChanged"] = arr;
                                    }
                                }
                            }
                            catch (Exception ex) { Logger.Debug("[GetIndexState] recentlyChanged failed: " + ex.Message); }

                            return Models.McpResponse.Ok(code: "IndexState", result: j);
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
                            DateTime sinceArgQ = default(DateTime);
                            DateTime modifiedBeforeArgQ = default(DateTime);
                            string sinceTokQ = args?["since"]?.ToString();
                            string mbTokQ = args?["modifiedBefore"]?.ToString();
                            if (!string.IsNullOrEmpty(sinceTokQ))
                                DateTime.TryParse(sinceTokQ, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out sinceArgQ);
                            if (!string.IsNullOrEmpty(mbTokQ))
                                DateTime.TryParse(mbTokQ, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out modifiedBeforeArgQ);

                            string searchResult = _searchService.Search(
                                target,
                                args?["typeFilter"]?.ToString(),
                                args?["domainFilter"]?.ToString(),
                                args?["limit"]?.ToObject<int?>() ?? 50,
                                args?["exactMatch"]?.ToObject<bool?>() ?? false,
                                args?["sort"]?.ToString(),
                                sinceArgQ,
                                modifiedBeforeArgQ,
                                args?["cursor"]?.ToString()
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
                                MaxResults = args?["maxResults"]?.ToObject<int?>() ?? 50,
                                // Issue #27 item 4: object scope + tunable timeout + resume cursor.
                                ObjectName = args?["objectName"]?.ToString(),
                                StartIndex = args?["startIndex"]?.ToObject<int?>() ?? 0,
                                TimeoutMs = args?["timeoutMs"]?.ToObject<int?>() ?? 30000
                            };
                            if (args?["scope"] is JArray scopeArr)
                                criteria.Scope = scopeArr.Select(t => t.ToString()).ToList();
                            // Item 22: fields=[source,caption,description,parmNames]
                            if (args?["fields"] is JArray fieldsArr)
                                criteria.Fields = fieldsArr.Select(t => t.ToString()).ToList();
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
                            DateTime sinceArg = default(DateTime);
                            DateTime modifiedBeforeArg = default(DateTime);
                            string sinceTok = args?["since"]?.ToString();
                            string mbTok = args?["modifiedBefore"]?.ToString();
                            if (!string.IsNullOrEmpty(sinceTok))
                                DateTime.TryParse(sinceTok, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out sinceArg);
                            if (!string.IsNullOrEmpty(mbTok))
                                DateTime.TryParse(mbTok, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out modifiedBeforeArg);

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
                                args?["pathPrefix"]?.ToString(),
                                args?["sort"]?.ToString(),
                                sinceArg,
                                modifiedBeforeArg,
                                args?["cursor"]?.ToString()
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
                        if (action == "Create")
                        {
                            // Item 21 (friction 2026-05-22): dryRun=true returns the planned
                            // shape without calling newObj.Save(). args carries the flag.
                            return _objectService.CreateObject(args?["type"]?.ToString(), target, args);
                        }
                        if (action == "Delete") return _objectService.DeleteObject(target, args?["type"]?.ToString(), args?["confirm"]?.ToObject<bool?>() ?? false, args?["dryRun"]?.ToObject<bool?>() ?? false);
                        if (action == "SaveAs") return _saveAsService.SaveAs(args ?? new JObject());
                        if (action == "WorkerReload")
                        {
                            // FR#20 (v2.6.6 Stream B): mode=soft is the new default — drain
                            // in-flight commands and exit code 0 so the gateway respawns
                            // without losing JobRegistry state. mode=hard preserves the
                            // legacy copy-binaries-and-kill flow (still required when the
                            // caller passed a sourceDir of fresh bits to lay down).
                            string mode = args?["mode"]?.ToString()?.Trim().ToLowerInvariant();
                            string srcDir = args?["sourceDir"]?.ToString();
                            if (string.IsNullOrEmpty(mode))
                            {
                                // Negotiate: hard when sourceDir is supplied (copy-binaries
                                // intent), otherwise soft (clean respawn for state reset).
                                mode = string.IsNullOrWhiteSpace(srcDir) ? "soft" : "hard";
                            }
                            if (mode == "soft")
                            {
                                int drainMs = args?["drainTimeoutMs"]?.ToObject<int?>() ?? 30000;
                                return GxMcp.Worker.Program.PerformSoftReload(drainMs);
                            }
                            // Item 51 (Tier-S, EXPERIMENTAL) — warm reload. Persist the
                            // IndexCacheService state to <kb>/.gx/index-snapshot.bin
                            // gated by the worker DLL SHA, then do a soft reload. On
                            // the next boot the gateway respawns this worker and a
                            // future TryRestoreWarmIndex hook can pick it back up.
                            if (mode == "warm")
                            {
                                int drainMs = args?["drainTimeoutMs"]?.ToObject<int?>() ?? 30000;
                                var warmAck = TryCaptureWarmSnapshot();
                                var softResp = GxMcp.Worker.Program.PerformSoftReload(drainMs);
                                // Stitch warm metadata into the soft-reload ack so the
                                // caller sees both outcomes in a single response.
                                try
                                {
                                    var jo = Newtonsoft.Json.Linq.JObject.Parse(softResp);
                                    jo["mode"] = "warm";
                                    jo["warmSnapshot"] = warmAck;
                                    return jo.ToString(Newtonsoft.Json.Formatting.None);
                                }
                                catch { return softResp; }
                            }
                            return _objectService.WorkerReload(srcDir);
                        }
                        if (action == "ReadLogs") return _objectService.ReadLogs(
                            args?["lines"]?.ToObject<int?>() ?? 100,
                            args?["filterCorrelation"]?.ToString(),
                            args?["grep"]?.ToString(),
                            args?["since"]?.ToString(),
                            // Item 32: objectFilter = target param for object-name filtering.
                            args?["objectFilter"]?.ToString());
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
                            bool varDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                            return _writeService.AddVariable(
                                target,
                                args?["varName"]?.ToString(),
                                args?["typeName"]?.ToString(),
                                varDryRun);
                        }
                        if (action == "DeleteVariable")
                        {
                            bool varDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                            return _writeService.DeleteVariable(
                                target,
                                args?["varName"]?.ToString(),
                                varDryRun);
                        }
                        if (action == "ModifyVariable")
                        {
                            bool varDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                            return _writeService.ModifyVariable(
                                target,
                                args?["varName"]?.ToString(),
                                args?["typeName"]?.ToString(),
                                args?["basedOn"]?.ToString(),
                                varDryRun);
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
                        {
                            // Friction 2026-05-26 — validate=only is the LLM-facing
                            // contract for "run the write in-memory, do NOT persist".
                            // Already plumbed for mode=patch and mode=ops; mirror it
                            // here so PatternInstance / WebForm full-XML writes can
                            // probe SDK validation without touching disk.
                            string writeValidate = args?["validate"]?.ToString();
                            bool writeDryRun = (args?["dryRun"]?.ToObject<bool?>() ?? false)
                                || string.Equals(writeValidate, "only", StringComparison.OrdinalIgnoreCase);
                            var writeResp = _writeService.WriteObject(
                                target,
                                action,
                                payload,
                                args?["type"]?.ToString(),
                                true,
                                false,
                                true,
                                writeDryRun);
                            return VisualVerifyResponseHook.MaybeAttach(args, writeResp, _visualVerifyService);
                        }
                    case "editandbuild":
                        if (action == "Orchestrate")
                        {
                            var orchResp = _editAndBuildOrchestrator.Orchestrate(args ?? new JObject());
                            return VisualVerifyResponseHook.MaybeAttach(args, orchResp, _visualVerifyService);
                        }
                        break;
                    case "semanticops":
                        if (action == "Apply")
                        {
                            var soResp = _writeService.ApplySemanticOps(args ?? request);
                            return VisualVerifyResponseHook.MaybeAttach(args ?? request, soResp, _visualVerifyService);
                        }
                        break;
                    case "jsonpatch":
                        if (action == "Apply")
                        {
                            var jpResp = _writeService.ApplyJsonPatch(args ?? request);
                            return VisualVerifyResponseHook.MaybeAttach(args ?? request, jpResp, _visualVerifyService);
                        }
                        break;
                    case "patch":
                        if (action == "Apply")
                        {
                            // v2.6.6 FR#13 follow-up: validate=only is the LLM-facing
                            // contract for "run the patch in-memory, do NOT persist".
                            // Stream A only plumbed validate through ApplySemanticOps
                            // (mode=ops). For mode=patch, validate=only maps to
                            // dryRun=true — the PatchService.ApplyPatch dryRun branch
                            // returns the exact "Applied / write skipped" envelope the
                            // validate=only schema promises. validate=best-effort and
                            // validate=strict (the default) both fall through to the
                            // current strict path: IsPatchWriteSafe already refuses
                            // unsafe writes on NoMatch and surfaces the diagnostic
                            // envelope, so the two are observationally equivalent in
                            // mode=patch and we don't need a third branch.
                            string validateMode = args?["validate"]?.ToString();
                            bool dryRunArg = args?["dryRun"]?.ToObject<bool?>() ?? false;
                            bool validateOnly = string.Equals(validateMode, "only", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(validateMode, "validate-only", StringComparison.OrdinalIgnoreCase);
                            var patchResp = _patchService.ApplyPatch(
                                target,
                                args?["part"]?.ToString(),
                                args?["operation"]?.ToString(),
                                payload,
                                args?["context"]?.ToString(),
                                args?["expectedCount"]?.ToObject<int?>() ?? 1,
                                args?["type"]?.ToString(),
                                dryRunArg || validateOnly,
                                args?["verifyRollback"]?.ToObject<bool?>() ?? false,
                                args?["return_post_state"]?.ToObject<bool?>() ?? true,
                                args?["verbose"]?.ToObject<bool?>() ?? false,
                                args?["replaceAll"]?.ToObject<bool?>() ?? false);
                            return VisualVerifyResponseHook.MaybeAttach(args, patchResp, _visualVerifyService);
                        }
                        break;
                    case "analyze":
                        var analyzeType = args?["type"]?.ToString();
                        if (action == "GetNavigation") return _navigationService.GetNavigation(target);
                        if (action == "GetSqlForNavigation")
                        {
                            int? levelNumber = args?["levelNumber"]?.ToObject<int?>();
                            bool includeExecutionPlan = args?["includeExecutionPlan"]?.ToObject<bool?>() ?? false;
                            bool includeIndexAdvisor = args?["includeIndexAdvisor"]?.ToObject<bool?>() ?? false;
                            return _navigationSqlService.Generate(target, levelNumber, includeExecutionPlan, includeIndexAdvisor);
                        }
                        if (action == "GenerateSampleData")
                        {
                            int rows = args?["rows"]?.ToObject<int?>() ?? 5;
                            return _sampleDataService.Generate(target, rows);
                        }
                        if (action == "TranslationsImport")
                        {
                            return _translationsService.Import(payload);
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
                        if (action == "ParentContext") return _analyzeService.ParentContext(target);
                        // Wave-3 SOTA: mode=cross_platform_impact — Web vs SmartDevices divergence.
                        if (action == "CrossPlatformImpact") return _analyzeService.CrossPlatformImpact(target);
                        // Item 24: mode=callers — per-call-site detail with line + context.
                        if (action == "FindCallerSites") return _analyzeService.FindCallerSites(target);
                        if (action == "GetEventFlow") return _analyzeService.GetEventFlow(target, analyzeType);
                        // Wave-3 item 87: KB-wide dependency heat rankings.
                        if (action == "DependencyHeatmap")
                        {
                            string heatFormat = args?["format"]?.ToString();
                            return _analyzeService.DependencyHeatmap(_kbService.GetKbPath(), heatFormat);
                        }
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
                    case "buildplan":
                        // Wave-3 item 30: GeneratePlan walks the callee graph and emits
                        // {nodes, edges, totalEstimatedSeconds, ascii?}.
                        if (action == "Generate")
                        {
                            string planFormat = args?["format"]?.ToString();
                            JObject toolStatsP95 = args?["toolStatsP95"] as JObject;
                            int maxNodes = args?["maxNodes"]?.ToObject<int?>() ?? 100;
                            return _buildPlanService.GeneratePlan(target, planFormat, toolStatsP95, maxNodes);
                        }
                        break;
                    case "future":
                        // Wave-3 doc-flagged long-term / speculative items. The schema is
                        // shipped via tool_definitions.json + FutureItemRouter; the body
                        // is a single typed deferred envelope. See FutureItemStub.cs.
                        if (action == "Deferred")
                        {
                            int itemNumber = args?["itemNumber"]?.ToObject<int?>() ?? 0;
                            string hint = args?["hint"]?.ToString();
                            return FutureItemStub.Deferred(itemNumber, hint);
                        }
                        break;
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
                        if (action == "Apply")
                        {
                            bool reapply = args?["reapply"]?.ToObject<bool?>() ?? false;
                            var patSettings = args?["settings"] as JObject;
                            string patKey = args?["pattern"]?.ToString();
                            if (reapply) return _patternApplyService.ReapplyPattern(target, patSettings);
                            return _patternApplyService.ApplyPattern(target, patKey, patSettings);
                        }
                        if (action == "Diagnose")
                        {
                            // Item 45: read-only preflight — returns structured reasons without mutating.
                            var patSettings = args?["settings"] as JObject;
                            string patKey = args?["pattern"]?.ToString();
                            return _patternApplyService.DiagnosePattern(target, patKey, patSettings);
                        }
                        break;
                    case "sdkprobe":
                        if (action == "Run")
                        {
                            try
                            {
                                var probe = Services.SdkSurfaceProbe.Run(args?["outputDir"]?.ToString());
                                return Models.McpResponse.Ok(
                                    code: "SdkProbeCompleted",
                                    result: new JObject
                                    {
                                        ["rawJsonPath"] = probe.RawJsonPath,
                                        ["indexMdPath"] = probe.IndexMdPath,
                                        ["generatorsMdPath"] = probe.GeneratorsMdPath,
                                        ["rawSizeBytes"] = probe.RawSizeBytes,
                                        ["assembliesScanned"] = probe.AssembliesScanned,
                                        ["typesScanned"] = probe.TypesScanned,
                                        ["generatorCandidates"] = probe.GeneratorCandidates,
                                        ["warnings"] = new JArray(probe.Warnings),
                                        ["note"] = "Inspect rawJsonPath with jq, or read INDEX.md / generators.md for navigable views. See docs/sdk-probe/README.md."
                                    });
                            }
                            catch (Exception ex)
                            {
                                return Models.McpResponse.Err(code: "SdkProbeError", message: ex.Message, hint: "Check worker logs for the full stack trace.");
                            }
                        }
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
                        if (action == "Status")
                        {
                            // v2.6.6 Stream F: event-driven long-poll. When `wait` is 0/absent
                            // GetStatusWait short-circuits to the legacy GetStatus shape; >0
                            // blocks on the per-task StateChangeSignal up to 300s.
                            int wait = args?["wait"]?.ToObject<int?>() ?? 0;
                            string since = args?["since"]?.ToString();
                            return _buildService.GetStatusWait(
                                target,
                                wait,
                                since,
                                args?["page"]?.ToObject<int?>() ?? 1,
                                args?["pageSize"]?.ToObject<int?>() ?? 50,
                                args?["compact"]?.ToObject<bool?>() ?? false);
                        }
                        if (action == "Result") return _buildService.GetResult(
                            target,
                            args?["page"]?.ToObject<int?>() ?? 1,
                            args?["pageSize"]?.ToObject<int?>() ?? 50);
                        if (action == "Cancel") return _buildService.Cancel(target);
                        // Item 43 (friction 2026-05-22) — DDL diff/preview pre-reorg.
                        if (action == "ReorgPreview") return _buildService.ReorgPreview(target);
                        {
                            // v2.3.8 (Task 5.2): forward includeCallees + buildPlanCap from gateway.
                            var includeCallees = args?["includeCallees"]?.ToString();
                            var cap = args?["buildPlanCap"]?.ToObject<int?>() ?? 200;
                            if (string.IsNullOrWhiteSpace(includeCallees)) includeCallees = "transitive";
                            bool skipFullDeploy = args?["skipFullDeploy"]?.ToObject<bool?>() ?? false;
                            // Item 72 (friction 2026-05-22) — failure-webhook URL plumbed through to BuildService.
                            string notifyOnFailure = args?["notifyOnFailure"]?.ToString();
                            // Item 28 (Tier-S, EXPERIMENTAL) — fastIncremental opt-in.
                            bool fastIncremental = args?["fastIncremental"]?.ToObject<bool?>() ?? false;
                            bool buildDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                            if (buildDryRun)
                                return _buildService.BuildDryRun(action, target, includeCallees, cap);
                            return _buildService.Build(action, target, includeCallees, cap, skipFullDeploy, notifyOnFailure, fastIncremental);
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
                        {
                            int verId = args?["versionId"]?.ToObject<int?>() ?? 0;
                            // v2.6.6 Stream H (FR#28) — forward discard + snapshot + part
                            // so HistoryService can route restore through EditSnapshotStore.
                            string partName = args?["part"]?.ToString();
                            string snapshotToken = args?["snapshot"]?.ToString();
                            bool discard = args?["discard"]?.ToObject<bool?>() ?? false;
                            // Item 21 (friction 2026-05-22): dryRun=true returns the
                            // would-be diff without writing.
                            bool historyDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                            return _historyService.Execute(target, action, verId, partName, snapshotToken, discard, historyDryRun);
                        }
                    // Item 16 — genexus_undo last=N
                    case "undo":
                        {
                            int last = args?["last"]?.ToObject<int?>() ?? 1;
                            bool undoDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                            return _undoService.Undo(last, undoDryRun);
                        }
                    // Items 50 + 48 — genexus_security action=audit_gam|scan_secrets
                    case "security":
                        if (string.Equals(action, "audit_gam", StringComparison.OrdinalIgnoreCase))
                            return _securityAuditService.AuditGam();
                        if (string.Equals(action, "scan_secrets", StringComparison.OrdinalIgnoreCase))
                            return _securityAuditService.ScanSecrets();
                        return Models.McpResponse.Err(
                            code: "UnknownAction",
                            message: $"Unsupported security action '{action}'.",
                            hint: "Call genexus_security with no action to see the supported list.",
                            nextSteps: new JArray(
                                Models.McpResponse.NextStep(
                                    tool: "genexus_orient",
                                    args: new JObject(),
                                    why: "Shows the tool catalog so the right action can be chosen.")),
                            target: target);
                    // Item 65 — genexus_orient welcome card
                    case "orient":
                        if (string.Equals(action, "Welcome", StringComparison.OrdinalIgnoreCase))
                            return _orientService.Welcome();
                        return Models.McpResponse.Err(
                            code: "UnknownAction",
                            message: $"Unsupported orient action '{action}'.",
                            hint: "Call genexus_orient with no action to see the supported list.",
                            nextSteps: new JArray(
                                Models.McpResponse.NextStep(
                                    tool: "genexus_orient",
                                    args: new JObject(),
                                    why: "Shows the tool catalog so the right action can be chosen.")),
                            target: target);
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
                    {
                        bool refactorDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                        return _refactorService.Refactor(target, action, payload, refactorDryRun);
                    }
                    case "popup":
                        if (action == "Create")
                        {
                            string popupName = target ?? args?["name"]?.ToString();
                            var popupSpec = args?["spec"] as JObject;
                            // Item 21 (friction 2026-05-22): dryRun=true previews layout XML without persisting.
                            bool popupDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                            return _popupTemplateService.CreatePopup(popupName, popupSpec, popupDryRun);
                        }
                        break;
                    case "dbdrift":
                        // Item 41 (mcp-improvements-2026-05-22) — Transaction ↔ DB drift.
                        if (string.Equals(action, "Check", StringComparison.OrdinalIgnoreCase))
                            return _dbDriftService.Check(target);
                        if (string.Equals(action, "Report", StringComparison.OrdinalIgnoreCase))
                            return _dbDriftService.Report(target);
                        break;
                    case "dboptimize":
                        // SOTA — static index advisor. Walks For each blocks and proposes
                        // covering indexes for hot Transaction × where-signature paths.
                        if (string.Equals(action, "Analyze", StringComparison.OrdinalIgnoreCase))
                            return _dbOptimizeService.Analyze(target);
                        if (string.Equals(action, "SuggestIndexes", StringComparison.OrdinalIgnoreCase))
                            return _dbOptimizeService.SuggestIndexes(target);
                        if (string.Equals(action, "Report", StringComparison.OrdinalIgnoreCase))
                            return _dbOptimizeService.Report(args?["format"]?.ToString());
                        break;
                    case "webformedit":
                        // Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
                        return _webFormEditService.Execute(action, args);
                    case "runobject":
                        // Item 11 (mcp-improvements-2026-05-22) — runtime URL + optional GAM cookies.
                        if (string.Equals(action, "Resolve", StringComparison.OrdinalIgnoreCase))
                        {
                            string roName = target ?? args?["name"]?.ToString();
                            var roArgs = args?["args"] as JArray;
                            var gamToken = args?["gamSession"];
                            bool roDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                            return _runObjectService.Resolve(roName, roArgs, gamToken, roDryRun);
                        }
                        break;
                    case "explain":
                        // Item 68 — PM-readable deterministic summary.
                        if (string.Equals(action, "Explain", StringComparison.OrdinalIgnoreCase))
                            return _explainService.Explain(target, args?["type"]?.ToString(), args?["depth"]?.ToString());
                        break;
                    case "generateddiff":
                        // Item 12 — unified diff of generated artifacts vs baseline.
                        if (string.Equals(action, "Diff", StringComparison.OrdinalIgnoreCase))
                            return _generatedDiffService.Diff(target, args?["against"]?.ToString());
                        break;
                    case "kbreadme":
                        // Item 90 — Markdown README generation.
                        if (string.Equals(action, "Generate", StringComparison.OrdinalIgnoreCase))
                            return _kbReadmeService.Generate("generate", args?["outputPath"]?.ToString());
                        break;
                    case "ocr":
                        if (string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase))
                            return _ocrScreenshotService.Run(args?["path"]?.ToString());
                        break;
                    case "prdescription":
                        if (string.Equals(action, "Generate", StringComparison.OrdinalIgnoreCase))
                        {
                            int last = args?["last"]?.ToObject<int?>() ?? 10;
                            return _prDescriptionService.Generate(last, args?["workingDir"]?.ToString());
                        }
                        break;
                    case "screenshotpublish":
                        if (string.Equals(action, "Publish", StringComparison.OrdinalIgnoreCase))
                            return _screenshotPublishService.Publish(args?["path"]?.ToString());
                        break;
                    case "frictionlog":
                        if (string.Equals(action, "Append", StringComparison.OrdinalIgnoreCase))
                        {
                            return _frictionLogService.Append(
                                args?["tool"]?.ToString(),
                                args?["message"]?.ToString(),
                                args?["severity"]?.ToString());
                        }
                        if (string.Equals(action, "Tail", StringComparison.OrdinalIgnoreCase))
                        {
                            int n = args?["n"]?.ToObject<int?>() ?? 20;
                            return _frictionLogService.Tail(n);
                        }
                        break;
                    case "wcagcheck":
                        if (string.Equals(action, "Check", StringComparison.OrdinalIgnoreCase))
                            return _wcagCheckService.Check(target ?? args?["target"]?.ToString() ?? args?["name"]?.ToString());
                        break;
                    case "learning":
                        if (string.Equals(action, "Report", StringComparison.OrdinalIgnoreCase))
                            return _learningReportService.Report(args?["since"]?.ToString(), args?["until"]?.ToString());
                        break;
                    case "gxserver":
                        // genexus_gxserver — GxServer sync state (SDK-backed) plus
                        // write actions (commit/update/lock/resolve), routed inside
                        // GxServerSyncService.Run to the sibling GxServerWriteService.
                        return _gxServerSyncService.Run(args ?? new JObject());
                    case "compare":
                        // genexus_compare — read-only IDE "Compare Objects" parity over
                        // the SDK's IComparerService. See docs/sdk_coverage_gap_matrix.md P0 #2.
                        return _compareService.Run(args ?? new JObject());
                    case "module":
                        // genexus_module — GeneXus Module Manager (install/update) over
                        // the SDK's IModuleManagerService. See ModuleService for the
                        // feasibility-gate notes on which overloads are wired.
                        return _moduleService.Run(args ?? new JObject());
                    case "gam":
                        // genexus_gam — GAM / integrated-security provisioning over the
                        // SDK's IIntegratedSecurityService. action=status is read-only;
                        // define_api/deploy are destructive (see GamService for guards).
                        return _gamService.Run(args ?? new JObject());
                    case "merge":
                        // genexus_merge — WRITE surface over the SDK's IMergeService.
                        // dryRun defaults true (see MergeToolService). destructiveHint=true.
                        return _mergeToolService.Run(args ?? new JObject());
                    case "sdpanel":
                        return _sdPanelService.Dispatch(action, target ?? args?["name"]?.ToString() ?? args?["target"]?.ToString(), args?["params"] as JObject ?? args);
                    case "multiagentlock":
                        return _multiAgentLockService.Dispatch(
                            action,
                            target ?? args?["target"]?.ToString(),
                            args?["part"]?.ToString(),
                            args?["ownerId"]?.ToString(),
                            args?["ttlSec"]?.ToObject<int?>() ?? 300,
                            kbPathOverride: null,
                            dryRun: request["dryRun"]?.ToObject<bool?>() ?? false);
                    case "whatif":
                        if (string.Equals(action, "Simulate", StringComparison.OrdinalIgnoreCase))
                            return _whatIfService.Simulate(args?["change"] as JObject);
                        break;
                    case "tutorial":
                        if (string.Equals(action, "Step", StringComparison.OrdinalIgnoreCase))
                            return _tutorialService.GetStep(args?["step"]?.ToObject<int?>() ?? 1);
                        break;
                    case "playbook":
                        if (string.Equals(action, "Read", StringComparison.OrdinalIgnoreCase))
                            return _playbookService.Read(
                                args?["topic"]?.ToString(),
                                args?["list"]?.ToObject<bool?>() == true);
                        break;
                    case "doctor":
                        // genexus_doctor — health/triage envelope. No args.
                        return _doctorService.Diagnose();
                    case "api":
                        // genexus_api — REST endpoint introspection + diff vs baseline.
                        // Single Run() switches on args.action (list/describe/diff_baseline/snapshot).
                        return _apiIntrospectService.Run(args ?? new JObject());
                    case "profile":
                        // genexus_profile — runtime profiler XML bridge.
                        // Single Run() switches on args.action (analyze/hotspots/correlate).
                        return _profileService.Run(args ?? new JObject());
                    case "types":
                        // genexus_types — Domain/SDT introspection + value validation.
                        // Run() switches on args.action (list/describe/validate_value).
                        return _typeIntrospectService.Run(args ?? new JObject());
                    case "github":
                        if (string.Equals(action, "CreatePr", StringComparison.OrdinalIgnoreCase))
                            return _githubService.CreatePr(args?["title"]?.ToString(), args?["body"]?.ToString(), args?["base"]?.ToString(), args?["workingDir"]?.ToString(), request["dryRun"]?.ToObject<bool?>() ?? false);
                        break;
                    case "aicomplete":
                        if (string.Equals(action, "Complete", StringComparison.OrdinalIgnoreCase))
                            return _aiCompleteService.Complete(args?["name"]?.ToString(), args?["part"]?.ToString(), args?["context"]?.ToString(), args?["maxTokens"]?.ToObject<int?>() ?? 200).ToString(Newtonsoft.Json.Formatting.None);
                        break;
                    case "timetravel":
                        if (string.Equals(action, "Recover", StringComparison.OrdinalIgnoreCase))
                            return _timeTravelService.Recover(target ?? args?["name"]?.ToString(), args?["at"]?.ToString());
                        break;
                    case "voice":
                        if (string.Equals(action, "Intent", StringComparison.OrdinalIgnoreCase))
                            return _voiceIntentService.Map(args?["transcript"]?.ToString()).ToString(Newtonsoft.Json.Formatting.None);
                        break;
                    case "autotest":
                        if (string.Equals(action, "Generate", StringComparison.OrdinalIgnoreCase))
                            return _autoTestService.Generate(args?["path"]?.ToString());
                        break;
                    case "reversepattern":
                        if (string.Equals(action, "Infer", StringComparison.OrdinalIgnoreCase))
                            return _reversePatternService.Infer(args?["source"] as JArray);
                        break;
                    case "crossbrowser":
                        if (string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase))
                            return _crossBrowserService.Run(target ?? args?["target"]?.ToString(), args?["browsers"] as JArray, args?["capture"] as JArray);
                        break;
                    case "preview":
                        if (action == "Render" || action == "Run")
                        {
                            string previewName = target ?? args?["name"]?.ToString();
                            var previewParms = args?["parms"] as JObject;
                            string launcher = args?["launcher"]?.ToString() ?? "auto";
                            bool buildFirst = args?["buildFirst"]?.ToObject<bool?>() ?? false;
                            int waitMs = args?["waitMs"]?.ToObject<int?>() ?? 3000;
                            string[] capture = (args?["capture"] as JArray)?.Select(t => t.ToString()).ToArray();
                            bool diffBaseline = args?["diffBaseline"]?.ToObject<bool?>() ?? false;
                            bool updateBaseline = args?["updateBaseline"]?.ToObject<bool?>() ?? false;
                            // Stream G (v2.6.6): GX-aware fill/click + GAM auth.
                            var fill = args?["fill"] as JObject;
                            string click = args?["click"]?.ToString();
                            var auth = args?["auth"] as JObject;
                            // Items 39/97: device emulation + network throttle, passed
                            // through to chrome-devtools-axi via --emulate / --throttle.
                            string emulate = args?["emulate"]?.ToString();
                            string network = args?["network"]?.ToString();
                            // v2.6.6 Stream H (FR#25): action=Run resolves the
                            // KB launcher object when target is omitted; action=Render
                            // requires an explicit target as before.
                            var previewTask = action == "Run"
                                ? _previewService.RunAsync(previewName, previewParms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline, fill, click, auth, emulate, network)
                                : _previewService.PreviewAsync(previewName, previewParms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline, fill, click, auth, emulate, network);
                            previewTask.Wait();
                            return previewTask.Result.ToString(Newtonsoft.Json.Formatting.None);
                        }
                        break;
                    // Wave-3: IDE right-click parity tools.
                    case "kbexplorer":
                        if (string.Equals(action, "Locate", StringComparison.OrdinalIgnoreCase))
                        {
                            string locName = target ?? args?["name"]?.ToString();
                            return _kbExplorerService.Locate(locName);
                        }
                        return Models.McpResponse.Err(
                            code: "UnknownAction",
                            message: $"Unsupported kbexplorer action '{action}'.",
                            hint: "Call genexus_kbexplorer with no action to see the supported list.",
                            nextSteps: new JArray(
                                Models.McpResponse.NextStep(
                                    tool: "genexus_orient",
                                    args: new JObject(),
                                    why: "Shows the tool catalog so the right action can be chosen.")),
                            target: target);
                    case "navigation":
                        if (string.Equals(action, "View", StringComparison.OrdinalIgnoreCase))
                        {
                            string navName = target ?? args?["name"]?.ToString();
                            bool latest = args?["latest"]?.ToObject<bool?>() ?? false;
                            return _navigationViewService.View(navName, latest);
                        }
                        return Models.McpResponse.Err(
                            code: "UnknownAction",
                            message: $"Unsupported navigation action '{action}'.",
                            hint: "Call genexus_navigation with no action to see the supported list.",
                            nextSteps: new JArray(
                                Models.McpResponse.NextStep(
                                    tool: "genexus_orient",
                                    args: new JObject(),
                                    why: "Shows the tool catalog so the right action can be chosen.")),
                            target: target);
                    case "blame":
                        if (string.Equals(action, "Get", StringComparison.OrdinalIgnoreCase))
                        {
                            var blameReq = new BlameService.BlameRequest
                            {
                                Name = target ?? args?["name"]?.ToString(),
                                Part = args?["part"]?.ToString(),
                                FilePath = args?["filePath"]?.ToString(),
                                Line = args?["line"]?.ToObject<int?>() ?? 0,
                                Context = args?["context"]?.ToObject<int?>() ?? 2
                            };
                            return _blameService.Blame(blameReq);
                        }
                        return Models.McpResponse.Err(
                            code: "UnknownAction",
                            message: $"Unsupported blame action '{action}'.",
                            hint: "Call genexus_blame with no action to see the supported list.",
                            nextSteps: new JArray(
                                Models.McpResponse.NextStep(
                                    tool: "genexus_orient",
                                    args: new JObject(),
                                    why: "Shows the tool catalog so the right action can be chosen.")),
                            target: target);
                    case "browser_capture":
                        if (action == "Capture")
                        {
                            string bcTarget = target ?? args?["name"]?.ToString();
                            var bcKinds = args?["capture"] as JArray;
                            return _browserCaptureService.Capture(bcTarget, bcKinds).ToString(Newtonsoft.Json.Formatting.None);
                        }
                        break;
                    case "smoke_test":
                        if (action == "Run")
                        {
                            string stTarget = target ?? args?["name"]?.ToString();
                            return _smokeTestService.Run(stTarget).ToString(Newtonsoft.Json.Formatting.None);
                        }
                        break;
                    case "a11y_audit":
                        if (action == "Audit")
                        {
                            string aaTarget = target ?? args?["name"]?.ToString();
                            return _a11yAuditService.Audit(aaTarget).ToString(Newtonsoft.Json.Formatting.None);
                        }
                        break;
                }

                return Models.McpResponse.Err(
                    code: "UnknownMethodOrAction",
                    message: string.Format("Unsupported dispatch combination. Method='{0}', Action='{1}'.", method ?? "", action ?? ""),
                    hint: "Call genexus_help action=route goal=<intent> for the right tool, or genexus_orient for an overview.",
                    nextSteps: new JArray(
                        Models.McpResponse.NextStep(
                            tool: "genexus_orient",
                            args: new JObject(),
                            why: "Shows the tool catalog so the right action can be chosen.")),
                    target: target);
                } // end using ProgressContext
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DispatcherException",
                    message: ex.Message,
                    hint: "Inspect worker logs for the full exception chain; this is an unhandled dispatcher error.");
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

