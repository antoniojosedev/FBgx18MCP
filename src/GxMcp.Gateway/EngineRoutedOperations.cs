using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Real routing seam for the experimental gx-engine backend
    /// (docs/engine_backend.md).
    ///
    /// When <c>Engine.Backend=gxengine</c>, the parity capabilities that declare
    /// product consumers in <see cref="EngineCapabilityRegistry"/> are served by
    /// gx-engine instead of the SDK Worker:
    ///
    /// <list type="bullet">
    /// <item><c>udm_storage_access</c> serves <c>genexus_read</c>,
    /// <c>genexus_query</c> and <c>genexus_edit</c> through the engine's
    /// <c>kb-read</c>/<c>kb-query</c>/<c>kb-write</c> invocations.</item>
    /// <item><c>parallel_rebuild_all</c> serves
    /// <c>genexus_lifecycle.build</c> and <c>genexus_lifecycle.rebuild</c>
    /// through the engine's <c>rebuild-all</c> invocation.</item>
    /// </list>
    ///
    /// Each operation admits a documented, deliberately narrow argument space
    /// (see docs/engine_backend.md, "Served operations and admitted space"). A
    /// call outside that space is never approximated: with
    /// <c>Engine.Fallback=sdk</c> it falls through to the SDK path unchanged, and
    /// with <c>Engine.Fallback=error</c> it is refused with
    /// <c>EngineOperationOutsideAdmittedSpace</c>. An engine invocation that
    /// fails is treated the same way, so an engine outage can never silently
    /// change a write into something weaker.
    ///
    /// Engine-served results carry an <c>engine</c> block
    /// (<c>{ backend: "gxengine", capability, operation, fallback }</c>) so the
    /// caller can tell which backend answered.
    /// </summary>
    internal sealed class EngineRoutedOperations
    {
        private readonly Func<Configuration?> _activeConfig;
        private readonly Action<string> _log;
        private readonly Func<JToken, bool, string?, JObject?, JObject> _buildToolResult;

        internal EngineRoutedOperations(
            Func<Configuration?> activeConfig,
            Action<string> log,
            Func<JToken, bool, string?, JObject?, JObject> buildToolResult)
        {
            _activeConfig = activeConfig;
            _log = log;
            _buildToolResult = buildToolResult;
        }

        internal const string EngineOutsideSpaceCode = "EngineOperationOutsideAdmittedSpace";
        internal const string EngineInvocationFailedCode = "EngineInvocationFailed";
        internal const string EnginePartNotFoundCode = "ObjectPartNotFound";
        internal const string EngineTypeMismatchCode = "ObjectTypeMismatch";

        /// <summary>
        /// Engine builds are serialized: two concurrent rebuild-all runs would
        /// interleave writes in the same output directory.
        /// </summary>
        private static readonly SemaphoreSlim EngineBuildGate = new SemaphoreSlim(1, 1);

        // The admitted argument space of each served operation. `genexus_edit` has
        // none: it is held outside the engine entirely (see DispatchEngineEdit).
        private static readonly EngineAdmittedSpace ReadSpace = new EngineAdmittedSpace(
            "read", "name", "objectName", "type", "part", "parts", "kb", "refresh");

        private static readonly EngineAdmittedSpace QuerySpace = new EngineAdmittedSpace(
            "query", "query", "typeFilter", "limit", "exactMatch", "kb");

        private static readonly EngineAdmittedSpace LifecycleSpace = new EngineAdmittedSpace(
            "lifecycle", "action", "target", "mode", "callers", "includeCallees",
            "wait_until_done", "estimated_seconds", "skipFullDeploy", "kb");

        /// <summary>
        /// Maps one MCP tool call onto an operation key the registry can route.
        /// Only the operations registered as capability consumers are eligible;
        /// every other tool name (or lifecycle action) returns null and keeps its
        /// existing path.
        /// </summary>
        internal static string? EngineOperationKeyFor(string? toolName, string? lifecycleAction)
        {
            if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)) return "genexus_read";
            if (string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase)) return "genexus_query";
            if (string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase)) return "genexus_edit";
            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(lifecycleAction, "build", StringComparison.OrdinalIgnoreCase))
                    return "genexus_lifecycle.build";
                if (string.Equals(lifecycleAction, "rebuild", StringComparison.OrdinalIgnoreCase))
                    return "genexus_lifecycle.rebuild";
            }
            return null;
        }

        /// <summary>
        /// The parity capability that declares <paramref name="operationKey"/> as
        /// a consumer, or null when no registered capability serves it. The
        /// registry stays the single source of truth: routing follows it instead
        /// of duplicating the mapping here.
        /// </summary>
        internal static string? EngineCapabilityIdForOperation(string operationKey)
        {
            if (string.IsNullOrWhiteSpace(operationKey)) return null;
            return EngineCapabilityRegistry.All
                .Where(capability => capability.MayRouteToEngine)
                .FirstOrDefault(capability => capability.Consumers.Any(
                    consumer => string.Equals(consumer, operationKey, StringComparison.OrdinalIgnoreCase)))
                ?.Id;
        }

        /// <summary>
        /// Entry point called from the tool dispatch pipeline before the SDK
        /// index gate. Returns null when the call must continue on its existing
        /// (SDK) path; otherwise returns the complete tool result for an
        /// engine-served call.
        /// </summary>
        internal async Task<JObject?> TryDispatchAsync(
            string toolName,
            string? lifecycleAction,
            JObject? toolArgs,
            string? kbPath,
            string? kbAlias,
            CancellationToken cancellationToken)
        {
            string? operationKey = EngineOperationKeyFor(toolName, lifecycleAction);
            if (operationKey == null) return null;

            string? capabilityId = EngineCapabilityIdForOperation(operationKey);
            if (capabilityId == null) return null;

            EngineBackendSettings settings = EngineBackendSettings.Resolve(_activeConfig());
            EngineRouteDecision decision = EngineRouter.Decide(capabilityId, settings);

            if (decision.Target == EngineRouteTarget.Reject)
            {
                var rejected = EngineErrorPayload(
                    decision.Code ?? EngineRouter.NotParityCode,
                    decision.Message ?? $"Engine capability '{capabilityId}' is not at parity.",
                    settings, capabilityId, operationKey, null);
                return _buildToolResult(rejected, true, toolName, toolArgs);
            }
            if (decision.Target == EngineRouteTarget.Sdk) return null;

            if (cancellationToken.IsCancellationRequested)
                throw new PreDispatchCancelledException(cancellationToken);

            // No Knowledge Base resolved yet: the SDK path owns the
            // KB_CONTEXT_REQUIRED answer, so the engine path stays out of it.
            if (string.IsNullOrWhiteSpace(kbPath)) return null;

            var context = new EngineRouteContext(
                settings, capabilityId, operationKey, toolName, toolArgs, kbPath!, kbAlias, cancellationToken);

            try
            {
                switch (operationKey)
                {
                    case "genexus_read":
                        return await DispatchEngineReadAsync(context).ConfigureAwait(false);
                    case "genexus_query":
                        return await DispatchEngineQueryAsync(context).ConfigureAwait(false);
                    case "genexus_edit":
                        return DispatchEngineEdit(context);
                    case "genexus_lifecycle.build":
                    case "genexus_lifecycle.rebuild":
                        return await DispatchEngineLifecycleAsync(context, lifecycleAction ?? "build").ConfigureAwait(false);
                    default:
                        return null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A report-shaping failure must not replay a completed engine build
                // on the SDK. Before invocation, the configured fallback still applies.
                _log($"[Engine] routing failed for {operationKey}: {ex.Message}");
                if (!settings.RejectUnsupported && !context.BuildInvocationCompleted) return null;
                var failure = EngineErrorPayload(EngineInvocationFailedCode, ex.Message, settings, capabilityId, operationKey, null);
                if (context.BuildInvocationCompleted) RequireEngineBuildReconciliation(failure);
                return _buildToolResult(failure, true, toolName, toolArgs);
            }
        }

        // ------------------------------------------------------------------
        // genexus_read -> kb-read
        // ------------------------------------------------------------------

        private async Task<JObject?> DispatchEngineReadAsync(EngineRouteContext ctx)
        {
            JObject args = ctx.Args ?? new JObject();
            string? name = FirstNonEmpty(EngineArgString(args, "name"), EngineArgString(args, "objectName"));
            if (string.IsNullOrWhiteSpace(name))
                return EngineOutsideResult(ctx, "`name` (or its `objectName` alias) is required by the engine read path");
            string? unsupportedArgument = ReadSpace.RefusalFor(args);
            if (unsupportedArgument != null) return EngineOutsideResult(ctx, unsupportedArgument);
            if (!EngineKbArgMatches(ctx, args))
                return EngineOutsideResult(ctx, $"`kb` selects a different Knowledge Base than the session's ('{ctx.KbAlias ?? ctx.KbPath}')");

            string? partsError = EngineReadRequestedParts(args, out List<string> requestedParts);
            if (partsError != null) return EngineOutsideResult(ctx, partsError);
            if (requestedParts.Count == 0)
                return EngineOutsideResult(ctx, "full-object reads (no `part`/`parts`) are outside the engine read space yet; request explicit parts (Source, Rules, Events, Conditions, Variables, Structure, WebForm, PatternInstance)");
            string? fullAlias = requestedParts.FirstOrDefault(part =>
                part.Equals("all", StringComparison.OrdinalIgnoreCase)
                || part.Equals("full", StringComparison.OrdinalIgnoreCase)
                || part.Equals("summary", StringComparison.OrdinalIgnoreCase)
                || part.Equals("360", StringComparison.OrdinalIgnoreCase));
            if (fullAlias != null)
                return EngineOutsideResult(ctx, $"full-object read aliases (`part={fullAlias}`) are outside the engine read space yet");

            var engineArgs = new List<string> { "--diagnostics=json", "kb-read", ctx.KbPath, name! };
            engineArgs.Add("--parts");
            engineArgs.Add(string.Join(",", requestedParts));

            GxEngineInvocation invocation = await GxEngineClient.InvokeAsync(ctx.Settings, engineArgs, ctx.CancellationToken).ConfigureAwait(false);
            if (!invocation.Ok) return EngineInvocationFailureResult(ctx, invocation);
            if (!TryParseEngineJson(invocation.StandardOutput, out JObject? json, out string? parseError) || json == null)
                return EngineInvocationFailureResult(ctx, GxEngineInvocation.Failure(
                    invocation.Executable, "EngineOutputUnrecognized",
                    $"kb-read did not produce a JSON object on stdout ({parseError}).",
                    stdout: invocation.StandardOutput));

            string typeName = json["type"]?.ToString() ?? "";
            string? requestedType = EngineArgString(args, "type");
            if (!string.IsNullOrWhiteSpace(requestedType) && !EngineTypeNamesMatch(requestedType!, typeName))
                return _buildToolResult(
                    EngineErrorPayload(EngineTypeMismatchCode,
                        $"object '{name}' is a {typeName}, not a {requestedType}.",
                        ctx.Settings, ctx.CapabilityId, ctx.OperationKey, null),
                    true, ctx.ToolName, ctx.Args);

            var partTexts = new List<(string StoredName, string Source)>();
            if (json["parts"] is JArray partsArray)
            {
                foreach (var entry in partsArray.OfType<JObject>())
                    partTexts.Add((entry["name"]?.ToString() ?? "", entry["source"]?.ToString() ?? ""));
            }
            List<string> missing = (json["missing_parts"] as JArray)?
                .Select(token => token.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList() ?? new List<string>();
            List<string> available = (json["available_parts"] as JArray)?
                .Select(token => token.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList() ?? new List<string>();

            if (partTexts.Count == 0 && missing.Count > 0)
            {
                var unavailable = EngineErrorPayload(EnginePartNotFoundCode,
                    $"part(s) {string.Join(", ", missing)} not found on '{name}'.",
                    ctx.Settings, ctx.CapabilityId, ctx.OperationKey, null);
                unavailable["availableParts"] = new JArray(available.Select(EngineFriendlyPartName));
                return _buildToolResult(unavailable, true, ctx.ToolName, ctx.Args);
            }

            if (requestedParts.Count == 1)
            {
                string storedName = partTexts.Count > 0 ? partTexts[0].StoredName : requestedParts[0];
                string text = partTexts.Count > 0 ? partTexts[0].Source : "";
                int lines = CountTextLines(text);
                var payload = new JObject
                {
                    ["name"] = json["name"]?.ToString() ?? name,
                    ["type"] = typeName,
                    ["part"] = EngineFriendlyPartName(storedName),
                    ["source"] = text,
                    ["isBase64"] = false,
                    ["isEmpty"] = text.Length == 0,
                    ["truncated"] = false,
                    ["isTruncatedByWorker"] = false,
                    ["offset"] = 0,
                    ["limit"] = lines,
                    ["totalLines"] = lines,
                    ["totalBytes"] = Encoding.UTF8.GetByteCount(text),
                    ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                };
                if (missing.Count > 0) payload["missingParts"] = new JArray(missing);
                _log($"[Engine] genexus_read served by gx-engine: {name} (part {storedName})");
                return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
            }

            var partsObject = new JObject();
            foreach (var (storedName, source) in partTexts)
                partsObject[EngineFriendlyPartName(storedName).ToLowerInvariant()] = source;

            var multiPayload = new JObject
            {
                ["name"] = json["name"]?.ToString() ?? name,
                ["type"] = typeName,
                ["parts"] = partsObject,
                ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
            };
            if (missing.Count > 0)
            {
                multiPayload["missingParts"] = new JArray(missing);
                multiPayload["availableParts"] = new JArray(available.Select(EngineFriendlyPartName));
            }
            _log($"[Engine] genexus_read served by gx-engine: {name} (parts {string.Join(",", requestedParts)})");
            return _buildToolResult(multiPayload, false, ctx.ToolName, ctx.Args);
        }

        // ------------------------------------------------------------------
        // genexus_query -> kb-query
        // ------------------------------------------------------------------

        private async Task<JObject?> DispatchEngineQueryAsync(EngineRouteContext ctx)
        {
            JObject args = ctx.Args ?? new JObject();
            string? unsupportedArgument = QuerySpace.RefusalFor(args);
            if (unsupportedArgument != null) return EngineOutsideResult(ctx, unsupportedArgument);
            if (!EngineKbArgMatches(ctx, args))
                return EngineOutsideResult(ctx, $"`kb` selects a different Knowledge Base than the session's ('{ctx.KbAlias ?? ctx.KbPath}')");
            if (args["exactMatch"]?.ToObject<bool?>() == false)
                return EngineOutsideResult(ctx, "`exactMatch:false` (fuzzy/text search) is outside the engine query space yet");

            string query = EngineArgString(args, "query") ?? "";
            string? objectName = null;
            string? objectType = null;
            string? usedBy = null;
            string[] tokens = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                int colon = token.IndexOf(':');
                if (colon > 0)
                {
                    string prefix = token.Substring(0, colon).ToLowerInvariant();
                    string value = token.Substring(colon + 1).Trim();
                    if (value.Length == 0)
                        return EngineOutsideResult(ctx, $"`{prefix}:` requires a value in the engine query space");
                    if (prefix == "type")
                    {
                        if (objectType != null) return EngineOutsideResult(ctx, "multiple `type:` filters are outside the engine query space yet");
                        objectType = value;
                    }
                    else if (prefix == "usedby")
                    {
                        if (usedBy != null) return EngineOutsideResult(ctx, "multiple `usedby:` filters are outside the engine query space yet");
                        usedBy = value;
                    }
                    else
                    {
                        return EngineOutsideResult(ctx, $"query prefix `{prefix}:` is outside the engine query space yet (supported: `type:`, `usedby:` and one exact object name)");
                    }
                }
                else
                {
                    if (objectName != null)
                        return EngineOutsideResult(ctx, "multi-term text queries are outside the engine query space yet (one exact object name, `type:` and `usedby:` are supported)");
                    objectName = token;
                }
            }

            string? typeFilter = EngineArgString(args, "typeFilter");
            if (!string.IsNullOrWhiteSpace(typeFilter))
            {
                if (objectType != null)
                    return EngineOutsideResult(ctx, "`typeFilter` combined with a `type:` query term is ambiguous in the engine query space");
                objectType = typeFilter;
            }

            if (objectName == null && objectType == null && usedBy == null)
                return EngineOutsideResult(ctx, "an empty query is outside the engine query space yet; pass an exact object name, `type:X` or `usedby:Y`");

            int limit = args["limit"]?.ToObject<int?>() ?? 0;
            if (limit < 0) limit = 0;

            var engineArgs = new List<string> { "--diagnostics=json", "kb-query", ctx.KbPath };
            if (objectName != null) { engineArgs.Add("--name"); engineArgs.Add(objectName); }
            if (objectType != null) { engineArgs.Add("--type"); engineArgs.Add(objectType); }
            if (usedBy != null) { engineArgs.Add("--usedby"); engineArgs.Add(usedBy); }
            if (limit > 0)
            {
                engineArgs.Add("--limit");
                engineArgs.Add(limit.ToString(CultureInfo.InvariantCulture));
            }

            GxEngineInvocation invocation = await GxEngineClient.InvokeAsync(ctx.Settings, engineArgs, ctx.CancellationToken).ConfigureAwait(false);
            if (!invocation.Ok) return EngineInvocationFailureResult(ctx, invocation);
            if (!TryParseEngineJson(invocation.StandardOutput, out JObject? json, out string? parseError) || json == null)
                return EngineInvocationFailureResult(ctx, GxEngineInvocation.Failure(
                    invocation.Executable, "EngineOutputUnrecognized",
                    $"kb-query did not produce a JSON object on stdout ({parseError}).",
                    stdout: invocation.StandardOutput));

            var results = new JArray();
            if (json["objects"] is JArray objects)
            {
                foreach (var entry in objects.OfType<JObject>())
                {
                    results.Add(new JObject
                    {
                        ["name"] = entry["name"]?.ToString(),
                        ["type"] = entry["type"]?.ToString(),
                    });
                }
            }
            int total = json["total"]?.ToObject<int?>() ?? results.Count;
            bool truncated = json["truncated"]?.ToObject<bool?>() ?? false;

            var payload = new JObject
            {
                ["results"] = results,
                ["count"] = results.Count,
                ["total"] = total,
                ["hasMore"] = truncated,
                ["truncated"] = truncated,
                ["pagination"] = new JObject
                {
                    ["offset"] = 0,
                    ["limit"] = limit > 0 ? limit : 50,
                    ["returned"] = results.Count,
                    ["total"] = total,
                    ["hasMore"] = truncated,
                },
                ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
            };
            _log($"[Engine] genexus_query served by gx-engine: query='{query}' ({results.Count}/{total})");
            return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
        }

        // ------------------------------------------------------------------
        // genexus_edit -> kb-write (held outside the served space)
        // ------------------------------------------------------------------

        /// <summary>
        /// The engine's writer mutates its in-process store only: against a live
        /// Knowledge Base database the stored object keeps its old version, so an
        /// engine-served edit would report a save that does not exist. Edits
        /// therefore stay outside the engine space (fail-closed: `Fallback=error`
        /// refuses, `Fallback=sdk` uses the SDK) until a live write path exists
        /// and is verified end to end. `gx-engine kb-write` itself refuses live
        /// writes for the same reason.
        /// </summary>
        private JObject? DispatchEngineEdit(EngineRouteContext ctx) =>
            EngineOutsideResult(ctx,
                "engine UDM writes are not persisted to live Knowledge Base storage yet (`kb-write` updates only its in-process store); the SDK path must serve `genexus_edit`");

        // ------------------------------------------------------------------
        // genexus_lifecycle build/rebuild -> rebuild-all
        // ------------------------------------------------------------------

        private async Task<JObject?> DispatchEngineLifecycleAsync(EngineRouteContext ctx, string action)
        {
            JObject args = ctx.Args ?? new JObject();
            string? unsupportedArgument = LifecycleSpace.RefusalFor(args);
            if (unsupportedArgument != null) return EngineOutsideResult(ctx, unsupportedArgument);
            if (!EngineKbArgMatches(ctx, args))
                return EngineOutsideResult(ctx, $"`kb` selects a different Knowledge Base than the session's ('{ctx.KbAlias ?? ctx.KbPath}')");
            string? target = EngineArgString(args, "target")?.Trim();
            bool targetClosure = !string.IsNullOrWhiteSpace(target);
            if (targetClosure && !IsEngineTargetClosure(args, action, target!))
                return EngineOutsideResult(ctx, "directed engine builds require action=build, mode=compile_check, callers=false and transitive callees, using an object name (not a GUID, typed selector or path)");
            string? mode = EngineArgString(args, "mode");
            if (!targetClosure && !string.IsNullOrWhiteSpace(mode))
                return EngineOutsideResult(ctx, $"mode={mode} without an admitted target is outside the engine lifecycle space");
            if (!targetClosure && (EngineArgPresent(args["includeCallees"]) || EngineArgPresent(args["callers"])))
                return EngineOutsideResult(ctx, "`includeCallees` and `callers` are outside the engine lifecycle space yet without an admitted `target`");
            if (args["skipFullDeploy"]?.ToObject<bool?>() == false)
                return EngineOutsideResult(ctx, "full deploy (`skipFullDeploy=false`) is outside the engine lifecycle space yet; the engine specifies and emits, it does not deploy");

            // The engine runs the build to completion inside the call, so it only
            // serves calls that asked for that contract: wait_until_done=true
            // (the blocking lifecycle contract) or an estimated_seconds the
            // gateway itself would have run synchronously. A bare build/rebuild
            // would otherwise change an accept-then-poll answer into a blocking
            // one, so it stays on the SDK path (or is refused under Fallback=error).
            bool waitUntilDone = args["wait_until_done"]?.ToObject<bool?>() == true;
            int? callerEstimate = args["estimated_seconds"]?.ToObject<int?>();
            int syncThresholdSeconds = _activeConfig()?.Server?.BuildSyncThresholdSeconds ?? 20;
            bool syncIntent = waitUntilDone
                || (callerEstimate.HasValue && BuildPathSelector.UseSync(callerEstimate.Value, syncThresholdSeconds));
            if (!syncIntent)
                return EngineOutsideResult(ctx, "this call would run as an accepted async job; engine builds run to completion inside the call, so pass wait_until_done=true (or an estimated_seconds below the gateway's sync threshold) to request that contract");

            // A target build must not pick up whole-KB or another target's old sources.
            string outputDir = targetClosure
                ? Path.Combine(ctx.KbPath, ".gx-engine", "build-targets",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target!))).ToLowerInvariant())
                : Path.Combine(ctx.KbPath, ".gx-engine", "build");
            var engineArgs = new List<string>
            {
                "--diagnostics=json", "rebuild-all",
                "--kb", ctx.KbPath,
                "--output", outputDir,
            };
            if (targetClosure)
            {
                engineArgs.AddRange(new[] { "--object", target!, "--closure" });
            }
            if (string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase))
                engineArgs.Add("--clean");

            try
            {
                await EngineBuildGate.WaitAsync(ctx.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
            {
                throw new PreDispatchCancelledException(ctx.CancellationToken);
            }
            try
            {
                if (ctx.CancellationToken.IsCancellationRequested)
                    throw new PreDispatchCancelledException(ctx.CancellationToken);
                GxEngineInvocation invocation = await GxEngineClient.InvokeAsync(ctx.Settings, engineArgs, ctx.CancellationToken).ConfigureAwait(false);
                ctx.BuildInvocationCompleted = true;
                ctx.CancellationToken.ThrowIfCancellationRequested();
                bool haveReport = TryParseEngineJson(invocation.StandardOutput, out JObject? report, out _)
                    && IsRecognizedEngineBuildReport(report!);
                int errorCount = (report?["errors"] as JArray)?.Count ?? 0;
                bool partial = haveReport && IsPartialEngineReport(report!, invocation.ExitCode);
                bool failed = errorCount > 0
                    || string.Equals(report?["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase);
                bool inconsistentReport = haveReport && !invocation.Ok && !partial && !failed;
                if (inconsistentReport) haveReport = false;

                if (invocation.Ok && haveReport && !partial && !failed)
                {
                    var payload = new JObject
                    {
                        ["status"] = "Success",
                        ["action"] = action,
                        ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                        ["report"] = EngineBuildReportPayload(report!),
                    };
                    _log($"[Engine] genexus_lifecycle.{action} served by gx-engine");
                    return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
                }

                if (partial)
                {
                    // Exit 3: the engine built what it could, but skipped or refused some
                    // objects (web UI, no generator). A partial build is not a success, so it
                    // is reported with isError: true: a client that checks only isError must not
                    // read it as done. status "Partial" and the per-object counts say what was
                    // left out; the message does not call it a failure.
                    var partialPayload = new JObject
                    {
                        ["status"] = "Partial",
                        ["action"] = action,
                        ["error"] = "EngineBuildPartial",
                        ["message"] = EnginePartialMessage(report!),
                        ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                        ["report"] = EngineBuildReportPayload(report!),
                    };
                    _log($"[Engine] genexus_lifecycle.{action} engine build partial");
                    return _buildToolResult(partialPayload, true, ctx.ToolName, ctx.Args);
                }

                if (haveReport)
                {
                    // The engine ran the whole pipeline and its report carries the
                    // build's errors: that is the build's answer, not an engine
                    // outage, so it is surfaced regardless of Engine.Fallback.
                    var failedPayload = new JObject
                    {
                        ["status"] = "Failed",
                        ["action"] = action,
                        ["error"] = "EngineBuildFailed",
                        ["message"] = $"the engine build finished with {errorCount} error(s).",
                        ["engine"] = EngineInfoBlock(ctx, invocation.Executable),
                        ["report"] = EngineBuildReportPayload(report!),
                    };
                    string? stderr = EngineExcerpt(invocation.StandardError);
                    if (stderr != null) failedPayload["stderr"] = stderr;
                    _log($"[Engine] genexus_lifecycle.{action} engine build failed with {errorCount} error(s)");
                    return _buildToolResult(failedPayload, true, ctx.ToolName, ctx.Args);
                }

                // Only a proven pre-start failure is safe to replay on the SDK.
                // A running process can have built files even without a JSON report.
                bool neverStarted = invocation.ErrorCode == "EngineNotFound"
                    || invocation.ErrorCode == "EngineStartFailed";
                if (neverStarted && !ctx.Settings.RejectUnsupported) return null;
                string outageCode = invocation.Ok || inconsistentReport
                    ? "EngineOutputUnrecognized"
                    : (string.IsNullOrWhiteSpace(invocation.ErrorCode) ? EngineInvocationFailedCode : invocation.ErrorCode!);
                string outageMessage = inconsistentReport
                    ? "rebuild-all reported success but did not exit successfully; its outcome is uncertain."
                    : invocation.Ok ? "rebuild-all produced no recognized JSON report on stdout."
                    : invocation.ErrorMessage ?? "gx-engine rebuild failed.";
                var outage = EngineErrorPayload(
                    outageCode, outageMessage,
                    ctx.Settings, ctx.CapabilityId, ctx.OperationKey, invocation);
                outage["action"] = action;
                if (!neverStarted) RequireEngineBuildReconciliation(outage);
                return _buildToolResult(outage, true, ctx.ToolName, ctx.Args);
            }
            finally
            {
                EngineBuildGate.Release();
            }
        }

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------

        /// <summary>One resolved engine route for one tool call.</summary>
        internal sealed class EngineRouteContext
        {
            internal EngineRouteContext(
                EngineBackendSettings settings, string capabilityId, string operationKey,
                string toolName, JObject? args, string kbPath, string? kbAlias,
                CancellationToken cancellationToken)
            {
                Settings = settings;
                CapabilityId = capabilityId;
                OperationKey = operationKey;
                ToolName = toolName;
                Args = args;
                KbPath = kbPath;
                KbAlias = kbAlias;
                CancellationToken = cancellationToken;
            }

            internal bool BuildInvocationCompleted { get; set; }
            internal EngineBackendSettings Settings { get; }
            internal string CapabilityId { get; }
            internal string OperationKey { get; }
            internal string ToolName { get; }
            internal JObject? Args { get; }
            internal string KbPath { get; }
            internal string? KbAlias { get; }
            internal CancellationToken CancellationToken { get; }
        }

        private static bool IsEngineTargetClosure(JObject args, string action, string target)
        {
            string? callees = EngineArgString(args, "includeCallees");
            return string.Equals(action, "build", StringComparison.OrdinalIgnoreCase)
                && string.Equals(EngineArgString(args, "mode"), "compile_check", StringComparison.OrdinalIgnoreCase)
                && args["callers"]?.ToObject<bool?>() == false
                && (string.IsNullOrWhiteSpace(callees) || string.Equals(callees, "transitive", StringComparison.OrdinalIgnoreCase))
                && System.Text.RegularExpressions.Regex.IsMatch(target, @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z");
        }

        private static JObject EngineInfoBlock(EngineRouteContext ctx, string? executable)
        {
            var info = new JObject
            {
                ["backend"] = "gxengine",
                ["capability"] = ctx.CapabilityId,
                ["operation"] = ctx.OperationKey,
                ["fallback"] = ctx.Settings.Fallback,
            };
            if (!string.IsNullOrWhiteSpace(executable)) info["executable"] = executable;
            if (ctx.OperationKey.StartsWith("genexus_lifecycle.", StringComparison.Ordinal))
            {
                string? target = EngineArgString(ctx.Args ?? new JObject(), "target")?.Trim();
                info["scope"] = string.IsNullOrEmpty(target) ? "whole-kb" : "target-closure";
                if (!string.IsNullOrEmpty(target)) info["target"] = target;
            }
            return info;
        }

        private static JObject EngineErrorPayload(
            string code, string message, EngineBackendSettings settings,
            string capabilityId, string operationKey, GxEngineInvocation? invocation)
        {
            var info = new JObject
            {
                ["backend"] = "gxengine",
                ["capability"] = capabilityId,
                ["operation"] = operationKey,
                ["fallback"] = settings.Fallback,
            };
            if (invocation != null)
            {
                if (!string.IsNullOrWhiteSpace(invocation.Executable)) info["executable"] = invocation.Executable;
                if (invocation.ExitCode.HasValue) info["exitCode"] = invocation.ExitCode.Value;
                if (invocation.TimedOut) info["timedOut"] = true;
            }
            var payload = new JObject
            {
                ["error"] = code,
                ["message"] = message,
                ["engine"] = info,
            };
            if (invocation != null)
            {
                string? stderr = EngineExcerpt(invocation.StandardError);
                string? stdout = EngineExcerpt(invocation.StandardOutput);
                if (stderr != null) payload["stderr"] = stderr;
                if (stdout != null) payload["stdout"] = stdout;
            }
            if (settings.RejectUnsupported)
                payload["hint"] = "Engine.Fallback=error refuses the SDK path; fix the engine invocation, or set Engine.Fallback=sdk to serve this call through the SDK.";
            return payload;
        }

        /// <summary>
        /// Outcome for a call the engine path does not serve yet: Fallback=sdk
        /// continues on the SDK path unchanged (null), Fallback=error refuses.
        /// </summary>
        private JObject? EngineOutsideResult(EngineRouteContext ctx, string reason)
        {
            if (!ctx.Settings.RejectUnsupported) return null;
            string message = $"The engine path does not serve this call yet: {reason}. Engine.Fallback=error refuses the SDK path.";
            _log($"[Engine] {ctx.OperationKey} refused (outside admitted space): {reason}");
            return _buildToolResult(
                EngineErrorPayload(EngineOutsideSpaceCode, message, ctx.Settings, ctx.CapabilityId, ctx.OperationKey, null),
                true, ctx.ToolName, ctx.Args);
        }

        /// <summary>
        /// Outcome for a failed engine invocation: Fallback=sdk continues on the
        /// SDK path unchanged (null), Fallback=error refuses with the invocation
        /// diagnostics attached.
        /// </summary>
        private JObject? EngineInvocationFailureResult(EngineRouteContext ctx, GxEngineInvocation invocation)
        {
            // Cancellation is not an engine outage: never replay the request on the SDK.
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!ctx.Settings.RejectUnsupported) return null;
            string code = string.IsNullOrWhiteSpace(invocation.ErrorCode) ? EngineInvocationFailedCode : invocation.ErrorCode!;
            _log($"[Engine] {ctx.OperationKey} invocation failed ({code}): {invocation.ErrorMessage}");
            return _buildToolResult(
                EngineErrorPayload(code, invocation.ErrorMessage ?? "the engine invocation failed.", ctx.Settings, ctx.CapabilityId, ctx.OperationKey, invocation),
                true, ctx.ToolName, ctx.Args);
        }

        /// <summary>
        /// Parses the operation JSON printed on stdout, tolerating leading noise
        /// before the first object (the rebuild report is emitted through the
        /// same stream).
        /// </summary>
        internal static bool TryParseEngineJson(string stdout, out JObject? json, out string? error)
        {
            json = null;
            error = null;
            if (string.IsNullOrWhiteSpace(stdout))
            {
                error = "stdout was empty";
                return false;
            }
            string trimmed = stdout.Trim();
            int firstBrace = trimmed.IndexOf('{');
            if (firstBrace < 0)
            {
                error = "no JSON object found on stdout";
                return false;
            }
            try
            {
                json = JObject.Parse(firstBrace == 0 ? trimmed : trimmed.Substring(firstBrace));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Compact projection of an invocation stream for a payload field.</summary>
        private static string? EngineExcerpt(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string value = text.Trim();
            const int cap = 800;
            return value.Length <= cap ? value : value.Substring(0, cap) + "…";
        }

        private static JObject EngineBuildReportPayload(JObject report)
        {
            var payload = new JObject();
            foreach (string key in new[]
            {
                "total_ms", "catalog_load_ms", "read_ms", "spec_ms", "emit_ms", "compile_ms",
                "total_objects", "procedures_count", "transactions_count", "sdts_count", "webpanels_count",
            })
            {
                if (report[key] != null) payload[key] = report[key];
            }
            if (report["emitted_files"] is JArray files) payload["emitted_file_count"] = files.Count;
            foreach (string key in new[] { "errors", "warnings" })
            {
                if (report[key] is JArray list && list.Count > 0) payload[key] = list;
            }
            // Status and the per-object counts: a partial run is only honest if the caller
            // can see what was skipped. Copied through as the engine reports them.
            foreach (string key in new[]
            {
                "status", "generated_count", "compiled_count", "skipped_count",
                "not_generated_count", "refused_count",
            })
            {
                if (report[key] != null) payload[key] = report[key];
            }
            foreach (string key in new[] { "skipped_objects", "not_generated_objects", "refused_objects" })
            {
                if (report[key] is JArray list && list.Count > 0) payload[key] = list;
            }
            return payload;
        }

        private static void RequireEngineBuildReconciliation(JObject payload)
        {
            payload["reconciliationRequired"] = true;
            payload["mutationOutcomeUnknown"] = true;
            payload["hint"] = "Inspect the operation and generated outputs before retrying; SDK fallback cannot safely replay this build.";
        }

        private static bool IsRecognizedEngineBuildReport(JObject report)
        {
            if (report["errors"] != null && !(report["errors"] is JArray)) return false;
            JToken? status = report["status"];
            if (status != null)
            {
                if (status.Type != JTokenType.String) return false;
                string value = status.ToString();
                if (value.Equals("complete", StringComparison.OrdinalIgnoreCase))
                    return report["errors"] is JArray;
                return value.Equals("partial", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("failed", StringComparison.OrdinalIgnoreCase);
            }
            // Older reports have no status; require their pipeline fields rather
            // than treating an arbitrary JSON object as a completed build.
            return report["errors"] is JArray
                && report["emitted_files"] is JArray
                && (report["total_ms"]?.Type == JTokenType.Integer
                    || report["total_ms"]?.Type == JTokenType.Float);
        }

        /// <summary>
        /// A rebuild-all run that exited 3 and reported no errors: partial by the engine's own
        /// contract. The report's status is the authority when present; the exit code is the
        /// fallback for an older engine that does not print it.
        /// </summary>
        private static bool IsPartialEngineReport(JObject report, int? exitCode)
        {
            if (report["errors"] is JArray errors && errors.Count > 0) return false;
            if (report["status"] is JValue status && status.Type == JTokenType.String)
                return string.Equals(status.ToString(), "partial", StringComparison.OrdinalIgnoreCase);
            return exitCode == 3;
        }

        private static string EnginePartialMessage(JObject report)
        {
            int skipped = report["skipped_count"]?.Value<int?>() ?? 0;
            int notGenerated = report["not_generated_count"]?.Value<int?>() ?? 0;
            int refused = report["refused_count"]?.Value<int?>() ?? 0;
            return $"the engine build is partial: {skipped} web UI object(s) skipped, " +
                   $"{notGenerated} without a generator, {refused} refused; nothing was reported as built for them.";
        }

        private static string? EngineReadRequestedParts(JObject args, out List<string> parts)
        {
            parts = new List<string>();
            JToken? partToken = args["part"];
            if (partToken != null && partToken.Type != JTokenType.Null)
            {
                if (partToken.Type != JTokenType.String) return "`part` must be a string part name";
                string value = partToken.ToString().Trim();
                if (value.Length > 0) parts.Add(value);
            }
            JToken? partsToken = args["parts"];
            if (partsToken != null && partsToken.Type != JTokenType.Null)
            {
                if (!(partsToken is JArray array)) return "`parts` must be an array of part names";
                foreach (JToken entry in array)
                {
                    if (entry.Type != JTokenType.String) return "`parts` entries must be strings";
                    string value = entry.ToString().Trim();
                    if (value.Length > 0) parts.Add(value);
                }
            }
            return null;
        }

        private static bool EngineKbArgMatches(EngineRouteContext ctx, JObject args)
        {
            string? kbArg = EngineArgString(args, "kb");
            if (string.IsNullOrWhiteSpace(kbArg)) return true;
            string candidate = kbArg!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var identifiers = new List<string?>
            {
                ctx.KbAlias,
                ctx.KbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFileName(ctx.KbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            };
            return identifiers.Any(identifier =>
                !string.IsNullOrWhiteSpace(identifier)
                && string.Equals(identifier!.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Maps a stored part name onto the SDK-facing spelling.</summary>
        internal static string EngineFriendlyPartName(string? storageName)
        {
            string name = (storageName ?? "").Trim();
            switch (name.ToUpperInvariant())
            {
                case "SOURCE": return "Source";
                case "RULES": return "Rules";
                case "EVENTS": return "Events";
                case "CONDITIONS": return "Conditions";
                case "VARIABLES": return "Variables";
                case "STRUCTURE": return "Structure";
                case "WEBFORM": return "WebForm";
                case "PATTERNINSTANCE": return "PatternInstance";
                default: return name;
            }
        }

        /// <summary>
        /// Compares requested and stored type names, treating the SDK's
        /// StructuredDataType spelling and the engine's SDT as the same kind.
        /// </summary>
        internal static bool EngineTypeNamesMatch(string requested, string stored)
        {
            return string.Equals(NormalizeEngineTypeName(requested), NormalizeEngineTypeName(stored), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEngineTypeName(string? value)
        {
            string name = (value ?? "").Trim();
            if (string.Equals(name, "StructuredDataType", StringComparison.OrdinalIgnoreCase)) return "SDT";
            return name;
        }

        private static int CountTextLines(string text)
        {
            if (text.Length == 0) return 0;
            int lines = 1;
            foreach (char ch in text)
            {
                if (ch == '\n') lines++;
            }
            return lines;
        }

        internal static bool EngineArgPresent(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.String) return !string.IsNullOrWhiteSpace(token.ToString());
            if (token is JArray array) return array.Count > 0;
            if (token is JObject obj) return obj.Count > 0;
            return true;
        }

        internal static string? EngineArgString(JObject? args, string name)
        {
            JToken? token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.ToString();
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }
    }
}
