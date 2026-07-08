using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    class Program
    {
        private const string McpAxiSchemaVersion = "mcp-axi/2";
        private static WorkerPool? _workerPool;
        private static KbResolver? _kbResolver;
        // Set per-call at the top of ProcessMcpRequest; SendWorkerCommandAsync reads it
        // to route the command to the correct WorkerProcess in the pool.
        private static readonly AsyncLocal<KbHandle?> _currentKb = new AsyncLocal<KbHandle?>();
        // Legacy single-worker accessor: returns the worker for the AsyncLocal KB if set,
        // otherwise the worker for the DefaultKb (acquiring it lazily).
        private static async Task<WorkerProcess> GetActiveWorkerAsync()
        {
            if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
            KbHandle? kb = _currentKb.Value;
            if (kb == null)
            {
                // Fall back to default for callers outside a tool-call context (warmup, etc.).
                kb = _kbResolver!.Resolve(null, _workerPool.ListOpen(), _workerPool.ListKnown());
            }
            return await _workerPool.AcquireAsync(kb, CancellationToken.None);
        }
        internal static WorkerPool? GetWorkerPool() => _workerPool;
        internal static KbResolver? GetKbResolver() => _kbResolver;

        // Tools that are not KB-scoped: routed by the gateway itself or operate on global state.
        // Must mirror the exclusion list in tool_definitions.json (no `kb` param on these).
        private static readonly HashSet<string> _metaTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_kb", "genexus_whoami", "genexus_logs", "genexus_doc", "genexus_worker_reload", "genexus_recipe"
        };
        private static bool IsMetaTool(string name) => _metaTools.Contains(name);
        private sealed class PendingWorkerRequest
        {
            public TaskCompletionSource<string> CompletionSource { get; init; } = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            public string ToolName { get; init; } = "unknown";
            public string CorrelationId { get; init; } = string.Empty;
            public string? OperationId { get; init; }
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
            /// <summary>Worker (KB) the command was routed to; used to abort pending on per-worker crash.</summary>
            public string? WorkerAlias { get; init; }
        }

        private static ConcurrentDictionary<string, PendingWorkerRequest> _pendingRequests = new ConcurrentDictionary<string, PendingWorkerRequest>();
        private static ConcurrentDictionary<string, JObject> _semanticCache = new ConcurrentDictionary<string, JObject>();
        private static HttpSessionRegistry _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(10));
        private static IdempotencyCache _idempotencyCache = new IdempotencyCache(15, 1000);
        private static readonly OperationTracker _operationTracker = new OperationTracker(TimeSpan.FromMinutes(60));
        internal static OperationTracker OperationTracker => _operationTracker;

        // User-macro storage: <configRoot>/recipes/user-macros/<name>.json.
        // Same configRoot used by sandboxes (Configuration.CurrentConfigPath dir,
        // falling back to AppContext.BaseDirectory).
        internal static string GetUserMacroDir()
        {
            string configDir = !string.IsNullOrEmpty(Configuration.CurrentConfigPath)
                ? System.IO.Path.GetDirectoryName(Configuration.CurrentConfigPath!)!
                : AppContext.BaseDirectory;
            return System.IO.Path.Combine(configDir, "recipes", "user-macros");
        }
        internal static BackgroundJobRegistry JobRegistry = new BackgroundJobRegistry(600);
        private static int _workerWarmupStarted;
        private static int _indexBootstrapStarted;
        // v2.6.8 (review C6): incremented before any planned worker exit
        // (worker_reload, KB switch, shutdown) so OnWorkerExited can skip the
        // eager respawn — RestartWorker is already orchestrating a fresh spawn.
        // Refcounted so concurrent planned exits don't race.
        private static int _plannedExitSuppression = 0;
        private sealed class RespawnSuppressionScope : IDisposable
        {
            public void Dispose() => System.Threading.Interlocked.Decrement(ref _plannedExitSuppression);
        }
        internal static IDisposable SuppressEagerRespawn()
        {
            System.Threading.Interlocked.Increment(ref _plannedExitSuppression);
            return new RespawnSuppressionScope();
        }
        private static bool IsEagerRespawnSuppressed() =>
            System.Threading.Volatile.Read(ref _plannedExitSuppression) > 0;

        // issue #26 P1: last eager-respawn failure per KB alias. When eager respawn
        // exhausts its retries, we record (time, error) here so whoami/health can report
        // an honest "respawn_failed" with the real cause + a recovery hint, instead of a
        // perpetual, misleading "respawning" while no process is actually coming up.
        private static readonly ConcurrentDictionary<string, (DateTime AtUtc, string Error)> _respawnFailures =
            new ConcurrentDictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private static bool _stdioActive;
        private static readonly TimeSpan _pendingRequestRetention = TimeSpan.FromMinutes(65);
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gateway_debug.log");
        // Rotation: when the log exceeds this many bytes the current file is renamed to
        // gateway_debug.log.1 and a fresh file is opened.  Only two files are kept.
        private const long _logRotateBytes = 10 * 1024 * 1024; // 10 MB
        private static StreamWriter? _logWriter;
        private static readonly string[] _defaultLocalOrigins = new[]
        {
            "http://localhost",
            "http://127.0.0.1",
            "https://localhost",
            "https://127.0.0.1"
        };

        private static readonly object _logLock = new object();
        private static readonly System.Threading.SemaphoreSlim _stdoutGate = new System.Threading.SemaphoreSlim(1, 1);
        private static Configuration? _activeConfig;

        public static void TryWriteStderr(string message)
        {
            Log(message);
        }

        public static async Task TryWriteStdout(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            try {
                await _stdoutGate.WaitAsync().ConfigureAwait(false);
                try {
                    await Console.Out.WriteLineAsync(msg);
                    await Console.Out.FlushAsync();
                } finally {
                    _stdoutGate.Release();
                }
            } catch { }
        }

        private static void InitializeLogging()
        {
            try
            {
                lock (_logLock)
                {
                    _logWriter = new StreamWriter(_logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
                }
            }
            catch { /* fall back to no-op if the file is unavailable */ }
            Log("=== Gateway starting (Stdio Mode) ===");
        }

        public static void Log(string msg)
        {
            try {
                lock (_logLock) {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n";
                    if (_logWriter == null)
                    {
                        // InitializeLogging() has not been called (e.g. in tests).
                        // Fall back to the direct append-all path so the file is
                        // always written and existing tests that snapshot file length
                        // continue to work.
                        File.AppendAllText(_logPath, line);
                        return;
                    }
                    // Size-based rotation: rename current to .1 (overwriting any previous .1)
                    // then open a fresh log file.
                    try
                    {
                        var fi = new FileInfo(_logPath);
                        if (fi.Exists && fi.Length > _logRotateBytes)
                        {
                            _logWriter.Dispose();
                            _logWriter = null;
                            string rotated = _logPath + ".1";
                            if (File.Exists(rotated)) File.Delete(rotated);
                            File.Move(_logPath, rotated);
                            _logWriter = new StreamWriter(_logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
                        }
                    }
                    catch { /* rotation failure is non-fatal; keep using existing writer */ }
                    _logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}");
                }
            } catch { }
        }

        // Keeps the master's gateway lease fresh. Paced by LeaseHeartbeatInterval,
        // which is deliberately well under GatewayProcessLease.LeaseStaleAfter — see
        // that constant for why the two MUST NOT drift apart. (Previously the lease
        // was only refreshed by the 1-minute session-cleanup loop, leaving a ~15s
        // window each minute where a live master looked stale and got killed by a
        // newly-spawned gateway → intermittent "Transport closed".)
        private static async Task RunLeaseHeartbeatLoop(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(GatewayProcessLease.LeaseHeartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    if (_activeConfig != null)
                    {
                        try { GatewayProcessLease.RefreshCurrentProcess(_activeConfig); }
                        catch (Exception ex) { Log($"[Gateway] Lease heartbeat failed: {ex.Message}"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task RunSessionCleanupLoop(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    int removed = _httpSessions.CleanupExpired();
                    if (removed > 0)
                    {
                        Log($"[HTTP] Removed {removed} expired MCP session(s).");
                    }

                    _operationTracker.CleanupExpired();
                    int stalePending = CleanupStalePendingRequests();
                    if (stalePending > 0)
                    {
                        Log($"[Gateway] Removed {stalePending} stale pending worker request(s).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static int CleanupStalePendingRequests()
        {
            int removed = 0;
            DateTime cutoff = DateTime.UtcNow - _pendingRequestRetention;
            foreach (var kvp in _pendingRequests.ToArray())
            {
                if (kvp.Value.CreatedAtUtc > cutoff)
                {
                    continue;
                }

                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                {
                    _operationTracker.MarkFailedByRequest(kvp.Key, "Pending worker request expired before completion.");
                    pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = kvp.Key,
                        error = new
                        {
                            code = -32603,
                            message = "Pending worker request expired before completion."
                        }
                    }));
                    removed++;
                }
            }

            return removed;
        }

        // Logger names whose notifications/message events are safe to surface to
        // stdio AI clients (Antigravity / Claude Desktop / Cursor surface these in
        // chat or system messages). Internal operational telemetry — "Operation X
        // started/finished", "Worker warmup started" — stays out because agents
        // interpret it as KB state changes and get confused.
        private static readonly HashSet<string> _stdioLoggerAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "indexing",
            "update-check"
        };

        private static void BroadcastNotification(string method, object payload)
        {
            _ = Task.Run(() => {
                try {
                    string json = JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        method,
                        @params = payload
                    });

                    if (ShouldForwardNotificationToStdio(method, payload))
                    {
                        EmitStdioNotification(json);
                    }

                    foreach (var session in _httpSessions.ActiveSessions)
                    {
                        QueueSessionMessage(session, json);
                    }
                } catch (Exception ex) {
                    Log($"[Broadcast] Error: {ex.Message}");
                }
            });
        }

        // Forward a pre-serialized JSON-RPC notification envelope to the stdio
        // client (Claude Desktop / Cursor / Antigravity). Required because
        // BroadcastNotification only reaches HTTP sessions otherwise.
        private static void EmitStdioNotification(string json)
        {
            if (!_stdioActive) return;
            _ = TryWriteStdout(json);
        }

        private static bool ShouldForwardNotificationToStdio(string method, object? payload)
        {
            if (method == "notifications/progress") return true;
            if (method != "notifications/message") return false;

            // notifications/message is the loudest channel — be strict. Only surface
            // warnings/errors (user-actionable) or explicit allowlisted loggers
            // (indexing cold-start, update-check). Routine operation start/finish
            // events stay HTTP-only.
            string? level = null, logger = null;
            try
            {
                JObject? jp = payload as JObject;
                if (jp == null && payload is JToken token) jp = token as JObject;
                if (jp == null && payload != null) jp = JObject.FromObject(payload);
                if (jp == null) return false;
                level = jp["level"]?.ToString();
                logger = jp["logger"]?.ToString();
            }
            catch { return false; }

            if (!string.IsNullOrEmpty(logger) && _stdioLoggerAllowlist.Contains(logger)) return true;
            if (string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void BroadcastToolsListChanged(string reason)
        {
            BroadcastNotification("notifications/tools/list_changed", new
            {
                reason,
                timestamp = DateTime.UtcNow
            });
        }

        private static void BroadcastResourcesListChanged(string reason)
        {
            BroadcastNotification("notifications/resources/list_changed", new
            {
                reason,
                timestamp = DateTime.UtcNow
            });
        }

        private static void BroadcastResourceUpdated(string uri, string reason)
        {
            BroadcastNotification("notifications/resources/updated", new
            {
                uri,
                reason,
                timestamp = DateTime.UtcNow
            });
        }

        internal static string? DetectGeneXusVersion(string? installationPath)
        {
            if (string.IsNullOrWhiteSpace(installationPath)) return null;
            string[] candidates = {
                Path.Combine(installationPath, "version.txt"),
                Path.Combine(installationPath, "Version.txt"),
                Path.Combine(installationPath, "GeneXus.version")
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    string raw = File.ReadAllText(candidate).Trim();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    }
                }
                catch { }
            }
            // Fallback: read FileVersion from GeneXus.exe metadata (standard install layout)
            try
            {
                string exePath = Path.Combine(installationPath, "GeneXus.exe");
                if (File.Exists(exePath))
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    string? version = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return version.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        internal const string SupportedGeneXusMajor = "18";

        private static void LogGeneXusVersionCheck(Configuration config)
        {
            string? gxPath = config.GeneXus?.InstallationPath;
            string? detected = DetectGeneXusVersion(gxPath);
            if (string.IsNullOrEmpty(gxPath))
            {
                Log("[Gateway] GeneXus installation path is not configured.");
                return;
            }
            if (detected == null)
            {
                Log($"[Gateway] GeneXus version not detected at '{gxPath}' (no version.txt). Target major: {SupportedGeneXusMajor}.");
                return;
            }
            Log($"[Gateway] Detected GeneXus version: {detected} (target major: {SupportedGeneXusMajor}).");
            if (!detected.StartsWith(SupportedGeneXusMajor, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[Gateway] WARNING: detected GeneXus version '{detected}' may not match MCP target major '{SupportedGeneXusMajor}'. Some tools may behave unexpectedly.");
            }
        }

        // v2.3.8 Task 1.2: gateway-side mirror of the worker's IndexCacheService.GetState().
        // The worker is the source of truth (CommandDispatcher.GetIndexState), but the gateway
        // keeps a last-known snapshot so `whoami` returns instantly without round-tripping
        // and works even when no worker is currently attached (default Cold/0).
        //
        // IMPORTANT: the ONLY production writer is TryRefreshIndexStateFromWorkerAsync (called
        // from whoami and from the SDK-bound short-circuit's self-heal path). There is no
        // background push from the worker, so this mirror can lag reality until something
        // refreshes it — the short-circuit below compensates with a synchronous re-check rather
        // than trusting a stale snapshot. Do not assume search/list/lifecycle calls keep it warm.
        private sealed class IndexStateSnapshot
        {
            public string Status = "Cold";
            public int TotalObjects;
            public DateTime? LastIndexedAt;
            public double? Progress;
            public int? EtaMs;
            public DateTime RefreshedAtUtc = DateTime.MinValue;
            // PERFORMANCE (W-M2): mirror the worker's flush-failure telemetry so whoami
            // surfaces silent snapshot persistence failures (disk full / permission).
            public int FlushFailuresConsecutive;
            public DateTime? FlushLastSuccessUtc;
            public string? FlushLastError;
            // v2.6.8: top-5 recently-changed projection from the worker's
            // in-memory index. Cached so subsequent whoami calls don't pay
            // another round-trip — refreshed every TryRefreshIndexStateFromWorkerAsync.
            public JArray? RecentlyChanged;
        }
        private static IndexStateSnapshot _lastKnownIndexState = new IndexStateSnapshot();
        private static readonly object _lastKnownIndexStateLock = new object();

        // Per-KB database configuration (DataStores). Read once per KB via the worker
        // on first whoami after open; cached until the gateway restarts or KB switches.
        // DataStore config is stable across a session — re-fetching every whoami is
        // pure waste. Keyed by normalized KB alias.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JObject> _databaseInfoByKb
            = new System.Collections.Concurrent.ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        internal static void UpdateLastKnownIndexState(string status, int totalObjects, DateTime? lastIndexedAt, double? progress, int? etaMs,
            int flushFailuresConsecutive = 0, DateTime? flushLastSuccessUtc = null, string? flushLastError = null,
            JArray? recentlyChanged = null)
        {
            lock (_lastKnownIndexStateLock)
            {
                _lastKnownIndexState = new IndexStateSnapshot
                {
                    Status = string.IsNullOrEmpty(status) ? "Cold" : status,
                    TotalObjects = totalObjects,
                    LastIndexedAt = lastIndexedAt,
                    Progress = progress,
                    EtaMs = etaMs,
                    RefreshedAtUtc = DateTime.UtcNow,
                    FlushFailuresConsecutive = flushFailuresConsecutive,
                    FlushLastSuccessUtc = flushLastSuccessUtc,
                    FlushLastError = flushLastError,
                    // Preserve prior recentlyChanged when the caller doesn't pass a fresh
                    // value — search/lifecycle pushes update telemetry without it.
                    RecentlyChanged = recentlyChanged ?? _lastKnownIndexState?.RecentlyChanged
                };
            }
            // Keep AutoTypeInjector's name→type map warm whenever we get fresh index data.
            if (recentlyChanged != null)
                AutoTypeInjector.RefreshFromRecentlyChanged(recentlyChanged);
        }

        // True when the index has enough populated entries for SDK-bound reads/edits.
        // v2.6.9 perf: accept UltraLiteReady + LiteReady + Enriching as usable too. The lite
        // pass streams partial snapshots every 500-1000 objects (UltraLiteReady), then completes
        // (LiteReady), then enrichment runs in background (Enriching → Ready). All four states
        // have name/type/path/lifecycle populated — enough for list_objects, query, inspect,
        // read, explain, edit. mode=impact does on-demand per-target enrichment. (Note: "Complete"
        // is KbService._currentStatus vocabulary, NOT an IndexCacheService state — the worker
        // publishes "Ready" here when indexing finishes; the two must not be conflated.)
        private static bool IsIndexUsableForReads(IndexStateSnapshot snap)
        {
            if (snap == null || snap.TotalObjects <= 0) return false;
            string s = snap.Status ?? string.Empty;
            return string.Equals(s, "Ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "LiteReady", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Enriching", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "UltraLiteReady", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildIndexBlock()
        {
            IndexStateSnapshot snap;
            lock (_lastKnownIndexStateLock) { snap = _lastKnownIndexState; }
            return new JObject
            {
                ["status"] = snap.Status,
                ["totalObjects"] = snap.TotalObjects,
                ["lastIndexedAt"] = snap.LastIndexedAt.HasValue
                    ? (JToken)snap.LastIndexedAt.Value.ToUniversalTime().ToString("o")
                    : JValue.CreateNull(),
                ["progress"] = snap.Progress.HasValue ? (JToken)snap.Progress.Value : JValue.CreateNull(),
                ["etaMs"] = snap.EtaMs.HasValue ? (JToken)snap.EtaMs.Value : JValue.CreateNull(),
                // PERFORMANCE (W-M2): expose flush health so a degraded snapshot is
                // visible without combing through worker_debug.log.
                ["flushHealth"] = new JObject
                {
                    ["consecutiveFailures"] = snap.FlushFailuresConsecutive,
                    ["lastSuccessUtc"] = snap.FlushLastSuccessUtc.HasValue
                        ? (JToken)snap.FlushLastSuccessUtc.Value.ToUniversalTime().ToString("o")
                        : JValue.CreateNull(),
                    ["lastError"] = snap.FlushLastError != null ? (JToken)snap.FlushLastError : JValue.CreateNull()
                },
                // v2.6.8: top-5 recently-changed objects — set only when the worker
                // had populated lifecycle data to surface. Omitted otherwise so the
                // whoami payload stays tight for cold/legacy KBs.
                ["recentlyChanged"] = snap.RecentlyChanged != null
                    ? (JToken)snap.RecentlyChanged.DeepClone()
                    : JValue.CreateNull()
            };
        }

        // v2.3.8 Task 1.2: live-fetch index state from worker (source of truth).
        // Called from BuildWhoamiPayloadAsync; on success refreshes _lastKnownIndexState
        // so subsequent timeouts/worker outages still see the last good value.
        // Short timeout (1500ms): whoami is supposed to be near-instant.
        private static async Task<bool> TryRefreshIndexStateFromWorkerAsync(int timeoutMs = 1500)
        {
            if (_workerPool == null) return false;
            try
            {
                var cmd = new JObject
                {
                    ["module"] = "kb",
                    ["action"] = "GetIndexState"
                };
                JObject? env = await SendWorkerCommandAsync(
                    cmd,
                    timeoutMs,
                    "Timeout fetching index state for whoami",
                    e => e,
                    (_, correlationId) => new JObject { ["__timeout"] = true, ["correlationId"] = correlationId },
                    toolName: "genexus_whoami",
                    toolArgs: null,
                    trackOperation: false);

                if (env == null) return false;
                if (env["__timeout"] != null) return false;
                JObject? result = env["result"] as JObject;
                if (result == null) return false;

                return ApplyIndexStateFromWorkerResult(result);
            }
            catch (Exception ex)
            {
                Log($"[Whoami] index state fetch failed; using cached snapshot: {ex.Message}");
                return false;
            }
        }

        // Parses the worker's GetIndexState payload and refreshes _lastKnownIndexState.
        // Extracted from TryRefreshIndexStateFromWorkerAsync so the envelope-shape handling
        // is unit-testable without spinning up a worker. `workerResult` is env["result"] —
        // the JSON-RPC result the worker returned for the GetIndexState command.
        // Returns true when a state was applied.
        internal static bool ApplyIndexStateFromWorkerResult(JObject workerResult)
        {
            if (workerResult == null) return false;

            // The worker may wrap the JSON-RPC result as either a JObject or a string payload.
            JObject state = workerResult;
            if (state["indexStatus"] == null && state["status"] == null && state["data"] is JValue dv && dv.Type == JTokenType.String)
            {
                try { state = JObject.Parse(dv.ToString()); } catch { return false; }
            }

            // v2.8.1 — v2.8.0 (commit e639b04) wrapped the worker's GetIndexState reply in the
            // canonical McpResponse.Ok envelope { status:"ok", code:"IndexState", result:{ … } },
            // which nests the real index payload one level below env["result"]. Without descending
            // we read the envelope's status:"ok" / totalObjects:null (→0), so the gateway's
            // SDK-bound short-circuit fast-failed every read/query/list with IndexNotReady even
            // though the worker's index was Ready. Descend only when the current level lacks
            // indexStatus but the nested result carries it — pre-2.8.0 flat replies pass through.
            if (state["indexStatus"] == null && state["result"] is JObject canonicalInner && canonicalInner["indexStatus"] != null)
            {
                state = canonicalInner;
            }

            string status = state["indexStatus"]?.ToString() ?? state["status"]?.ToString() ?? "Cold";
            int totalObjects = state["totalObjects"]?.ToObject<int?>() ?? 0;
            DateTime? lastIndexedAt = null;
            var liTok = state["lastIndexedAt"];
            if (liTok != null && liTok.Type != JTokenType.Null)
            {
                if (DateTime.TryParse(liTok.ToString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    lastIndexedAt = parsed;
                }
            }
            double? progress = state["progress"]?.ToObject<double?>();
            int? etaMs = state["etaMs"]?.ToObject<int?>();
            int flushFailuresConsecutive = state["flushFailuresConsecutive"]?.ToObject<int?>() ?? 0;
            DateTime? flushLastSuccessUtc = null;
            var fls = state["flushLastSuccessUtc"];
            if (fls != null && fls.Type != JTokenType.Null &&
                DateTime.TryParse(fls.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var flsParsed))
            {
                flushLastSuccessUtc = flsParsed;
            }
            string? flushLastError = state["flushLastError"]?.Type == JTokenType.Null ? null : state["flushLastError"]?.ToString();

            JArray? recentlyChanged = state["recentlyChanged"] as JArray;
            UpdateLastKnownIndexState(status, totalObjects, lastIndexedAt, progress, etaMs,
                flushFailuresConsecutive, flushLastSuccessUtc, flushLastError, recentlyChanged);
            return true;
        }

        private static async Task<bool> TryRefreshDatabaseInfoFromWorkerAsync(int timeoutMs = 800)
        {
            if (_workerPool == null) return false;
            KbHandle? kb = _currentKb.Value;
            string? alias = kb?.NormalizedAlias;
            if (string.IsNullOrEmpty(alias))
            {
                // whoami is a meta-tool, so the per-request KB isn't resolved and _currentKb is
                // null — which left the database block stuck at "Pending" because this refresh
                // returned before ever dispatching GetDatabaseInfo. (The index refresh isn't
                // affected: it doesn't key by alias.) Fall back to the single open KB so the
                // block populates; with multiple KBs open there's no unambiguous "the" KB, so skip.
                try
                {
                    var open = _workerPool.ListOpen();
                    if (open != null && open.Count == 1) alias = open[0].NormalizedAlias;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(alias)) return false;
            if (_databaseInfoByKb.ContainsKey(alias!)) return true;
            try
            {
                var cmd = new JObject { ["module"] = "kb", ["action"] = "GetDatabaseInfo" };
                JObject? env = await SendWorkerCommandAsync(
                    cmd, timeoutMs,
                    "Timeout fetching database info for whoami",
                    e => e,
                    (_, correlationId) => new JObject { ["__timeout"] = true, ["correlationId"] = correlationId },
                    toolName: "genexus_whoami",
                    toolArgs: null,
                    trackOperation: false);

                JObject? info = ExtractDatabaseInfoFromWorkerResult(env);
                if (info == null) return false;
                _databaseInfoByKb[alias!] = info;
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Whoami] database info fetch failed: {ex.Message}");
                return false;
            }
        }

        // Extracts the database-info payload from a worker GetDatabaseInfo reply, or null when the
        // reply didn't signal success. `env` is the JSON-RPC response (its `result` holds the
        // worker's payload). Extracted from TryRefreshDatabaseInfoFromWorkerAsync so the envelope
        // handling is unit-testable without a worker.
        //
        // v2.8.1 — v2.8.0 (commit e639b04) wrapped GetDatabaseInfo in the canonical McpResponse.Ok
        // envelope { status:"ok", code:"DatabaseInfoCollected", result:{ default, … } }. whoami's
        // database block and the SQL-dialect injection read the store fields (default/additional)
        // off the top level, so descend into the nested result. Pre-2.8.0 flat replies (store
        // fields beside status, no nested result) pass through unchanged.
        internal static JObject? ExtractDatabaseInfoFromWorkerResult(JObject? env)
        {
            if (env == null || env["__timeout"] != null) return null;
            JObject? result = env["result"] as JObject;
            if (result == null) return null;

            JObject info = result;
            if (result["status"] == null && result["data"] is JValue dv && dv.Type == JTokenType.String)
            {
                try { info = JObject.Parse(dv.ToString()); } catch { return null; }
            }
            // success is signalled by env["status"] == "ok"; legacy envelope carried
            // status:"Success" inside result. Accept either form. Check the OUTER envelope
            // before unwrapping, since the canonical "status" lives there.
            bool dbInfoOk = string.Equals(env["status"]?.ToString(), "ok", StringComparison.Ordinal)
                         || string.Equals(info["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
            if (!dbInfoOk) return null;

            if (info["result"] is JObject canonicalInner && (info["code"] != null || info["status"] != null))
            {
                info = canonicalInner;
            }
            return info;
        }

        private static JObject? GetCachedDatabaseInfo()
        {
            KbHandle? kb = _currentKb.Value;
            string? alias = kb?.NormalizedAlias;
            if (string.IsNullOrEmpty(alias)) return null;
            return _databaseInfoByKb.TryGetValue(alias!, out var info) ? info : null;
        }

        // v2.3.8 Task 1.2: async variant that performs a live fetch against the worker
        // before assembling whoami. The sync BuildWhoamiPayload() is kept for tests and
        // any caller that doesn't want to block on a worker round-trip.
        internal static Task<JObject> BuildWhoamiPayloadAsync() => BuildWhoamiPayloadAsync(false);

        internal static async Task<JObject> BuildWhoamiPayloadAsync(bool verbose)
        {
            // Skip the worker round-trip when our cached snapshot is recent enough.
            // whoami is the most-called first-turn tool — a stale-by-a-few-seconds
            // index status is far better UX than a 1.5s blocking call. Search/lifecycle
            // paths refresh the snapshot whenever they receive new telemetry, so the
            // cache stays warm during real use.
            IndexStateSnapshot snap;
            lock (_lastKnownIndexStateLock) { snap = _lastKnownIndexState; }
            bool cacheFresh = snap.RefreshedAtUtc != DateTime.MinValue
                && (DateTime.UtcNow - snap.RefreshedAtUtc).TotalSeconds < 15;

            // v2.6.8: degraded mode. If the worker process is dead or still booting,
            // don't even attempt the round-trip — clients with tight timeouts (VS
            // Code Codex closes the transport after a few seconds) will get a
            // structured "Booting" answer instantly instead of a hang.
            bool workerHealthy = IsActiveWorkerHealthy();
            if (!cacheFresh && workerHealthy)
            {
                // v2.6.9 perf: 400ms aggressive timeout — when the worker is slow to
                // respond (cold STA thread, busy with another call), we don't want
                // every whoami to block here. After the timeout, if the cache is
                // STILL empty (RefreshedAtUtc == MinValue), stamp a placeholder
                // snapshot so the next whoami call inside the 15s window hits
                // cacheFresh and returns instantly. The worker's own telemetry push
                // path (index/lifecycle progress) will overwrite the placeholder
                // with real data as soon as it arrives. Net effect on the bench:
                // first whoami pays ~400ms once, subsequent calls drop to ms-range.
                bool refreshed = await TryRefreshIndexStateFromWorkerAsync(timeoutMs: 400).ConfigureAwait(false);
                // DB info is stable; fetch once per KB and cache forever. Await on first
                // whoami of a session so the database block populates inline; subsequent
                // calls short-circuit on the cache and pay nothing. The timeout is generous
                // (3s) because GetDatabaseInfo enumerates the DataStoresPart across the
                // environment's models, which the SDK lazy-loads on first touch after a cold
                // start — 600ms missed it, leaving database stuck at "Pending" for the session.
                await TryRefreshDatabaseInfoFromWorkerAsync(timeoutMs: 3000).ConfigureAwait(false);
                if (!refreshed)
                {
                    lock (_lastKnownIndexStateLock)
                    {
                        if (_lastKnownIndexState.RefreshedAtUtc == DateTime.MinValue)
                        {
                            // Stamp a "Unknown" placeholder. Status stays Cold so the
                            // agent can still see that the index hasn't reported yet;
                            // we just stop hammering the round-trip every call.
                            _lastKnownIndexState = new IndexStateSnapshot
                            {
                                Status = "Cold",
                                TotalObjects = 0,
                                RefreshedAtUtc = DateTime.UtcNow,
                                RecentlyChanged = _lastKnownIndexState?.RecentlyChanged
                            };
                        }
                    }
                }
            }
            var payload = BuildWhoamiPayload(verbose);
            if (!workerHealthy)
            {
                // v2.6.8 (review C7): workerHealth is purely additive — emit it
                // whenever the worker is down, regardless of cache freshness, so
                // the agent always has a signal that tool calls may transient-fail.
                // The index.status downgrade stays gated on !cacheFresh because the
                // last-good snapshot is still useful when it's recent.
                if (!cacheFresh && payload["index"] is JObject idx)
                {
                    idx["status"] = "Booting";
                }
                payload["workerHealth"] = BuildHonestWorkerHealth(payload);
            }
            return payload;
        }

        // v2.6.8: cheap health check used by whoami's degraded-mode path. Returns
        // true only when a worker process exists and HasExited is false; any error
        // looking at the pool is treated as "not healthy" so we err on the side of
        // skipping the round-trip.
        private static bool IsActiveWorkerHealthy()
        {
            try
            {
                if (_workerPool == null) return false;
                // v2.6.8 (review C3): prefer the AsyncLocal-bound KB if present;
                // otherwise probe every open worker — healthy if at least one is
                // alive. This avoids KbResolver.Resolve(null, ...) throwing on
                // multi-KB or no-default-KB setups (which the outer catch would
                // turn into a false 'respawning' signal).
                KbHandle? kb = _currentKb.Value;
                if (kb != null)
                {
                    var worker = _workerPool.TryGet(kb.NormalizedAlias);
                    return worker != null && worker.Pid.HasValue;
                }
                var open = _workerPool.ListOpen();
                if (open == null || open.Count == 0) return false;
                foreach (var handle in open)
                {
                    var w = _workerPool.TryGet(handle.NormalizedAlias);
                    if (w != null && w.Pid.HasValue) return true;
                }
                return false;
            }
            catch { return false; }
        }

        // issue #26 P1: report the worker's REAL state instead of a blanket "respawning".
        // The old code always said "respawning" whenever no live worker was found — even
        // when nothing was actually spawning and no self-heal would ever happen, stranding
        // the agent for minutes. This distinguishes:
        //   starting        — a process really is coming up right now (or we just kicked one)
        //   respawn_failed   — eager respawn exhausted its retries; needs manual recovery
        //   no_worker        — no KB opened yet / nothing to run
        // and, crucially, SELF-HEALS: when there's a known KB but no worker and none
        // spawning, it kicks an AcquireAsync so the worker actually comes back without the
        // agent having to run worker_reload force by hand.
        private static JObject BuildHonestWorkerHealth(JObject payload)
        {
            try
            {
                var pool = _workerPool;
                if (pool == null)
                    return new JObject { ["status"] = "no_worker", ["hint"] = "Gateway not fully initialised yet. Retry whoami shortly." };

                // Which KB does the session care about? Prefer whoami's resolved active
                // alias, else the AsyncLocal, else the single/first known KB.
                string? alias = payload?["kb"]?["active"]?.ToString();
                if (string.IsNullOrEmpty(alias)) alias = _currentKb.Value?.NormalizedAlias;
                var known = pool.ListKnown();
                if (string.IsNullOrEmpty(alias)) alias = known.FirstOrDefault()?.NormalizedAlias;

                if (!string.IsNullOrEmpty(alias) && pool.IsSpawning(alias))
                {
                    return new JObject
                    {
                        ["status"] = "starting",
                        ["hint"] = "Worker process is coming up (cold load ~10–15s). Retry in a few seconds; tool calls during this window may return 'crashed/exited' and should be retried."
                    };
                }

                if (!string.IsNullOrEmpty(alias)
                    && _respawnFailures.TryGetValue(alias!, out var fail))
                {
                    return new JObject
                    {
                        ["status"] = "respawn_failed",
                        ["alias"] = alias,
                        ["error"] = fail.Error,
                        ["failedAtUtc"] = fail.AtUtc,
                        ["hint"] = "The gateway tried and failed to respawn this KB's worker. Recover with genexus_worker_reload mode=soft force=true, then reopen/retry. This is NOT a transient cold-start."
                    };
                }

                // No live worker, not spawning, no recorded failure. If we know the KB,
                // self-heal by kicking a spawn now (fire-and-forget) and report "starting".
                if (!string.IsNullOrEmpty(alias))
                {
                    var handle = known.FirstOrDefault(h =>
                        string.Equals(h.NormalizedAlias, alias, StringComparison.OrdinalIgnoreCase));
                    if (handle != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await pool.AcquireAsync(handle, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token).ConfigureAwait(false); }
                            catch (Exception ex) { _respawnFailures[handle.NormalizedAlias] = (DateTime.UtcNow, ex.Message); }
                        });
                        return new JObject
                        {
                            ["status"] = "starting",
                            ["alias"] = alias,
                            ["hint"] = "No live worker was found for this KB; the gateway is starting one now. Retry in a few seconds."
                        };
                    }
                }

                return new JObject
                {
                    ["status"] = "no_worker",
                    ["hint"] = "No KB worker is running. Open a KB with genexus_kb action=open path=<kbPath> (or set a DefaultKb), then retry."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "unknown", ["hint"] = "Worker health probe failed: " + ex.Message };
            }
        }

        internal static JObject BuildWhoamiPayload() => BuildWhoamiPayload(false);

        // issue #25 #5: whoami defaulted to dumping ~3k tokens of STATIC content
        // (playbooks + skills catalog) plus a session-growing stats/heatmap block
        // on EVERY call — wasteful when the agent just wants a health check after a
        // worker respawn. Lean by default (verbose=false): keep the dynamic health
        // blocks (kb/geneXus/update/worker/index/database/metricsSummary/suggestedNext)
        // and drop the static reference blocks. verbose=true restores the full payload.
        // For a pure connection/index health probe, genexus_doctor is even leaner.
        internal static JObject BuildWhoamiPayload(bool verbose)
        {
            var cfg = _activeConfig;
            string? gxPath = cfg?.GeneXus?.InstallationPath;

            // issue #26 P4/P1: report the KB the session is ACTUALLY working against,
            // not the raw Environment.KBPath scaffold (which `kb open` never updated, so
            // whoami used to keep showing the empty "YourKB" while real work went to a
            // different, explicitly-opened KB). Priority: currently-open worker matching
            // DefaultKb → any open worker → a KB opened this session (known) → the
            // declared DefaultKb entry → legacy Environment.KBPath.
            string? activeAlias = null;
            string? kbPath = null;
            try
            {
                var pool = _workerPool;
                var open = pool?.ListOpen() ?? new List<KbHandle>();
                var known = pool?.ListKnown() ?? new List<KbHandle>();
                string? defAlias = cfg?.Environment?.DefaultKb;
                KbHandle? pick =
                    (defAlias != null ? open.FirstOrDefault(h => string.Equals(h.Alias, defAlias, StringComparison.OrdinalIgnoreCase)) : null)
                    ?? open.FirstOrDefault()
                    ?? (defAlias != null ? known.FirstOrDefault(h => string.Equals(h.Alias, defAlias, StringComparison.OrdinalIgnoreCase)) : null)
                    ?? known.FirstOrDefault();
                if (pick != null) { activeAlias = pick.Alias; kbPath = pick.Path; }
                else if (!string.IsNullOrWhiteSpace(defAlias))
                {
                    var decl = cfg?.Environment?.KBs?.FirstOrDefault(
                        k => string.Equals(k.Alias, defAlias, StringComparison.OrdinalIgnoreCase));
                    if (decl != null) { activeAlias = decl.Alias; kbPath = decl.Path; }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(kbPath)) kbPath = cfg?.Environment?.KBPath;
            string? kbName = !string.IsNullOrEmpty(kbPath) ? Path.GetFileName(kbPath!.TrimEnd('\\', '/')) : null;
            bool kbExists = !string.IsNullOrEmpty(kbPath) && Directory.Exists(kbPath);
            bool kbValid = false;
            if (kbExists)
            {
                try
                {
                    kbValid = Directory.EnumerateFiles(kbPath!).Any(f =>
                        f.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Equals("KnowledgeBase.Connection", StringComparison.OrdinalIgnoreCase));
                }
                catch { }
            }
            string? gxVersion = DetectGeneXusVersion(gxPath);

            var payload = new JObject
            {
                ["connected"] = cfg != null,
                ["kb"] = new JObject
                {
                    ["name"] = kbName,
                    ["path"] = kbPath,
                    ["exists"] = kbExists,
                    ["looksValid"] = kbValid,
                    // issue #26 P4: the alias actually active this session (null if none
                    // opened yet), and how many workers are live — so the agent can tell
                    // an opened KB apart from the config scaffold.
                    ["active"] = activeAlias,
                    ["openCount"] = _workerPool?.ListOpen().Count ?? 0
                },
                ["geneXus"] = new JObject
                {
                    ["installationPath"] = gxPath,
                    ["version"] = gxVersion,
                    ["supportedMajor"] = SupportedGeneXusMajor,
                    ["versionMatches"] = gxVersion != null && gxVersion.StartsWith(SupportedGeneXusMajor, StringComparison.OrdinalIgnoreCase)
                },
                ["config"] = new JObject
                {
                    ["path"] = Configuration.CurrentConfigPath
                },
                ["mcp"] = new JObject
                {
                    ["serverVersion"] = McpRouter.ServerVersion,
                    ["protocolVersion"] = McpRouter.SupportedProtocolVersion
                },
                ["worker"] = BuildWorkerBlock(),
                // Self-update awareness — LLM-visible structured data sourced from the
                // 24h-cached UpdateNotifier result. Lets the agent check whoami.update.
                // updateAvailable before each session and proactively offer the upgrade
                // command instead of relying on the stderr-style notifications/message.
                ["update"] = UpdateNotifier.GetCachedStatusSync() ?? new JObject {
                    ["currentVersion"] = McpRouter.ServerVersion,
                    ["updateAvailable"] = false
                },
                // v2.3.8 Task 1.2: surface index readiness so agents know whether to
                // call `lifecycle action=index` before relying on search/analyze.
                ["index"] = BuildIndexBlock(),
                // Per-KB database configuration. SQL-generating tools should default to
                // database.default.dialect (oracle / sqlserver / mysql / postgres / db2 / …)
                // instead of guessing. Populated once per session via the worker, cached
                // until gateway restart. Null when no KB has been selected yet.
                ["database"] = GetCachedDatabaseInfo() ?? new JObject { ["status"] = "Pending" },
                // Compact tool-call health summary. Full per-tool breakdown remains at
                // `genexus_lifecycle status target=gateway:metrics`; whoami carries only
                // the roll-up so first-turn cost stays minimal while still surfacing red
                // flags (high error/timeout ratio, a tool with a >10s p95).
                ["metricsSummary"] = _operationTracker?.BuildMetricsSummary() ?? new JObject(),
                // v2.8.0 — concrete next-action hints so a weakly-capable LLM
                // doesn't have to guess "what do I call now?" after whoami.
                // Heuristics inspect KB / index / worker / update state and emit
                // {tool, args, why} triples matching the canonical envelope's
                // nextSteps shape. Empty when state is healthy + nothing pending.
                ["suggestedNext"] = BuildSuggestedNextBlock(kbPath, kbExists, kbValid)
            };

            if (verbose)
            {
                // Item 73: per-tool latency breakdown. In-memory, resets on gateway restart.
                payload["stats"] = BuildStatsWithHeatmap();
                // Inline playbooks for the flows that drove the largest token spend
                // in real sessions. Each entry is a 1-line route, not full docs — for
                // full recipes the agent can fetch genexus_recipe(name=...).
                payload["playbooks"] = BuildPlaybooksBlock();
                // v2.8.0 — first-class skill catalog. Each entry is one resources/read away.
                payload["skills"] = BuildSkillsCatalogBlock();
            }
            else
            {
                // Lean default: point at where the static reference lives instead of
                // re-shipping it every call.
                payload["reference"] = new JObject
                {
                    ["hint"] = "Call genexus_whoami(verbose=true) once for inline playbooks + skills catalog, or genexus_recipe / resources/list on demand. genexus_doctor gives a minimal connection+index health check.",
                    ["playbooksVia"] = "genexus_whoami(verbose=true)",
                    ["skillsVia"] = "resources/list"
                };
            }
            return payload;
        }

        // v2.8.0 — skill catalog block. Mirrors SkillCatalog.All so the
        // gateway doesn't duplicate the source-of-truth content; it only
        // surfaces titles + URIs + a one-line "when to read".
        internal static JArray BuildSkillsCatalogBlock()
        {
            var arr = new JArray();
            foreach (var skill in SkillCatalog.All)
            {
                arr.Add(new JObject
                {
                    ["uri"] = "genexus://kb/skills/" + skill.Key,
                    ["title"] = skill.Title,
                    ["summary"] = skill.Description,
                    ["whenToRead"] = SkillWhenToRead(skill.Key),
                    ["readVia"] = new JObject
                    {
                        ["tool"] = "resources/read",
                        ["args"] = new JObject { ["uri"] = "genexus://kb/skills/" + skill.Key }
                    }
                });
            }
            return arr;
        }

        private static string SkillWhenToRead(string key)
        {
            switch (key)
            {
                case "navigation":
                    return "BEFORE setting any panel property whose name involves Call, Protocol, IsMain, Main, Master, or before invoking Call/Return/ReplaceMainPanel — read this first. CallProtocol does NOT apply to Web Panel / SD Panel and 'Modal' is not a valid value.";
                case "gam-integrated-security":
                    return "BEFORE writing any code that depends on GAM authentication / authorization, or before touching Integrated Security Level / Login flow — verify the real property name and accepted values here.";
                case "sd-panel-mobile":
                    return "BEFORE marking a Smart Device object as Main, claiming an 'IsMain' property exists, or setting Native Mobile application-level properties — confirm the real name (it's 'Main program') and which object types support it.";
                case "webpanel-events":
                    return "BEFORE writing Web Panel event code (Start / Refresh / Load) — confirm the firing order and what attribute access each event has. Refresh runs BEFORE Load (per record), not after.";
                default:
                    return "Read before invoking related properties or methods you aren't fully certain about.";
            }
        }

        // v2.8.0 — first-turn navigation aid. Returns a JArray of
        // {tool, args, why} suggestions based on observable state. The array
        // is intentionally short (max 3 entries) and ordered by urgency so
        // an LLM that only reads the first item still picks the right call.
        internal static JArray BuildSuggestedNextBlock(string? kbPath, bool kbExists, bool kbValid)
        {
            var arr = new JArray();
            try
            {
                // Worker boot / health overrides everything — if it's not up,
                // every other tool call will return crashed/exited.
                if (!IsActiveWorkerHealthy())
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_whoami",
                        ["args"] = new JObject(),
                        ["why"] = "Worker is booting or respawning. Re-call whoami in 5–10s; the workerHealth block flips to ready once the SDK finishes loading."
                    });
                    return arr;
                }

                // No KB opened yet — that's the canonical first step.
                if (string.IsNullOrEmpty(kbPath) || !kbExists || !kbValid)
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_kb",
                        ["args"] = new JObject { ["action"] = "open", ["path"] = kbPath ?? "<absolute-path-to-.gx-or-folder>" },
                        ["why"] = "No valid KB is selected. Open one before any read/edit tool can resolve objects."
                    });
                    return arr;
                }

                // Index state drives discovery tools. Cold / 0 objects means
                // search / list / impact will all return empty until indexed.
                IndexStateSnapshot snap;
                lock (_lastKnownIndexStateLock) { snap = _lastKnownIndexState; }
                bool indexEmpty = snap.TotalObjects == 0;
                bool indexCold = string.Equals(snap.Status, "Cold", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(snap.Status, "Unknown", StringComparison.OrdinalIgnoreCase);
                if (indexCold || indexEmpty)
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_lifecycle",
                        ["args"] = new JObject { ["action"] = "index", ["force"] = true },
                        ["why"] = "Index is " + (indexEmpty ? "empty" : "cold") + ". Run a full index so list_objects / query / impact return real data."
                    });
                }

                // Update available — surface as a soft hint, not blocking.
                try
                {
                    var update = UpdateNotifier.GetCachedStatusSync();
                    if (update != null && update["updateAvailable"]?.ToObject<bool>() == true)
                    {
                        string latest = update["latestVersion"]?.ToString() ?? "<latest>";
                        arr.Add(new JObject
                        {
                            ["tool"] = "genexus_orient",
                            ["args"] = new JObject { ["topic"] = "update" },
                            ["why"] = $"GeneXus MCP v{latest} is available. Ask the user before installing — npx genexus-mcp@latest init."
                        });
                    }
                }
                catch { /* update check best-effort */ }

                // Default exploration nudge when everything is healthy — gives
                // a dumb LLM a deterministic 'what now' instead of guessing.
                if (arr.Count == 0)
                {
                    arr.Add(new JObject
                    {
                        ["tool"] = "genexus_list_objects",
                        ["args"] = new JObject { ["limit"] = 25 },
                        ["why"] = "KB is open and indexed. Listing objects is the cheapest way to anchor the next decision."
                    });
                }

                // v2.8.0 — always nudge the LLM toward authoritative skills
                // before it invents a property or method that doesn't exist
                // (CallProtocol=Modal, IsMain property, etc.). Cheap reminder;
                // the resource bodies are fact-checked against docs.genexus.com.
                arr.Add(new JObject
                {
                    ["tool"] = "resources/read",
                    ["args"] = new JObject { ["uri"] = "genexus://kb/skills/navigation" },
                    ["why"] = "Before claiming a navigation property/method exists (CallProtocol values, IsMain, ReplaceMainPanel, CallOptions.Target), read this skill — it lists what the SDK actually exposes and the common LLM pitfalls."
                });
            }
            catch { /* never let suggestion logic break whoami */ }
            return arr;
        }

        // Friction 2026-05-22: which worker exe is actually running was opaque —
        // users had to inspect tasklist + git status to know if their rebuilt
        // worker was in use, or if the gateway was still serving from publish/.
        // Surface it here so every whoami answers "which binary am I running"
        // without a side investigation.
        private static JObject BuildWorkerBlock()
        {
            try
            {
                WorkerProcess? wp = null;
                KbHandle? kb = _currentKb.Value;
                if (kb != null && _workerPool != null)
                {
                    wp = _workerPool.TryGet(kb.NormalizedAlias);
                }
                if (wp == null && _workerPool != null)
                {
                    // No KB selected (multi-KB session or first turn) — pick the first open.
                    var open = _workerPool.ListOpen();
                    if (open != null)
                    {
                        foreach (var h in open)
                        {
                            var w = _workerPool.TryGet(h.NormalizedAlias);
                            if (w != null) { wp = w; break; }
                        }
                    }
                }
                if (wp == null)
                {
                    return new JObject
                    {
                        ["status"] = "not_spawned",
                        ["hint"] = "Worker hasn't been spawned this session yet. Triggered on the first KB tool call."
                    };
                }
                string? exe = wp.SpawnedExePath;
                DateTime? builtAt = wp.SpawnedExeBuiltAtUtc;
                string? sourceLabel = null;
                if (!string.IsNullOrEmpty(exe))
                {
                    string e = exe!.Replace('/', '\\');
                    if (e.IndexOf(@"\publish\worker\", StringComparison.OrdinalIgnoreCase) >= 0)
                        sourceLabel = "publish (config.json WorkerExecutable)";
                    else if (e.IndexOf(@"\src\GxMcp.Worker\bin\Debug\", StringComparison.OrdinalIgnoreCase) >= 0)
                        sourceLabel = "dev-Debug (bin/Debug fallback)";
                    else if (e.IndexOf(@"\src\GxMcp.Worker\bin\Release\", StringComparison.OrdinalIgnoreCase) >= 0)
                        sourceLabel = "dev-Release (bin/Release fallback)";
                    else
                        sourceLabel = "custom";
                }
                // Item 52: worker memory + uptime for proactive reload hints.
                long? memoryMb = null;
                int? uptimeMin = null;
                string? reloadHint = null;
                try
                {
                    long? wsBytes = wp.WorkingSetBytes;
                    if (wsBytes.HasValue) memoryMb = wsBytes.Value / (1024 * 1024);
                    if (wp.SpawnedAtUtc.HasValue)
                        uptimeMin = (int)(DateTime.UtcNow - wp.SpawnedAtUtc.Value).TotalMinutes;
                    if (memoryMb > 1500)
                        reloadHint = "Consider genexus_worker_reload — heap >1.5GB or uptime >2h";
                    else if (uptimeMin > 120)
                        reloadHint = "Consider genexus_worker_reload — heap >1.5GB or uptime >2h";
                }
                catch { /* non-fatal: process may have exited between checks */ }

                var workerBlock = new JObject
                {
                    ["status"] = wp.Pid.HasValue ? "running" : "stopped",
                    ["pid"] = wp.Pid,
                    ["exePath"] = exe,
                    ["exeSource"] = sourceLabel,
                    ["builtAtUtc"] = builtAt?.ToString("o"),
                    ["spawnMs"] = wp.SpawnMs,
                    ["sdkInitMs"] = wp.SdkInitMs,
                    ["memoryMb"] = memoryMb,
                    ["uptimeMin"] = uptimeMin
                };
                if (reloadHint != null) workerBlock["reloadHint"] = reloadHint;
                return workerBlock;
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["error"] = ex.Message
                };
            }
        }

        private static JObject BuildPlaybooksBlock()
        {
            return new JObject
            {
                ["wwp_on_transaction"] = "genexus_apply_pattern { name: <Trn>, pattern: 'WorkWithPlus' } → generates WW<Trn>+View+Export family; no template needed.",
                ["wwp_on_webpanel"] = "genexus_apply_pattern { name: <WebPanel>, pattern: 'WorkWithPlus', settings: { template: '<TemplateName>' } } → direct-attach via host WorkWithPlus<WebPanel>. CHECK PARENT TYPE FIRST with genexus_inspect: WebPanel + SDPanel are direct-attach; Transaction is family-gen; other types are REJECTED.",
                ["edit_wwp_layout"] = "genexus_edit { name: WorkWithPlus<X>, part: 'PatternInstance', mode: 'patch', ... } → edit the host's XML; the MCP auto-projects to the WebForm.",
                ["create_popup"] = "genexus_create_popup { name, spec: { title, inputs:[{type,varName,...}], buttons:[{caption,event}] } } → one call replaces ~6 edits (Form layout=true + inputs + buttons + parms).",
                ["read_object_structure"] = "genexus_inspect { name, include:['parts','variables','signature'] } → cheap snapshot before any edit. ALWAYS run this first when unsure of object type.",
                ["unbreak_build"] = "Build failed with CS0246/CS2001? Check response.suggested_retry — it already carries `target` as a CSV of the missing objects. Fire `genexus_lifecycle { action:'build', target:<that CSV>, includeCallees:'direct' }` BEFORE asking the user. Don't grep raw error[] paths by hand and don't hand the list back to the user.",
                // Friction 2026-05-22: each of these cost multiple iterations the first time.
                ["unbreak_html_form"] = "HTML-form (Form type=html) gotchas — verify BEFORE writing code:\n  1. gxButton ignores custom event=... (always fires Enter). Workaround: route via a single Enter event + dispatch by sender.\n  2. gxTextBlock CaptionExpression Type=Variable renders the literal &<varName>; in HTML form. Use Caption=&varName (no Expression).\n  3. gxAttribute referencing a freshly-added variable fails 'Visual write failed' until the variable is committed. Add the variable + flush, then add the gxAttribute in a second edit.\n  4. Buttons are NOT addressable from Events as Btn<Name>.Enabled / .Caption (src0265). Drive visibility/enable via control properties at design-time or via &flag variables bound through CaptionExpression.",
                ["bulk_edit"] = "Need N patches on the same object? Use genexus_bulk_edit instead of N parallel genexus_edit. Parallel edits race on the file hash: only the first applies, the rest return 'Context block not found'.",
                ["wait_long_builds"] = "Don't poll genexus_lifecycle status in a loop — pass wait_until_done:true on the build call OR wait_seconds:600 on status. One turn instead of 12.",
                ["xml_comments_in_form"] = "XML comments (<!-- ... -->) inside HTML form Source are emitted as visible text by the generator. Strip them before genexus_edit (or use mode=patch to avoid touching them).",
                ["partial_success"] = "If a build returns Status=Failed but response.partial_success=true, Generation+Compilation already succeeded — try running the object once before rebuilding. The DLL is updated; only a late MSBuild step (often WebAppConfig) failed.",
                ["recipes_index"] = "For full step-by-step recipes call genexus_recipe { name: 'wwp_on_webpanel' | 'wwp_on_transaction' | 'create_popup' | 'edit_pattern_instance' | 'add_custom_button' | 'list' }.",
                // Friction 2026-05-22 items 2, 3, 13.
                ["html_form_inline_js"] = "GeneXus HTML form sanitization (KB-agnostic but observed in v18):\n  - <script>, <iframe>, <img onerror> inside gxTextBlock Format=\"HTML\" render as\n    literal escaped text — they do NOT execute.\n  - Inline event-attrs in raw HTML are preserved: onclick on <input type=radio>,\n    onmousedown/onunload on <body>, likely other on* on <td>/<div>.\n  - For post-event JS in a popup, hook via <body onmousedown> (installs listeners\n    on first mousedown) + addEventListener at runtime, instead of emitting <script>.",
                ["popup_call_async"] = ".Popup() is async. The line immediately after .Popup() sees out-params STILL EMPTY —\nchecking &OutVar.IsEmpty() there always returns true. Values arrive on a subsequent\nRefresh fired by AUTO_REFRESH=VARS_CHANGE, IF that property detects a change. In\nseveral KBs AUTO_REFRESH does NOT fire after popup close and the visibility set in\nStart does not restore.\n\nRecipe for \"blocking popup + locked screen + auto-reload\": see\ngenexus://kb/tool-help/recipes/popup_blocking_with_reload.",
                ["verify_in_browser"] = "chrome-devtools-axi (global npm CLI) drives Chrome via CDP.\nUsage:\n  chrome-devtools-axi open <url>      # navigate + return a11y tree\n  chrome-devtools-axi click @<uid>    # click by accessible uid\n  chrome-devtools-axi eval <js>       # eval JS in page (IIFE for multi-line)\n  chrome-devtools-axi screenshot <path>\n\nFor GeneXus popups (iframe): document.getElementById('gxp0_ifrm').contentDocument\nto access internal DOM.",
                // Friction 2026-05-22 items 26 + 67 — control-type glossary.
                ["control_type_glossary"] = "GeneXus 18 control types — which to use when:\n  • <gxButton> — standalone button. caption/event/buttonClass; ignores OnClickEvent\n    in HTML form (always fires Enter). Use 'Event' attribute, not 'OnClickEvent'.\n  • <gxAttribute ControlType=\"Button\"> — attribute-bound button. Rare; prefer\n    gxButton unless you need attribute binding.\n  • <gxAttribute> default (no ControlType) — IS editable. Sanitizer-safe target\n    for hidden-value bridges (set via document.getElementById('vNAME').value).\n  • <gxAttribute ControlType=\"Radio\"|\"Combo\"> on Form type='free style' →\n    rendered READ-ONLY. Switch Form type='layout' OR use raw HTML radios\n    inside gxTextBlock Format='HTML' + a hidden gxAttribute.\n  • <gxTextBlock> Format='Text' — plain text, sanitized.\n  • <gxTextBlock> Format='HTML' — raw HTML. <script>/<iframe>/<img onerror>\n    inside CDATA are escaped (see html_form_inline_js); inline event-attrs are kept.\n  • <gxImage> — clickable when 'eventGX' is set; mirrors the gxAttribute pattern.",
                ["dep_tree_view"] = "Need an ASCII tree of who-calls-what? Use genexus_analyze mode='hierarchy'\nfor a tree under target, or mode='callers' for per-call-site detail with line+context.\nmode='impact' is the flat caller list — fastest when you only need a count."
            };
        }

        // Wave-3 item 30: surface the live p95 ring-buffer to SystemRouter so the
        // build-plan worker call can derive per-node estimatedSeconds. Returns an
        // empty object when the tracker is null (first-call before init).
        internal static JObject GetToolP95MapForBuildPlan()
        {
            return _operationTracker?.BuildToolP95Map() ?? new JObject();
        }

        // Item 94: assemble the existing stats block + heatmap array. Purely additive
        // (no field renamed in stats.tools); heatmap is the new key. Heatmap is per-tool
        // [{tool,totalMs,percentOfSession,lastUsedAt}] sorted descending by totalMs.
        private static JObject BuildStatsWithHeatmap()
        {
            JObject stats = _operationTracker?.BuildToolStatsBlock() ?? new JObject();
            if (_operationTracker != null)
            {
                stats["heatmap"] = _operationTracker.BuildHeatmapBlock();
            }
            return stats;
        }

        private static async Task RunSelfTestAndExitAsync()
        {
            var result = new JObject();
            var checks = new JArray();
            int failCount = 0;
            int warnCount = 0;

            void AddCheck(string id, string status, string detail)
            {
                if (status == "fail") failCount++;
                else if (status == "warn") warnCount++;
                checks.Add(new JObject { ["id"] = id, ["status"] = status, ["detail"] = detail });
            }

            // 1. Gateway exe location (so callers see where the test ran from).
            string gatewayExe;
            try { gatewayExe = Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory; }
            catch { gatewayExe = AppContext.BaseDirectory; }
            result["gatewayExe"] = gatewayExe;

            // 2. Config load — surfaces missing GX_CONFIG_PATH and JSON parse errors.
            Configuration? config = null;
            try
            {
                config = Configuration.Load();
                AddCheck("config_load", "pass", $"config.json loaded from {Configuration.CurrentConfigPath ?? "<unknown>"}");
            }
            catch (Exception ex)
            {
                AddCheck("config_load", "fail", $"config.json load failed: {ex.Message}");
            }

            // 3. GeneXus installation.
            string? gxPath = config?.GeneXus?.InstallationPath;
            if (string.IsNullOrWhiteSpace(gxPath))
            {
                AddCheck("gx_installation", "fail", "GeneXus.InstallationPath is not set in config.json");
            }
            else
            {
                string exe = Path.Combine(gxPath, "genexus.exe");
                if (File.Exists(exe))
                    AddCheck("gx_installation", "pass", $"genexus.exe present at {gxPath}");
                else
                    AddCheck("gx_installation", "fail", $"genexus.exe NOT found at {gxPath} (config points here but it is missing)");
            }

            // 4. In-process build assembly — loadable means Stream D's build daemon works.
            if (!string.IsNullOrWhiteSpace(gxPath))
            {
                string dll = Path.Combine(gxPath, "Genexus.MsBuild.Tasks.dll");
                if (File.Exists(dll))
                    AddCheck("in_process_build_assembly", "pass", $"Genexus.MsBuild.Tasks.dll present ({new FileInfo(dll).Length / 1024} KB)");
                else
                    AddCheck("in_process_build_assembly", "warn", $"Genexus.MsBuild.Tasks.dll missing — build will fall back to MSBuild.exe spawn");
            }

            // 5. KB path(s).
            string? kbPath = config?.Environment?.KBPath;
            if (string.IsNullOrWhiteSpace(kbPath))
            {
                AddCheck("kb_path", "warn", "No KB path configured");
            }
            else if (!Directory.Exists(kbPath))
            {
                AddCheck("kb_path", "fail", $"Configured KB path does not exist: {kbPath}");
            }
            else
            {
                bool looksLikeKb = false;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(kbPath))
                    {
                        var name = Path.GetFileName(f).ToLowerInvariant();
                        if (name.EndsWith(".gxw") || name == "knowledgebase.connection") { looksLikeKb = true; break; }
                    }
                }
                catch { }
                AddCheck("kb_path", looksLikeKb ? "pass" : "warn",
                    looksLikeKb ? $"KB folder shape OK at {kbPath}" : $"KB path exists but no .gxw / KnowledgeBase.Connection found: {kbPath}");
            }

            result["checks"] = checks;
            result["summary"] = new JObject
            {
                ["pass"] = checks.Count - failCount - warnCount,
                ["warn"] = warnCount,
                ["fail"] = failCount,
                ["total"] = checks.Count
            };
            result["ok"] = failCount == 0;
            result["schemaVersion"] = "gateway-selftest/1";

            // Single JSON line on stdout so the PowerShell installer can ConvertFrom-Json it.
            await Console.Out.WriteLineAsync(result.ToString(Formatting.None));
            await Console.Out.FlushAsync();
            Environment.Exit(failCount == 0 ? 0 : 1);
        }

        public static async Task Main(string[] args)
        {
            // Short-circuit self-test before any I/O setup. The CLI installer calls this
            // to validate the install: it loads config, checks GeneXus install + KB
            // existence + the in-process build dll, and prints a single JSON line to
            // stdout. No worker is started, no HTTP listener is opened, no logs file
            // is created. Replaces the no-op `--axi-spawn-probe` flag the installer
            // used to call (which only verified that the exe could be launched).
            if (args != null && args.Length > 0 && (args[0] == "--self-test" || args[0] == "--axi-self-test"))
            {
                await RunSelfTestAndExitAsync();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += async (s, e) => {
                string msg = $"[{DateTime.Now}] FATAL UNHANDLED: {e.ExceptionObject}\n";
                var errorObj = new { jsonrpc = "2.0", method = "notifications/message", @params = new { level = "error", logger = "gateway", data = msg } };
                await TryWriteStdout(Newtonsoft.Json.JsonConvert.SerializeObject(errorObj));
                try { File.AppendAllText("gateway_panic.log", msg); } catch { }
            };

            // Register encoding provider for Windows-1252 support in .NET 8
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            try
            {
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch (IOException)
            {
                // Detached HTTP-only launches may not have a console handle.
            }

            InitializeLogging();

            // Squirrel-style: if a previous session staged a newer build, swap it in
            // now (before any worker is spawned or files are opened). Fail-safe — a
            // locked file or any error just leaves the install untouched and retries
            // next launch. No-op for npx-cache launches (managed-install only).
            SelfUpdater.ApplyStagedUpdateOnStartup();

            var config = Configuration.Load();
            _activeConfig = config;
            LogGeneXusVersionCheck(config);
            try { RecipeCatalog.ConfigureUserMacroDirectory(GetUserMacroDir()); }
            catch (Exception ex) { Log("[RecipeCatalog] User-macro discovery skipped: " + ex.Message); }
            Log("[Gateway] Startup orphan-kill disabled. Existing gateway reuse is handled by the extension client.");
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                if (_activeConfig != null)
                {
                    GatewayProcessLease.ReleaseCurrentProcess(_activeConfig);
                }
            };

            var leaseRegistration = GatewayProcessLease.TryRegisterCurrentProcess(config);
            bool isMaster = leaseRegistration.Success;

            if (!isMaster)
            {
                if (leaseRegistration.IsDuplicate && leaseRegistration.Lease != null)
                {
                    Log($"[Gateway] existing_master_detected currentPid={Environment.ProcessId} masterPid={leaseRegistration.Lease.ProcessId}");
                    
                    if (leaseRegistration.Lease.HttpPort > 0) 
                    {
                        bool shouldPromote = await RunMcpProxyAsync(leaseRegistration.Lease, config);
                        if (!shouldPromote) return;
                        
                        Log("[Gateway] Starting promotion to Master...");
                        var forced = GatewayProcessLease.ForceRegisterCurrentProcess(config);
                        if (!forced.Success) {
                            Log("[Gateway] Promotion failed: lease acquisition blocked.");
                            return;
                        }
                        isMaster = true;
                    }
                    else 
                    {
                        Log($"[Gateway] Existing master (PID {leaseRegistration.Lease.ProcessId}) has no HTTP port. Reusing or exiting.");
                        return;
                    }
                }
                else 
                {
                    Log($"[Gateway] Registration failed: {leaseRegistration.FailureReason}");
                    return;
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                Log("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject as Exception)?.ToString());
            };

            TaskScheduler.UnobservedTaskException += (s, e) => {
                Log("UNOBSERVED TASK EXCEPTION: " + e.Exception?.ToString());
                e.SetObserved();
            };

            Log("=== Gateway starting (Stdio Mode) ===");
            
            _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(config.Server?.SessionIdleTimeoutMinutes ?? 10));
            _idempotencyCache = new IdempotencyCache(
                config.Server?.IdempotencyTtlMinutes ?? 15,
                config.Server?.IdempotencyCacheSize ?? 1000);
            
            // Subscribing to Configuration Changes
            Configuration.OnConfigurationChanged += (newConfig) => {
                if (newConfig.Environment?.KBPath != config.Environment?.KBPath || 
                    newConfig.GeneXus?.InstallationPath != config.GeneXus?.InstallationPath ||
                    newConfig.Environment?.GX_SHADOW_PATH != config.Environment?.GX_SHADOW_PATH ||
                    newConfig.Server?.HttpPort != config.Server?.HttpPort ||
                    newConfig.Server?.WorkerIdleTimeoutMinutes != config.Server?.WorkerIdleTimeoutMinutes) {
                    Log($"[Gateway] Core configuration changed! Restarting Worker process...");
                    config = newConfig; // Update reference
                    _activeConfig = config;
                    GatewayProcessLease.RefreshCurrentProcess(config);
                    RestartWorker(config);
                    BroadcastResourcesListChanged("core_configuration_changed");
                } else {
                    Log($"[Gateway] Minor configuration changed. Ignoring.");
                }
            };

            // 1. Start HTTP Server first (it's critical for VS Code communication)
            if (config.Server?.HttpPort > 0)
            {
                Log($"[Gateway] Starting HTTP server on port {config.Server.HttpPort}...");
                _ = Task.Run(async () => {
                    int retryCount = 0;
                    while (retryCount < 5) {
                        try { 
                            await StartHttpServer(config); 
                            Log("[Gateway] HTTP server bound and active.");
                            while(true) {
                                await Task.Delay(30000);
                                Log("[Gateway] Heartbeat: HTTP server still active.");
                            }
                        }
                        catch (Exception exHttp) { 
                            Log($"[HTTP] Bind failure (5000): {exHttp.Message}. Attempting port recovery ({retryCount + 1}/5)...");
                            TryKillProcessOnPort(config.Server?.HttpPort ?? 5000);
                            retryCount++;
                            await Task.Delay(1000);
                        }
                    }
                });
            }

            // 2. Start Worker in background
            Log("[Gateway] Initializing Worker lifecycle...");
            StartWorker(config);
            Log("[Gateway] Worker lifecycle ready.");

            // 3. Subscribing to KB changes for Semantic Cache Invalidation
            if (!string.IsNullOrEmpty(config.Environment?.KBPath))
            {
                Log("[Gateway] Setting up .gx_mirror watcher...");
                try 
                {
                    string mirrorPath = Path.Combine(config.Environment.KBPath, ".gx_mirror");
                    if (!Directory.Exists(mirrorPath)) Directory.CreateDirectory(mirrorPath);
                    var watcher = new FileSystemWatcher(mirrorPath) 
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += (s, e) => {
                        Log($"[Cache] Invalidation triggered by external change: {e.Name}");
                        _semanticCache.Clear();
                        BroadcastResourceUpdated("genexus://objects", "external_kb_change");
                    };
                    Log("[Gateway] .gx_mirror watcher active.");
                } catch (Exception ex) { Log($"[Cache] Watcher error: {ex.Message}"); }
            }

            if (config.Server?.McpStdio == true)
            {
                Log("[Gateway] Entering Stdio Loop...");
                _stdioActive = true;
                var reader = Console.In;
                while (true)
                {
                    string? line = null;
                    try { line = await reader.ReadLineAsync(); } catch { }

                    if (line == null)
                    {
                        if (config.Server?.HttpPort > 0)
                        {
                            Log("Stdio closed, keeping alive for HTTP...");
                            await Task.Delay(-1);
                        }
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Trim().StartsWith("{")) {
                        Log($"[Protocol] Ignored non-JSON noise on stdin: {line}");
                        continue;
                    }

                    // Dispatch each request concurrently so a slow tool call (worker
                    // cold-start, index build, edit reapply, self-refresh) can't park
                    // the read loop and starve the host's keepalive `ping` — the symptom
                    // behind the IDE's "MCP Server parou de responder" popup while idle.
                    // JSON-RPC correlates responses by id, so out-of-order replies are
                    // spec-legal; TryWriteStdout serializes writes via _stdoutGate and
                    // _currentKb is AsyncLocal, so each dispatched request keeps its own
                    // KB routing.
                    string capturedLine = line;
                    _ = Task.Run(async () =>
                    {
                        JToken? capturedId = null;
                        try
                        {
                            JObject request;
                            try
                            {
                                request = JObject.Parse(capturedLine);
                            }
                            catch (Exception parseEx)
                            {
                                Log("MCP parse error: " + parseEx.Message);
                                var parseErr = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = JValue.CreateNull(),
                                    ["error"] = new JObject { ["code"] = -32700, ["message"] = "Parse error" }
                                };
                                await TryWriteStdout(parseErr.ToString(Formatting.None));
                                return;
                            }
                            capturedId = request["id"];
                            var response = await ProcessMcpRequest(request);
                            if (response != null)
                            {
                                await TryWriteStdout(response.ToString(Formatting.None));
                            }
                        }
                        catch (WorkerPoolFullException poolEx)
                        {
                            Log("MCP WorkerPoolFull: " + poolEx.Message);
                            var errResp = new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = capturedId?.DeepClone() ?? JValue.CreateNull(),
                                ["error"] = new JObject { ["code"] = -32000, ["message"] = poolEx.Message }
                            };
                            await TryWriteStdout(errResp.ToString(Formatting.None));
                        }
                        catch (Exception ex)
                        {
                            Log("MCP Error: " + ex.Message);
                            if (capturedId != null)
                            {
                                var errResp = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = capturedId.DeepClone(),
                                    ["error"] = new JObject { ["code"] = -32603, ["message"] = "Internal error" }
                                };
                                await TryWriteStdout(errResp.ToString(Formatting.None));
                            }
                        }
                    });
                }
            }
            else if (config.Server?.HttpPort > 0)
            {
                Log("[Gateway] MCP stdio disabled. Serving HTTP only.");
                await Task.Delay(-1);
            }
        }

        private static async Task<bool> RunMcpProxyAsync(GatewayLeaseRecord master, Configuration config)
        {
            string baseUrl = $"http://localhost:{master.HttpPort}/mcp";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30); // Do not let proxy hang forever if master is dead
            httpClient.DefaultRequestHeaders.Add("MCP-Protocol-Version", McpRouter.SupportedProtocolVersion);
            
            string? sessionId = null;
            var reader = Console.In;
            var cts = new CancellationTokenSource();
            var ct = cts.Token;

            Log($"[Proxy] Proxy mode active (Master PID {master.ProcessId} on port {master.HttpPort}).");

            while (true)
            {
                string? line = null;
                try { line = await reader.ReadLineAsync(ct); } catch { break; }
                if (line == null) break;

                int retryCount = 0;
                bool success = false;
                while (retryCount < 3 && !success)
                {
                    try
                    {
                        string body = line;
                        var request = JObject.Parse(body);
                        string requestId = request["id"]?.ToString() ?? "unknown";
                        var content = new StringContent(body, Encoding.UTF8, "application/json");
                        
                        if (sessionId != null) content.Headers.Add("MCP-Session-Id", sessionId);

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, baseUrl) { Content = content };
                        if (sessionId != null) requestMessage.Headers.Add("MCP-Session-Id", sessionId);

                        var response = await httpClient.SendAsync(requestMessage, ct);
                        
                        if (sessionId == null && response.Headers.TryGetValues("MCP-Session-Id", out var values))
                        {
                            sessionId = values.FirstOrDefault();
                            if (sessionId != null)
                            {
                                Log($"[Proxy] Handshake complete. ID: {sessionId}");
                                // Wait a moment for master to stabilize before streaming notifications
                                await Task.Delay(2000);
                                _ = Task.Run(() => RunProxySseForwarderAsync(master.HttpPort, sessionId, cts.Token));
                            }
                        }

                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(ct);
                            if (string.IsNullOrWhiteSpace(responseBody))
                            {
                                Log($"[Proxy] Master returned empty response for command {requestId}. Retrying...");
                                throw new Exception("Empty response from master");
                            }

                            if (!responseBody.Trim().StartsWith("{"))
                            {
                                Log($"[Proxy] Master returned non-JSON response: {responseBody}");
                                throw new Exception("Invalid response from master");
                            }

                            await TryWriteStdout(responseBody);
                            success = true;
                        }
                        else
                        {
                            string remoteError = await response.Content.ReadAsStringAsync(ct);
                            Log($"[Proxy] Master status {response.StatusCode}: {remoteError}");
                            var id = request["id"];
                            if (id != null)
                            {
                                var errorResponse = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = id.DeepClone(),
                                    ["error"] = new JObject { ["code"] = (int)response.StatusCode, ["message"] = $"Master error: {response.StatusCode}" }
                                };
                                await TryWriteStdout(errorResponse.ToString(Formatting.None));
                            }
                            success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        Log($"[Proxy] Connection failed to Master ({retryCount}/3): {ex.Message}");
                        if (retryCount >= 3)
                        {
                            Log("[Proxy] Master unresponsive. Triggering promotion...");
                            // We return true but we need to keep the last line for the Master to process!
                            // To keep it simple, we let Main know to promote. 
                            // The IDE will likely retry the last command or we can buffer it.
                            return true; 
                        }
                        await Task.Delay(1000);
                    }
                }
            }
            cts.Cancel();
            Log("[Proxy] Stdio closed.");
            return false;
        }

        private static async Task RunProxySseForwarderAsync(int port, string sessionId, CancellationToken ct)
        {
            string url = $"http://localhost:{port}/mcp";
            using var client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("MCP-Session-Id", sessionId);
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", McpRouter.SupportedProtocolVersion);

            try
            {
                Log("[Proxy-SSE] Notification link active.");
                using var stream = await client.GetStreamAsync(url, ct);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (line.StartsWith("data: "))
                    {
                        string data = line.Substring(6).Trim();
                        // SSE message events (notifications) usually come in formatted as data blocks
                        // We must enforce jsonrpc wrapper to avoid client parsing errors on metadata like {"sessionId":"..."}
                        if (data.StartsWith("{") && data.EndsWith("}") && data.Contains("\"jsonrpc\""))
                        {
                            await TryWriteStdout(data);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[Proxy-SSE] Background channel error: {ex.Message}");
            }
        }

        private static void StartWorker(Configuration config)
        {
            _kbResolver = new KbResolver(config);
            _workerPool = new WorkerPool(config);
            _workerPool.OnRpcResponse += HandleWorkerResponse;
            _workerPool.OnWorkerExited += (kb, stopReason) => {
                string alias = kb.NormalizedAlias;
                int aborted = 0;
                foreach (var kvp in _pendingRequests.ToArray())
                {
                    if (!string.Equals(kvp.Value.WorkerAlias, alias, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string id = kvp.Key;
                    if (_pendingRequests.TryRemove(id, out var pending))
                    {
                        _operationTracker.MarkFailedByRequest(id, $"Worker for KB '{kb.Alias}' crashed/exited.");
                        var errorJson = JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            error = new { code = -32603, message = $"Worker for KB '{kb.Alias}' crashed/exited." }
                        });
                        pending.CompletionSource.TrySetResult(errorJson);
                        aborted++;
                    }
                }
                Log($"Worker for KB '{kb.Alias}' exited. Aborted {aborted} pending request(s) bound to it.");

                // v2.6.8: eager respawn. Without this, the next tool call paid the
                // ~10–15s cold-start latency inline — long enough for short-timeout
                // MCP clients (VS Code Codex) to close the transport entirely.
                // Fire-and-forget: failures are logged but don't propagate; the
                // lazy path in WorkerPool.AcquireAsync still works as a fallback.
                // Skip eager respawn for intentional/planned exits.
                if (stopReason == WorkerStopReason.IdleTimeout ||
                    stopReason == WorkerStopReason.GatewayShutdown ||
                    stopReason == WorkerStopReason.BusyReject ||
                    stopReason == WorkerStopReason.ExplicitClose ||
                    stopReason == WorkerStopReason.PlannedReload)
                {
                    Log($"[Respawn] Skipped eager respawn for KB '{kb.Alias}' — stop reason: {stopReason}.");
                    return;
                }
                if (IsEagerRespawnSuppressed())
                {
                    Log($"[Respawn] Skipped eager respawn for KB '{kb.Alias}' — planned exit in progress.");
                    return;
                }
                Task.Run(async () =>
                {
                    // issue #26 P1: retry the respawn a few times with backoff instead of
                    // giving up after a single throw. A transient spawn failure used to
                    // leave the pool with no worker AND no process coming up, while whoami
                    // kept reporting "respawning" forever (nothing was). On final failure we
                    // record it so health can report the truth.
                    const int maxAttempts = 3;
                    Exception? lastEx = null;
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            var ctSrc = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            // Drop only the dead LIVE entry so AcquireAsync's fast path can't
                            // return the just-exited WorkerProcess — but keep the durable
                            // _known record (issue #26 P3) so the KB stays resolvable.
                            try { _workerPool!.DropLiveEntry(kb.NormalizedAlias); } catch { }
                            await _workerPool!.AcquireAsync(kb, ctSrc.Token).ConfigureAwait(false);
                            _respawnFailures.TryRemove(kb.NormalizedAlias, out _);
                            Log($"[Respawn] Replacement worker spawned for KB '{kb.Alias}' (attempt {attempt}).");
                            // issue #25 #2: the index bootstrap fires once per gateway process,
                            // so a crash-respawned worker (same gateway) otherwise never gets a
                            // reindex trigger and its index stays Cold until an explicit
                            // lifecycle call — forcing the agent to re-walk. Re-arm and re-fire
                            // the one-shot: BulkIndex(force:false) reuses the persisted on-disk
                            // snapshot (delta-on-open) instead of a cold 38k re-walk.
                            Interlocked.Exchange(ref _indexBootstrapStarted, 0);
                            TriggerIndexBootstrapOnce();
                            return;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            Log($"[Respawn] Attempt {attempt}/{maxAttempts} to respawn worker for KB '{kb.Alias}' failed: {ex.Message}");
                            if (attempt < maxAttempts)
                            {
                                try { await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false); } catch { }
                            }
                        }
                    }
                    _respawnFailures[kb.NormalizedAlias] = (DateTime.UtcNow, lastEx?.Message ?? "unknown");
                    Log($"[Respawn] Gave up respawning worker for KB '{kb.Alias}' after {maxAttempts} attempts. " +
                        $"whoami/health will report respawn_failed. Recovery: genexus_worker_reload mode=soft force=true.");
                });
            };
        }

        private static void RestartWorker(Configuration config)
        {
            if (_workerPool != null)
            {
                using (SuppressEagerRespawn())
                {
                    try { _workerPool.StopAll(); } catch { }
                }
            }
            // Clear cache on KB change
            _semanticCache.Clear();
            StartWorker(config);
            BroadcastToolsListChanged("worker_restarted");
            BroadcastResourcesListChanged("worker_restarted");
        }

        private static void HandleWorkerResponse(string json)
        {
            try {
                var val = JObject.Parse(json);
                string? id = val["id"]?.ToString();
                
                if (string.IsNullOrEmpty(id))
                {
                    // JSON-RPC Notification from Worker
                    string? method = val["method"]?.ToString();
                    if (method == "notifications/resources/updated")
                    {
                        var p = val["params"];
                        string name = p?["name"]?.ToString() ?? "unknown";
                        Log($"[Gateway] Notification from Worker: Resource {name} updated externally.");
                        BroadcastResourceUpdated($"genexus://objects/{name}", "external_kb_change");
                    }
                    else if (method == "notifications/progress" || method == "notifications/message")
                    {
                        if (ShouldForwardNotificationToStdio(method, val["params"]))
                        {
                            EmitStdioNotification(json);
                        }
                        if (val["params"] != null)
                        {
                            foreach (var session in _httpSessions.ActiveSessions)
                            {
                                QueueSessionMessage(session, json);
                            }
                        }
                    }
                    return;
                }

                _operationTracker.CompleteFromWorker(id, val);
                if (_pendingRequests.TryRemove(id, out var pending))
                {
                    pending.CompletionSource.TrySetResult(json);
                    if (!string.IsNullOrWhiteSpace(pending.OperationId))
                    {
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "operation",
                            data = $"Operation {pending.OperationId} finished.",
                            operationId = pending.OperationId,
                            correlationId = pending.CorrelationId,
                            status = val["error"] != null ? "Failed" : "Completed",
                            timestamp = DateTime.UtcNow
                        });
                    }
                }
            } catch (Exception ex) { Log($"HandleWorkerResponse Error: {ex.Message}"); }
        }

        private static JObject BuildWorkerRpcRequest(JObject workerCommand, string requestId, string? operationId = null)
        {
            var rpc = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = workerCommand["module"]?.ToString() ?? string.Empty,
                ["action"] = workerCommand["action"]?.DeepClone(),
                ["target"] = workerCommand["target"]?.DeepClone(),
                ["payload"] = workerCommand["payload"]?.DeepClone(),
                ["params"] = workerCommand.DeepClone()
            };

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                rpc["_meta"] = new JObject
                {
                    ["progressToken"] = operationId
                };
            }

            return rpc;
        }

        // Safety ceiling for waiting on worker SDK-ready before billing the op timeout.
        // Generous (cold-start is ~50s); only caps a wedged/never-ready worker.
        private const int WorkerSdkReadyCeilingMs = 180000;

        // issue #25 #2: read-only / idempotent tools that are safe to re-send once
        // after a worker crash. Writes/edits/builds are deliberately excluded — a
        // blind resend of a mutation could double-apply. The gateway already eagerly
        // respawns the worker; this retry hides the transient "crashed/exited" error
        // from the client for reads so the agent doesn't have to reconnect + re-issue.
        private static readonly HashSet<string> RetrySafeReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_read", "genexus_list_objects", "genexus_inspect", "genexus_query",
            "genexus_search_source", "genexus_analyze", "genexus_structure", "genexus_navigation",
            "genexus_whoami", "genexus_doctor"
        };

        private static bool IsWorkerCrashEnvelope(JObject workerResponse)
        {
            var err = workerResponse?["error"];
            string msg = err is JObject eo ? eo["message"]?.ToString() : err?.ToString();
            return !string.IsNullOrEmpty(msg) &&
                   msg.IndexOf("crashed/exited", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldRetryWorkerCrash(JObject workerResponse, string toolName, int attempt)
        {
            return attempt == 1
                && !string.IsNullOrEmpty(toolName)
                && RetrySafeReadTools.Contains(toolName)
                && IsWorkerCrashEnvelope(workerResponse);
        }

        private static async Task<JObject?> SendWorkerCommandAsync(
            JObject workerCommand,
            int timeoutMs,
            string timeoutLogMessage,
            Func<JObject, JObject> onSuccess,
            Func<string?, string, JObject> onTimeout,
            string toolName = "unknown",
            JObject? toolArgs = null,
            bool trackOperation = false,
            JToken? progressToken = null,
            Func<JObject, Task>? heartbeat = null)
        {
            string requestId = Guid.NewGuid().ToString();
            string correlationId = Guid.NewGuid().ToString("N");
            string? operationId = null;

            if (trackOperation)
            {
                operationId = _operationTracker.StartOperation(requestId, toolName, toolArgs, correlationId);
                BroadcastNotification("notifications/message", new
                {
                    level = "info",
                    logger = "operation",
                    data = $"Operation {operationId} started for tool {toolName}.",
                    operationId,
                    correlationId,
                    status = "Running",
                    timestamp = DateTime.UtcNow
                });
            }

            workerCommand["correlationId"] = correlationId;

            // issue #25 #2: idempotent single retry for read-only tools. When a worker
            // crashes mid-call the completion resolves with a "crashed/exited" envelope;
            // the gateway already eagerly respawns, so for retry-safe reads we re-send
            // once to the replacement instead of surfacing the transient error (which
            // forced the user to manually /mcp reconnect and re-issue).
            int workerAttempt = 0;
            while (true)
            {
                workerAttempt++;
                string attemptRequestId = workerAttempt == 1 ? requestId : Guid.NewGuid().ToString();
                var workerRequest = BuildWorkerRpcRequest(workerCommand, attemptRequestId, operationId);
                var worker = await GetActiveWorkerAsync();

                // Don't bill worker cold-start against the per-tool timeout. If the worker is
                // still initializing (SDK init ~50s on a large KB), wait for its sdk_ready signal
                // FIRST — emitting progress heartbeats so the client stays alive — and only then
                // start the operation's timeout clock below. Capped so a wedged worker can't block
                // forever; on cap we proceed and let the normal op timeout apply.
                if (!worker.IsSdkReady)
                {
                    bool ready = await McpRouter.AwaitWithHeartbeat(
                        worker.SdkReadyTask, WorkerSdkReadyCeilingMs, progressToken, heartbeat, $"{toolName} (worker starting)");
                    if (!ready)
                        Log($"[Gateway] worker not SDK-ready after {WorkerSdkReadyCeilingMs}ms for tool {toolName}; proceeding — op timeout applies.");
                }

                var pending = new PendingWorkerRequest
                {
                    CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                    ToolName = toolName,
                    CorrelationId = correlationId,
                    OperationId = operationId,
                    CreatedAtUtc = DateTime.UtcNow,
                    WorkerAlias = worker.Kb?.NormalizedAlias
                };
                _pendingRequests[attemptRequestId] = pending;

                await worker.SendCommandAsync(workerRequest.ToString(Formatting.None));

                if (timeoutMs <= 0)
                {
                    var workerResponse = JObject.Parse(await pending.CompletionSource.Task.ConfigureAwait(false));
                    if (ShouldRetryWorkerCrash(workerResponse, toolName, workerAttempt))
                    {
                        Log($"[Retry] {toolName} hit worker crash on attempt {workerAttempt}; re-sending to replacement worker.");
                        await Task.Delay(750).ConfigureAwait(false);
                        continue;
                    }
                    if (workerResponse["result"] is JObject workerResultObjNoTimeout && workerResultObjNoTimeout["correlationId"] == null)
                    {
                        workerResultObjNoTimeout["correlationId"] = correlationId;
                    }
                    if (workerResponse["error"] is JObject workerErrorObjNoTimeout && workerErrorObjNoTimeout["correlationId"] == null)
                    {
                        workerErrorObjNoTimeout["correlationId"] = correlationId;
                    }
                    return onSuccess(workerResponse);
                }

                // MCP-spec keepalive for long synchronous tool calls: while waiting on the
                // worker, emit `notifications/progress` every HeartbeatIntervalSeconds when the
                // client supplied a progressToken, so it doesn't fire its own request timeout
                // (the -32001 "Request timed out" users hit on long apply_pattern / delete).
                // The call stays synchronous and returns the real result inline — not a job.
                bool workerCompleted = await McpRouter.AwaitWithHeartbeat(
                    pending.CompletionSource.Task, timeoutMs, progressToken, heartbeat, toolName);
                if (workerCompleted)
                {
                    var workerResponse = JObject.Parse(await pending.CompletionSource.Task);
                    if (ShouldRetryWorkerCrash(workerResponse, toolName, workerAttempt))
                    {
                        Log($"[Retry] {toolName} hit worker crash on attempt {workerAttempt}; re-sending to replacement worker.");
                        await Task.Delay(750).ConfigureAwait(false);
                        continue;
                    }
                    if (workerResponse["result"] is JObject workerResultObj && workerResultObj["correlationId"] == null)
                    {
                        workerResultObj["correlationId"] = correlationId;
                    }
                    if (workerResponse["error"] is JObject workerErrorObj && workerErrorObj["correlationId"] == null)
                    {
                        workerErrorObj["correlationId"] = correlationId;
                    }
                    return onSuccess(workerResponse);
                }
                break; // timeout — fall through to the timeout handling below
            }

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                _operationTracker.MarkTimeout(operationId);
                BroadcastNotification("notifications/message", new
                {
                    level = "warning",
                    logger = "operation",
                    data = $"Operation {operationId} is still running after timeout budget.",
                    operationId,
                    correlationId,
                    status = "Running",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                _pendingRequests.TryRemove(requestId, out _);
            }

            Log($"{timeoutLogMessage} (operationId={operationId ?? "n/a"}, correlationId={correlationId})");
            return onTimeout(operationId, correlationId);
        }

        internal static int GetToolTimeoutMs(string? toolName, JObject? args)
        {
            if (toolName == "genexus_lifecycle" || toolName == "genexus_analyze" || toolName == "genexus_test")
            {
                return 600000;
            }

            string? part = args?["part"]?.ToString();
            if (string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(part, "Layout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Events", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "PatternVirtual", StringComparison.OrdinalIgnoreCase))
                {
                    return 180000;
                }
            }

            if (string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Events", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Rules", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Variables", StringComparison.OrdinalIgnoreCase))
                {
                    return 300000;
                }
            }

            // apply_pattern (esp. reapply) runs the WWP projection step, which on a
            // large host or an IDE-tab-held object takes minutes. The worker bounds it
            // with GENEXUS_MCP_REAPPLY_TIMEOUT_MS (default 5 min); align the gateway
            // ceiling so the client doesn't get a premature -32001 mid-reapply while the
            // worker is still legitimately working.
            if (string.Equals(toolName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase))
            {
                int reapplyMs = 300000;
                var envVal = Environment.GetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS");
                if (!string.IsNullOrWhiteSpace(envVal) && int.TryParse(envVal, out var parsed) && parsed > 0)
                    reapplyMs = parsed;
                // Add a 30s gateway-side cushion over the worker's own hard-timeout
                // window so that when the projection DOES return near the deadline, the
                // client receives the worker's rich envelope (slowReapply / recoveryRequired
                // / recoveryHint) rather than a bare transport -32001. If the STA call never
                // returns, the gateway times out here and recoveryHint tells the agent to
                // genexus_worker_reload mode=hard — the worker can't self-abort an STA SDK call.
                return reapplyMs + 30000;
            }

            return 60000;
        }

        internal static bool IsAsyncMutationTool(string? toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            return string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildAsyncAcceptedPayload(JobEntry job, string acceptedSummary)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            return new JObject
            {
                ["job_id"] = job.Id,
                ["operationId"] = job.Id,
                ["status"] = "running",
                ["estimated_seconds"] = job.EstimatedSeconds,
                ["pollTarget"] = "op:" + job.Id,
                ["hint"] = acceptedSummary + " poll genexus_lifecycle(action='status'|'result', target='op:" + job.Id + "') or watch _meta.background_jobs."
            };
        }

        internal static JObject BuildAsyncEditAcceptedPayload(JobEntry job)
            => BuildAsyncAcceptedPayload(job, "Edit accepted;");

        internal static JObject BuildAsyncVariableAcceptedPayload(JobEntry job)
            => BuildAsyncAcceptedPayload(job, "Variable update accepted;");

        internal static JObject BuildAsyncLifecycleAcceptedPayload(JobEntry job, string? action)
        {
            string acceptedSummary = string.Equals(action, "validate", StringComparison.OrdinalIgnoreCase)
                ? "Validate accepted;"
                : string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase)
                    ? "Rebuild accepted;"
                    : "Build accepted;";
            return BuildAsyncAcceptedPayload(job, acceptedSummary);
        }

        internal static string BuildAsyncMutationCompletionSummary(string? toolName, bool success)
        {
            bool isVariableTool = string.Equals(toolName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(toolName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(toolName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(toolName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase);
            if (isVariableTool)
            {
                return success ? "Variable update succeeded" : "Variable update failed";
            }

            return success ? "Edit succeeded" : "Edit failed";
        }

        internal static void NormalizeEditAndBuildPayload(JObject? payload)
        {
            if (payload == null) return;
            if (payload["build"] is not JObject buildBlock) return;

            string? taskId = buildBlock["taskId"]?.ToString() ?? buildBlock["TaskId"]?.ToString();
            if (string.IsNullOrWhiteSpace(taskId)) return;

            if (buildBlock["pollTarget"] == null)
            {
                // edit_and_build currently orchestrates its caller rebuild entirely on
                // the worker side, so the follow-up handle is the worker build taskId,
                // not a gateway background-job operationId.
                buildBlock["pollTarget"] = taskId;
            }

            if (buildBlock["hint"] == null)
            {
                buildBlock["hint"] = "Poll genexus_lifecycle(action='status'|'result', target='" + taskId + "') for the caller rebuild.";
            }
        }

        internal static bool IsSuccessfulBackgroundToolCompletion(JObject? workerEnvelope)
        {
            if (workerEnvelope == null) return false;
            if (workerEnvelope["error"] != null) return false;

            string? outerStatus = workerEnvelope["status"]?.ToString();
            if (string.Equals(outerStatus, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outerStatus, "Running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outerStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            JObject? resultObj = workerEnvelope["result"] as JObject;
            if (resultObj == null && workerEnvelope["result"]?.Type == JTokenType.String)
            {
                string? raw = workerEnvelope["result"]?.ToString();
                if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try { resultObj = JObject.Parse(raw); }
                    catch { }
                }
            }

            if (resultObj == null) return true;
            if (resultObj["error"] != null) return false;
            if (resultObj["isError"]?.ToObject<bool?>() == true) return false;

            string? innerStatus = resultObj["status"]?.ToString();
            if (string.Equals(innerStatus, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(innerStatus, "Running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(innerStatus, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(innerStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        internal static async Task<JObject?> ProcessMcpRequest(JObject request, string sessionId = "stdio")
        {
            string? method = request["method"]?.ToString();
            var idToken = request["id"];

            // Resolve KB once per request and stash in AsyncLocal so SendWorkerCommandAsync routes correctly.
            // Meta-tools (whoami, logs, doc, worker_reload, kb) don't address a specific KB and
            // must not trigger KB_AMBIGUOUS when several KBs are open.
            if (_kbResolver != null && _workerPool != null)
            {
                bool isMetaTool = false;
                string? toolNameForResolver = null;
                if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
                {
                    toolNameForResolver = (request["params"] as JObject)?["name"]?.ToString();
                    isMetaTool = !string.IsNullOrEmpty(toolNameForResolver) && IsMetaTool(toolNameForResolver);
                }

                if (!isMetaTool)
                {
                    try
                    {
                        string? kbArg = null;
                        if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
                        {
                            var paramsObj = request["params"] as JObject;
                            var argsObj = paramsObj?["arguments"] as JObject;
                            kbArg = argsObj?["kb"]?.ToString();
                            // Strip `kb` from worker-bound args (worker is single-KB scoped).
                            argsObj?.Remove("kb");
                        }
                        _currentKb.Value = _kbResolver.Resolve(kbArg, _workerPool.ListOpen(), _workerPool.ListKnown());
                    }
                    catch (KbResolutionException ex)
                    {
                        // Friction 2026-05-22 #63: surface suggested_next_step on KB_AMBIGUOUS
                        // (and KB_NOT_FOUND) so the agent knows to retry with kb=<alias>.
                        var dataObj = new JObject
                        {
                            ["code"] = ex.Code,
                            ["openKbs"] = JArray.FromObject(_workerPool!.ListOpen().Select(k => k.Alias))
                        };
                        var nextStep = McpRouter.AttachSuggestedNextStep(
                            new JObject { ["code"] = ex.Code, ["message"] = ex.Message });
                        if (nextStep != null) dataObj["suggested_next_step"] = nextStep;
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = new JObject
                            {
                                ["code"] = -32602,
                                ["message"] = ex.Message,
                                ["data"] = dataObj
                            }
                        };
                    }
                }
                else
                {
                    // Meta-tools still strip any stray `kb` arg (cosmetic) and leave _currentKb null.
                    var argsObj = (request["params"] as JObject)?["arguments"] as JObject;
                    argsObj?.Remove("kb");
                    _currentKb.Value = null;
                }
            }

            // Reject removed tools early with JSON-RPC -32601 + structured `data`
            if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
            {
                string? earlyToolName = (request["params"] as JObject)?["name"]?.ToString();
                if (!string.IsNullOrEmpty(earlyToolName) &&
                    RemovedToolsRegistry.Map.TryGetValue(earlyToolName, out var removedInfo))
                {
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32601,
                            ["message"] = $"Method not found: {earlyToolName}",
                            ["data"] = new JObject
                            {
                                ["replacedBy"] = removedInfo.ReplacedBy,
                                ["argHint"] = removedInfo.ArgHint
                            }
                        }
                    };
                }
            }

            // Protocol level
            var mcpResponse = McpRouter.Handle(request);
            if (mcpResponse != null)
            {
                if (string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase))
                {
                    TriggerWorkerWarmupOnce();
                    TriggerIndexBootstrapOnce();
                    UpdateNotifier.TriggerOnce();
                }
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = idToken?.DeepClone(), ["result"] = JToken.FromObject(mcpResponse) };
            }

            // Tool Calls
            if (method == "tools/call")
            {
                // ... (logic handled below) ...
            }

            // Resource Calls
            if (method == "resources/read")
            {
                var rawWorkerCmd = McpRouter.ConvertResourceCall(request);
                var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                if (workerCmd != null)
                {
                    workerCmd["client"] = "mcp";
                    return await SendWorkerCommandAsync(
                        workerCmd,
                        60000,
                        $"Timeout waiting for resource: {request["params"]?["uri"]}",
                        resultObj =>
                        {
                            if (resultObj["error"] != null || string.Equals(resultObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                            {
                                // v2.8.0: error details live under error.{message,hint} in the canonical envelope.
                                var errBlock = resultObj["error"] as JObject;
                                string errorMsg = errBlock?["message"]?.ToString()
                                    ?? errBlock?["hint"]?.ToString()
                                    ?? resultObj["error"]?.ToString()
                                    ?? "Unknown error reading resource";

                                // Enrich with suggestions if available (from HealingService)
                                string? suggestion = resultObj["suggestion"]?.ToString() ?? errBlock?["hint"]?.ToString();
                                if (!string.IsNullOrEmpty(suggestion) && !errorMsg.Contains(suggestion)) errorMsg += "\n" + suggestion;

                                string? tip = resultObj["actionable_tip"]?.ToString();
                                if (!string.IsNullOrEmpty(tip)) errorMsg += "\nTip: " + tip;

                                return new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = idToken?.DeepClone(),
                                    ["error"] = JToken.FromObject(new { code = -32603, message = $"GeneXus MCP Worker error: {errorMsg}" })
                                };
                            }

                            // v2.8.0: tool payload is under result.source; fall back to result as string.
                            var resultPayload = resultObj["result"] as JObject;
                            var content = resultPayload?["source"]?.ToString()
                                ?? resultObj["result"]?.ToString()
                                ?? "";
                            return new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = idToken?.DeepClone(),
                                ["result"] = JToken.FromObject(new
                                {
                                    contents = new[]
                                    {
                                        new
                                        {
                                            uri = request["params"]?["uri"]?.ToString(),
                                            mimeType = "text/plain",
                                            text = content
                                        }
                                    }
                                })
                            };
                        },
                        (_, correlationId) => new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32000, message = "GeneXus MCP Worker timed out reading resource.", correlationId })
                        },
                        toolName: "resources/read",
                        toolArgs: request["params"] as JObject,
                        trackOperation: false);
                }
            }

            // Tool Calls (Actual logic)
            if (method == "tools/call")
            {
                var paramsObj = request["params"] as JObject;
                string toolName = paramsObj?["name"]?.ToString() ?? "";
                var args = paramsObj?["arguments"] as JObject;

                // Soft-alias rewrite for consolidated umbrella tools. Runs here (before
                // the gateway-only handlers below AND before McpRouter.ConvertToolCall
                // downstream) so every code path sees the post-rewrite name and args.
                // Default ON; opt out with GXMCP_LEGACY_TOOL_ALIASES=0.
                if (!string.IsNullOrEmpty(toolName)
                    && Environment.GetEnvironmentVariable("GXMCP_LEGACY_TOOL_ALIASES") != "0"
                    && McpRouter.TryRewriteLegacyTool(toolName, args, out var rewrittenName, out var rewrittenArgs))
                {
                    toolName = rewrittenName;
                    args = rewrittenArgs;
                    if (paramsObj != null)
                    {
                        paramsObj["name"] = rewrittenName;
                        paramsObj["arguments"] = rewrittenArgs;
                    }
                }

                // Gateway-side schema pre-validation: reject malformed args immediately,
                // before forwarding to the worker. Saves an STA round-trip and gives LLM
                // clients faster feedback on typos / missing required fields.
                if (!string.IsNullOrEmpty(toolName))
                {
                    var validationResult = GatewayArgsValidator.Validate(toolName, args);
                    if (!validationResult.Ok)
                    {
                        var firstViolation = validationResult.Violations[0];
                        string hint = firstViolation.Actual == "missing"
                            ? $"Required field '{firstViolation.Path}' is missing — expected {firstViolation.Expected}."
                            : $"Field '{firstViolation.Path}': expected {firstViolation.Expected}, got {firstViolation.Actual}.";

                        var violationsArr = new Newtonsoft.Json.Linq.JArray(
                            validationResult.Violations.Select(v => new JObject
                            {
                                ["path"] = v.Path,
                                ["expected"] = v.Expected,
                                ["actual"] = v.Actual
                            }));

                        var invalidArgsPayload = new JObject
                        {
                            ["status"] = "error",
                            ["error"] = new JObject
                            {
                                ["code"] = "InvalidArgs",
                                ["message"] = $"Arguments for tool '{toolName}' failed schema validation.",
                                ["hint"] = hint,
                                ["nextSteps"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["tool"] = "genexus_orient",
                                        ["args"] = new JObject(),
                                        ["why"] = "Lists each tool's input schema."
                                    }
                                },
                                ["violations"] = violationsArr
                            }
                        };

                        return BuildToolTextResponse(idToken, invalidArgsPayload, isError: true, toolName: toolName, toolArgs: args);
                    }
                }

                // Auto-inject 'type' when the LLM omits it but 'name' resolves to a
                // unique object in the cached index.  Runs AFTER schema pre-validation so
                // we never inject into a known-bad call.  Mutates args in-place; the
                // injectedType local is checked after dispatch to annotate the response.
                string? _autoInjectedType = null;
                if (!string.IsNullOrEmpty(toolName) && args != null
                    && AutoTypeInjector.TryInject(toolName, args, out var _ait))
                {
                    _autoInjectedType = _ait;
                    // Keep paramsObj["arguments"] in sync (same ref, but be explicit)
                    if (paramsObj != null) paramsObj["arguments"] = args;
                }

                // Friction 2026-05-22: genexus_worker_reload force=true bypasses
                // the JSON-RPC pipe and kills the worker directly. The soft path
                // is unreachable when the worker is wedged on a hung preview
                // subprocess — by definition it can't ACK the reload command.
                if (string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase)
                    && args?["force"]?.ToObject<bool?>() == true)
                {
                    // Refuse the force path when configuration isn't loaded — otherwise we'd
                    // kill the worker pool with no way to bring it back up and the caller
                    // would only learn from subsequent tool failures.
                    if (_activeConfig == null)
                    {
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Force-reload refused: no active configuration. The gateway hasn't completed startup or a prior config load failed; respawning the worker without a config would leave the pool empty." })
                        };
                    }
                    try
                    {
                        if (_workerPool != null)
                        {
                            using (SuppressEagerRespawn())
                            {
                                _workerPool.StopAll();
                            }
                        }
                        _semanticCache.Clear();
                        StartWorker(_activeConfig);
                        BroadcastToolsListChanged("worker_reloaded_force");
                        BroadcastResourcesListChanged("worker_reloaded_force");
                        var ok = new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["result"] = new JObject
                            {
                                ["content"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = "{\"status\":\"Forced\",\"detail\":\"Worker process(es) killed by gateway and respawned. Any in-flight worker job was abandoned.\"}"
                                    }
                                }
                            }
                        };
                        return ok;
                    }
                    catch (Exception ex)
                    {
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Force-reload failed: " + ex.Message })
                        };
                    }
                }

                // Non-force genexus_worker_reload: gateway-orchestrated graceful drain + respawn.
                // Previously this fell through to the worker as a normal RPC; the worker ACK'd
                // then exited, leaving a window where AcquireAsync returned the dying process.
                // Now the gateway marks the pool entry as draining (blocking concurrent
                // AcquireAsync callers), sends StopWithReason(PlannedReload), waits for the OS
                // process to exit, spawns a fresh worker, awaits its SdkReadyTask, then responds.
                if (string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase)
                    && args?["force"]?.ToObject<bool?>() != true)
                {
                    if (_activeConfig == null || _workerPool == null)
                    {
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Reload refused: gateway has no active configuration." })
                        };
                    }
                    string? reloadAlias = args?["alias"]?.ToString();
                    KbHandle? reloadKb = null;
                    if (!string.IsNullOrWhiteSpace(reloadAlias))
                    {
                        reloadKb = _workerPool.ListOpen().FirstOrDefault(h =>
                            string.Equals(h.NormalizedAlias, reloadAlias.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        reloadKb = _workerPool.ListOpen().FirstOrDefault();
                    }
                    if (reloadKb == null)
                    {
                        return BuildToolTextResponse(idToken,
                            new JObject { ["status"] = "NoWorker", ["detail"] = "No open KB worker found to reload." },
                            isError: false, toolName: toolName, toolArgs: args);
                    }
                    try
                    {
                        using (SuppressEagerRespawn())
                        {
                            // Drain timeout: 30s for the old worker process to exit.
                            var newWorker = await _workerPool.DrainAndReplaceAsync(reloadKb, drainTimeoutMs: 30_000, ct: CancellationToken.None).ConfigureAwait(false);
                            // Wait for the new worker's SDK to be ready (cap at 180s to avoid hanging).
                            bool sdkReady = await McpRouter.AwaitWithHeartbeat(
                                newWorker.SdkReadyTask, timeoutMs: 180_000,
                                progressToken: null, heartbeat: null,
                                toolName: "worker_reload").ConfigureAwait(false);
                            BroadcastToolsListChanged("worker_reloaded_soft");
                            BroadcastResourcesListChanged("worker_reloaded_soft");
                            return BuildToolTextResponse(idToken,
                                new JObject
                                {
                                    ["status"] = "Reloaded",
                                    ["swappedAndReady"] = sdkReady,
                                    ["detail"] = sdkReady
                                        ? "Worker gracefully drained and replaced; new worker is SDK-ready."
                                        : "Worker replaced but new worker did not signal SDK-ready within 180s."
                                },
                                isError: false, toolName: toolName, toolArgs: args);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Reload] Soft reload failed for KB '{reloadKb.Alias}': {ex.Message}");
                        return new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = idToken?.DeepClone(),
                            ["error"] = JToken.FromObject(new { code = -32603, message = "Soft reload failed: " + ex.Message })
                        };
                    }
                }

                // genexus_telemetry: gateway-only short-circuit for ring-buffer views (executions, watch_event).
                // Other actions (logs/friction_*/learning_report/profile_*) fall through to the router.
                if (string.Equals(toolName, "genexus_telemetry", StringComparison.OrdinalIgnoreCase))
                {
                    string? telAction = args?["action"]?.ToString()?.ToLowerInvariant();
                    if (telAction == "executions")
                    {
                        string targetName = args?["target"]?.ToString() ?? args?["name"]?.ToString();
                        int lastN = args?["last"]?.ToObject<int?>() ?? 10;
                        JObject historyPayload = _operationTracker?.BuildExecutionHistory(targetName, lastN)
                            ?? new JObject { ["status"] = "Unwired", ["code"] = "TrackerUnavailable", ["runs"] = new JArray() };
                        return BuildToolTextResponse(idToken, historyPayload, isError: false, toolName: "genexus_telemetry", toolArgs: args);
                    }
                    if (telAction == "watch_event")
                    {
                        string watchTarget = args?["target"]?.ToString() ?? args?["name"]?.ToString();
                        string watchEvent = args?["event"]?.ToString();
                        int watchLast = args?["last"]?.ToObject<int?>() ?? 10;
                        JObject watchPayload = _operationTracker?.BuildWatchEvent(watchTarget, watchEvent, watchLast)
                            ?? new JObject { ["status"] = "Unwired", ["code"] = "TrackerUnavailable", ["runs"] = new JArray() };
                        return BuildToolTextResponse(idToken, watchPayload, isError: false, toolName: "genexus_telemetry", toolArgs: args);
                    }
                    // Any other action falls through to OperationsRouter ConvertTelemetryUmbrella.
                }

                // genexus_kb — meta-tool for managing the WorkerPool (list/open/close).
                // Handled entirely in the Gateway; never reaches a Worker. The
                // set_startup/get_startup actions are SDK-bound and fall through
                // to the router pipeline (SystemRouter forwards them to the
                // worker's KB module).
                if (string.Equals(toolName, "genexus_kb", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "set_startup", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args?["action"]?.ToString(), "get_startup", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        if (_workerPool == null)
                        {
                            throw new InvalidOperationException("WorkerPool not initialised.");
                        }

                        string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
                        switch (action)
                        {
                            case "list":
                                payload = new JObject
                                {
                                    ["openKbs"] = JArray.FromObject(_workerPool.Snapshot()
                                        .Select(s => new
                                        {
                                            alias = s.Handle.Alias,
                                            path = s.Handle.Path,
                                            pid = s.Pid,
                                            workingSetBytes = s.WorkingSetBytes,
                                            workingSetMB = s.WorkingSetBytes.HasValue
                                                ? Math.Round(s.WorkingSetBytes.Value / (1024.0 * 1024.0), 1)
                                                : (double?)null,
                                            lastActivityUtc = s.LastActivityUtc,
                                            idleSeconds = (int)Math.Max(0, (DateTime.UtcNow - s.LastActivityUtc).TotalSeconds)
                                        })),
                                    ["maxOpenKbs"] = _activeConfig?.Server?.MaxOpenKbs ?? 3,
                                    ["defaultKb"] = _activeConfig?.Environment?.DefaultKb,
                                    ["declaredKbs"] = JArray.FromObject(
                                        (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                                            .Select(k => new { alias = k.Alias, path = k.Path }))
                                };
                                break;
                            case "set_default":
                            {
                                string? alias = args?["alias"]?.ToString();
                                if (string.IsNullOrWhiteSpace(alias))
                                    throw new ArgumentException("Missing 'alias' for action=set_default.");
                                if (_activeConfig == null || string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                                    throw new InvalidOperationException("No active config to persist.");
                                var declared = _activeConfig.Environment?.KBs?.FirstOrDefault(
                                    k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                // issue #26 P4: accept any alias that is currently open or was
                                // opened this session (ad-hoc via `open path=...`), not just the
                                // ones pre-declared in config.json. When the alias exists only as
                                // an open/known handle, promote it to a declared KbEntry so the
                                // default survives a restart — instead of dead-ending with
                                // KB_NOT_FOUND right after `open` succeeded.
                                string resolvedAlias;
                                string? resolvedPath = null;
                                if (declared != null)
                                {
                                    resolvedAlias = declared.Alias;
                                    resolvedPath = declared.Path;
                                }
                                else
                                {
                                    var known = _workerPool.ListKnown().FirstOrDefault(
                                        k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                    if (known == null)
                                        throw new KbResolutionException("KB_NOT_FOUND",
                                            $"Alias '{alias}' is neither declared in config.Environment.KBs[] nor currently open. Use 'open' with a path first, or add it to config.");
                                    resolvedAlias = known.Alias;
                                    resolvedPath = known.Path;
                                }
                                // Patch the JSON on disk to preserve any fields we don't model.
                                string configPath = Configuration.CurrentConfigPath!;
                                JObject root;
                                try { root = JObject.Parse(System.IO.File.ReadAllText(configPath)); }
                                catch (Exception ex) { throw new InvalidOperationException($"Failed to read config.json: {ex.Message}"); }
                                if (root["Environment"] is not JObject envObj)
                                {
                                    envObj = new JObject();
                                    root["Environment"] = envObj;
                                }
                                bool promoted = false;
                                if (declared == null && !string.IsNullOrWhiteSpace(resolvedPath))
                                {
                                    // Persist the ad-hoc KB as a declared entry so it's resolvable
                                    // after a restart, mirroring the in-memory _known registry.
                                    if (envObj["KBs"] is not JArray kbsArr)
                                    {
                                        kbsArr = new JArray();
                                        envObj["KBs"] = kbsArr;
                                    }
                                    bool alreadyThere = kbsArr.OfType<JObject>().Any(o =>
                                        string.Equals(o["Alias"]?.ToString(), resolvedAlias, StringComparison.OrdinalIgnoreCase));
                                    if (!alreadyThere)
                                    {
                                        kbsArr.Add(new JObject { ["Alias"] = resolvedAlias, ["Path"] = resolvedPath });
                                        _activeConfig.Environment!.KBs.Add(new KbEntry { Alias = resolvedAlias, Path = resolvedPath });
                                        promoted = true;
                                    }
                                }
                                envObj["DefaultKb"] = resolvedAlias;
                                System.IO.File.WriteAllText(configPath, root.ToString(Formatting.Indented));
                                _activeConfig.Environment!.DefaultKb = resolvedAlias;
                                payload = new JObject
                                {
                                    ["defaultKb"] = resolvedAlias,
                                    ["persistedTo"] = configPath,
                                    ["promotedToDeclared"] = promoted
                                };
                                break;
                            }
                            case "open":
                            {
                                string? alias = args?["alias"]?.ToString();
                                string? path = args?["path"]?.ToString();
                                KbHandle handleToOpen;
                                // Path wins: register ad-hoc (with optional caller-supplied alias).
                                if (!string.IsNullOrWhiteSpace(path))
                                {
                                    string finalAlias = string.IsNullOrWhiteSpace(alias)
                                        ? System.IO.Path.GetFileName(path!.TrimEnd('\\', '/')).ToLowerInvariant()
                                        : alias!;
                                    if (string.IsNullOrEmpty(finalAlias)) finalAlias = "adhoc";
                                    handleToOpen = new KbHandle(finalAlias, path!);
                                }
                                // No path → resolve the alias against config-declared KBs.
                                else if (!string.IsNullOrWhiteSpace(alias) && _activeConfig != null)
                                {
                                    handleToOpen = new KbResolver(_activeConfig).Resolve(alias, _workerPool.ListOpen(), _workerPool.ListKnown());
                                }
                                else
                                {
                                    throw new ArgumentException("Provide 'path' (ad-hoc) or 'alias' of a KB declared in config.Environment.KBs[].");
                                }

                                var w = await _workerPool.AcquireAsync(handleToOpen, CancellationToken.None);
                                // issue #26 P4: opening a KB makes it the active one for this
                                // session so whoami/doctor and no-`kb`-arg calls reflect it,
                                // instead of continuing to report the empty scaffold. This is
                                // in-memory only; `set_default` persists to config.json.
                                if (_activeConfig?.Environment != null)
                                {
                                    _activeConfig.Environment.DefaultKb = handleToOpen.Alias;
                                }
                                _currentKb.Value = handleToOpen;
                                payload = new JObject
                                {
                                    ["opened"] = handleToOpen.Alias,
                                    ["path"] = handleToOpen.Path,
                                    ["workerPid"] = w?.Pid,
                                    ["active"] = true
                                };
                                break;
                            }
                            case "close":
                            {
                                string? alias = args?["alias"]?.ToString();
                                if (string.IsNullOrWhiteSpace(alias))
                                {
                                    throw new ArgumentException("Missing 'alias' for action=close.");
                                }
                                bool closed = _workerPool.Close(alias!);
                                payload = new JObject { ["closed"] = closed, ["alias"] = alias };
                                break;
                            }
                            default:
                                throw new ArgumentException($"Unknown action '{action}'. Use list|open|close.");
                        }
                    }
                    catch (KbResolutionException ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = ex.Code };
                    }
                    catch (WorkerPoolFullException ex)
                    {
                        isError = true;
                        payload = new JObject
                        {
                            ["error"] = ex.Message,
                            ["code"] = "KB_POOL_FULL",
                            ["openKbs"] = JArray.FromObject(ex.OpenKbs.Select(k => k.Alias))
                        };
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message };
                    }

                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb", args);
                }

                // Item 53: genexus_worker_pool action=warm_spares — gateway-side
                // meta-tool. Configures N pre-spawned workers bound to declared KBs
                // so the first KB-bound call doesn't pay cold-start. Capped at
                // WorkerPool.MaxWarmSpareCount; spareCount<0 disables. Never reaches
                // a worker — purely a gateway lifecycle knob.
                if (string.Equals(toolName, "genexus_worker_pool", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
                        string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "warm_spares";
                        if (action != "warm_spares")
                            throw new ArgumentException($"Unknown action '{action}'. Use warm_spares.");
                        int spareCount = args?["spareCount"]?.ToObject<int?>() ?? args?["count"]?.ToObject<int?>() ?? 0;
                        var declared = (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                            .Select(k => new KbHandle(k.Alias, k.Path))
                            .ToList();
                        var result = _workerPool.ConfigureWarmSpares(spareCount, declared);
                        payload = new JObject
                        {
                            ["status"] = result.Configured == 0 ? "Disabled" : "Configured",
                            ["requested"] = result.Requested,
                            ["configured"] = result.Configured,
                            ["capped"] = result.Capped,
                            ["maxAllowed"] = WorkerPool.MaxWarmSpareCount,
                            ["prespawned"] = JArray.FromObject(result.Prespawned),
                            ["skipped"] = JArray.FromObject(result.Skipped),
                            ["declaredKbCount"] = declared.Count
                        };
                        if (result.Capped)
                            payload["warning"] = $"spareCount={result.Requested} exceeded cap {WorkerPool.MaxWarmSpareCount}; clamped to {result.Configured}.";
                        if (result.Configured > declared.Count)
                            payload["note"] = $"requested {result.Configured} spares but only {declared.Count} KBs declared in config.Environment.KBs[]; declare more aliases to use the full budget.";
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_worker_pool", args);
                }

                // Item 54: genexus_sandbox — gateway-side filesystem clone of a KB.
                // No SDK touch; pure file copy under <configRoot>/sandboxes/<name>/.
                // remove is idempotent. create on an existing target returns
                // {status:"AlreadyExists"} unless overwrite=true.
                if (string.Equals(toolName, "genexus_sandbox", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "create";
                        string name = args?["name"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing 'name'.");
                        // Sanitize: alphanumeric + dash/underscore only.
                        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
                            throw new ArgumentException("name must match [A-Za-z0-9_-]+ (no spaces/path-separators).");

                        string configDir = !string.IsNullOrEmpty(Configuration.CurrentConfigPath)
                            ? System.IO.Path.GetDirectoryName(Configuration.CurrentConfigPath!)!
                            : AppContext.BaseDirectory;
                        string sandboxRoot = System.IO.Path.Combine(configDir, "sandboxes");
                        string targetPath = System.IO.Path.Combine(sandboxRoot, name);

                        if (action == "remove")
                        {
                            bool existed = System.IO.Directory.Exists(targetPath);
                            if (existed)
                            {
                                try { System.IO.Directory.Delete(targetPath, true); }
                                catch (Exception ex) { throw new InvalidOperationException($"Failed to remove sandbox: {ex.Message}"); }
                            }
                            payload = new JObject
                            {
                                ["status"] = existed ? "Removed" : "NotFound",
                                ["name"] = name,
                                ["path"] = targetPath
                            };
                        }
                        else if (action == "create")
                        {
                            string from = args?["from"]?.ToString();
                            if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Missing 'from' (source KB alias or path).");
                            bool overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false;

                            // Resolve `from` as alias against config, else treat as path.
                            string sourcePath = null;
                            var fromKb = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                                k => string.Equals(k.Alias, from, StringComparison.OrdinalIgnoreCase));
                            if (fromKb != null) sourcePath = fromKb.Path;
                            else if (System.IO.Directory.Exists(from)) sourcePath = from;
                            else throw new ArgumentException($"'from'='{from}' is not a declared KB alias and not an existing directory.");

                            if (System.IO.Directory.Exists(targetPath))
                            {
                                if (!overwrite)
                                {
                                    payload = new JObject
                                    {
                                        ["status"] = "AlreadyExists",
                                        ["name"] = name,
                                        ["path"] = targetPath,
                                        ["hint"] = "Pass overwrite=true to replace, or action=remove first."
                                    };
                                    return BuildToolTextResponse(idToken, payload, false, "genexus_sandbox", args);
                                }
                                try { System.IO.Directory.Delete(targetPath, true); }
                                catch (Exception ex) { throw new InvalidOperationException($"Failed to remove existing sandbox before overwrite: {ex.Message}"); }
                            }

                            System.IO.Directory.CreateDirectory(sandboxRoot);
                            var copy = SandboxCopyHelper.CopyDirectory(sourcePath, targetPath);
                            payload = new JObject
                            {
                                ["status"] = "Created",
                                ["name"] = name,
                                ["path"] = targetPath,
                                ["from"] = sourcePath,
                                ["filesCopied"] = copy.Files,
                                ["bytesCopied"] = copy.Bytes,
                                ["durationMs"] = copy.DurationMs,
                                ["alias"] = "sandbox-" + name,
                                ["hint"] = $"Open with: genexus_kb action=open path=\"{targetPath}\" alias=sandbox-{name}"
                            };
                        }
                        else
                        {
                            throw new ArgumentException($"Unknown action '{action}'. Use create|remove.");
                        }
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_sandbox", args);
                }

                // Item 55: genexus_kb_diff — gateway-side object-index diff between two
                // KB directories. No SDK touch. Walks each KB's filesystem Objects/<Type>/<Name>/
                // tree (and .gx/index-snapshot.bin if present) and returns
                // {onlyInA, onlyInB, modified[]}.
                if (string.Equals(toolName, "genexus_kb_diff", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        string kbA = args?["kbA"]?.ToString();
                        string kbB = args?["kbB"]?.ToString();
                        if (string.IsNullOrWhiteSpace(kbA) || string.IsNullOrWhiteSpace(kbB))
                            throw new ArgumentException("Both 'kbA' and 'kbB' are required (alias or path).");
                        string pathA = ResolveKbPath(kbA) ?? throw new ArgumentException($"'kbA'='{kbA}' not a declared alias and not an existing directory.");
                        string pathB = ResolveKbPath(kbB) ?? throw new ArgumentException($"'kbB'='{kbB}' not a declared alias and not an existing directory.");
                        if (string.Equals(System.IO.Path.GetFullPath(pathA), System.IO.Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("kbA and kbB resolve to the same path.");
                        payload = KbDiffHelper.Diff(pathA, pathB);
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_diff", args);
                }

                // Item 56: genexus_kb_import — limited filesystem-level copy of an
                // object's files between two KBs. Full SDK-level import would require
                // opening a second KB inside the worker (one SDK can only host one
                // KB at a time), so this ships as a directory-copy + index-rescan
                // recommendation. Callers should run genexus_lifecycle action=index
                // afterwards.
                if (string.Equals(toolName, "genexus_kb_import", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload;
                    bool isError = false;
                    try
                    {
                        string from = args?["from"]?.ToString();
                        string name = args?["name"]?.ToString();
                        string type = args?["type"]?.ToString();
                        if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Missing 'from' (source KB alias or path).");
                        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing 'name' (object name to import).");
                        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Missing 'type' (object type, e.g. WebPanel, Procedure).");
                        string sourcePath = ResolveKbPath(from) ?? throw new ArgumentException($"'from'='{from}' not a declared alias and not an existing directory.");
                        // Active KB resolution: use first open KB or default.
                        string targetPath = null;
                        var openKbs = _workerPool?.ListOpen();
                        if (openKbs != null && openKbs.Count > 0) targetPath = openKbs[0].Path;
                        else if (!string.IsNullOrEmpty(_activeConfig?.Environment?.DefaultKb))
                        {
                            var d = _activeConfig.Environment.KBs?.FirstOrDefault(k =>
                                string.Equals(k.Alias, _activeConfig.Environment.DefaultKb, StringComparison.OrdinalIgnoreCase));
                            targetPath = d?.Path;
                        }
                        if (string.IsNullOrEmpty(targetPath))
                        {
                            payload = new JObject
                            {
                                ["status"] = "MissingFeature",
                                ["code"] = "NoActiveKb",
                                ["error"] = "kb_import requires an open active KB. Open one via genexus_kb action=open."
                            };
                            isError = true;
                            return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_import", args);
                        }
                        if (string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("source and target KB resolve to the same path.");
                        payload = KbImportHelper.ImportObject(sourcePath, targetPath, name, type);
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                    }
                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_import", args);
                }

                if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
                {
                    string? lifecycleAction = args?["action"]?.ToString();
                    string? lifecycleTarget = args?["target"]?.ToString();
                    if ((string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                        lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                    {
                        string operationId = lifecycleTarget.Substring(3);
                        // v2.6.2 (Item B follow-up): JobRegistry covers build/edit jobs;
                        // OperationTracker covers gateway-internal request lifecycles.
                        // status/result with op:<id> should resolve in EITHER — fall through
                        // to the JobRegistry long-poll path below when the id is a job.
                        if (JobRegistry.Get(operationId) == null)
                        {
                            JObject opPayload = string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                                ? _operationTracker.BuildOperationResult(operationId)
                                : _operationTracker.BuildOperationStatus(operationId);
                            return BuildToolTextResponse(
                                idToken,
                                opPayload,
                                isError: string.Equals(opPayload["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase),
                                toolName: "genexus_lifecycle",
                                toolArgs: args);
                        }
                    }

                    // FR#7 (friction-report 2026-05-14): support best-effort cancellation for
                    // op:<id> targets. Previously this fell through to the worker, which only
                    // knows about build taskIds, returning "Task ID not found". Now we mark
                    // the op as Cancelled in the tracker and abandon any matching pending
                    // request. The worker thread may still finish its SDK call, but the
                    // client gets a deterministic answer.
                    // v2.3.8 (Task 7.2 + post-fix) — cancel via job_id: short-circuit the
                    // async pollers (build/edit) by signaling the registered CTS, flip the
                    // job to "cancelled", AND fan a Control:Cancel command out to the
                    // worker so the in-flight thread-safe handler (search/impact) can
                    // stop mid-loop. Without the fan-out the worker kept running its
                    // current call to completion while the gateway poller exited.
                    if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        string? cancelJobId = McpRouter.ResolveJobId(args);
                        if (!string.IsNullOrWhiteSpace(cancelJobId) && JobRegistry.Get(cancelJobId!) != null)
                        {
                            bool ok = JobRegistry.Cancel(cancelJobId!, "Cancelled by client via lifecycle action=cancel.");
                            // Fire-and-forget worker signal. Thread-safe Control:Cancel runs
                            // on the parallel dispatch path so it interleaves with in-flight calls.
                            _ = SendWorkerCommandAsync(
                                new JObject
                                {
                                    ["method"] = "control",
                                    ["module"] = "Control",
                                    ["action"] = "Cancel",
                                    ["params"] = new JObject { ["cancelToken"] = cancelJobId }
                                },
                                5000, "cancel-fanout",
                                env => env,
                                (_, __) => new JObject(),
                                toolName: "genexus_lifecycle", toolArgs: args, trackOperation: false);
                            var jp = new JObject
                            {
                                ["status"] = ok ? "Cancelled" : "NotFound",
                                ["jobId"] = cancelJobId,
                                ["message"] = ok
                                    ? "Job marked Cancelled and Control:Cancel fanned out to the worker. Handlers honouring CancellationToken (search, analyze, build expansion) will terminate within one iteration."
                                    : "Job not found in registry (may have completed and been pruned)."
                            };
                            return BuildToolTextResponse(idToken, jp, isError: !ok, toolName: "genexus_lifecycle", toolArgs: args);
                        }
                    }

                    if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                        lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                    {
                        string operationId = lifecycleTarget.Substring(3);
                        bool existed = _operationTracker.MarkCancelled(operationId, "Cancelled by client via lifecycle action=cancel.");
                        // Try to find and abandon the pending request bound to this op.
                        string? abandonedRequestId = null;
                        foreach (var kvp in _pendingRequests.ToArray())
                        {
                            if (string.Equals(kvp.Value.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
                            {
                                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                                {
                                    abandonedRequestId = kvp.Key;
                                    pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(new
                                    {
                                        jsonrpc = "2.0",
                                        id = kvp.Key,
                                        error = new { code = -32603, message = "Operation cancelled by client." }
                                    }));
                                    break;
                                }
                            }
                        }

                        var cancelPayload = new JObject
                        {
                            ["status"] = existed ? "Cancelled" : "NotFound",
                            ["operationId"] = operationId,
                            ["abandonedRequestId"] = abandonedRequestId,
                            ["message"] = existed
                                ? "Operation marked as Cancelled. Worker may still finish its current SDK call but no further response will be delivered."
                                : "Operation not found in tracker (may have completed and been pruned, or never existed)."
                        };
                        return BuildToolTextResponse(idToken, cancelPayload, isError: !existed, toolName: "genexus_lifecycle", toolArgs: args);
                    }

                    if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(lifecycleTarget, "gateway:metrics", StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildToolTextResponse(idToken, _operationTracker.BuildMetricsPayload(), isError: false, toolName: "genexus_lifecycle", toolArgs: args);
                    }

                    // action=result + op:<jobId> — return the stored JobEntry.Result directly.
                    // v2.6.3 fixed status/cancel for op:<id> via JobRegistry but result still
                    // forwarded to the worker, which only knows about its internal taskId and
                    // returned "Task ID not found" for completed jobs (visible in
                    // _meta.background_jobs). Symmetric handler closes that gap.
                    if (string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase))
                    {
                        string? resultJobId = McpRouter.ResolveJobId(args);
                        if (!string.IsNullOrWhiteSpace(resultJobId))
                        {
                            var probe = JobRegistry.Get(resultJobId);
                            if (probe != null)
                            {
                                // Envelope shape extracted into McpRouter.BuildJobResultEnvelope
                                // for unit-test coverage and parity with status long-poll.
                                var (resultPayload, isErr) = McpRouter.BuildJobResultEnvelope(probe);
                                return BuildToolTextResponse(idToken, resultPayload, isError: isErr, toolName: "genexus_lifecycle", toolArgs: args);
                            }
                            // Unknown id → fall through to the legacy worker-side taskId result path.
                        }
                    }

                    // Long-poll intercept (Task 4.5): action=status + job_id (BackgroundJobRegistry)
                    // wait_seconds is clamped [0, MaxLongPollSeconds]; 0 = immediate poll (default behaviour).
                    if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase))
                    {
                        string? jobId = McpRouter.ResolveJobId(args);
                        if (!string.IsNullOrWhiteSpace(jobId))
                        {
                            // Check registry first; only route to the long-poll path when the job
                            // is known to the registry. Unknown IDs fall through to the legacy
                            // worker-side taskId status path for backward compatibility.
                            var probe = JobRegistry.Get(jobId);
                            if (probe != null)
                            {
                                int waitSeconds = Math.Min(Math.Max(args?["wait_seconds"]?.ToObject<int?>() ?? 0, 0), McpRouter.MaxLongPollSeconds);
                                var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                                bool hasProgressToken = clientProgressToken != null && clientProgressToken.Type != JTokenType.Null;
                                JObject pollResult = await McpRouter.LongPollJob(
                                    JobRegistry, jobId, waitSeconds,
                                    progressToken: clientProgressToken,
                                    heartbeat: hasProgressToken ? (n => TryWriteStdout(n.ToString(Formatting.None))) : null);
                                bool isError = pollResult["error"] != null;
                                // Friction 2026-05-22 item 10: a "Build succeeded: 0w/0e/exit=0"
                                // result was previously dropped onto an <e>error{}> envelope when
                                // the JobEntry.Status string didn't match what callers expected.
                                // Run the build-outcome classifier on the inner BuildTaskStatus
                                // (if present) so the envelope's isError matches the actual outcome.
                                if (!isError && pollResult["result"] is JObject buildPayloadEarly)
                                {
                                    var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(buildPayloadEarly);
                                    if (outcome == LifecycleResponseShaper.BuildOutcome.Error) isError = true;
                                    else if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                                    {
                                        // Surface partial_success on the outer envelope as a warning marker.
                                        pollResult["partial_success"] = true;
                                        if (pollResult["envelope"] == null) pollResult["envelope"] = "warning";
                                    }
                                    else if (outcome == LifecycleResponseShaper.BuildOutcome.Success)
                                    {
                                        // Defensive: a job stamped "failed" by the registry but whose
                                        // BuildTaskStatus says Succeeded/0/0/exit=0 should not be an error.
                                        // (We've seen this race: registry summary stamped before final
                                        // status normalized.) Keep isError=false in that case.
                                    }
                                }
                                // v2.3.8 (post-Task 6.1 fix): the DispatchCore compact pass below
                                // only runs for the worker-side legacy taskId path. job_id results
                                // arrive through this short-circuit and were skipping the shaper —
                                // callers using wait_seconds>0 + job_id were getting the verbose
                                // BuildTaskStatus payload under result. Compact here too.
                                if (!isError
                                    && LifecycleResponseShaper.ShouldCompact(args)
                                    && pollResult["result"] is JObject innerResult)
                                {
                                    var compactJson = LifecycleResponseShaper.Compact(innerResult.ToString(Formatting.None), compact: true);
                                    try { pollResult["result"] = JObject.Parse(compactJson); }
                                    catch { /* shaper passthrough on non-JSON */ }
                                }
                                return BuildToolTextResponse(idToken, pollResult, isError: isError, toolName: "genexus_lifecycle", toolArgs: args);
                            }
                            // Not in registry → fall through to existing worker-side status path (legacy taskId).
                        }
                    }
                }

                // Idempotency middleware wraps the rest of the tool dispatch
                // Scope cache by the resolved KB so independent KBs don't share idempotency.
                string activeKbPath = _currentKb.Value?.Path ?? _activeConfig?.Environment?.KBPath ?? "";
                var idempotencyMiddleware = new IdempotencyMiddleware(_idempotencyCache, activeKbPath);
                var toolCallParams = request["params"] as JObject ?? new JObject();

                // Inner dispatch: returns { isError, content } (tool result payload, no JSON-RPC envelope)
                async Task<JObject> DispatchCore(JObject tcParams)
                {
                    string tName = tcParams["name"]?.ToString() ?? "";
                    var tArgs = tcParams["arguments"] as JObject;

                    // 1. CACHE INVALIDATION: If it's a write operation or a re-index, clear the cache
                    if (IsMutatingTool(tName, tArgs))
                    {
                        Log($"[Cache] Invalidation triggered by {tName}");
                        _semanticCache.Clear();
                        BroadcastResourcesListChanged($"cache_invalidated:{tName}");
                        BroadcastResourceUpdated("genexus://objects", $"tool:{tName}");
                    }

                    // 2. SEMANTIC CACHE: Try to get from cache for read-only tools.
                    // Skip caching for live-progress lifecycle reads (status/result/cancel) and logs —
                    // these must always reflect current worker state, not a stale snapshot.
                    string lcAction = tArgs?["action"]?.ToString()?.ToLowerInvariant();
                    bool isLiveLifecycle = string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                                           && (lcAction == "status" || lcAction == "result" || lcAction == "cancel");
                    bool isLiveTool = isLiveLifecycle
                                      || string.Equals(tName, "genexus_logs", StringComparison.OrdinalIgnoreCase);

                    string cKey = $"{tName}:{tArgs?.ToString(Formatting.None)}";
                    if (!isLiveTool && _semanticCache.TryGetValue(cKey, out var cachedResponse))
                    {
                        Log($"[Cache] HIT for {tName}");
                        var cached = cachedResponse["result"] as JObject;
                        if (cached != null) return (JObject)cached.DeepClone();
                    }

                    // Rebuild full request so ConvertToolCall works
                    var fullReq = new JObject
                    {
                        ["method"] = "tools/call",
                        ["params"] = tcParams
                    };

                    // v2.6.9 perf: gateway-side fast-fail for SDK-bound tools when
                    // the worker is still doing its initial BulkIndex on the STA thread.
                    // Without this the request queues behind a 30-60s SDK enumeration
                    // and the agent eats the full 60s gateway-timeout before learning
                    // the index isn't ready. Once _lastKnownIndexState reports a usable
                    // status we forward normally; when it doesn't, the block below does a
                    // synchronous refresh-and-recheck before fast-failing so a ready index
                    // isn't masked by a stale mirror. Worker-side ListService has its own
                    // fast-fail for callers that bypass this short-circuit.
                    //
                    // Allow-list of tools that are blocked behind the STA thread and
                    // therefore benefit from the short-circuit. Gateway-served tools
                    // (whoami, recipe, kb_diff, sandbox, etc.) bypass naturally because they
                    // never hit the worker dispatcher. genexus_doctor is deliberately NOT
                    // listed: it runs off the STA thread (worker "health" method) and reads
                    // the on-disk snapshot, returning its own SearchIndexMissing/Empty report
                    // with retry hints — far more useful than a generic IndexNotReady while
                    // indexing, and it doubles as an escape hatch when the mirror is wrong.
                    if (string.Equals(tName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_query", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_inspect", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_search_source", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_analyze", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_explain", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_inject_context", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_db_optimize", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_api", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_types", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_edit", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_edit_form", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_edit_and_build", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_save_as", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_create_object", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_create_popup", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_bulk_edit", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_navigation", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_kb_explorer", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_run_object", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_diff_generated", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_what_if", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_db_drift", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_orient", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tName, "genexus_security", StringComparison.OrdinalIgnoreCase))
                    {
                        IndexStateSnapshot idxSnap;
                        lock (_lastKnownIndexStateLock) { idxSnap = _lastKnownIndexState; }
                        bool indexUsable = IsIndexUsableForReads(idxSnap);
                        if (!indexUsable)
                        {
                            // The gateway's index mirror is only written by TryRefreshIndexStateFromWorkerAsync
                            // (whoami), so a "not usable" snapshot may just be STALE — the worker finished
                            // indexing but no whoami has refreshed the mirror since. Rather than fast-fail
                            // on a possibly-stale cache (which left agents stuck on IndexNotReady until they
                            // manually called whoami), do ONE bounded synchronous refresh and re-check.
                            // GetIndexState runs off the worker's STA thread, so this stays fast even while a
                            // cold-start BulkIndex is in flight. Skip the refresh when the mirror was just
                            // refreshed (index genuinely still building) so tight retry loops don't each pay
                            // a round-trip.
                            bool cacheStale = idxSnap == null
                                || idxSnap.RefreshedAtUtc == DateTime.MinValue
                                || (DateTime.UtcNow - idxSnap.RefreshedAtUtc).TotalSeconds > 2;
                            if (cacheStale && await TryRefreshIndexStateFromWorkerAsync(timeoutMs: 1200))
                            {
                                lock (_lastKnownIndexStateLock) { idxSnap = _lastKnownIndexState; }
                                indexUsable = IsIndexUsableForReads(idxSnap);
                            }
                        }
                        if (!indexUsable)
                        {
                            var indexingEnvelope = new JObject
                            {
                                ["status"] = "Indexing",
                                ["code"] = "IndexNotReady",
                                ["indexStatus"] = idxSnap?.Status ?? "Cold",
                                ["totalObjects"] = idxSnap?.TotalObjects ?? 0,
                                ["message"] = BuildIndexingMessage(idxSnap?.Status, idxSnap?.Progress, idxSnap?.EtaMs),
                                ["hint"] = "Call genexus_whoami to observe progress, then re-issue this tool."
                            };
                            if (idxSnap?.Progress != null) indexingEnvelope["progress"] = idxSnap.Progress.Value;
                            if (idxSnap?.EtaMs != null) indexingEnvelope["etaMs"] = idxSnap.EtaMs.Value;
                            return new JObject
                            {
                                ["isError"] = false,
                                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = indexingEnvelope.ToString(Formatting.None) } }
                            };
                        }
                    }

                    // Gateway-served tools (no worker involvement)
                    if (string.Equals(tName, "genexus_whoami", StringComparison.OrdinalIgnoreCase))
                    {
                        bool whoamiVerbose = tArgs?["verbose"]?.ToObject<bool>() ?? false;
                        JObject whoami = await BuildWhoamiPayloadAsync(whoamiVerbose);
                        return new JObject
                        {
                            ["isError"] = false,
                            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = whoami.ToString(Formatting.None) } }
                        };
                    }

                    if (string.Equals(tName, "genexus_recipe", StringComparison.OrdinalIgnoreCase))
                    {
                        string action = tArgs?["action"]?.ToString()?.ToLowerInvariant();
                        JObject payload;
                        bool isErr;

                        if (string.Equals(action, "suggest_macro", StringComparison.OrdinalIgnoreCase))
                        {
                            int windowMinutes = tArgs?["windowMinutes"]?.ToObject<int?>() ?? 30;
                            int minReps = tArgs?["minRepetitions"]?.ToObject<int?>() ?? 3;
                            var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                            payload = svc.Suggest(windowMinutes, minReps);
                            isErr = string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (string.Equals(action, "crystallize", StringComparison.OrdinalIgnoreCase))
                        {
                            string macroName = tArgs?["macroName"]?.ToString();
                            string description = tArgs?["description"]?.ToString();
                            var steps = tArgs?["steps"] as JArray;

                            // If steps were not supplied, try to re-derive from current history
                            // using the proposedName as the discriminator.
                            if (steps == null || steps.Count == 0)
                            {
                                var svc = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                                JObject sugg = svc.Suggest(60, 2);
                                if (sugg["candidateMacros"] is JArray arr)
                                {
                                    foreach (var c in arr)
                                    {
                                        if (string.Equals(c?["proposedName"]?.ToString(), macroName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            steps = c["steps"] as JArray;
                                            if (string.IsNullOrWhiteSpace(description))
                                                description = c["suggestedDescription"]?.ToString();
                                            break;
                                        }
                                    }
                                }
                            }

                            var svc2 = new MacroSuggestionService(_operationTracker, GetUserMacroDir());
                            payload = svc2.Crystallize(macroName, description, steps);
                            isErr = string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            // Default: legacy behavior. action=list/describe via RecipeCatalog.Get.
                            // If action is provided and is list/describe, route via Dispatch.
                            string recipeName = tArgs?["name"]?.ToString();
                            if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(action, "describe", StringComparison.OrdinalIgnoreCase))
                            {
                                payload = RecipeCatalog.Dispatch(action, recipeName);
                            }
                            else if (!string.IsNullOrEmpty(action))
                            {
                                payload = new JObject
                                {
                                    ["status"] = "Error",
                                    ["error"] = $"Unknown action '{action}'.",
                                    ["hint"] = "Supported: list, describe, run, suggest_macro, crystallize."
                                };
                            }
                            else
                            {
                                payload = RecipeCatalog.Get(recipeName);
                            }
                            isErr = payload?["error"] != null || string.Equals(payload?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                        }

                        return new JObject
                        {
                            ["isError"] = isErr,
                            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = payload.ToString(Formatting.None) } }
                        };
                    }

                    // Async build intercept (Tasks 4.3 + 4.4):
                    // build / rebuild actions go through path selection:
                    //   - estimated_seconds < BuildSyncThresholdSeconds  → sync fast-path (fall through)
                    //   - estimated_seconds >= BuildSyncThresholdSeconds  → async Task.Run, return job_id immediately
                    if (string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(lcAction, "build", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase)))
                    {
                        int estimatedSeconds = tArgs?["estimated_seconds"]?.ToObject<int?>()
                                               ?? (string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase) ? 120 : 60);
                        int threshold = _activeConfig?.Server?.BuildSyncThresholdSeconds ?? 20;

                        if (!BuildPathSelector.UseSync(estimatedSeconds, threshold))
                        {
                            // --- ASYNC PATH (Task 4.3) ---
                            // Register the job first, then fire-and-forget the actual build.
                            // The worker call is synchronous over the JSON-RPC pipe, so we wrap
                            // it in Task.Run so the gateway thread returns to the caller immediately.
                            var job = JobRegistry.Start(sessionId, $"lifecycle/{lcAction}", estimatedSeconds);
                            Log($"[AsyncBuild] Dispatching job={job.Id} action={lcAction} target={tArgs?["target"]?.ToString() ?? "(all)"} estimated={estimatedSeconds}s");

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    // Step 1: Kick off the build on the worker — returns {status:"Accepted", taskId:...}
                                    var buildCmd = new JObject
                                    {
                                        ["module"] = "Build",
                                        ["action"] = string.Equals(lcAction, "rebuild", StringComparison.OrdinalIgnoreCase)
                                            ? "RebuildAll"
                                            : "Build",
                                        ["target"] = tArgs?["target"]?.ToString(),
                                        ["client"] = "mcp",
                                        // v2.3.8 (Task 5.2) — forward callee-expansion knobs through the async path.
                                        ["includeCallees"] = tArgs?["includeCallees"]?.ToString(),
                                        ["buildPlanCap"] = tArgs?["buildPlanCap"]?.ToObject<int?>(),
                                        // Friction 2026-05-22 (experimental): single-target shortcut.
                                        ["skipFullDeploy"] = tArgs?["skipFullDeploy"]?.ToObject<bool?>(),
                                        // v2.6.2 (Item B): forward job_id as cancelToken so
                                        // the worker registers it. A sibling lifecycle action=cancel
                                        // target=op:<id> then resolves to a real Cancel() call.
                                        ["cancelToken"] = job.Id
                                    };

                                    JObject? ackEnvelope = await SendWorkerCommandAsync(
                                        buildCmd,
                                        60000,
                                        $"Timeout starting async build (job={job.Id})",
                                        env => env,
                                        (_, correlationId) => new JObject { ["error"] = "Gateway timeout starting build.", ["correlationId"] = correlationId },
                                        toolName: tName, toolArgs: tArgs, trackOperation: false);

                                    JObject? ack = (ackEnvelope?["result"] as JObject) ?? ackEnvelope;
                                    if (ack == null || ack["error"] != null)
                                    {
                                        JobRegistry.Complete(job.Id, false,
                                            $"Build start failed: {ack?["error"]?.ToString() ?? "unknown error"}", ack);
                                        return;
                                    }

                                    string? taskId = ack["taskId"]?.ToString();
                                    if (string.IsNullOrEmpty(taskId))
                                    {
                                        // No taskId means the worker returned a synchronous result already
                                        // (or an error response). Complete with what we have.
                                        bool syncSuccess = !string.Equals(ack["status"]?.ToString(), "Error",
                                            StringComparison.OrdinalIgnoreCase) && ack["error"] == null;
                                        JobRegistry.Complete(job.Id, syncSuccess,
                                            syncSuccess ? "Build completed (sync)" : $"Build error: {ack["error"]?.ToString() ?? "unknown"}",
                                            ack);
                                        return;
                                    }

                                    Log($"[AsyncBuild] job={job.Id} taskId={taskId} — polling status until terminal");

                                    // v2.3.8 (Task 7.2) — register a CTS so lifecycle action=cancel
                                    // with this job_id can short-circuit the polling loop.
                                    var pollCt = JobRegistry.RegisterCancellation(job.Id);

                                    // Step 2: Poll worker Build/Status until status is terminal.
                                    // Terminal states from BuildTaskStatus: Succeeded | Failed | Error | Cancelled.
                                    JObject? finalStatus = null;
                                    var hardCap = DateTime.UtcNow.AddMinutes(30);
                                    while (DateTime.UtcNow < hardCap)
                                    {
                                        if (pollCt.IsCancellationRequested)
                                        {
                                            // Best-effort: tell the worker to kill the MSBuild child if any.
                                            try
                                            {
                                                _ = SendWorkerCommandAsync(
                                                    new JObject { ["module"] = "Build", ["action"] = "Cancel", ["target"] = taskId },
                                                    5000, "cancel-fanout",
                                                    env => env,
                                                    (_, __) => new JObject(),
                                                    toolName: tName, toolArgs: tArgs, trackOperation: false);
                                            }
                                            catch { /* fire-and-forget */ }
                                            finalStatus = new JObject { ["status"] = "Cancelled", ["taskId"] = taskId };
                                            break;
                                        }
                                        await Task.Delay(2000).ConfigureAwait(false);

                                        var statusCmd = new JObject
                                        {
                                            ["module"] = "Build",
                                            ["action"] = "Status",
                                            ["target"] = taskId
                                        };
                                        JObject? statusEnv = await SendWorkerCommandAsync(
                                            statusCmd,
                                            30000,
                                            $"Timeout polling build status (job={job.Id})",
                                            env => env,
                                            (_, correlationId) => new JObject { ["error"] = "Status poll timeout", ["correlationId"] = correlationId },
                                            toolName: tName, toolArgs: tArgs, trackOperation: false);

                                        finalStatus = (statusEnv?["result"] as JObject) ?? statusEnv;
                                        string? s = finalStatus?["status"]?.ToString() ?? finalStatus?["Status"]?.ToString();
                                        if (string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase))
                                        {
                                            break;
                                        }
                                    }

                                    // Step 3: Complete the JobRegistry entry with the real final status.
                                    string? finalState = finalStatus?["status"]?.ToString() ?? finalStatus?["Status"]?.ToString() ?? "Timeout";
                                    bool success = string.Equals(finalState, "Succeeded", StringComparison.OrdinalIgnoreCase);
                                    int errs = finalStatus?["errorCount"]?.ToObject<int?>() ?? finalStatus?["ErrorCount"]?.ToObject<int?>() ?? 0;
                                    int warns = finalStatus?["warningCount"]?.ToObject<int?>() ?? finalStatus?["WarningCount"]?.ToObject<int?>() ?? 0;
                                    string summary = success
                                        ? $"Build succeeded: {warns} warnings, {errs} errors"
                                        : $"Build {finalState}: {errs} errors, {warns} warnings";
                                    JobRegistry.Complete(job.Id, success, summary, finalStatus);
                                    Log($"[AsyncBuild] Completed job={job.Id} status={finalState} errors={errs} warnings={warns}");
                                }
                                catch (Exception ex)
                                {
                                    JobRegistry.Complete(job.Id, false, $"Build exception: {ex.Message}");
                                    Log($"[AsyncBuild] Exception in job={job.Id}: {ex.Message}");
                                }
                            });

                            // Friction 2026-05-22: wait_until_done=true blocks in a single turn
                            // up to MaxLongPollSeconds instead of forcing the caller to poll. Falls
                            // back to job_id+running if the build outruns the cap.
                            bool waitUntilDone = tArgs?["wait_until_done"]?.ToObject<bool?>() ?? false;
                            if (waitUntilDone)
                            {
                                int blockingCap = tArgs?["wait_seconds"]?.ToObject<int?>() ?? McpRouter.MaxLongPollSeconds;
                                var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                                bool hasProgressToken = clientProgressToken != null && clientProgressToken.Type != JTokenType.Null;
                                JObject pollResult = await McpRouter.LongPollJob(
                                    JobRegistry, job.Id, blockingCap,
                                    progressToken: clientProgressToken,
                                    heartbeat: hasProgressToken ? (n => TryWriteStdout(n.ToString(Formatting.None))) : null);
                                // Classify the terminal status so the MCP envelope's isError
                                // matches the build outcome. LongPollJob surfaces JobEntry.Status
                                // which is one of: running, succeeded, failed, cancelled.
                                // running == we hit the long-poll cap without termination — not an
                                // error per se, the caller can re-poll.
                                //
                                // Friction 2026-05-22 item 10: this used to compare against
                                // "completed" (which the registry never emits — it stamps
                                // "succeeded"/"failed"). Result: every successful build wrapped
                                // in an error envelope. Fix routes the inner BuildTaskStatus
                                // through ClassifyBuildOutcome so 0/0/exit=0 = success and
                                // partial_success surfaces as a warning marker, not an error.
                                string terminalStatus = pollResult["status"]?.ToString();
                                bool stillRunning = string.Equals(terminalStatus, "running", StringComparison.OrdinalIgnoreCase);
                                bool isErr;
                                if (stillRunning) isErr = false;
                                else if (pollResult["result"] is JObject buildPayloadFinal)
                                {
                                    var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(buildPayloadFinal);
                                    isErr = outcome == LifecycleResponseShaper.BuildOutcome.Error;
                                    if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                                    {
                                        pollResult["partial_success"] = true;
                                        if (pollResult["envelope"] == null) pollResult["envelope"] = "warning";
                                    }
                                }
                                else
                                {
                                    // No structured result — trust the registry summary string.
                                    bool succeeded = string.Equals(terminalStatus, "succeeded", StringComparison.OrdinalIgnoreCase);
                                    isErr = !succeeded;
                                }
                                if (!isErr
                                    && LifecycleResponseShaper.ShouldCompact(tArgs)
                                    && pollResult["result"] is JObject innerResult2)
                                {
                                    var compactJson = LifecycleResponseShaper.Compact(innerResult2.ToString(Formatting.None), compact: true);
                                    try { pollResult["result"] = JObject.Parse(compactJson); }
                                    catch { /* shaper passthrough on non-JSON */ }
                                }
                                return new JObject
                                {
                                    ["isError"] = isErr,
                                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = pollResult.ToString(Newtonsoft.Json.Formatting.None) } }
                                };
                            }

                            // Return immediately with job_id
                            var asyncResponse = BuildAsyncLifecycleAcceptedPayload(job, lcAction);
                            return new JObject
                            {
                                ["isError"] = false,
                                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = asyncResponse.ToString(Newtonsoft.Json.Formatting.None) } }
                            };
                        }
                        // else: UseSync == true → fall through to the normal synchronous dispatch below
                        Log($"[AsyncBuild] Short build (estimated={estimatedSeconds}s < threshold={threshold}s): using sync fast-path");
                    }

                    object? rawWorkerCmd = null;
                    if (string.Equals(tName, "genexus_export_object", StringComparison.OrdinalIgnoreCase))
                    {
                        rawWorkerCmd = new
                        {
                            module = "Object",
                            action = "ExportText",
                            target = tArgs?["name"]?.ToString(),
                            path = tArgs?["outputPath"]?.ToString(),
                            part = tArgs?["part"]?.ToString() ?? "Source",
                            type = tArgs?["type"]?.ToString(),
                            overwrite = tArgs?["overwrite"]?.ToObject<bool?>() ?? false
                        };
                    }
                    else if (string.Equals(tName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
                    {
                        rawWorkerCmd = new
                        {
                            module = "Object",
                            action = "ImportText",
                            target = tArgs?["name"]?.ToString(),
                            path = tArgs?["inputPath"]?.ToString(),
                            part = tArgs?["part"]?.ToString() ?? "Source",
                            type = tArgs?["type"]?.ToString()
                        };
                    }

                    try
                    {
                        rawWorkerCmd ??= McpRouter.ConvertToolCall(fullReq);
                    }
                    catch (UsageException ux)
                    {
                        // Return as error result (not JSON-RPC level — that happens in wrapper below)
                        return new JObject
                        {
                            ["isError"] = true,
                            ["usageException"] = true,
                            ["code"] = -32602,
                            ["message"] = ux.Message,
                            ["usageCode"] = ux.Code
                        };
                    }

                    var workerCmd = rawWorkerCmd != null ? JObject.FromObject(rawWorkerCmd) : null;
                    if (workerCmd == null)
                    {
                        return new JObject { ["isError"] = true, ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "{}" } } };
                    }

                    workerCmd["client"] = "mcp";
                    int timeoutMs = GetToolTimeoutMs(tName, tArgs);

                    // async=true on edit/variable tools → fire-and-forget; result piggybacks via _meta.background_jobs.
                    bool editAsync = (tArgs?["async"]?.ToObject<bool?>() ?? false)
                                     && IsAsyncMutationTool(tName);
                    if (editAsync)
                    {
                        int estEdit = tArgs?["estimated_seconds"]?.ToObject<int?>() ?? 30;
                        var editJob = JobRegistry.Start(sessionId, $"edit/{tName}", estEdit);
                        Log($"[AsyncEdit] Dispatching job={editJob.Id} tool={tName} estimated={estEdit}s");
                        // v2.6.2 (Item B): inject cancelToken=jobId so the worker's
                        // blanket-register at dispatch entry makes lifecycle cancel resolvable.
                        if (workerCmd?["params"] is JObject capturedParams)
                            capturedParams["cancelToken"] = editJob.Id;
                        var capturedCmd = workerCmd;
                        var capturedName = tName;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var inner = await SendWorkerCommandAsync(
                                    capturedCmd, 0,
                                    $"Timeout waiting for async edit: {capturedName}",
                                    r => r, (_, __) => new JObject { ["status"] = "Running" });
                                bool ok = IsSuccessfulBackgroundToolCompletion(inner);
                                JobRegistry.Complete(editJob.Id, ok, BuildAsyncMutationCompletionSummary(capturedName, ok), inner);
                            }
                            catch (Exception ex)
                            {
                                string failurePrefix = string.Equals(capturedName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(capturedName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(capturedName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(capturedName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase)
                                    ? "Variable update exception"
                                    : "Edit exception";
                                JobRegistry.Complete(editJob.Id, false, $"{failurePrefix}: {ex.Message}");
                                Log($"[AsyncEdit] Exception in job={editJob.Id}: {ex.Message}");
                            }
                        });
                        var asyncEditResponse = string.Equals(tName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(tName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(tName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(tName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase)
                            ? BuildAsyncVariableAcceptedPayload(editJob)
                            : BuildAsyncEditAcceptedPayload(editJob);
                        return new JObject
                        {
                            ["isError"] = false,
                            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = asyncEditResponse.ToString(Formatting.None) } }
                        };
                    }

                    JObject? innerResult = null;
                    // MCP keepalive: when the client supplied a progressToken, emit
                    // notifications/progress while the worker runs so long synchronous
                    // tools (apply_pattern, delete, analyze, …) don't trip the client's
                    // request timeout. No-op when the client omits the token.
                    var toolProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                    bool toolHasProgressToken = toolProgressToken != null && toolProgressToken.Type != JTokenType.Null;
                    innerResult = await SendWorkerCommandAsync(
                        workerCmd,
                        timeoutMs,
                        $"Timeout waiting for tool: {tName}",
                        resultObj =>
                        {
                            JToken? finalResult = null;
                            try {
                                finalResult = TruncateResponseIfNeeded(resultObj["result"] ?? resultObj["error"], tName);
                            } catch (Exception exTrunc) {
                                Log($"[Gateway] Error during truncation: {exTrunc.Message}");
                                finalResult = resultObj["result"] ?? resultObj["error"];
                            }

                            if (string.Equals(tName, "genexus_edit_and_build", StringComparison.OrdinalIgnoreCase)
                                && finalResult is JObject editAndBuildPayload)
                            {
                                NormalizeEditAndBuildPayload(editAndBuildPayload);
                            }

                            bool isErr = resultObj["error"] != null || string.Equals(resultObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                            // Inner-payload error detection — tools that return their
                            // failure envelope as result.{error|status} (e.g. genexus_read
                            // returning {"part":"Source","error":"Part 'Source' not found ..."})
                            // were previously sent with isError=false because we only checked
                            // the outer envelope. Mirror the inner shape so MCP clients that
                            // branch on isError stay correct.
                            if (!isErr && finalResult is JObject innerErrObj)
                            {
                                bool innerHasError = innerErrObj["error"] != null
                                    || string.Equals(innerErrObj["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(innerErrObj["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(innerErrObj["status"]?.ToString(), "NotImplemented", StringComparison.OrdinalIgnoreCase);
                                if (innerHasError) isErr = true;
                            }

                            // Friction 2026-05-22 #63: attach suggested_next_step on every error
                            // envelope (verbose OR terse). McpRouter.AttachSuggestedNextStep is a
                            // pure pattern-match over code/message text; routes patch NoMatch →
                            // near-match inspection, visual write failures → LayoutGotchaScanner,
                            // KB_AMBIGUOUS → kb=<alias>, spc0150 → extract_to_procedure recipe.
                            if (isErr && finalResult is JObject preTrimErr && preTrimErr["suggested_next_step"] == null)
                            {
                                var hint = McpRouter.AttachSuggestedNextStep(preTrimErr);
                                if (hint != null) preTrimErr["suggested_next_step"] = hint;
                            }

                            // TerseErrors: trim error envelopes to {message, code, hint} by default.
                            // verbose_errors=true restores the full payload. Gated by PerfProfile.V1Enabled.
                            // Runs BEFORE ResponseSizeGuard so the guard sees the trimmed (smaller) payload.
                            if (isErr && PerfProfile.V1Enabled && finalResult is JObject errObj)
                            {
                                bool verbose = tArgs?["verbose_errors"]?.ToObject<bool?>() ?? false;
                                finalResult = McpRouter.TrimErrorEnvelope(errObj, verbose);
                            }

                            // ResponseSizeGuard: replace oversize JObject results with a truncation sentinel.
                            // Gated by PerfProfile.V1Enabled so it can be disabled via MCP_PERF_PROFILE=legacy.
                            if (!isErr && PerfProfile.V1Enabled && finalResult is JObject finalJObj)
                            {
                                var guard = new ResponseSizeGuard();
                                var (guarded, _) = guard.Apply(finalJObj, tName, tArgs);
                                finalResult = guarded;
                            }

                            // StripNulls: remove null-valued properties from the result to reduce wire size.
                            // Runs after ResponseSizeGuard so the guard measures the pre-stripped payload.
                            if (PerfProfile.V1Enabled && finalResult is JObject stripObj)
                            {
                                McpRouter.StripNulls(stripObj);
                            }

                            // v2.3.8 (Task 6.1) — compact lifecycle status by default.
                            // Replaces the verbose BuildTaskStatus payload (full Errors/Warnings/Output)
                            // with counts + top-10 errors + warning dedup. compact=false restores legacy shape.
                            if (!isErr
                                && string.Equals(tName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(lcAction, "status", StringComparison.OrdinalIgnoreCase)
                                && finalResult is JObject lifecycleObj
                                && LifecycleResponseShaper.ShouldCompact(tArgs))
                            {
                                var compactJson = LifecycleResponseShaper.Compact(lifecycleObj.ToString(Formatting.None), compact: true);
                                try { finalResult = JObject.Parse(compactJson); }
                                catch { /* shaper passed through non-JSON; keep original */ }
                            }

                            JToken axiPayload = NormalizeToolPayloadForAxi(finalResult, tName, tArgs, isErr);

                            var toolResult = new JObject
                            {
                                ["isError"] = isErr,
                                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = axiPayload.ToString(Formatting.None) } }
                            };

                            // v2.3.8 (post-self-review) — don't cache transient envelopes.
                            // A "Reindexing"/"IndexCold"/"Timeout"/"Cancelled"/"BuildPlanTooLarge"
                            // response is a snapshot of the worker's current state, not a stable
                            // semantic answer. Caching it kept analyze impact pinned to the
                            // first response (often Timeout during a reindex), so callers got
                            // the same stale envelope on every retry until cache eviction.
                            bool isTransient = false;
                            if (finalResult is JObject transientCheck)
                            {
                                var s = transientCheck["status"]?.ToString();
                                isTransient = string.Equals(s, "Reindexing", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "IndexCold", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "Timeout", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s, "Running", StringComparison.OrdinalIgnoreCase);
                            }

                            if (!isErr && !isTransient && !tName.Contains("write") && !tName.Contains("patch") && !isLiveTool)
                            {
                                // Store full envelope in semantic cache (rebuilt on hit above)
                                _semanticCache[cKey] = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["result"] = toolResult
                                };
                            }

                            return toolResult;
                        },
                        (operationId, correlationId) =>
                        {
                            string message = $"GeneXus MCP Worker timed out executing tool: {tName}.";
                            var timeoutPayload = new JObject
                            {
                                ["status"] = "Running",
                                ["error"] = "Gateway timeout waiting for worker response.",
                                ["message"] = message,
                                ["correlationId"] = correlationId,
                                ["retriable"] = true
                            };

                            var help = new JArray();
                            if (!string.IsNullOrWhiteSpace(operationId))
                            {
                                timeoutPayload["operationId"] = operationId;
                                help.Add($"Operation is still running. Query genexus_lifecycle(action='status', target='op:{operationId}') or action='result'.");
                                if (tName != null && (tName.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || tName.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || tName.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    // Writes have usually persisted by the time the gateway times out; poll result, then read — don't retry the edit.
                                    help.Add("For long writes the change is usually already persisted; check action='result' once, then read back instead of retrying.");
                                }
                            }
                            else
                            {
                                help.Add("Retry with narrower scope or lower limit.");
                            }
                            timeoutPayload["help"] = help;

                            JToken axiPayload = NormalizeToolPayloadForAxi(timeoutPayload, tName, tArgs, true);
                            return new JObject
                            {
                                ["isError"] = true,
                                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = axiPayload.ToString(Formatting.None) } }
                            };
                        },
                        toolName: tName,
                        toolArgs: tArgs,
                        trackOperation: true,
                        progressToken: toolProgressToken,
                        heartbeat: toolHasProgressToken ? (n => TryWriteStdout(n.ToString(Formatting.None))) : null);

                    // apply_pattern { validate: true } — post-apply build of the
                    // generated host so the LLM sees compile failures in a single
                    // tool call. Without this the agent declared "Success" and only
                    // discovered the broken binding by opening the IDE. Runs OUTSIDE
                    // the SendWorkerCommandAsync onSuccess lambda (which is sync) so
                    // we can await the validation build here.
                    if (innerResult != null
                        && !(innerResult["isError"]?.ToObject<bool?>() ?? false)
                        && string.Equals(tName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                        && (tArgs?["validate"]?.ToObject<bool?>() ?? false))
                    {
                        string applyText = (innerResult["content"] as JArray)?[0]?["text"]?.ToString() ?? "";
                        JObject applyPayload = null;
                        try { applyPayload = JObject.Parse(applyText); } catch { }
                        string hostName = applyPayload?["patternHost"]?.ToString();

                        var vBlock = new JObject { ["target"] = hostName };
                        if (string.IsNullOrWhiteSpace(hostName))
                        {
                            vBlock["status"] = "skipped";
                            vBlock["reason"] = "No patternHost in apply response — nothing to validate.";
                        }
                        else
                        {
                            // Worker's BuildService.Build is async internally — kicks off
                            // Task.Run and returns a Running envelope with a taskId in ms.
                            // We must (1) fire Build/Build, (2) poll Build/Status with that
                            // taskId until terminal, (3) fetch Build/Result for errors.
                            var vSw = System.Diagnostics.Stopwatch.StartNew();
                            JObject startCmd = new JObject
                            {
                                ["module"] = "Build",
                                ["action"] = "Build",
                                ["target"] = hostName,
                                ["includeCallees"] = "direct"
                            };
                            JObject? startEnv = await SendWorkerCommandAsync(
                                startCmd, 10000,
                                "validate-start timeout",
                                env => env,
                                (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                                toolName: "genexus_apply_pattern.validate.start",
                                toolArgs: null, trackOperation: false);

                            string taskId = null;
                            JObject startInner = startEnv?["result"] as JObject;
                            if (startInner == null && startEnv?["result"] is JValue sjv && sjv.Type == JTokenType.String)
                            {
                                try { startInner = JObject.Parse(sjv.ToString()); } catch { }
                            }
                            taskId = startInner?["TaskId"]?.ToString() ?? startInner?["taskId"]?.ToString();

                            JObject terminal = null;
                            if (!string.IsNullOrEmpty(taskId))
                            {
                                // Poll up to 180s (180 iterations × 1s sleep + RPC ms)
                                for (int i = 0; i < 180 && vSw.ElapsedMilliseconds < 180000; i++)
                                {
                                    await Task.Delay(1000);
                                    var statusCmd = new JObject
                                    {
                                        ["module"] = "Build",
                                        ["action"] = "Status",
                                        ["target"] = taskId
                                    };
                                    JObject? statusEnv = await SendWorkerCommandAsync(
                                        statusCmd, 8000, "validate-poll timeout",
                                        env => env,
                                        (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                                        toolName: "genexus_apply_pattern.validate.poll",
                                        toolArgs: null, trackOperation: false);
                                    JObject sObj = statusEnv?["result"] as JObject;
                                    if (sObj == null && statusEnv?["result"] is JValue pjv && pjv.Type == JTokenType.String)
                                    {
                                        try { sObj = JObject.Parse(pjv.ToString()); } catch { }
                                    }
                                    string sStatus = sObj?["Status"]?.ToString() ?? sObj?["status"]?.ToString();
                                    if (!string.IsNullOrEmpty(sStatus) && !string.Equals(sStatus, "Running", StringComparison.OrdinalIgnoreCase))
                                    {
                                        terminal = sObj;
                                        break;
                                    }
                                }
                            }
                            vSw.Stop();
                            vBlock["durationMs"] = vSw.ElapsedMilliseconds;

                            if (string.IsNullOrEmpty(taskId))
                            {
                                vBlock["status"] = "error";
                                vBlock["error"] = "Worker did not return a taskId for the validation build.";
                                if (startInner != null) vBlock["startResponse"] = startInner;
                            }
                            else if (terminal == null)
                            {
                                vBlock["status"] = "timeout";
                                vBlock["error"] = "Validation build did not finish in 180s.";
                                vBlock["taskId"] = taskId;
                            }
                            else
                            {
                                int errorCount = terminal["ErrorCount"]?.ToObject<int?>() ?? terminal["errorCount"]?.ToObject<int?>() ?? (terminal["Errors"] as JArray)?.Count ?? 0;
                                int warningCount = terminal["WarningCount"]?.ToObject<int?>() ?? terminal["warningCount"]?.ToObject<int?>() ?? (terminal["Warnings"] as JArray)?.Count ?? 0;
                                string termStatus = terminal["Status"]?.ToString() ?? "";
                                bool buildOk = errorCount == 0
                                    && !string.Equals(termStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                                    && terminal["error"] == null;
                                vBlock["status"] = buildOk ? "ok" : "failed";
                                vBlock["errorCount"] = errorCount;
                                vBlock["warningCount"] = warningCount;
                                vBlock["taskId"] = taskId;
                                var errArr = terminal["Errors"] as JArray ?? terminal["errors"] as JArray;
                                if (errArr != null && errArr.Count > 0)
                                    vBlock["errors"] = new JArray(errArr.Take(10));
                                var warnArr = terminal["Warnings"] as JArray ?? terminal["warnings"] as JArray;
                                if (warnArr != null && warnArr.Count > 0)
                                    vBlock["warnings"] = new JArray(warnArr.Take(5));
                            }
                        }

                        // Fold the validation block back into the toolResult text
                        // payload, and promote isError when validation didn't pass.
                        if (applyPayload != null)
                        {
                            applyPayload["validation"] = vBlock;
                            var newText = applyPayload.ToString(Formatting.None);
                            var contentArr = innerResult["content"] as JArray;
                            if (contentArr != null && contentArr.Count > 0 && contentArr[0] is JObject c0)
                            {
                                c0["text"] = newText;
                            }
                            if (!string.Equals(vBlock["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(vBlock["status"]?.ToString(), "skipped", StringComparison.OrdinalIgnoreCase))
                            {
                                innerResult["isError"] = true;
                            }
                        }
                    }

                    return innerResult ?? new JObject { ["isError"] = true };
                }

                JObject toolInnerResult;
                try
                {
                    toolInnerResult = await idempotencyMiddleware.Invoke(toolCallParams, DispatchCore);
                }
                catch (UsageException ux)
                {
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32602,
                            ["message"] = ux.Message,
                            ["data"] = new JObject { ["usageCode"] = ux.Code }
                        }
                    };
                }

                // Handle UsageException surfaced from DispatchCore as a result object
                if (toolInnerResult["usageException"]?.ToObject<bool>() == true)
                {
                    return new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idToken?.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = toolInnerResult["code"]?.ToObject<int?>() ?? -32602,
                            ["message"] = toolInnerResult["message"]?.ToString() ?? "Usage error",
                            ["data"] = new JObject { ["usageCode"] = toolInnerResult["usageCode"]?.ToString() }
                        }
                    };
                }

                // Piggyback background_jobs: attach snapshot of running/unseen-completed jobs to _meta.
                // Gated by PerfProfile.V1Enabled. Completions are marked seen so they surface exactly once.
                if (PerfProfile.V1Enabled)
                    McpRouter.PiggybackJobs(toolInnerResult, sessionId, JobRegistry);

                // Item 61: inject _meta.tokens (used/limit/hint) into every tool response
                // so the LLM can reason about response size and self-paginate.
                McpRouter.InjectMetaTokens(toolInnerResult);

                // Auto-inject annotation: when we inferred 'type' for this call, surface
                // it in the payload under _meta.autoInjected so the LLM can self-correct.
                if (_autoInjectedType != null)
                    InjectAutoTypeAnnotation(toolInnerResult, _autoInjectedType);

                // Wrap tool result in JSON-RPC envelope
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["result"] = toolInnerResult
                };
            }

            // Explicitly return an error for unknown tools if convert failed
            if (method == "tools/call")
            {
                string fallbackToolName = (request["params"] as JObject)?["name"]?.ToString() ?? "";
                return new JObject {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = JToken.FromObject(new { code = -32601, message = $"Method not found: {fallbackToolName}" })
                };
            }

            // Fix 6a: unknown method with an id is a request (not a notification) — must
            // respond. Notifications have no "id" field; those return null (no response).
            if (idToken != null && !string.IsNullOrEmpty(method)
                && !method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken.DeepClone(),
                    ["error"] = new JObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" }
                };
            }

            // Fix 6e: notifications/cancelled — acknowledge by resolving/aborting matching request.
            if (string.Equals(method, "notifications/cancelled", StringComparison.OrdinalIgnoreCase))
            {
                var cancelledId = request["params"]?["requestId"];
                if (cancelledId != null)
                {
                    Log($"[Protocol] notifications/cancelled for requestId={cancelledId}");
                    // Best-effort: no tracked pending-request map yet; the caller will time out naturally.
                }
                return null;
            }

            return null;
        }

        // Proactively kick off the KB search index on first MCP initialize so the
        // first `genexus_query` doesn't pay the full cold-start cost. Worker side
        // short-circuits to "AlreadyIndexed" if cache is warm, so this is cheap on
        // warm starts. When a real cold-start kicks in, an upfront
        // notifications/message tells the agent that search/analyze return partial
        // results while indexing runs in the background — read/edit/build are
        // immediate regardless.
        private static void TriggerIndexBootstrapOnce()
        {
            if (Interlocked.CompareExchange(ref _indexBootstrapStarted, 1, 0) != 0) return;

            Log("[IndexBootstrap] firing on initialize");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null) { Log("[IndexBootstrap] worker pool null"); return; }

                    var indexCommand = new JObject
                    {
                        ["module"] = "KB",
                        ["action"] = "BulkIndex",
                        ["client"] = "mcp"
                    };

                    var resp = await SendWorkerCommandAsync(
                        indexCommand,
                        30000,
                        "Index bootstrap timeout",
                        wr => wr,
                        (_, correlationId) => new JObject(),
                        toolName: "gateway_index_bootstrap",
                        trackOperation: false);

                    // BulkIndex now returns the canonical envelope ({status:"ok", code, result}).
                    // The fresh-vs-warm signal lives in `code`; fall back to the legacy top-level
                    // `status` for any pre-canonical worker still in the pool.
                    var result = resp?["result"] as JObject;
                    string? status = result?["code"]?.ToString()
                        ?? result?["status"]?.ToString();
                    Log($"[IndexBootstrap] worker reply code={status ?? "<null>"}");

                    // The default lite-index path returns "LiteStarted"; the legacy full path
                    // returns "Started". Either means a fresh cold-start index just kicked off,
                    // so the agent should see the one-time background-indexing notice.
                    // ("AlreadyIndexed" / "AlreadyInProgress" / "DeltaStarted" are warm starts — no notice.)
                    if (string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "LiteStarted", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("[IndexBootstrap] emitting cold-start notice");
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "indexing",
                            data = "First-time indexing of this KB has started in the background. "
                                + "Search and analyze tools will return partial results while it runs; "
                                + "read, edit, build, and list tools are immediate and unaffected. "
                                + "Watch notifications/progress for live progress."
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log($"[IndexBootstrap] {ex.Message}");
                }
            });
        }

        private static void TriggerWorkerWarmupOnce()
        {
            if (Interlocked.CompareExchange(ref _workerWarmupStarted, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null)
                    {
                        Log("[Warmup] WorkerPool not available, skipping warmup.");
                        return;
                    }

                    Log("[Warmup] Starting worker warmup sequence...");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup started.",
                        timestamp = DateTime.UtcNow
                    });

                    var listCommand = new JObject
                    {
                        ["module"] = "List",
                        ["action"] = "Objects",
                        ["target"] = string.Empty,
                        ["limit"] = 1,
                        ["offset"] = 0,
                        ["client"] = "mcp"
                    };

                    var listResponse = await SendWorkerCommandAsync(
                        listCommand,
                        30000,
                        "Warmup list timeout",
                        workerResponse => workerResponse,
                        (_, correlationId) => new JObject
                        {
                            ["error"] = new JObject
                            {
                                ["message"] = "Warmup list operation timed out.",
                                ["correlationId"] = correlationId
                            }
                        },
                        toolName: "gateway_warmup_list",
                        trackOperation: false);

                    var result = listResponse?["result"];
                    JArray? items = null;
                    if (result is JObject obj)
                    {
                        items = (obj["results"] ?? obj["objects"]) as JArray;
                    }
                    else if (result is JArray arr)
                    {
                        items = arr;
                    }

                    string? objectName = items?.FirstOrDefault()?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(objectName))
                    {
                        var readCommand = new JObject
                        {
                            ["module"] = "Read",
                            ["action"] = "ExtractSource",
                            ["target"] = objectName,
                            ["part"] = "Source",
                            ["offset"] = 0,
                            ["limit"] = 1,
                            ["client"] = "mcp"
                        };

                        await SendWorkerCommandAsync(
                            readCommand,
                            30000,
                            "Warmup read timeout",
                            workerResponse => workerResponse,
                            (_, correlationId) => new JObject
                            {
                                ["error"] = new JObject
                                {
                                    ["message"] = "Warmup read operation timed out.",
                                    ["correlationId"] = correlationId
                                }
                            },
                            toolName: "gateway_warmup_read",
                            trackOperation: false);
                    }

                    Log("[Warmup] Worker warmup finished.");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup finished.",
                        timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    Log("[Warmup] Worker warmup failed: " + ex.Message);
                    BroadcastNotification("notifications/message", new
                    {
                        level = "warning",
                        logger = "warmup",
                        data = "Worker warmup failed: " + ex.Message,
                        timestamp = DateTime.UtcNow
                    });
                }
            });
        }

        private static JToken TruncateResponseIfNeeded(JToken? result, string toolName)
        {
            if (result == null) return JValue.CreateNull();
            
            string? readPart = (result as JObject)?["part"]?.ToString();
            bool isXmlMetadataRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                                     (string.Equals(readPart, "Layout", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternVirtual", StringComparison.OrdinalIgnoreCase));

            string raw = result.ToString(Formatting.None);
            // issue #25 #6: the worker already paginates genexus_read to ~200 lines /
            // 16 KB and reports it via `isTruncatedByWorker` + offset/limit/
            // suggestedNextOffset. When the worker already bounded the page, the
            // gateway must NOT char-slice `source` again — that re-cut dropped the
            // middle of an already-bounded page and orphaned the pagination fields.
            bool workerPaginatedRead =
                string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                ((result as JObject)?["isTruncatedByWorker"]?.ToObject<bool>() ?? false);
            int softBudget = isXmlMetadataRead
                ? 220000
                : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                ? 24000
                : string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase)
                    ? 400000
                    : 60000;
            if (raw.Length < softBudget) return result;

            Log($"[Budget] Truncating response for {toolName} ({raw.Length} chars)");

            if (result is JObject obj)
            {
                if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var metadataField in new[] { "variables", "calls", "dataSchema", "patternMetadata" })
                    {
                        if (obj[metadataField] != null)
                        {
                            obj.Remove(metadataField);
                            obj["isTruncated"] = true;
                            obj["message"] = "Gateway trimmed derived metadata from genexus_read to keep the response within the MCP context budget.";
                        }
                    }
                }

                if (obj["results"] is JArray searchResults && searchResults.Count > 10)
                {
                    int originalCount = searchResults.Count;
                    string currentRaw = obj.ToString(Formatting.None);
                    if (currentRaw.Length > 80000)
                    {
                        // Drastic pruning: keep only first 5
                        while (searchResults.Count > 5) searchResults.RemoveAt(searchResults.Count - 1);
                        obj["isTruncated"] = true;
                        obj["returnedCount"] = 5;
                        obj["originalCount"] = originalCount;
                        return obj;
                    }
                }

                bool isRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase);

                // issue #26 P7: for genexus_read source/content, the gateway used to
                // head+tail slice and DROP THE MIDDLE — leaving a silent hole and an
                // offset that no longer described the returned bytes. Replace that with a
                // single, predictable, LINE-ALIGNED PREFIX cut that shares the worker's
                // line-based pagination model: keep whole lines from the front, tell the
                // caller exactly which limit hit and the safe line offset to continue from.
                // No middle is ever dropped.
                if (isRead && !isXmlMetadataRead)
                {
                    foreach (var field in new[] { "source", "content", "code" })
                    {
                        // Worker already paginated this page — its offset/suggestedNextOffset
                        // are authoritative; don't second-guess with a gateway cut.
                        if (workerPaginatedRead && string.Equals(field, "source", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (obj[field]?.Type != JTokenType.String) continue;
                        string val = obj[field]!.ToString();
                        const int readFieldBudget = 20000;
                        if (val.Length <= readFieldBudget) continue;

                        // Cut on a line boundary at/under the budget so we never split a line.
                        int cut = val.LastIndexOf('\n', Math.Min(readFieldBudget, val.Length) - 1);
                        if (cut <= 0) cut = Math.Min(readFieldBudget, val.Length); // no newline: hard prefix
                        string kept = val.Substring(0, cut);
                        int keptLines = kept.Length == 0 ? 0 : kept.Split('\n').Length;
                        int baseOffset = obj["offset"]?.ToObject<int?>() ?? 0;
                        int safeNextOffset = baseOffset + keptLines;

                        obj[field] = kept;
                        obj["isTruncated"] = true;
                        obj["truncatedByGateway"] = true;
                        obj["truncatedBy"] = "gateway";
                        obj["gatewaySafeNextOffset"] = safeNextOffset;
                        obj["gatewayTruncationHint"] =
                            $"Gateway trimmed '{field}' to the context budget by keeping whole lines from the front (NO middle dropped). " +
                            $"Continue cleanly with genexus_read offset={safeNextOffset} (line-based) to read the next page.";
                    }
                }

                // Non-read tools (and read metadata fields): head+tail trim is fine here —
                // these are derived blobs, not paginable source, so a middle elision just
                // fits the budget without a pagination contract to break.
                var fieldsToTruncate = (isRead && !isXmlMetadataRead)
                    ? new[] { "fileContent", "details" }
                    : new[] { "source", "content", "code", "fileContent", "details" };
                foreach (var field in fieldsToTruncate)
                {
                    var fieldValue = obj[field];
                    if (fieldValue != null && fieldValue.Type == JTokenType.String)
                    {
                        string val = fieldValue.ToString();
                        int fieldBudget = isXmlMetadataRead ? 180000 : 20000;
                        int headBudget = isXmlMetadataRead ? 140000 : 15000;
                        int tailBudget = isXmlMetadataRead ? 40000 : 5000;
                        if (val.Length > fieldBudget)
                        {
                            obj[field] = val.Substring(0, headBudget) +
                                           "\n\n[... TRUNCATED BY GATEWAY TOKEN BUDGET ...] \n\n" +
                                           val.Substring(val.Length - tailBudget);
                            obj["isTruncated"] = true;
                            obj["truncatedByGateway"] = true;
                            obj["gatewayTruncationHint"] = "Gateway trimmed this field to fit the context budget (a middle slice was dropped). This field is not paginable; re-request the specific object/part if you need the full bytes.";
                        }
                    }
                }

                string truncatedRaw = obj.ToString(Formatting.None);
                if (truncatedRaw.Length > 80000)
                {
                    // issue #25 #6: for genexus_read, preserve the head+tail-trimmed
                    // object instead of wiping it to a bare error (the old fallback
                    // discarded the tail it had just carefully kept). Only non-read
                    // shapes fall back to the structural error.
                    if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                    {
                        obj["isTruncated"] = true;
                        obj["truncatedByGateway"] = true;
                        obj["message"] = "Response exceeded the gateway budget even after trimming; re-request with a smaller `limit` or use offset/limit pagination for exact bytes.";
                        return obj;
                    }
                    // Fallback to ensuring valid JSON structure when heavily nested Strings overfill
                    return JToken.FromObject(new {
                        jsonrpc = "2.0",
                        error = "Response exceeded 80k token budget and could not be safely parsed. Try lower limits or pagination.",
                        isTruncated = true
                    });
                }
                return obj;
            }
            else if (result is JArray arr)
            {
                // Truncate arrays if they exceed limits
                while (arr.Count > 5 && arr.ToString(Formatting.None).Length > 80000)
                {
                    arr.RemoveAt(arr.Count - 1);
                }
                if (arr.ToString(Formatting.None).Length > 80000)
                {
                    return JToken.FromObject(new { 
                        error = "Array response exceeded 80k token budget. Try lower limits or pagination.", 
                        isTruncated = true 
                    });
                }
                return arr;
            }

            return new JValue(raw.Substring(0, 75000) + "... [TRUNCATED]");
        }

        private static bool IsMutatingTool(string toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            if (string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (toolName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("refactor", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("add_variable", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("modify_variable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(toolName, "genexus_properties", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "set", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "write", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_history", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "save", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_structure", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "update_visual", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_layout", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "set_property", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "set_properties", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "rename_printblock", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "add_printblock", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "index", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "reorg", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        // Items 54/55/56: resolve a "KB ref" argument that may be either an alias
        // declared in config.Environment.KBs[] or a literal filesystem path.
        // Returns the resolved absolute path, or null if neither match.
        private static string? ResolveKbPath(string aliasOrPath)
        {
            if (string.IsNullOrWhiteSpace(aliasOrPath)) return null;
            var declared = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, aliasOrPath, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return declared.Path;
            if (System.IO.Directory.Exists(aliasOrPath)) return aliasOrPath;
            return null;
        }

        /// <summary>
        /// Add <c>_meta.autoInjected: ["type"]</c> to the content text payload of a
        /// tool result envelope so the LLM sees that gateway inferred the type.
        /// Does not overwrite any existing <c>_meta</c> structure — merges only.
        /// </summary>
        private static void InjectAutoTypeAnnotation(JObject toolInnerResult, string injectedType)
        {
            try
            {
                var contentArr = toolInnerResult["content"] as JArray;
                if (contentArr == null || contentArr.Count == 0) return;
                var firstContent = contentArr[0] as JObject;
                if (firstContent == null) return;

                string? rawText = firstContent["text"]?.ToString();
                if (rawText == null) return;

                JObject payload;
                try { payload = JObject.Parse(rawText); }
                catch { return; }  // non-JSON text blob — skip

                // Merge into existing _meta or create new
                if (payload["_meta"] is not JObject meta)
                {
                    meta = new JObject();
                    payload["_meta"] = meta;
                }
                meta["autoInjected"] = new JArray("type");
                meta["autoInjectedType"] = injectedType;

                firstContent["text"] = payload.ToString(Formatting.None);
            }
            catch
            {
                // Best-effort — never fail a tool call over annotation
            }
        }

        private static JObject BuildToolTextResponse(JToken? idToken, JToken payload, bool isError, string? toolName = null, JObject? toolArgs = null)
        {
            JToken axiPayload = NormalizeToolPayloadForAxi(payload, toolName ?? "unknown", toolArgs, isError);
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken?.DeepClone(),
                ["result"] = JToken.FromObject(new
                {
                    content = new[] { new { type = "text", text = axiPayload.ToString(Formatting.None) } },
                    isError
                })
            };
        }

        private static JToken NormalizeToolPayloadForAxi(JToken? payload, string toolName, JObject? toolArgs, bool isError)
        {
            JObject sourceObj;
            if (payload is JArray arrayPayload)
            {
                sourceObj = new JObject
                {
                    ["results"] = arrayPayload.DeepClone()
                };
            }
            else if (payload is JObject objPayload)
            {
                sourceObj = objPayload;
            }
            else
            {
                return payload ?? JValue.CreateNull();
            }

            var obj = (JObject)sourceObj.DeepClone();
            // Per-response meta is intentionally lean: `schemaVersion` is emitted
            // once in the `initialize` handshake (`_meta.schemaVersion`) and the
            // client already knows which tool it called, so neither field is
            // repeated per response (~60B/response saved). Only emit `meta` when
            // a real signal (truncated/fields/totalByType/…) gets attached below.
            var meta = obj["meta"] as JObject ?? new JObject();
            HashSet<string>? requestedFields = ParseRequestedFields(toolArgs);
            // Friction 2026-05-22 #64: projection=minimal|standard|verbose lets the
            // agent opt into a smaller or larger field set without having to enumerate
            // fields[]. Resolves to a HashSet that overrides the axiCompact default —
            // explicit fields[] still wins (highest specificity).
            string projection = toolArgs?["projection"]?.ToString();
            bool verboseRequested = !string.IsNullOrWhiteSpace(projection)
                && string.Equals(projection.Trim(), "verbose", StringComparison.OrdinalIgnoreCase);
            if (requestedFields == null && !string.IsNullOrWhiteSpace(projection))
            {
                requestedFields = ResolveProjection(toolName, projection);
            }
            // projection=verbose explicitly opts OUT of the compact filter — earlier
            // versions silently fell into GetDefaultCompactFields here because
            // ResolveProjection returns null for both 'verbose' and unknown levels.
            if (requestedFields == null && !verboseRequested && ShouldUseCompactDefaults(toolArgs))
            {
                requestedFields = GetDefaultCompactFields(toolName);
            }

            if (obj["isTruncated"]?.Value<bool>() == true)
            {
                meta["truncated"] = true;
                var help = obj["help"] as JArray ?? new JArray();
                string truncateHint = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                    ? "Response truncated by gateway budget. Use limit/offset to page source content."
                    : "Response truncated by gateway budget. Narrow filters or lower limit for deterministic follow-up.";

                if (!help.Any(item => string.Equals(item?.ToString(), truncateHint, StringComparison.OrdinalIgnoreCase)))
                {
                    help.Add(truncateHint);
                }

                obj["help"] = help;
            }

            if (!isError &&
                string.Equals(obj["status"]?.ToString(), "ok", StringComparison.Ordinal) &&
                obj["noChange"] == null &&
                (string.Equals(obj["code"]?.ToString(), "NoChange", StringComparison.Ordinal)
                 || string.Equals(obj["result"]?["noChangeReason"]?.ToString(), "literal_identical", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(obj["details"]?.ToString(), "No change", StringComparison.OrdinalIgnoreCase)))
            {
                obj["noChange"] = true;
            }

            string[] collectionKeys = { "results", "objects", "items", "tools", "checks", "entries", "nodes", "controls" };
            foreach (var key in collectionKeys)
            {
                if (obj[key] is not JArray arr)
                {
                    continue;
                }

                if (requestedFields != null &&
                    requestedFields.Count > 0 &&
                    ShouldProjectFieldsForTool(toolName))
                {
                    obj[key] = ProjectArrayItems(arr, requestedFields);
                    meta["fields"] = new JArray(requestedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));
                    arr = (JArray)obj[key]!;
                }

                if (meta["totalByType"] == null)
                {
                    var totalsByType = BuildTotalsByType(arr);
                    if (totalsByType.Properties().Any())
                    {
                        meta["totalByType"] = totalsByType;
                    }
                }

                int returned = arr.Count;
                if (obj["returned"] == null) obj["returned"] = returned;
                if (obj["empty"] == null) obj["empty"] = returned == 0;
                if ((obj["empty"]?.Value<bool>() ?? false))
                {
                    EnsureEmptyStateHelp(obj, toolName);
                }

                int? total = TryReadInt(obj["total"]) ??
                             TryReadInt(obj["count"]) ??
                             TryReadInt(obj["totalCount"]);
                if (total.HasValue && obj["total"] == null)
                {
                    obj["total"] = total.Value;
                }

                int? limit = TryReadInt(toolArgs?["limit"]);
                int offset = TryReadInt(toolArgs?["offset"]) ?? 0;
                int? effectiveTotal = TryReadInt(obj["total"]);

                if (limit.HasValue && effectiveTotal.HasValue)
                {
                    bool hasMore = (offset + returned) < effectiveTotal.Value;
                    if (obj["hasMore"] == null) obj["hasMore"] = hasMore;
                    if (hasMore && obj["nextOffset"] == null)
                    {
                        obj["nextOffset"] = offset + returned;
                    }
                }

                break;
            }

            // Only emit a `meta` block when at least one signal was attached;
            // an empty `{}` is pure overhead for the 90% of responses that have
            // no truncation/projection/totals to surface.
            if (meta.Properties().Any())
            {
                obj["meta"] = meta;
            }
            else
            {
                obj.Remove("meta");
            }

            // SQL-dialect nudge for DB tools. The LLM already sees the dialect in
            // whoami.database.default.dialect, but planting it on the response of the
            // tool that actually returns SQL is the second-nudge that lets the agent
            // align dialect at point-of-use without re-reading whoami.
            try
            {
                if (IsSqlGeneratingTool(toolName, toolArgs) && obj["dialect"] == null)
                {
                    var info = GetCachedDatabaseInfo();
                    var defaultStore = info?["default"] as JObject;
                    string? dialect = defaultStore?["dialect"]?.ToString();
                    string? type = defaultStore?["type"]?.ToString();
                    if (!string.IsNullOrEmpty(dialect) && !string.Equals(dialect, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        obj["dialect"] = dialect;
                        if (!string.IsNullOrEmpty(type)) obj["dialectType"] = type;
                    }
                }
            }
            catch { /* best-effort UX sugar */ }

            // next_legal_actions injection — last step.
            // SOTA LLM-UX: state-changing tool responses carry an additive
            // array of the most-likely useful next tool calls so the LLM
            // doesn't have to guess across turns. Read-only tools and
            // payloads without a natural follow-up return null and the
            // field is simply omitted. Spec-clean: extra top-level field;
            // clients that don't know about it ignore it.
            try
            {
                if (obj["next_legal_actions"] == null)
                {
                    JArray? actions = NextLegalActionsBuilder.BuildFor(toolName, toolArgs, obj, isError);
                    if (actions != null && actions.Count > 0)
                    {
                        obj["next_legal_actions"] = actions;
                    }
                }
            }
            catch
            {
                // Builder is best-effort UX sugar; never let it break the
                // response envelope.
            }

            return obj;
        }

        private static void EnsureEmptyStateHelp(JObject obj, string toolName)
        {
            var help = obj["help"] as JArray ?? new JArray();
            string hint = string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase)
                ? "No matches found for the current query. Try broader terms or remove filters."
                : string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                    ? "No objects found for the current scope. Verify parentPath/parent filters."
                    : "No results returned for this request.";

            if (!help.Any(item => string.Equals(item?.ToString(), hint, StringComparison.OrdinalIgnoreCase)))
            {
                help.Add(hint);
            }

            obj["help"] = help;
        }

        private static bool ShouldProjectFieldsForTool(string toolName)
        {
            return string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildTotalsByType(JArray arr)
        {
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in arr.OfType<JObject>())
            {
                string type = row["type"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(type))
                {
                    continue;
                }

                if (!totals.ContainsKey(type))
                {
                    totals[type] = 0;
                }

                totals[type] += 1;
            }

            var outObj = new JObject();
            foreach (var kv in totals.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                outObj[kv.Key] = kv.Value;
            }

            return outObj;
        }

        private static JArray ProjectArrayItems(JArray arr, HashSet<string> fields)
        {
            var projected = new JArray();
            foreach (var row in arr)
            {
                if (row is not JObject rowObj)
                {
                    projected.Add(row.DeepClone());
                    continue;
                }

                var outRow = new JObject();
                foreach (var field in fields)
                {
                    if (rowObj.TryGetValue(field, StringComparison.OrdinalIgnoreCase, out var value))
                    {
                        outRow[field] = value.DeepClone();
                    }
                }

                projected.Add(outRow);
            }

            return projected;
        }

        // Returns true when compact-by-default projection should be applied for tools that
        // declare a default compact field set in GetDefaultCompactFields. Default behavior
        // (no axiCompact key) is TRUE — the LLM must pass `axiCompact: false` to opt out.
        private static bool ShouldUseCompactDefaults(JObject? toolArgs)
        {
            if (toolArgs == null) return true;
            var token = toolArgs["axiCompact"];
            if (token == null) return true;
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }
            return !bool.TryParse(token.ToString(), out bool parsed) || parsed;
        }

        /// <summary>
        /// Friction 2026-05-22 #64: resolve projection=minimal|standard|verbose to
        /// the field set the gateway should apply. Returns null for unknown levels
        /// or when the tool doesn't support projection (caller falls back to
        /// axiCompact defaults).
        /// </summary>
        ///   - minimal: name + kind/type + lastUpdate (3 fields, smallest legal shape)
        ///   - standard: GetDefaultCompactFields(toolName) — same as today's default
        ///   - verbose: returns null so no projection filter is applied → full payload
        internal static HashSet<string>? ResolveProjection(string toolName, string projection)
        {
            if (string.IsNullOrWhiteSpace(projection)) return null;
            string p = projection.Trim().ToLowerInvariant();
            if (p == "verbose")
            {
                // No filter at all — caller sees every field the worker emitted.
                return null;
            }
            if (p == "minimal")
            {
                // The smallest legal projection. Matches the schema description
                // exactly: {name, type, lastUpdate}. (Prior versions also whitelisted
                // 'kind' defensively but no worker emits it today — keeping the
                // field-set tight so 'minimal' is honest about its contract.)
                return new HashSet<string>(
                    new[] { "name", "type", "lastUpdate" },
                    StringComparer.OrdinalIgnoreCase);
            }
            if (p == "standard")
            {
                // Fall through to today's default. GetDefaultCompactFields is the
                // single source of truth — keeping projection=standard in lockstep.
                return GetDefaultCompactFields(toolName);
            }
            // Unknown projection level — treat like default (caller will fall back).
            return null;
        }

        private static string BuildIndexingMessage(string? status, double? progress, int? etaMs)
        {
            string s = status ?? "Cold";
            string phase = string.Equals(s, "Reindexing", StringComparison.OrdinalIgnoreCase) ? "Rebuilding index"
                : string.Equals(s, "UltraLiteReady", StringComparison.OrdinalIgnoreCase) ? "Walking KB (ultra-lite pass)"
                : string.Equals(s, "Cold", StringComparison.OrdinalIgnoreCase) ? "Building index from cold start"
                : "Building index";

            var parts = new System.Collections.Generic.List<string> { phase };
            if (progress.HasValue && progress.Value > 0 && progress.Value < 1)
            {
                parts.Add($"{(int)Math.Round(progress.Value * 100)}% complete");
            }
            if (etaMs.HasValue && etaMs.Value > 0)
            {
                int seconds = (int)Math.Ceiling(etaMs.Value / 1000.0);
                parts.Add(seconds <= 1 ? "~1s remaining" : $"~{seconds}s remaining");
            }
            return string.Join(", ", parts) + ".";
        }

        private static bool IsSqlGeneratingTool(string toolName, JObject? toolArgs)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase))
            {
                string? action = toolArgs?["action"]?.ToString();
                return string.Equals(action, "sql_ddl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "sql_navigation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_analyze", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_suggest", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_report", StringComparison.OrdinalIgnoreCase);
            }
            // Legacy aliases — keep emitting the nudge for callers using the old names
            // until they drop out of LegacyToolAliases.
            return string.Equals(toolName, "genexus_sql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_db_optimize", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string>? GetDefaultCompactFields(string toolName)
        {
            if (string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase))
            {
                // v2.6.8: lastUpdate is part of the compact projection — same
                // rationale as list_objects (small, answers "what changed").
                return new HashSet<string>(new[] { "name", "type", "path", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase))
            {
                // v2.6.8: keep lastUpdate in the compact projection — it's the
                // signal that powers "what changed?" workflows and is cheap (~30b).
                // createdAt/lastModifiedBy stay verbose-only at the worker.
                return new HashSet<string>(new[] { "name", "type", "path", "parentPath", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);
            }

            return null;
        }

        private static HashSet<string>? ParseRequestedFields(JObject? toolArgs)
        {
            if (toolArgs == null) return null;
            var token = toolArgs["fields"];
            if (token == null) return null;

            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        fields.Add(item.Trim());
                    }
                }
            }
            else
            {
                string raw = token.ToString();
                foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = piece.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        fields.Add(value);
                    }
                }
            }

            return fields.Count == 0 ? null : fields;
        }

        private static int? TryReadInt(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.Float) return (int)Math.Floor(token.Value<double>());
            if (token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool IsOriginAllowed(string? origin, ServerConfig? serverConfig)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
            if (originUri.IsLoopback) return true;

            var allowedOrigins = serverConfig?.AllowedOrigins;
            if (allowedOrigins == null || allowedOrigins.Count == 0) return false;

            return allowedOrigins.Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase));
        }

        private static HttpSessionState CreateHttpSession()
        {
            return _httpSessions.Create();
        }

        private static void QueueSessionMessage(HttpSessionState session, string payload)
        {
            _httpSessions.Enqueue(session, payload);
        }

        private static async Task<IResult> HandleMcpSseStream(HttpContext context)
        {
            var protocolError = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);
            if (protocolError != null)
                return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

            string? sessionId = context.Request.Headers["MCP-Session-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new { error = "Missing MCP-Session-Id header." });

            if (!_httpSessions.TryGet(sessionId, out var session))
                return Results.NotFound(new { error = "Unknown or expired MCP session." });

            if (session == null)
                return Results.NotFound(new { error = "Unknown or expired MCP session." });

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["MCP-Session-Id"] = session.Id;

            await context.Response.WriteAsync("retry: 5000\n");
            await context.Response.WriteAsync($"event: session\ndata: {{\"sessionId\":\"{session.Id}\"}}\n\n");
            await context.Response.Body.FlushAsync();

            try
            {
                // Ironclad SSE: No deadline, keep alive indefinitely until client or server disconnects.
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    string? payload = null;
                    lock (session.PendingMessages)
                    {
                        if (session.PendingMessages.Count > 0)
                            payload = session.PendingMessages.Dequeue();
                    }

                    if (payload != null)
                    {
                        string encodedPayload = payload.Replace("\r", "").Replace("\n", "\ndata: ");
                        await context.Response.WriteAsync($"event: message\ndata: {encodedPayload}\n\n");
                        await context.Response.Body.FlushAsync();
                        continue;
                    }

                    try
                    {
                        await context.Response.WriteAsync(": keepalive\n\n");
                        await context.Response.Body.FlushAsync();
                        await Task.Delay(5000, context.RequestAborted);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                Log($"[HTTP] SSE stream error for session {session.Id}: {ex.Message}");
            }

            return Results.Empty;
        }

        private static async Task<IResult> HandleJsonRpcHttpRequest(HttpRequest request)
        {
            using (var reader = new StreamReader(request.Body))
            {
                string body = await reader.ReadToEndAsync();
                string id = "no-id";

                try
                {
                    var requestObj = JsonConvert.DeserializeObject<JObject>(body);
                    if (requestObj == null) return Results.Json(new { jsonrpc = "2.0", id = (string?)null, error = new { code = -32700, message = "Invalid JSON" } }, statusCode: 400);

                    id = requestObj["id"]?.ToString() ?? "no-id";
                    var sessionError = McpHttpProtocol.TryGetValidSession(_httpSessions, request, requestObj, out var session);
                    if (sessionError != null)
                        return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32001, message = sessionError.Value.Message } }, statusCode: sessionError.Value.StatusCode);

                    var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers);
                    if (protocolError != null)
                        return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32002, message = protocolError.Value.Message } }, statusCode: protocolError.Value.StatusCode);

                    id = requestObj["id"]?.ToString() ?? "no-id";
                    string method = requestObj["method"]?.ToString() ?? "unknown";
                    string bodyBrief = body.Length > 100 ? body.Substring(0, 100) + "..." : body;
                    Log($"[HTTP] Received {method} (ID: {id}) - Body: {bodyBrief}");

                    string httpSessionId = session?.Id ?? request.Headers["MCP-Session-Id"].FirstOrDefault() ?? "http";
                    var response = await ProcessMcpRequest(requestObj, httpSessionId);

                    if (McpHttpProtocol.IsInitializeRequest(requestObj))
                    {
                        var newSession = CreateHttpSession();
                        request.HttpContext.Response.Headers["MCP-Session-Id"] = newSession.Id;
                        QueueSessionMessage(newSession, JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            method = "notifications/message",
                            @params = new
                            {
                                level = "info",
                                logger = "transport",
                                data = "HTTP MCP session initialized."
                            }
                        }));
                    }
                        
                    if (response != null)
                    {
                        Log($"[HTTP] Serializing response for {id}...");
                        string jsonResponse = response.ToString(Formatting.None);
                        Log($"[HTTP] Sending {jsonResponse.Length} bytes to {id}");
                        return Results.Content(jsonResponse, "application/json; charset=utf-8", Encoding.UTF8);
                    }

                    if (requestObj["id"] == null)
                    {
                        Log($"[HTTP] Notification {method} completed without response body.");
                        return Results.NoContent();
                    }

                    return Results.BadRequest(new { error = "No response generated" });
                }
                catch (OperationCanceledException)
                {
                    Log($"[HTTP] Request aborted by client: {id}");
                    return Results.StatusCode(499); // Client Closed Request
                }
                catch (Exception ex)
                {
                    Log($"[HTTP] Error processing {id}: {ex.Message}");
                    return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32603, message = $"Gateway Error: {ex.Message}" } });
                }
            }
        }

        static Task StartHttpServer(Configuration config)
        {
            var serverConfig = config.Server ?? new ServerConfig();
            string bindAddress = string.IsNullOrWhiteSpace(serverConfig.BindAddress) ? "0.0.0.0" : serverConfig.BindAddress;
            Log($"[HTTP] Starting server on {bindAddress}:{serverConfig.HttpPort}...");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://{bindAddress}:{serverConfig.HttpPort}");
            builder.Logging.ClearProviders();
            builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });
            var app = builder.Build();
            app.UseResponseCompression();
            _ = Task.Run(() => RunSessionCleanupLoop(app.Lifetime.ApplicationStopping));
            _ = Task.Run(() => RunLeaseHeartbeatLoop(app.Lifetime.ApplicationStopping));

            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                {
                    string? origin = context.Request.Headers["Origin"].FirstOrDefault();
                    if (!IsOriginAllowed(origin, serverConfig))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Origin not allowed.");
                        return;
                    }
                }

                await next();
            });

            app.MapPost("/mcp", async (HttpRequest request) => await HandleJsonRpcHttpRequest(request));
            app.MapGet("/mcp", async (HttpContext context) => await HandleMcpSseStream(context));
            app.MapDelete("/mcp", (HttpRequest request) =>
            {
                var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers);
                if (protocolError != null)
                    return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

                string? sessionId = request.Headers["MCP-Session-Id"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(sessionId))
                    return Results.BadRequest(new { error = "Missing MCP-Session-Id header." });

                if (!_httpSessions.Remove(sessionId))
                    return Results.NotFound(new { error = "Unknown or expired MCP session." });

                Log($"[HTTP] Session {sessionId} terminated by client.");
                return Results.NoContent();
            });

            return app.RunAsync();
        }

        private static void TryKillProcessOnPort(int port)
        {
            try {
               Log($"[PortRecovery] Attempting to find process on port {port}...");
               var process = new Process();
               process.StartInfo.FileName = "netstat";
               process.StartInfo.Arguments = "-ano";
               process.StartInfo.RedirectStandardOutput = true;
               process.StartInfo.UseShellExecute = false;
               process.StartInfo.CreateNoWindow = true;
               process.Start();
               string output = process.StandardOutput.ReadToEnd();
               process.WaitForExit();

               var lines = output.Split('\n');
               foreach (var line in lines)
               {
                   if (line.Contains($":{port}") && line.Contains("LISTENING"))
                   {
                       var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                       var pidStr = parts.Last().Trim();
                       if (int.TryParse(pidStr, out int pid) && pid != Environment.ProcessId)
                       {
                           Log($"[PortRecovery] Found zombie process {pid} on port {port}. Killing it...");
                           try {
                               var zombie = Process.GetProcessById(pid);
                               zombie.Kill(true);
                               zombie.WaitForExit(3000);
                           } catch { } // Process might already be gone
                       }
                   }
               }
            } catch (Exception ex) { Log($"[PortRecovery] Error: {ex.Message}"); }
        }
    }
}
