using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Discrete stages of the object reading pipeline.
    /// Used for granular observability and failure diagnostics (issue #479).
    /// </summary>
    public static class ReadDiagnosticStages
    {
        public const string ResolveObject = "resolve_object";
        public const string RefreshObject = "refresh_object";
        public const string AccessPart = "access_part";
        public const string QueryVariables = "query_variables";
        public const string ExtractContent = "extract_content";
    }

    /// <summary>
    /// Tracks ambient state and stage progression for a read operation.
    /// </summary>
    public sealed class ReadDiagnosticContext
    {
        private static readonly System.Threading.AsyncLocal<string> _ambientCorrelationId =
            new System.Threading.AsyncLocal<string>();

        public static string CurrentCorrelationId
        {
            get => _ambientCorrelationId.Value;
            set => _ambientCorrelationId.Value = value;
        }

        public string CorrelationId { get; set; }
        public string Target { get; set; }
        public string PartName { get; set; }
        public bool Refresh { get; set; }
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public Stopwatch Stopwatch { get; set; } = Stopwatch.StartNew();
        public string CurrentStage { get; set; } = ReadDiagnosticStages.ResolveObject;
        public KBObject ResolvedObject { get; set; }
        public JObject RequestedSelector { get; set; }
    }

    /// <summary>
    /// Test seam for simulating controlled failures at each pipeline stage (issue #479).
    /// Safe for production: inactive when InjectedStage is null.
    /// </summary>
    public static class ReadDiagnosticFaultInjector
    {
        public static string InjectedStage { get; set; }
        public static Exception InjectedException { get; set; }
        public static bool PersistenceFailure { get; set; }

        public static void MaybeThrow(string currentStage)
        {
            if (InjectedStage != null && string.Equals(InjectedStage, currentStage, StringComparison.OrdinalIgnoreCase))
            {
                var ex = InjectedException ?? new InvalidOperationException($"Fault injected at stage {currentStage}");
                throw ex;
            }
        }
    }

    /// <summary>
    /// In-memory ring buffer holding recent read failure diagnostics for inspection and tests.
    /// </summary>
    public static class ReadDiagnosticStore
    {
        private const int MaxEntries = 50;
        private static readonly ConcurrentDictionary<string, JObject> _records =
            new ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> _order = new ConcurrentQueue<string>();

        public static void Record(string correlationId, JObject diagnostic)
        {
            if (string.IsNullOrWhiteSpace(correlationId) || diagnostic == null) return;
            _records[correlationId] = diagnostic;
            _order.Enqueue(correlationId);
            while (_order.Count > MaxEntries && _order.TryDequeue(out var oldest))
            {
                _records.TryRemove(oldest, out _);
            }
        }

        public static bool TryGet(string correlationId, out JObject diagnostic)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                diagnostic = null;
                return false;
            }
            return _records.TryGetValue(correlationId, out diagnostic);
        }

        public static void Clear()
        {
            _records.Clear();
            while (_order.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// Redacts secrets, passwords, tokens, connection strings and KB source/variable values
    /// from exception messages and stack traces, enforcing explicit size and depth bounds (issue #479).
    /// </summary>
    public static class ReadDiagnosticSanitizer
    {
        public const int MaxMessageLength = 500;
        public const int MaxStackTraceLength = 2000;
        public const int MaxStackTraceFrames = 15;
        public const int MaxExceptionDepth = 5;

        private static readonly Regex VariableAssignmentRegex = new Regex(
            @"&[A-Za-z0-9_]+\s*=\s*[^;\r\n]+", RegexOptions.Compiled);

        private static readonly Regex SourceBlockRegex = new Regex(
            @"<(?:Source|Properties|Content)[^>]*>.*?</(?:Source|Properties|Content)>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex GeneXusBlockRegex = new Regex(
            @"(?is)\b(?:Event\b.*?EndEvent|Sub\b.*?EndSub|For\s+each\b.*?EndFor|Do\s+case\b.*?EndCase|Do\s+while\b.*?EndDo)\b",
            RegexOptions.Compiled);

        // Only continuation lines are treated as statements: the first line of a legitimate
        // SDK/Worker message may start with a keyword ("Event 'Start' is not defined ...")
        // and must survive. Opened blocks on the first line are still caught by GeneXusBlockRegex.
        private static readonly Regex GeneXusStatementRegex = new Regex(
            @"(?i)(?<=\n)[ \t]*(?:Event|EndEvent|Sub|EndSub|For\s+each|EndFor|Do\s+case|EndCase|Do\s+while|EndDo)\b[^\r\n]*",
            RegexOptions.Compiled);

        // The parenthesis must follow the command name directly (or ".Call") so prose such as
        // "call (RPC)" is preserved; one level of nested parentheses is consumed.
        private static readonly Regex GeneXusCommandRegex = new Regex(
            @"(?i)(?:\b(?:msg|call|prc)|\.call\s*)\((?:[^()]|\([^()]*\))*\)",
            RegexOptions.Compiled);

        public static string SanitizeText(string text, out bool truncated)
        {
            truncated = false;
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // 1. Redact credentials/tokens/secrets/passwords using shared LogRedaction
            string sanitized = LogRedaction.Redact(text);

            // 2. Redact variable value assignments
            sanitized = VariableAssignmentRegex.Replace(sanitized, match =>
            {
                int eq = match.Value.IndexOf('=');
                string varName = eq > 0 ? match.Value.Substring(0, eq).Trim() : "&variable";
                return varName + " = <redacted>";
            });

            // 3. Redact XML source/content blocks
            sanitized = SourceBlockRegex.Replace(sanitized, "<Source><redacted></Source>");

            // 4. Redact raw GeneXus source code constructs, commands and blocks (issue #549)
            sanitized = GeneXusBlockRegex.Replace(sanitized, "[source code omitted]");
            sanitized = GeneXusCommandRegex.Replace(sanitized, "[source code omitted]");
            sanitized = GeneXusStatementRegex.Replace(sanitized, "[source code omitted]");
            sanitized = Regex.Replace(sanitized, @"(\[source code omitted\]\s*)+", "[source code omitted]");

            // 5. Enforce explicit message length limit
            if (sanitized.Length > MaxMessageLength)
            {
                truncated = true;
                sanitized = sanitized.Substring(0, MaxMessageLength) + " ... [TRUNCATED: max length exceeded]";
            }

            return sanitized;
        }

        /// <summary>
        /// Redacts a stack trace and enforces both size bounds under a hard ceiling (issue #550):
        /// at most <see cref="MaxStackTraceFrames"/> frames are kept, and the returned string,
        /// truncation markers included, never exceeds <see cref="MaxStackTraceLength"/> characters.
        /// When both bounds are exceeded the kept frames are cut to fit and the output ends with
        /// the length marker followed by the frame marker, so each truncation stays recognizable.
        /// </summary>
        public static string SanitizeStackTrace(string stackTrace, out bool truncated)
        {
            truncated = false;
            if (string.IsNullOrWhiteSpace(stackTrace)) return null;

            string sanitized = LogRedaction.Redact(stackTrace);

            // 1. Cap frames; the marker is appended after the length check so it is never cut off
            var lines = sanitized.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            string frameSuffix = string.Empty;
            if (lines.Length > MaxStackTraceFrames)
            {
                truncated = true;
                sanitized = string.Join(Environment.NewLine, lines.Take(MaxStackTraceFrames));
                frameSuffix = Environment.NewLine + "... [TRUNCATED: max stack frames exceeded]";
            }

            // 2. Cap character length; both markers count towards the ceiling (issue #550)
            if (sanitized.Length + frameSuffix.Length > MaxStackTraceLength)
            {
                truncated = true;
                const string lengthMarker = " ... [TRUNCATED: max stack length exceeded]";
                sanitized = sanitized.Substring(0, MaxStackTraceLength - lengthMarker.Length - frameSuffix.Length)
                    + lengthMarker;
            }

            return sanitized + frameSuffix;
        }

        public static JObject SanitizeException(Exception ex, int currentDepth = 1)
        {
            if (ex == null) return null;

            var reasons = new List<string>();
            string sanitizedMsg = SanitizeText(ex.Message, out bool msgTruncated);
            if (msgTruncated) reasons.Add("message_length");

            string sanitizedStack = SanitizeStackTrace(ex.StackTrace, out bool stackTruncated);
            if (stackTruncated) reasons.Add("stack_trace_length");

            var result = new JObject
            {
                ["type"] = ex.GetType().FullName,
                ["message"] = sanitizedMsg
            };

            if (!string.IsNullOrEmpty(sanitizedStack))
                result["stackTrace"] = sanitizedStack;

            if (ex.InnerException != null)
            {
                if (currentDepth < MaxExceptionDepth)
                {
                    var innerException = SanitizeException(ex.InnerException, currentDepth + 1);
                    result["innerException"] = innerException;
                    if (innerException["truncationReasons"] is JArray innerReasons)
                    {
                        reasons.AddRange(innerReasons.Select(reason => reason.ToString()));
                    }
                }
                else
                {
                    reasons.Add("max_depth_exceeded");
                    result["innerException"] = new JObject
                    {
                        ["type"] = ex.InnerException.GetType().FullName,
                        ["message"] = "[TRUNCATED: max exception depth exceeded]"
                    };
                }
            }

            bool wasTruncated = reasons.Count > 0;
            result["truncated"] = wasTruncated;
            if (wasTruncated)
                result["truncationReasons"] = new JArray(reasons);

            return result;
        }
    }

    /// <summary>
    /// Builds sanitized read failure envelopes and persists diagnostics safely (issue #479).
    /// </summary>
    public static class ReadDiagnosticHelper
    {
        public static JObject CaptureWorkerContext(ReadDiagnosticContext context)
        {
            var ctx = new JObject
            {
                ["workerPid"] = Process.GetCurrentProcess().Id
            };

            string generation = Environment.GetEnvironmentVariable("GXMCP_WORKER_GENERATION");
            if (!string.IsNullOrWhiteSpace(generation))
                ctx["workerGeneration"] = generation;

            try
            {
                ctx["mcpVersion"] = typeof(ObjectService).Assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                ctx["mcpVersion"] = "unknown";
            }

            try
            {
                var sdk = SdkIdentity.Detect();
                ctx["sdkVersion"] = sdk?.Version ?? sdk?.Major ?? "unknown";
            }
            catch
            {
                ctx["sdkVersion"] = "unknown";
            }

            ctx["requestedSelector"] = context?.RequestedSelector ?? new JObject
            {
                ["target"] = context?.Target ?? "unknown",
                ["part"] = context?.PartName ?? "unknown",
                ["refresh"] = context?.Refresh ?? false
            };

            if (context?.ResolvedObject != null)
            {
                ctx["resolvedIdentity"] = new JObject
                {
                    ["name"] = context.ResolvedObject.Name,
                    ["type"] = context.ResolvedObject.TypeDescriptor?.Name ?? "unknown",
                    ["guid"] = context.ResolvedObject.Guid.ToString("D"),
                    ["entityKey"] = context.ResolvedObject.Key?.ToString()
                };
            }
            else
            {
                ctx["resolvedIdentity"] = "unknown";
            }

            ctx["part"] = context?.PartName ?? "unknown";
            ctx["refresh"] = context?.Refresh ?? false;

            return ctx;
        }

        public static JObject CaptureStateSnapshot(KbService kbService)
        {
            var snapshot = new JObject
            {
                ["observedAtUtc"] = DateTime.UtcNow.ToString("o")
            };

            try
            {
                var indexState = kbService?.GetIndexCache()?.GetState();
                if (indexState != null)
                {
                    snapshot["index"] = new JObject
                    {
                        ["status"] = indexState.Status ?? "unknown",
                        ["freshness"] = indexState.Freshness ?? "unknown",
                        ["totalObjects"] = indexState.TotalObjects,
                        ["progress"] = indexState.Progress
                    };
                }
            }
            catch { }

            try
            {
                var backfillState = SourceStoreBackfillService.Instance.GetState();
                if (backfillState != null)
                {
                    snapshot["sourceStore"] = BuildSourceStoreSnapshot(backfillState);
                }
            }
            catch { }

            return snapshot;
        }

        internal static JObject BuildSourceStoreSnapshot(SourceStoreBackfillState backfillState)
        {
            return new JObject
            {
                ["state"] = backfillState.State ?? "unknown",
                ["processedCount"] = backfillState.ProcessedObjects,
                ["totalCount"] = backfillState.TotalObjects,
                ["retryCount"] = backfillState.RetryCount,
                ["failedCount"] = backfillState.FailedObjects,
                ["readFailedObjects"] = new JArray((backfillState.ReadFailedObjects ?? new List<string>())
                    .Take(SourceStoreBackfillState.MaxReportedReadFailedObjects)),
                ["lastError"] = backfillState.LastError != null
                    ? LogRedaction.Redact(backfillState.LastError)
                    : null
            };
        }

        public static string CreateFailureEnvelope(ReadDiagnosticContext context, Exception ex, KbService kbService)
        {
            string correlationId = !string.IsNullOrWhiteSpace(context?.CorrelationId)
                ? context.CorrelationId
                : Guid.NewGuid().ToString("N");

            long durationMs = context?.Stopwatch?.ElapsedMilliseconds ?? 0;
            string stage = context?.CurrentStage ?? ReadDiagnosticStages.ResolveObject;
            string target = context?.Target ?? "unknown";
            string part = context?.PartName ?? "unknown";
            string timestampUtc = DateTime.UtcNow.ToString("o");

            var sanitizedException = ReadDiagnosticSanitizer.SanitizeException(ex);
            var workerContext = CaptureWorkerContext(context);
            var stateSnapshot = CaptureStateSnapshot(kbService);

            var diagnosticPayload = new JObject
            {
                ["correlationId"] = correlationId,
                ["timestampUtc"] = timestampUtc,
                ["durationMs"] = durationMs,
                ["failedStage"] = stage,
                ["exception"] = sanitizedException,
                ["context"] = workerContext,
                ["stateSnapshot"] = stateSnapshot,
                ["persisted"] = false,
                ["enqueued"] = false
            };

            // Attempt persistence without substituting original error on logging failure (Acceptance Criterion 2)
            try
            {
                if (ReadDiagnosticFaultInjector.PersistenceFailure)
                {
                    throw new System.IO.IOException("Simulated persistence failure to test capture degradation.");
                }

                string exType = sanitizedException?["type"]?.ToString() ?? "Exception";
                string exMsg = sanitizedException?["message"]?.ToString() ?? "Unknown error";
                Logger.Error($"[READ-DIAGNOSTIC] [cid:{correlationId}] stage={stage} target={target} part={part} durationMs={durationMs} exType={exType} msg={exMsg}");
                diagnosticPayload["persisted"] = false;
                diagnosticPayload["enqueued"] = true;
            }
            catch (Exception logEx)
            {
                diagnosticPayload["persisted"] = false;
                diagnosticPayload["enqueued"] = false;
                diagnosticPayload["persistenceError"] = "Failed to log diagnostic: " + logEx.Message;
            }

            ReadDiagnosticStore.Record(correlationId, diagnosticPayload);

            string clientMsg = sanitizedException?["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(clientMsg)) clientMsg = "Worker operation failed.";

            string hint = $"Read failed at stage '{stage}'. Complete diagnostics are available in error.diagnostics (correlation ID '{correlationId}').";

            var errorExtra = new JObject
            {
                ["correlationId"] = correlationId,
                ["timestampUtc"] = timestampUtc,
                ["durationMs"] = durationMs,
                ["failedStage"] = stage,
                ["diagnostics"] = diagnosticPayload
            };

            return McpResponse.Err(
                code: "ReadFailure",
                message: $"Failed to read object '{target}': {clientMsg}",
                hint: hint,
                target: target,
                errorExtra: errorExtra);
        }
    }
}
