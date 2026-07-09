using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security;

namespace GxMcp.Worker.Services
{
    public class BuildService : IBuildServiceFacade
    {
        private string _msbuildPath;
        private string _gxDir;
        private KbService _kbService;
        private IndexCacheService _indexCacheService;
        private CallerGraphService _callerGraphService;
        private static readonly ConcurrentDictionary<string, BuildTaskStatus> _tasks = new ConcurrentDictionary<string, BuildTaskStatus>();

        private static readonly System.Collections.Generic.Dictionary<string, int> _phaseProgressMap =
            new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "Starting",   5  },
                { "OpeningKB",  10 },
                { "Specifying", 15 },
                { "Generating", 35 },
                { "Compiling",  50 },
                { "Finishing",  85 },
                { "Linking",    85 },
                { "Done",       100 },
                { "Completed",  100 }
            };

        private static void EmitPhaseProgress(string phase, int total = 100)
        {
            int p;
            if (_phaseProgressMap.TryGetValue(phase, out p))
            {
                GxMcp.Worker.Helpers.ProgressEmitter.Emit(p, total, "Build phase: " + phase);
            }
        }

        // Friction 2026-05-22: MSBuild /m spawns N worker nodes per build; killing
        // only the parent (Process.Kill on net48) orphans the children as zombies.
        // We walk the tree via WMI's Win32_Process.ParentProcessId, kill leaves first,
        // then the parent. Tolerant of races (process already exited).
        internal static int KillProcessTree(System.Diagnostics.Process root)
        {
            if (root == null) return 0;
            int killed = 0;
            try
            {
                int rootPid;
                try { rootPid = root.Id; } catch { return 0; }
                // R10: WMI ParentProcessId is creation-time-only; if rootPid was
                // recycled, descendants of the OLD process get picked up. Capture
                // the root's start time and filter children born before it.
                DateTime rootStart;
                try { rootStart = root.StartTime.ToUniversalTime(); }
                catch { rootStart = DateTime.MinValue; }
                var descendants = CollectDescendants(rootPid, rootStart);
                // Kill leaves first so we don't reparent children to init mid-walk.
                foreach (var pid in descendants)
                {
                    try
                    {
                        using (var child = System.Diagnostics.Process.GetProcessById(pid))
                        {
                            if (!child.HasExited) { child.Kill(); killed++; }
                        }
                    }
                    catch { /* gone already */ }
                }
                try { if (!root.HasExited) { root.Kill(); killed++; } }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Warn("[KILL-TREE] root pid=" + (root?.Id) + ": " + ex.Message);
            }
            if (killed > 0)
                Logger.Info("[KILL-TREE] killed " + killed + " process(es) under root pid=" + (root?.Id));
            return killed;
        }

        private static List<int> CollectDescendants(int rootPid, DateTime rootStartUtc)
        {
            var result = new List<int>();
            // Build full parent → children map via WMI in one query (cheap).
            var byParent = new Dictionary<int, List<int>>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject mo in results)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            int parent = Convert.ToInt32(mo["ParentProcessId"]);
                            // R10: skip children that were born before the root —
                            // they're PID-reuse artifacts. CIM datetime string like
                            // "20260522141234.123456-180"; on parse failure default
                            // to INCLUDE (conservative: better to over-kill our own
                            // descendants than miss them).
                            bool include = true;
                            if (rootStartUtc > DateTime.MinValue)
                            {
                                try
                                {
                                    var cd = mo["CreationDate"] as string;
                                    if (!string.IsNullOrEmpty(cd))
                                    {
                                        var childStart = System.Management.ManagementDateTimeConverter
                                            .ToDateTime(cd).ToUniversalTime();
                                        if (childStart < rootStartUtc) include = false;
                                    }
                                }
                                catch { /* parse failure → include */ }
                            }
                            if (!include) continue;
                            if (!byParent.TryGetValue(parent, out var list))
                            {
                                list = new List<int>();
                                byParent[parent] = list;
                            }
                            list.Add(pid);
                        }
                        catch { }
                        finally { mo?.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[KILL-TREE] WMI scan failed: " + ex.Message);
                return result;
            }

            // BFS from rootPid; collect children, grandchildren, ...
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (byParent.TryGetValue(cur, out var kids))
                {
                    foreach (var k in kids)
                    {
                        if (seen.Add(k))
                        {
                            result.Add(k);
                            queue.Enqueue(k);
                        }
                    }
                }
            }
            // Reverse so leaves come first
            result.Reverse();
            return result;
        }

        private const int TailBufferSize = 30;

        // Regex parsers for MSBuild/GeneXus output
        private static readonly Regex _rxSpecifying = new Regex(@"^\s*Specifying\s+(?<obj>.+?)\s*\.\.\.\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // "Generating ObjectName ..." — exclude "Generating to <path>" (those are output-file lines, not objects)
        private static readonly Regex _rxGenerating = new Regex(@"^\s*Generating\s+(?!to\b)(?<obj>.+?)(?:\s*\.\.\.)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxCompiling  = new Regex(@"^\s*Compil(ation|ing)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // GeneXus spec/gen errors are 'error spc####:', C# compiler errors are 'error CS####:',
        // and MSBuild surfaces some lines as 'error : <message>' (no code). Capture all three forms.
        private static readonly Regex _rxError      = new Regex(@"\berror\s*(:|[A-Z]{2,4}\d+\s*:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxWarning    = new Regex(@"\bwarning\s*(:|[A-Z]{2,4}\d+\s*:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxOpenKb     = new Regex(@"^\s*Opening Knowledge Base\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxBuildDone  = new Regex(@"^\s*(Build|Specification|Compilation)\s+(succeeded|failed|complete)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxBuildOneEnd = new Regex(@"Build One Task\s+(terminado|finished|Sucesso|completed|ended)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Missing dependency surfacing — the GeneXus C# compile leaks CS0246 / CS2001
        // when a referenced object was never spec/gen'd. Pulling the missing type or
        // source file out of the error line gives the agent the next target to build
        // without spelunking the raw output:
        //   CS0246 ... 'wsretdatainicio' não pôde ser encontrado  → object 'retdatainicio'
        //   CS2001 ... 'painelclassesala_bc.cs' não pôde ser encontrado → object 'painelclassesala'
        private static readonly Regex _rxCs0246Missing = new Regex(
            @"error\s+CS0246:.*?['""‘’]([A-Za-z_][\w]*)['""‘’]",
            RegexOptions.Compiled);
        private static readonly Regex _rxCs2001Missing = new Regex(
            @"error\s+CS2001:.*?['""‘’]([^'""‘’]+\.cs)['""‘’]",
            RegexOptions.Compiled);
        // v2.6.6 Stream E (FR#7): index-aware normalizer. The pre-Stream-E version
        // could suggest a stripped name that doesn't exist in the KB (e.g.
        // "acessoperfil" → "cessoperfil" via a phantom 'a' strip; or "wsfoo"
        // when both "wsfoo" AND "foo" happen to be present, but only "wsfoo" is
        // the real object). When an IndexCacheService lookup is available the
        // result is verified:
        //   - stripped name known       → return stripped
        //   - stripped name unknown but
        //     ORIGINAL symbol known     → return original (no normalization)
        //   - neither known             → return null (suggestion dropped)
        // When lookup is null we keep legacy behaviour (return stripped form)
        // so call sites without an index cache still get a best-effort answer.
        private static string NormalizeMissingObjectName(string symbol, IndexCacheService lookup = null)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            // GX naming map: ws<obj>=web-service wrapper, a<obj>=async wrapper,
            //                <obj>_bc=business component, <obj>_level_detail=trn detail,
            //                p<obj>=print wrapper. Strip common prefixes/suffixes so the
            //                agent's next build hits the real KB object name.
            var original = symbol.Trim();
            var s = original;
            int us = s.LastIndexOf('_');
            if (us > 0)
            {
                var tail = s.Substring(us + 1).ToLowerInvariant();
                if (tail == "bc" || tail == "level" || tail == "detail" || tail == "ws" ||
                    tail == "impl" || tail == "client" || tail == "main")
                    s = s.Substring(0, us);
            }
            if (s.Length > 2 && s.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            // NOTE: do NOT strip 'a' / 'p' single-letter prefixes — those collide
            // with real KB object names that happen to start with those letters
            // (acessoperfil, painelclassesala, premiomerito, …). Only the explicit
            // 'ws' web-service wrapper prefix is safe to strip.

            if (lookup == null) return s;

            // Index-aware verification.
            bool strippedKnown = !string.IsNullOrEmpty(s) && lookup.IsObjectKnown(s);
            if (strippedKnown) return s;
            bool originalKnown = lookup.IsObjectKnown(original);
            if (originalKnown) return original;
            // Neither form is in the index — drop the suggestion rather than
            // sending the agent chasing a phantom target.
            return null;
        }

        // Friction 2026-05-22: late-phase failure markers in GeneXus MSBuild output.
        // >RO <name>          → a step ("Running ...") that ran but the build then exited 1
        // >E0 <code>: <msg>   → an explicit error from a later phase (no CS####/spc####)
        // Last match wins (most-recent step is the one that flipped exit code).
        private static readonly Regex _rxLatePhaseRO = new Regex(
            @"^\s*>?RO\s+(?<name>[^\r\n:]+?)\s*(?::\s*(?<msg>.+?))?\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex _rxLatePhaseE0 = new Regex(
            @"^\s*>?E0\s+(?<name>[^\r\n:]+?)\s*:\s*(?<msg>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);
        // "Generation: Sucesso" / "Generation succeeded" / "Compilation: Sucesso" etc.
        // Either Portuguese or English locale; we only check presence, not order.
        private static readonly Regex _rxGenerationOk = new Regex(
            @"^\s*Generation\s*[:\-]?\s*(succeeded|sucesso|ok|success)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex _rxCompilationOk = new Regex(
            @"^\s*Compilation\s*[:\-]?\s*(succeeded|sucesso|ok|success)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        internal static PhaseFailureInfo ExtractPhaseFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            // Prefer explicit E0 errors (carry a real message).
            Match lastE0 = null;
            foreach (Match m in _rxLatePhaseE0.Matches(output))
                lastE0 = m;
            if (lastE0 != null)
            {
                return new PhaseFailureInfo
                {
                    Name = lastE0.Groups["name"].Value.Trim(),
                    Message = lastE0.Groups["msg"].Value.Trim()
                };
            }
            // Fall back to the last RO marker — the step that was running when the build died.
            Match lastRO = null;
            foreach (Match m in _rxLatePhaseRO.Matches(output))
                lastRO = m;
            if (lastRO != null)
            {
                return new PhaseFailureInfo
                {
                    Name = lastRO.Groups["name"].Value.Trim(),
                    Message = string.IsNullOrEmpty(lastRO.Groups["msg"].Value)
                        ? "Step exited non-zero; no explicit error line emitted."
                        : lastRO.Groups["msg"].Value.Trim()
                };
            }
            return null;
        }

        internal static bool DidGenerationAndCompilationSucceed(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            return _rxGenerationOk.IsMatch(output) && _rxCompilationOk.IsMatch(output);
        }

        // v2.6.6 Stream E (FR#9): CS2001 referencing "<obj>_bc.cs" is treated as
        // an orphan demotion when the underlying object is not a Transaction in the
        // current index (either missing entirely, or renamed to a different type).
        // Conservative: if the index lookup is unavailable, the predicate returns
        // false so the legacy "error stands" behaviour holds.
        internal static bool IsBcOrphanError(string line, IndexCacheService lookup)
        {
            if (lookup == null || string.IsNullOrEmpty(line)) return false;
            var m = _rxCs2001Missing.Match(line);
            if (!m.Success) return false;
            var file = m.Groups[1].Value;
            var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
            var bare = slash >= 0 ? file.Substring(slash + 1) : file;
            if (bare.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - 3);
            if (!bare.EndsWith("_bc", StringComparison.OrdinalIgnoreCase)) return false;
            var trnName = bare.Substring(0, bare.Length - 3);
            if (string.IsNullOrEmpty(trnName)) return false;
            try
            {
                var entry = lookup.TryGetEntryByName(trnName);
                if (entry == null) return true; // Transaction gone — _bc.cs is orphan
                // Different type with the same name → not a Transaction → orphan.
                return !string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // FR#12 (friction-report 2026-05-14): MSBuild + GeneXus emit dozens of
        // "Copiando módulo X" / "Copying module X" / "Restoring NuGet packages" lines per build.
        // They go to FullOutput (terminal payload) but get filtered out of TailLines so the
        // live tail shows actionable signal. Phase change / Errors / Warnings always pass.
        private static readonly Regex _rxModuleCopyNoise = new Regex(
            @"^\s*(Copiando|Copying|Restoring NuGet|Restaurando NuGet|Wrote\s+|Touching\s+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public class ErrorDetail
        {
            public string raw { get; set; }
            public string rewritten { get; set; }
            public string phase { get; set; }
            public string gxObject { get; set; }
        }

        public class BuildTaskStatus
        {
            public string TaskId { get; set; }
            public string Status { get; set; }            // Accepted | Running | Succeeded | Failed | Error | Cancelled
            public string Phase { get; set; }             // Starting | OpeningKB | Specifying | Generating | Compiling | Finishing | Done
            public string Action { get; set; }
            public string Target { get; set; }
            public List<string> Targets { get; set; }   // populated when batch build (multi-target)
            public int? TargetsTotal { get; set; }
            public int? TargetsDone { get; set; }
            public string CurrentObject { get; set; }
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
            /// <summary>
            /// Object names extracted from CS0246/CS2001 compile errors —
            /// dependencies whose .dll is stale or never built. The agent
            /// should chain <c>genexus_lifecycle build target=...</c> on
            /// these before retrying.
            /// </summary>
            public HashSet<string> SuggestedRebuildTargets { get; set; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int LineCount { get; set; }
            public string LastLine { get; set; }
            public List<string> TailLines { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
            // FR#21 (v2.6.6 Stream C): keep both the raw MSBuild line and the
            // rewritten "[gx-object=... phase=...]" form so debugging is possible.
            public List<ErrorDetail> ErrorsDetailed { get; set; } = new List<ErrorDetail>();
            // FR#9 (v2.6.6 Stream E): CS2001 errors referencing "<obj>_bc.cs" where
            // the underlying Transaction no longer exists (or isn't a Transaction)
            // are demoted to warnings — counted here, full lines preserved in
            // Warnings[] with a "[demoted-bc-orphan]" prefix.
            public int DemotedErrors { get; set; }
            // FR#22 (v2.6.6 Stream C): shaped output envelope (head/tail/full-log-path).
            // The legacy flat string is kept for backwards compat in non-compact mode.
            public object Output { get; set; }
            public string FullLogPath { get; set; }
            public string StartTime { get; set; }
            public string EndTime { get; set; }
            public double? ElapsedSeconds { get; set; }
            public int ExitCode { get; set; }
            public string Error { get; set; }
            // v2.6.6 Stream D: "inproc" when GeneXus MSBuild tasks ran inside the
            // worker against the open KB, "msbuild-exe" when we fell back to the
            // external MSBuild.exe spawn. Surfaced for telemetry / debugging.
            public string BuildPath { get; set; }
            public List<string> CallersToAlsoBuild { get; set; }
            public string Hint { get; set; }
            // v2.3.8 (Task 5.1/5.2): expansion plan applied to the BuildOne
            // sequence. Surfaced under _meta.buildPlan in status/result.
            public BuildPlan BuildPlan { get; set; }
            // Friction 2026-05-22 (experimental): when true and the in-process
            // build path is taken, skip the IdeWebBuildAndDeploy step. See
            // InProcessBuildRunner.Run for the safety story.
            [JsonIgnore] internal bool SkipFullDeploy { get; set; }
            // Item 28 (Tier-S, EXPERIMENTAL) — fastIncremental decision metadata.
            // Surfaced under top-level response fields (not status output) so the
            // agent sees the decision exactly once, with the Accepted envelope.
            [JsonIgnore] internal bool FastIncrementalRequested { get; set; }
            public bool FastIncrementalFallback { get; set; }
            public string FastIncrementalFallbackReason { get; set; }
            [JsonIgnore] internal bool FastIncrementalCanSkipDeploy { get; set; }
            [JsonIgnore] internal IReadOnlyList<string> FastIncrementalCanSkipSpecify { get; set; }
            // Item 72 (friction 2026-05-22) — webhook URL to POST a failure summary
            // to when terminal Status == "Failed". Empty / null disables the call.
            [JsonIgnore] internal string NotifyOnFailureUrl { get; set; }
            // Friction 2026-05-22: "Build Failed: 0 errors, 0 warnings" with no
            // signal was forcing the agent to read the raw Output and guess what
            // happened. When ErrorCount=0 but ExitCode!=0 (a late MSBuild step
            // like WebAppConfig fails), surface the last >RO/>E0 line as a
            // structured phase_failure block.
            public PhaseFailureInfo PhaseFailure { get; set; }
            // "partial_success" means Generation: Sucesso + Compilation: Sucesso
            // were observed in the Output but the overall build was marked Failed
            // (typically due to a downstream packaging/deploy step). The compiled
            // DLLs are usually fine and the run-time picks them up.
            public bool? PartialSuccess { get; set; }

            [JsonIgnore] internal Process Process { get; set; }
            [JsonIgnore] internal DateTime StartedAt { get; set; }
            [JsonIgnore] internal StringBuilder FullOutput { get; set; } = new StringBuilder();
            [JsonIgnore] internal object _lock = new object();
            // v2.6.6 Stream F (FR#19 follow-up): event-driven long-poll. Polling
            // `status` every ~250ms generated 24 round-trips for a 10-minute
            // build. Wait callers block on this signal until HandleLine sees a
            // change worth surfacing (phase / counts / TargetsDone / terminal).
            [JsonIgnore] internal ManualResetEventSlim StateChangeSignal { get; } = new ManualResetEventSlim(false);
            // Aggregated-warnings memo. AggregateWarnings is O(N log codes) but
            // is called on every paginated status poll (the LLM polls many times
            // during a long build). Invalidated whenever WarningCount changes —
            // _aggregateMemoForCount stores the WarningCount the cache was built at.
            [JsonIgnore] internal List<BuildOutputShaper.WarningGroup> AggregatedWarningsCache { get; set; }
            [JsonIgnore] internal int AggregateMemoForCount { get; set; } = -1;

            /// <summary>Compact baseline string used by GetStatusWait for ETag-style change detection.
            /// Stable across pagination, kept short (&lt;50 chars) so it round-trips cheaply.</summary>
            internal string ComputeBaseline()
            {
                return (Phase ?? "") + "|" + (TargetsDone?.ToString() ?? "") + "|" + ErrorCount + "|" + WarningCount + "|" + (Status ?? "");
            }
        }

        // v2.6.6 Stream F: terminal statuses always return immediately from GetStatusWait.
        private static readonly HashSet<string> _terminalStatuses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Succeeded", "Failed", "Cancelled", "Error" };
        private static bool IsTerminalStatus(string s)
            => !string.IsNullOrEmpty(s) && _terminalStatuses.Contains(s);

        // Item 28 (mcp-improvements-2026-05-22, Tier-S) — EXPERIMENTAL. Pluggable
        // decision strategy for the fastIncremental opt-in. Default impl reads
        // EditDirtyTracker + IndexCacheService; tests inject a fake.
        private IFastIncrementalDecision _fastIncrementalDecision;
        public void SetFastIncrementalDecision(IFastIncrementalDecision d) { _fastIncrementalDecision = d; }
        internal IFastIncrementalDecision GetFastIncrementalDecisionForTest() => _fastIncrementalDecision;

        public BuildService()
        {
            _gxDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";

            string[] searchPaths = new[] {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
                Path.Combine(_gxDir, "MSBuild.exe"),
                @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
            };

            foreach (var p in searchPaths) { if (File.Exists(p)) { _msbuildPath = p; break; } }
        }

        public void SetKbService(KbService kbService) { _kbService = kbService; }
        public void SetIndexCacheService(IndexCacheService ics) { _indexCacheService = ics; }
        public void SetCallerGraphService(CallerGraphService graph) { _callerGraphService = graph; }
        public KbService KbService => _kbService;

        // v2.3.8 (Task 5.1) — friction report 2026-05-15 #7. Building a
        // WebPanel that calls N procedures emitted CS0246 because each
        // generated csproj lacked references to callee DLLs. Fix: expand the
        // target list via CallerGraphService and order it reverse-topologically
        // (deepest callees first, originally requested targets last), then
        // emit BuildOne for each — every csproj sees its dependencies on
        // disk by the time GeneXus generates it.
        public class PhaseFailureInfo
        {
            // e.g. "WebAppConfig", "Copying Module", "Deploy". Pulled from the last
            // ">RO <name>" or ">E0..." line the worker saw before the build exited.
            public string Name { get; set; }
            public string Message { get; set; }
        }

        public class BuildPlan
        {
            public List<string> Expanded { get; set; } = new List<string>();
            public List<string> Skipped { get; set; } = new List<string>();
            public bool Truncated { get; set; }
            public int NodeCap { get; set; }
            public int RequestedNodes { get; set; }
            public string IncludeCallees { get; set; }
        }

        public BuildPlan ExpandTargets(IEnumerable<string> targets, string includeCallees = "transitive", int cap = 200)
        {
            var plan = new BuildPlan { NodeCap = cap, IncludeCallees = includeCallees ?? "transitive" };
            var originalList = (targets ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .ToList();
            var originalSet = new HashSet<string>(originalList, StringComparer.OrdinalIgnoreCase);

            // No graph or "none" → preserve original order, but still inject
            // BC variants (FR#8) since the BC heuristic is index-driven, not
            // caller-graph-driven.
            if (_callerGraphService == null || string.Equals(plan.IncludeCallees, "none", StringComparison.OrdinalIgnoreCase))
            {
                var bcOnly = CollectBcVariants(originalList, originalSet, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                plan.Expanded.AddRange(bcOnly);
                plan.Expanded.AddRange(originalList);
                return plan;
            }

            // Collect callees per requested target. We over-fetch (cap+1) to
            // detect truncation before mixing in the originally requested set.
            var calleeOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int requestedTotal = 0;

            foreach (var t in originalList)
            {
                if (string.Equals(plan.IncludeCallees, "direct", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var c in _callerGraphService.GetCallees(t))
                    {
                        requestedTotal++;
                        if (originalSet.Contains(c)) continue;
                        if (seen.Add(c)) calleeOrder.Add(c);
                    }
                }
                else // transitive (default)
                {
                    var trans = _callerGraphService.GetCalleesTransitive(t, maxNodes: cap + 1);
                    requestedTotal += trans.Nodes.Count;
                    if (trans.Truncated)
                    {
                        plan.Truncated = true;
                        plan.RequestedNodes = requestedTotal;
                    }
                    foreach (var c in trans.Nodes)
                    {
                        if (originalSet.Contains(c)) continue;
                        if (seen.Add(c)) calleeOrder.Add(c);
                    }
                }
            }

            if (plan.Truncated)
            {
                // Don't emit a partial plan — caller decides (BuildPlanTooLarge response).
                plan.Expanded = originalList;
                return plan;
            }

            // Reverse so the deepest leaves come first (callees before callers).
            // BFS yields shallowest-first; reversing gives the topological order
            // we want for sequential BuildOne emission.
            calleeOrder.Reverse();

            // v2.6.6 Stream E (FR#8): inject BC variants ahead of the trn so
            // per-object csprojs see the BC .dll on disk by the time the
            // Transaction is generated.
            var bcPrefix = CollectBcVariants(originalList, originalSet, seen);
            if (bcPrefix.Count > 0)
            {
                calleeOrder.InsertRange(0, bcPrefix);
            }

            plan.Expanded.AddRange(calleeOrder);
            plan.Expanded.AddRange(originalList);
            return plan;
        }

        // v2.6.6 Stream E (FR#8): for each Transaction in <originalList>, if the
        // index also carries a "<name>_bc" object the build pipeline needs that
        // variant compiled first. Returns the BC variants in input order; skips
        // ones already in the original set or already collected as callees.
        private List<string> CollectBcVariants(IList<string> originalList, HashSet<string> originalSet, HashSet<string> seen)
        {
            var result = new List<string>();
            if (_indexCacheService == null) return result;
            foreach (var t in originalList)
            {
                var entry = _indexCacheService.TryGetEntryByName(t);
                if (entry == null) continue;
                if (!string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase)) continue;
                var bcName = t + "_bc";
                if (originalSet.Contains(bcName)) continue;
                if (seen.Contains(bcName)) continue;
                // Single name-keyed lookup confirms presence; previously this
                // line redundantly scanned the index a second time via
                // IsObjectKnown(bcName).
                if (_indexCacheService.TryGetEntryByName(bcName) == null) continue;
                result.Add(bcName);
                seen.Add(bcName);
            }
            return result;
        }

        public string Build(string action, string target)
        {
            return Build(action, target, includeCallees: "transitive", buildPlanCap: 200);
        }

        public string Build(string action, string target, string includeCallees, int buildPlanCap)
        {
            return Build(action, target, includeCallees, buildPlanCap, skipFullDeploy: false);
        }

        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy)
            => Build(action, target, includeCallees, buildPlanCap, skipFullDeploy, null);

        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy, string notifyOnFailure)
            => Build(action, target, includeCallees, buildPlanCap, skipFullDeploy, notifyOnFailure, fastIncremental: false);

        /// <summary>
        /// dryRun=true: expands the build plan without dispatching a build task.
        /// Returns code=DryRun with the resolved targets list so the agent can
        /// preview what would compile.
        /// </summary>
        public string BuildDryRun(string action, string target, string includeCallees, int buildPlanCap)
        {
            try
            {
                var targets = ParseTargets(target);
                BuildPlan plan = null;
                if (action != null && action.Equals("Build", StringComparison.OrdinalIgnoreCase) && targets.Count > 0)
                {
                    plan = ExpandTargets(targets, includeCallees ?? "transitive", buildPlanCap);
                    if (plan.Truncated)
                    {
                        return McpResponse.Err(
                            code: "BuildPlanTooLarge",
                            message: "Build plan exceeded cap in dryRun expansion.",
                            extra: new JObject
                            {
                                ["requestedNodes"] = plan.RequestedNodes,
                                ["cap"] = plan.NodeCap,
                                ["includeCallees"] = plan.IncludeCallees
                            });
                    }
                    targets = plan.Expanded;
                }
                return McpResponse.Ok(
                    code: "DryRun",
                    result: new JObject
                    {
                        ["preview"] = new JObject
                        {
                            ["action"] = action,
                            ["wouldBuild"] = new JArray(targets.ToArray()),
                            ["includeCallees"] = includeCallees ?? "transitive",
                            ["buildPlanCap"] = buildPlanCap
                        }
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "DryRunFailed", message: ex.Message);
            }
        }

        // Item 28 — EXPERIMENTAL. When fastIncremental=true, ask the configured
        // IFastIncrementalDecision what we can skip given the current dirty set.
        // Three short-circuit envelopes are surfaced ahead of the normal
        // "Accepted" response so the agent sees the chosen path:
        //   - nothingDirty=true                    → no build queued
        //   - fastIncrementalFallback=true         → legacy full build queued
        //   - fastIncremental={canSkipDeploy,...}  → fast build queued
        public string Build(string action, string target, string includeCallees, int buildPlanCap, bool skipFullDeploy, string notifyOnFailure, bool fastIncremental)
        {
            // Parse target: single name OR comma/semicolon-separated list for batch "Build With These Only"
            var targets = ParseTargets(target);

            // Item 28 — fastIncremental opt-in. The decision is computed BEFORE
            // BuildPlan expansion so the agent sees the same targets it passed.
            FastIncrementalDecision fiDecision = null;
            if (fastIncremental
                && action != null
                && action.Equals("Build", StringComparison.OrdinalIgnoreCase)
                && targets.Count > 0)
            {
                var decider = _fastIncrementalDecision ?? new DefaultFastIncrementalDecision(_indexCacheService);
                try { fiDecision = decider.Decide(GetKBPath(), targets); }
                catch (Exception ex)
                {
                    fiDecision = new FastIncrementalDecision
                    {
                        ForceFullBuild = true,
                        FallbackReason = "decision-threw: " + ex.Message
                    };
                }

                if (fiDecision.NothingDirty)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "NoBuildNeeded",
                        nothingDirty = true,
                        targets = targets,
                        message = "fastIncremental=true and EditDirtyTracker has no dirty entries for the requested targets. No build dispatched.",
                        experimental = true
                    });
                }
            }

            // v2.3.8 (Task 5.1) — expand via CallerGraphService so callees compile
            // before callers and every per-object csproj sees its dependency DLLs.
            // Only applies to Build action with concrete targets; RebuildAll/Reorg/etc. ignore.
            BuildPlan plan = null;
            if (action != null && action.Equals("Build", StringComparison.OrdinalIgnoreCase) && targets.Count > 0)
            {
                plan = ExpandTargets(targets, includeCallees ?? "transitive", buildPlanCap);
                if (plan.Truncated)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "BuildPlanTooLarge",
                        suggested = "Build All from IDE, or set includeCallees='none' / 'direct' / reduce the target set",
                        graph = new { requestedNodes = plan.RequestedNodes, cap = plan.NodeCap },
                        requested = targets,
                        includeCallees = plan.IncludeCallees
                    });
                }
                targets = plan.Expanded;
            }

            string taskId = Guid.NewGuid().ToString().Substring(0, 8);
            var status = new BuildTaskStatus {
                TaskId = taskId,
                Action = action,
                Target = target,
                // Always echo the parsed list so the agent can confirm what got dispatched,
                // including the single-target case. Doc contract says "the parsed list".
                Targets = targets.Count > 0 ? targets : null,
                Status = "Running",
                Phase = "Starting",
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StartedAt = DateTime.UtcNow,
                BuildPlan = plan,
                SkipFullDeploy = skipFullDeploy
                    && string.Equals(action, "Build", StringComparison.OrdinalIgnoreCase)
                    && targets.Count == 1
                    && string.Equals(includeCallees ?? "transitive", "none", StringComparison.OrdinalIgnoreCase),
                NotifyOnFailureUrl = notifyOnFailure,
                // Item 28 — surface decision on the task so status/result echo it.
                FastIncrementalRequested = fastIncremental,
                FastIncrementalFallback = fiDecision?.ForceFullBuild == true,
                FastIncrementalFallbackReason = fiDecision?.ForceFullBuild == true ? fiDecision.FallbackReason : null,
                FastIncrementalCanSkipDeploy = fiDecision?.CanSkipDeploy == true && fiDecision.ForceFullBuild == false,
                FastIncrementalCanSkipSpecify = fiDecision?.ForceFullBuild == false ? fiDecision.CanSkipSpecify : null
            };

            // Best-effort caller lookup for hint (only meaningful for single-object builds)
            if (targets.Count == 1)
            {
                var callers = TryGetDirectCallers(targets[0]);
                if (callers != null && callers.Count > 0)
                {
                    status.CallersToAlsoBuild = callers;
                    status.Hint = "After this build finishes, also build the caller(s) listed in 'callersToAlsoBuild' — generated DLLs of callers are NOT regenerated by an individual object build. You can pass them all to a single 'build' call as a comma-separated 'target' to run them in one MSBuild cycle (saves ~30s of OpenKB per object).";
                }
            }

            _tasks[taskId] = status;

            Task.Run(() => RunBuild(status, action, targets));

            return JsonConvert.SerializeObject(new {
                status = "Accepted",
                message = targets.Count > 1
                    ? $"Batch build started for {targets.Count} objects in a single KB-open cycle. Poll action='status' target=<taskId> for progress."
                    : "Build task started in background. Poll genexus_lifecycle action='status' with target=<taskId> for progress.",
                taskId = taskId,
                targets = targets.Count > 0 ? targets : null,
                callersToAlsoBuild = status.CallersToAlsoBuild,
                hint = status.Hint,
                // Item 28 — EXPERIMENTAL. Surfaces decision outcome when fastIncremental=true.
                fastIncrementalFallback = status.FastIncrementalFallback ? (bool?)true : null,
                fallbackReason = status.FastIncrementalFallbackReason,
                fastIncremental = (fastIncremental && fiDecision != null && !fiDecision.ForceFullBuild)
                    ? new {
                        canSkipDeploy = fiDecision.CanSkipDeploy,
                        canSkipSpecify = fiDecision.CanSkipSpecify,
                        experimental = true
                    }
                    : null,
                _meta = plan != null ? new {
                    buildPlan = new {
                        requested = ParseTargets(target),
                        expanded = plan.Expanded,
                        includeCallees = plan.IncludeCallees,
                        cap = plan.NodeCap
                    }
                } : null
            });
        }

        private static List<string> ParseTargets(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return new List<string>();
            return target
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetOEMCP();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleOutputCP();

        private static Encoding _msbuildEncodingCached;
        private static Encoding ResolveMsbuildEncoding()
        {
            if (_msbuildEncodingCached != null) return _msbuildEncodingCached;
            try
            {
                // Worker runs detached from a console — Console.OutputEncoding falls back to
                // UTF-8 in that mode. MSBuild however emits bytes using the host's OEM /
                // console code page (CP850/CP1252 on PT-BR Windows, CP437 on en-US). Ask
                // Win32 directly so we don't trust the framework's stub: GetConsoleOutputCP
                // → live console CP, GetOEMCP → system-wide OEM CP. Either is right; OEM is
                // a stable fallback when no console is attached.
                uint cp = 0;
                try { cp = GetConsoleOutputCP(); } catch { }
                if (cp == 0 || cp == 65001) { try { cp = GetOEMCP(); } catch { } }
                if (cp != 0)
                {
                    // .NET Framework 4.8 ships all OEM/ANSI code pages natively, so no
                    // CodePagesEncodingProvider registration is required (that lives in the
                    // System.Text.Encoding.CodePages NuGet package, .NET 5+ only).
                    _msbuildEncodingCached = Encoding.GetEncoding((int)cp);
                }
                else
                {
                    _msbuildEncodingCached = Console.OutputEncoding ?? Encoding.UTF8;
                }
            }
            catch
            {
                _msbuildEncodingCached = Encoding.UTF8;
            }
            return _msbuildEncodingCached;
        }

        // Issue #27 item 1 follow-up: the most-recent terminal build outcome,
        // reachable WITHOUT a taskId/jobId. Surfaced in the compact lifecycle
        // status so an agent that lost the job id (or whose async poller wedged)
        // can still answer "did my last build pass?" from a plain status call.
        public static JObject GetLatestBuildSummary()
        {
            BuildTaskStatus latest = null;
            foreach (var t in _tasks.Values)
            {
                if (t == null || !IsTerminalStatus(t.Status)) continue;
                if (latest == null) { latest = t; continue; }
                // Order by end (fallback start) timestamp; ISO-8601 sorts lexicographically.
                string a = t.EndTime ?? t.StartTime ?? "";
                string b = latest.EndTime ?? latest.StartTime ?? "";
                if (string.CompareOrdinal(a, b) > 0) latest = t;
            }
            if (latest == null) return null;
            return new JObject
            {
                ["taskId"] = latest.TaskId,
                ["status"] = latest.Status,
                ["target"] = latest.Target,
                ["errorCount"] = latest.ErrorCount,
                ["warningCount"] = latest.WarningCount,
                ["elapsedSeconds"] = latest.ElapsedSeconds,
                ["endTime"] = latest.EndTime
            };
        }

        public string GetStatus(string taskId, int page = 1, int pageSize = 50, bool compact = false)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                return JsonConvert.SerializeObject(new { tasks = _tasks.Values.OrderByDescending(t => t.StartTime).Take(10) });
            }

            if (_tasks.TryGetValue(taskId, out var status))
            {
                lock (status._lock)
                {
                    if (status.Status == "Running" && status.StartedAt != default(DateTime))
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);

                    // Serialize the status without the full warnings list, then inject paginated warnings
                    var jo = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
                    StripOutputWhileRunning(jo, status.Status);

                    // FR#23: compact=true returns a sibling "warningsAggregated" grouped
                    // by spc/gen/CS/MSB code so the agent sees N occurrences of the same
                    // spc0022 collapsed to one row. The flat "warnings" array stays for
                    // backwards compat with existing consumers.
                    if (compact)
                    {
                        // Memoize the aggregation across polls — invalidate only
                        // when WarningCount changes.
                        if (status.AggregatedWarningsCache == null
                            || status.AggregateMemoForCount != status.WarningCount)
                        {
                            status.AggregatedWarningsCache = BuildOutputShaper.AggregateWarnings(status.Warnings);
                            status.AggregateMemoForCount = status.WarningCount;
                        }
                        jo["warningsAggregated"] = JArray.FromObject(status.AggregatedWarningsCache);
                    }

                    // Replace the flat warnings array with a paginated wrapper
                    var paginatedWarnings = BatchService.BuildStatusPayload(status.Warnings, page, pageSize);
                    jo["warnings"] = paginatedWarnings["warnings"];
                    jo["_meta"] = paginatedWarnings["_meta"];

                    return jo.ToString(Formatting.None);
                }
            }
            return "{\"status\":\"Error\",\"message\": \"Task ID not found\"}";
        }

        // v2.6.6 Stream F: event-driven status wait. Replaces the gateway's 25s
        // polling cap with a worker-side block that wakes on baseline change
        // (Phase / TargetsDone / ErrorCount / WarningCount / terminal Status).
        // Caller passes the previous snapshot string under `since`; the response
        // surfaces the new baseline under `_meta.snapshot` for chaining.
        // - waitSeconds clamped to [0, 600]; 0 = immediate (today's behaviour).
        // - Terminal task → returns immediately regardless of since.
        // - Unknown taskId → returns immediately (Task ID not found).
        // - Baseline mismatch → returns immediately.
        public string GetStatusWait(string taskId, int waitSeconds, string sinceBaseline, int page = 1, int pageSize = 50, bool compact = false)
        {
            if (waitSeconds < 0) waitSeconds = 0;
            if (waitSeconds > 600) waitSeconds = 600;

            // No taskId, or wait disabled → match legacy GetStatus shape.
            if (string.IsNullOrEmpty(taskId) || waitSeconds == 0)
            {
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            BuildTaskStatus status;
            if (!_tasks.TryGetValue(taskId, out status))
            {
                // Unknown taskId — GetStatus emits "Task ID not found"; return immediately.
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            string currentBaseline;
            bool terminal;
            lock (status._lock)
            {
                currentBaseline = status.ComputeBaseline();
                terminal = IsTerminalStatus(status.Status);
            }

            // Terminal → always return now. Baseline mismatch → caller is behind, return now.
            // Empty sinceBaseline means caller has no prior snapshot; surface current state.
            bool baselineDiffers = !string.IsNullOrEmpty(sinceBaseline)
                                   && !string.Equals(sinceBaseline, currentBaseline, StringComparison.Ordinal);
            if (terminal || baselineDiffers || string.IsNullOrEmpty(sinceBaseline))
            {
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            // Wait. ManualResetEventSlim was reset by a previous waiter or never set;
            // we reset BEFORE wait so an in-flight signal racing with us is honoured.
            // (We re-check the baseline after wait so a spurious wake is harmless.)
            try { status.StateChangeSignal.Reset(); } catch { }
            // Re-check baseline AFTER reset: HandleLine might have signalled and we'd
            // miss the edge otherwise.
            string postResetBaseline;
            lock (status._lock) { postResetBaseline = status.ComputeBaseline(); }
            if (!string.Equals(postResetBaseline, currentBaseline, StringComparison.Ordinal)
                || IsTerminalStatus(GetStatusValue(status)))
            {
                return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
            }

            try
            {
                status.StateChangeSignal.Wait(TimeSpan.FromSeconds(waitSeconds));
            }
            catch (ObjectDisposedException)
            {
                // Task pruned during wait — fall through to GetStatus (will report not found).
            }

            return AnnotateWithBaseline(GetStatus(taskId, page, pageSize, compact), taskId);
        }

        private static string GetStatusValue(BuildTaskStatus s)
        {
            lock (s._lock) { return s.Status; }
        }

        // Adds _meta.snapshot=<current baseline> to a status JSON response so the
        // caller can chain status wait calls without recomputing the baseline.
        private string AnnotateWithBaseline(string statusJson, string taskId)
        {
            if (string.IsNullOrEmpty(statusJson) || string.IsNullOrEmpty(taskId)) return statusJson;
            if (!_tasks.TryGetValue(taskId, out var status)) return statusJson;
            try
            {
                var jo = JObject.Parse(statusJson);
                string baseline;
                lock (status._lock) { baseline = status.ComputeBaseline(); }
                var meta = jo["_meta"] as JObject;
                if (meta == null) { meta = new JObject(); jo["_meta"] = meta; }
                meta["snapshot"] = baseline;
                return jo.ToString(Formatting.None);
            }
            catch { return statusJson; }
        }

        // FR#19 (friction-report 2026-05-14): during long-poll each `status` call used to return
        // the full Output blob (often 200+ lines), repeated 3× per build. Drop it while Running
        // — TailLines already gives the live tail in a much smaller surface.
        private static void StripOutputWhileRunning(JObject jo, string status)
        {
            if (!string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)) return;
            jo.Remove("Output");
            jo.Remove("output");
        }

        public string GetResult(string taskId, int page = 1, int pageSize = 50)
        {
            if (string.IsNullOrEmpty(taskId))
                return "{\"status\":\"Error\",\"message\": \"taskId required\"}";

            if (_tasks.TryGetValue(taskId, out var status))
            {
                lock (status._lock)
                {
                    if (status.Status == "Running" && status.StartedAt != default(DateTime))
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);

                    var jo = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
                    StripOutputWhileRunning(jo, status.Status);

                    // Replace the flat errors/items array with a paginated wrapper
                    var paginatedResult = BatchService.BuildResultPayload(status.Errors, page, pageSize);
                    jo["items"] = paginatedResult["items"];
                    jo["_meta"] = paginatedResult["_meta"];

                    return jo.ToString(Formatting.None);
                }
            }
            return "{\"status\":\"Error\",\"message\": \"Task ID not found\"}";
        }

        public string Cancel(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return "{\"status\":\"Error\",\"message\": \"taskId required\"}";
            // FR#7 (friction-report 2026-05-14): the old "Task ID not found" was ambiguous —
            // the operation might have completed and been pruned, or never existed, or
            // expired from the registry. Return a more specific message + hint so the LLM
            // does not silently lose state.
            if (!_tasks.TryGetValue(taskId, out var status))
                return JsonConvert.SerializeObject(new
                {
                    error = "Unknown build taskId",
                    taskId,
                    hint = "Task may have completed and been pruned, or the worker restarted. " +
                           "Call lifecycle action=status without a target to list recent tasks."
                });

            try
            {
                var p = status.Process;
                if (p != null && !p.HasExited)
                {
                    // R9: write the cancelled state BEFORE we go kill children so
                    // the next status poll sees "Cancelled" immediately, even while
                    // the descendant kill is still in flight.
                    status.Status = "Cancelled";
                    status.Phase = "Done";
                    EmitPhaseProgress(status.Phase);
                    status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    try { status.StateChangeSignal.Set(); } catch { }
                    // Friction 2026-05-22: p.Kill() on net48 kills only the parent.
                    // MSBuild spawns N child nodes (/m); orphaned children pile up
                    // as zombies in tasklist after each cancel. Walk the tree and
                    // kill all descendants before the parent — fire-and-forget so
                    // the cancel RPC returns quickly (best-effort cleanup; the
                    // caller has already been told the task is Cancelled).
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { KillProcessTree(p); }
                        catch (Exception ex) { Logger.Warn("[KILL-TREE-BG] " + ex.Message); }
                    });
                    return JsonConvert.SerializeObject(new { status = "Cancelled", taskId = taskId });
                }
                return JsonConvert.SerializeObject(new { status = status.Status, message = "Task already finished" });
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private List<string> TryGetDirectCallers(string target)
        {
            if (string.IsNullOrEmpty(target) || _indexCacheService == null) return null;
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects == null) return null;

                SearchIndex.IndexEntry entry = null;
                if (target.Contains(":")) index.Objects.TryGetValue(target, out entry);
                if (entry == null)
                {
                    var key = index.Objects.Keys.FirstOrDefault(k => k.EndsWith(":" + target, StringComparison.OrdinalIgnoreCase));
                    if (key != null) index.Objects.TryGetValue(key, out entry);
                }
                if (entry == null || entry.CalledBy == null || entry.CalledBy.Count == 0) return null;
                return entry.CalledBy.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
            }
            catch { return null; }
        }

        private void RunBuild(BuildTaskStatus status, string action, List<string> targets)
        {
            string tempFile = null;
            // FR#24: thread-tag the Logger so worker_debug.log carries [phase=...]
            // on every line emitted from this build (and only this build).
            Helpers.Logger.CurrentPhase = "Starting";
            try
            {
                if (_kbService != null)
                {
                    int waits = 0;
                    while (_kbService.IsInitializing && waits < 15) { System.Threading.Thread.Sleep(1000); waits++; }
                }

                string kbPath = GetKBPath();
                if (string.IsNullOrEmpty(kbPath))
                {
                    SetFailure(status, "KB Path not found in Environment (GX_KB_PATH)");
                    return;
                }

                // v2.6.6 Stream D — in-process build path. Reuse the already-open
                // KbService._kb instance + invoke GeneXus MSBuild tasks directly
                // instead of spawning MSBuild.exe (which re-opens the KB out of
                // process, the dominant cost in targeted builds).
                //
                // 2026-05-21 LIVE-TEST FINDING + FIX: ArtechTask's static ctor
                // activates the GxServiceManager process-singleton and throws
                // `GxException: O Service Manager já foi ativado` if another
                // path (KbService.OpenKB → InitializeSdk) activated SM first.
                // Worker boot now warms the ArtechTask cctor BEFORE
                // InitializeSdk (Program.TryWarmupArtechTaskCctor) so the IDE
                // ordering holds and subsequent in-process builds succeed.
                // ON by default; opt-out with GXMCP_INPROCESS_BUILD=0.
                bool useInProcess =
                    !string.Equals(Environment.GetEnvironmentVariable("GXMCP_INPROCESS_BUILD"), "0", StringComparison.OrdinalIgnoreCase)
                    && _kbService != null && _kbService.IsOpen;
                if (useInProcess)
                {
                    status.Phase = "InProcess-Specifying";
                    EmitPhaseProgress(status.Phase);
                    Logger.Info("[BUILD-INPROCESS] taskId=" + status.TaskId
                                + " kb=" + _kbService.GetKbPath()
                                + " targets=" + (targets != null ? string.Join(";", targets) : "<all>"));
                    var sw = Stopwatch.StartNew();
                    bool ok = false;
                    try
                    {
                        ok = InProcessBuildRunner.Run(
                            status, action, targets,
                            (s, l, err) => HandleLine(s, l, err),
                            _kbService.KbObject, _kbService.KbLock,
                            skipFullDeploy: status.SkipFullDeploy,
                            kbPath: _kbService.GetKbPath());
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("[BUILD-INPROCESS] orchestrator threw: " + ex.Message);
                        ok = false;
                    }
                    sw.Stop();
                    Logger.Info("[BUILD-INPROCESS-DONE] taskId=" + status.TaskId
                                + " ok=" + ok + " elapsedMs=" + sw.ElapsedMilliseconds);
                    if (ok)
                    {
                        status.BuildPath = "inproc";
                        // FR#22: still emit shaped output envelope from FullOutput.
                        try
                        {
                            string fullText = status.FullOutput.ToString();
                            string fullLogPath;
                            BuildOutputShaper.TryWriteFullLog(fullText, status.TaskId, out fullLogPath);
                            status.FullLogPath = fullLogPath;
                            status.Output = BuildOutputShaper.Shape(fullText, status.LineCount, fullLogPath);
                        }
                        catch { }

                        status.Status = (status.ErrorCount == 0) ? "Succeeded" : "Failed";
                        status.Phase = "Done";
                        EmitPhaseProgress(status.Phase);
                        status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
                        Logger.Info("Background Build " + status.TaskId + " " + status.Status
                                    + " (inproc, errors=" + status.ErrorCount + ", warnings=" + status.WarningCount
                                    + ", " + status.ElapsedSeconds + "s)");
                        // Item 72 (friction 2026-05-22) — POST webhook on terminal Failed (not PartialSuccess).
                        MaybeNotifyOnFailure(status);
                        return;
                    }
                    Logger.Warn("[BUILD-INPROCESS-FALLBACK] taskId=" + status.TaskId + " falling back to MSBuild.exe spawn");
                }

                status.BuildPath = "msbuild-exe";

                tempFile = Path.Combine(Path.GetTempPath(), "GxBuild_" + Guid.NewGuid().ToString().Substring(0, 8) + ".msbuild");
                string importPath = SecurityElement.Escape(Path.Combine(_gxDir, "Genexus.Tasks.targets"));
                string kbPathEsc = SecurityElement.Escape(kbPath);

                var sb = new StringBuilder();
                sb.AppendLine("<Project DefaultTargets=\"Execute\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
                sb.AppendLine("  <Import Project=\"" + importPath + "\" />");
                sb.AppendLine("  <Target Name=\"Execute\">");
                // Open with Output="IDE" — same flag the GeneXus IDE passes. Without it
                // the standalone msbuild later hits opaque Win32 ERROR_FILE_NOT_FOUND
                // inside the deploy/IIS step ("Atualização de configuração da web").
                sb.AppendLine("    <OpenKnowledgeBase Directory=\"" + kbPathEsc + "\" Output=\"IDE\" />");

                if (action.Equals("Build", StringComparison.OrdinalIgnoreCase) && targets != null && targets.Count > 0)
                {
                    status.TargetsTotal = targets.Count;
                    status.TargetsDone = 0;
                    // IDE-parity build pipeline. The IDE's "Build With This Only" runs
                    // Spec → Gen → .NET compile → Web-app config registration through
                    // <IdeWebBuildAndDeploy> with Output="IDE" + EventsSuspended=true.
                    // We mirror that here for targeted builds so the produced .dll +
                    // HandlerFactory registration land in the same state the IDE leaves
                    // the KB in. SpecifyOneOnly first scopes the work to the requested
                    // targets; IdeWebBuildAndDeploy then completes the chain.
                    //
                    // Why not <BuildOne>? <BuildOne> bundles the same sub-steps but the
                    // IIS-config sub-step inside it explodes with an opaque
                    // "O sistema não pode encontrar o arquivo especificado" when run
                    // from a standalone msbuild process. <IdeWebBuildAndDeploy>
                    // routes the same work through the IDE-style entry point that
                    // resolves correctly.
                    string joined = string.Join(";", targets.Select(t => SecurityElement.Escape(t)));
                    sb.AppendLine("    <SpecifyOneOnly ObjectNames=\"" + joined + "\" />");
                    sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"false\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");
                }
                else if (action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase))
                {
                    // Full force-rebuild — same task the IDE's "Rebuild All" fires.
                    sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"true\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");
                }
                else if (action.Equals("Reorg", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <CheckAndInstallDatabase />");
                else if (action.Equals("Validate", StringComparison.OrdinalIgnoreCase) || action.Equals("Check", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <CheckKnowledgeBase />");
                else if (action.Equals("Sync", StringComparison.OrdinalIgnoreCase))
                {
                    // Sync = full incremental KB build (no force). IDE-style.
                    sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"false\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");
                }
                else
                {
                    sb.AppendLine("    <IdeWebBuildAndDeploy ForceRebuild=\"false\" CompileMains=\"true\" Output=\"IDE\" EventsSuspended=\"true\" />");
                }

                sb.AppendLine("    <CloseKnowledgeBase />");
                sb.AppendLine("  </Target></Project>");
                File.WriteAllText(tempFile, sb.ToString());

                // Use /v:n (normal) so we get per-object progress lines, not /v:q
                var psi = new ProcessStartInfo
                {
                    FileName = _msbuildPath,
                    Arguments = "/nologo /m /v:n /nodeReuse:false /target:Execute \"" + tempFile + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _gxDir,
                    // MSBuild on Windows writes via the system OEM/ANSI code page (PT-BR usually
                    // 850/1252). Reading the streams as UTF-8 mangles accented characters
                    // ("Compila��o", "n�", etc.) which is unreadable for LLM consumers. Pin
                    // both streams to the console's actual output encoding so TailLines/Output
                    // stay legible. Fall back to UTF-8 only when CodePagesEncodingProvider
                    // isn't registered.
                    StandardOutputEncoding = ResolveMsbuildEncoding(),
                    StandardErrorEncoding = ResolveMsbuildEncoding()
                };

                using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    status.Process = process;
                    process.OutputDataReceived += (s, e) => HandleLine(status, e.Data, false);
                    process.ErrorDataReceived  += (s, e) => HandleLine(status, e.Data, true);

                    process.Start();
                    status.Phase = "OpeningKB";
                    EmitPhaseProgress(status.Phase);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    status.ExitCode = process.ExitCode;
                    status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);

                    // FR#22 (v2.6.6 Stream C): full log → disk, shaped envelope → status.
                    string fullText = status.FullOutput.ToString();
                    string fullLogPath;
                    BuildOutputShaper.TryWriteFullLog(fullText, status.TaskId, out fullLogPath);
                    status.FullLogPath = fullLogPath;
                    status.Output = BuildOutputShaper.Shape(fullText, status.LineCount, fullLogPath);

                    status.Phase = "Done";
                    Helpers.Logger.CurrentPhase = "Done";
                    EmitPhaseProgress(status.Phase);

                    if (process.ExitCode == 0 && status.ErrorCount == 0)
                        status.Status = "Succeeded";
                    else
                        status.Status = "Failed";

                    // Friction 2026-05-22: when ErrorCount==0 and ExitCode!=0, the
                    // failure is a late MSBuild step (WebAppConfig, deploy task,
                    // file-missing) that doesn't emit a proper "error <code>:" line.
                    // Parse the raw output for >RO/>E0 markers so the agent gets a
                    // named phase_failure instead of "Failed: 0 errors, 0 warnings".
                    if (status.ErrorCount == 0 && process.ExitCode != 0)
                    {
                        status.PhaseFailure = ExtractPhaseFailure(fullText);
                        if (DidGenerationAndCompilationSucceed(fullText))
                        {
                            status.PartialSuccess = true;
                        }
                    }

                    // Stream F: wake any pending status wait callers.
                    try { status.StateChangeSignal.Set(); } catch { }

                    Logger.Info("Background Build " + status.TaskId + " " + status.Status +
                                " (errors=" + status.ErrorCount + ", warnings=" + status.WarningCount +
                                ", " + status.ElapsedSeconds + "s)");
                    // Item 72 (friction 2026-05-22) — POST webhook on terminal Failed (not PartialSuccess).
                    MaybeNotifyOnFailure(status);
                }
            }
            catch (Exception ex)
            {
                SetFailure(status, ex.Message);
                Logger.Error("Background Build " + status.TaskId + " EXCEPTION: " + ex.Message);
            }
            finally
            {
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                status.Process = null;
                // Don't leak the phase tag onto unrelated work on this thread.
                Helpers.Logger.CurrentPhase = null;
            }
        }

        private void HandleLine(BuildTaskStatus status, string line, bool isError)
        {
            if (line == null) return;
            lock (status._lock)
            {
                // v2.6.6 Stream F: capture pre-mutation baseline so we can fire
                // the StateChangeSignal once at the end IFF anything that affects
                // the wait-baseline actually changed (phase / counts / TargetsDone).
                string baselineBefore = status.ComputeBaseline();
                try
                {

                status.LineCount++;
                status.LastLine = line;
                status.FullOutput.AppendLine(line);

                // FR#12: keep noise out of TailLines but always preserve it in FullOutput.
                // Errors/warnings/phase-change lines never count as noise.
                bool isNoise = _rxModuleCopyNoise.IsMatch(line) &&
                               !_rxError.IsMatch(line) &&
                               !_rxWarning.IsMatch(line);
                if (!isNoise)
                {
                    status.TailLines.Add(line);
                    if (status.TailLines.Count > TailBufferSize) status.TailLines.RemoveAt(0);
                }

                if (_rxOpenKb.IsMatch(line))      { status.Phase = "OpeningKB"; EmitPhaseProgress(status.Phase); return; }
                var m = _rxSpecifying.Match(line); if (m.Success) { status.Phase = "Specifying"; EmitPhaseProgress(status.Phase); status.CurrentObject = m.Groups["obj"].Value.Trim(); return; }
                m = _rxGenerating.Match(line);     if (m.Success) { status.Phase = "Generating"; EmitPhaseProgress(status.Phase); status.CurrentObject = m.Groups["obj"].Value.Trim(); return; }
                if (_rxCompiling.IsMatch(line))   { status.Phase = "Compiling"; EmitPhaseProgress(status.Phase); return; }
                if (_rxBuildDone.IsMatch(line))   { status.Phase = "Finishing"; EmitPhaseProgress(status.Phase); }
                if (_rxBuildOneEnd.IsMatch(line) && status.TargetsTotal.HasValue)
                {
                    status.TargetsDone = (status.TargetsDone ?? 0) + 1;
                }

                if (_rxError.IsMatch(line))
                {
                    // v2.6.6 Stream E (FR#9): CS2001 for "<obj>_bc.cs" where the
                    // underlying Transaction has BC disabled (or doesn't exist) is
                    // an orphan — the stale generated source file is not the
                    // requested target's fault. Demote it to a warning so the
                    // build status reflects the actual failure mode.
                    if (IsBcOrphanError(line, _indexCacheService))
                    {
                        status.DemotedErrors++;
                        status.WarningCount++;
                        if (status.Warnings.Count < 50) status.Warnings.Add("[demoted-bc-orphan] " + line.Trim());
                        return;
                    }

                    status.ErrorCount++;

                    // FR#21 (Stream C followup): rewrite the GxBuild_*.msbuild(N,M)
                    // location to [gx-object=... phase=...] so the agent gets an
                    // actionable target instead of a temp-file address. Keep the
                    // raw form too — ErrorsDetailed carries both for debugging.
                    string rawErr = line.Trim();
                    string rewritten;
                    bool didRewrite = GxMcp.Worker.Helpers.BuildOutputShaper.TryRewriteErrorLocation(
                        rawErr, status.CurrentObject, status.Phase, out rewritten);
                    string surface = didRewrite ? rewritten : rawErr;
                    if (status.Errors.Count < 50) status.Errors.Add(surface);
                    if (status.ErrorsDetailed.Count < 50)
                    {
                        status.ErrorsDetailed.Add(new ErrorDetail
                        {
                            raw = rawErr,
                            rewritten = didRewrite ? rewritten : null,
                            phase = status.Phase,
                            gxObject = status.CurrentObject
                        });
                    }

                    // CS0246: missing type — wrapper class for an un-built object.
                    var cs0246 = _rxCs0246Missing.Match(line);
                    if (cs0246.Success)
                    {
                        var norm = NormalizeMissingObjectName(cs0246.Groups[1].Value, _indexCacheService);
                        if (!string.IsNullOrEmpty(norm) && status.SuggestedRebuildTargets.Count < 50)
                            status.SuggestedRebuildTargets.Add(norm);
                    }
                    // CS2001: missing source file — extract <obj> from "<obj>_bc.cs" / "<obj>.cs".
                    var cs2001 = _rxCs2001Missing.Match(line);
                    if (cs2001.Success)
                    {
                        var file = cs2001.Groups[1].Value;
                        var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
                        var bare = slash >= 0 ? file.Substring(slash + 1) : file;
                        if (bare.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            bare = bare.Substring(0, bare.Length - 3);
                        var norm = NormalizeMissingObjectName(bare, _indexCacheService);
                        if (!string.IsNullOrEmpty(norm) && status.SuggestedRebuildTargets.Count < 50)
                            status.SuggestedRebuildTargets.Add(norm);
                    }
                }
                else if (_rxWarning.IsMatch(line))
                {
                    status.WarningCount++;
                    if (status.Warnings.Count < 50) status.Warnings.Add(line.Trim());
                }
                }
                finally
                {
                    // Stream F: fire signal exactly once per HandleLine when the
                    // wait-baseline shifted. Reset+Set so wait callers see an edge
                    // even if another thread is still inside Wait().
                    if (!string.Equals(baselineBefore, status.ComputeBaseline(), StringComparison.Ordinal))
                    {
                        try { status.StateChangeSignal.Set(); } catch { }
                    }
                }
            }
        }

        private void SetFailure(BuildTaskStatus status, string error)
        {
            status.Status = "Error";
            status.Phase = "Done";
            EmitPhaseProgress(status.Phase);
            status.Error = error;
            status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            status.ElapsedSeconds = Math.Round((DateTime.UtcNow - status.StartedAt).TotalSeconds, 1);
            try { status.StateChangeSignal.Set(); } catch { }
        }

        public string GetKBPath()
        {
            return Environment.GetEnvironmentVariable("GX_KB_PATH") ?? "";
        }

        // Item 72 (friction 2026-05-22) — fire-and-forget POST to the configured
        // Slack/Discord webhook on terminal Failed state. PartialSuccess is NOT
        // notified (the build's downstream packaging step failed but the DLLs
        // compiled — usually the agent doesn't care). No retries, no auth — by
        // design (the spec asked for ~30 lines).
        internal static void MaybeNotifyOnFailure(BuildTaskStatus status)
        {
            if (status == null) return;
            string url = status.NotifyOnFailureUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!string.Equals(status.Status, "Failed", StringComparison.OrdinalIgnoreCase)) return;
            if (status.PartialSuccess == true) return;

            try
            {
                var payload = BuildNotificationPayload(status);
                PostWebhook(url, payload);
            }
            catch (Exception ex) { Logger.Warn("[NOTIFY-WEBHOOK] " + ex.Message); }
        }

        // Test seam: pure payload builder, no IO.
        internal static string BuildNotificationPayload(BuildTaskStatus status)
        {
            var errorsArr = new JArray();
            if (status.Errors != null)
            {
                foreach (var e in status.Errors.Take(10)) errorsArr.Add(e);
            }
            var detailedArr = new JArray();
            if (status.ErrorsDetailed != null)
            {
                foreach (var d in status.ErrorsDetailed.Take(5))
                {
                    detailedArr.Add(JObject.FromObject(d));
                }
            }
            var jo = new JObject
            {
                ["kb"] = Environment.GetEnvironmentVariable("GX_KB_PATH") ?? string.Empty,
                ["target"] = status.Target ?? string.Empty,
                ["jobId"] = status.TaskId ?? string.Empty,
                ["durationSec"] = status.ElapsedSeconds ?? 0.0,
                ["errors"] = errorsArr,
                ["errorsDetailedHead"] = detailedArr
            };
            return jo.ToString(Formatting.None);
        }

        private static void PostWebhook(string url, string jsonBody)
        {
            // Synchronous one-shot post; the failure path is rare and we don't
            // want to wedge the build thread on a slow network. 5s hard cap.
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 5000;
            byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
            req.ContentLength = bytes.Length;
            using (var stream = req.GetRequestStream()) { stream.Write(bytes, 0, bytes.Length); }
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            {
                // Drain and discard — we don't care about the body.
                using (var rs = resp.GetResponseStream())
                {
                    if (rs != null) { var buf = new byte[1024]; while (rs.Read(buf, 0, buf.Length) > 0) { } }
                }
            }
        }

        // Item 43 (friction 2026-05-22) — DDL diff/preview pre-reorg. The SDK
        // doesn't expose a clean "compute reorg plan, do not execute" entry
        // point on net48 (CheckAndInstallDatabase always touches the live DB).
        // Stub: return an empty plan + a hint pointing at the live reorg.
        // Refined when the SDK surface probe finds a non-mutating entry point.
        public string ReorgPreview(string target)
        {
            return new JObject
            {
                ["status"] = "Stub",
                ["target"] = target ?? string.Empty,
                ["ddl"] = new JArray(),
                ["summary"] = new JObject
                {
                    ["tables_added"] = 0,
                    ["tables_changed"] = 0,
                    ["columns_added"] = 0,
                    ["columns_dropped"] = 0
                },
                ["note"] = "reorg_preview is a stub. The CheckAndInstallDatabase MSBuild task that powers genexus_lifecycle action=reorg executes against the live DB; a non-mutating SDK plan API has not yet been wired. Run action=reorg on a non-production environment to obtain the actual ALTER TABLE statements, or use action=validate-kb to surface schema-drift findings without touching the DB."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
