using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway;

internal sealed partial class EngineRoutedOperations
{
    private static readonly EngineAdmittedSpace EnvironmentSpace = new("environment", "action", "kb");
    private static readonly EngineAdmittedSpace ProcedureListSpace = new("list_objects", "typeFilter", "limit", "offset", "kb");
    private static readonly EngineAdmittedSpace VersionListSpace = new("kb_version", "action", "kb");
    private static readonly EngineAdmittedSpace InspectSpace = new("inspect", "name", "type", "projection", "kb");

    private async Task<JObject?> DispatchEngineEnvironmentAsync(EngineRouteContext ctx)
    {
        string? refusal = RefusalForArguments(ctx, EnvironmentSpace);
        if (refusal != null) return EngineOutsideResult(ctx, refusal);
        if (!string.Equals(EngineArgString(ctx.Args ?? new JObject(), "action"), "get_environment", StringComparison.OrdinalIgnoreCase))
            return EngineOutsideResult(ctx, "only action=get_environment is admitted for native environment reads");

        var arguments = new List<string> { "--diagnostics=json", "kb-environment", ctx.KbPath };
        return await ServeActiveScopeReadAsync(ctx, arguments, "kb-environment", (json, _) =>
        {
            if (json["environment"]?.Type != JTokenType.String || json.Count != 1)
                return (null, "kb-environment did not return exactly one string environment field");
            string name = json["environment"]!.Value<string>()!;
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                return (null, "kb-environment returned an empty or malformed environment name");
            return (json, null);
        }, _ => "genexus_kb.get_environment served by gx-engine").ConfigureAwait(false);
    }

    private async Task<JObject?> DispatchEngineVersionListAsync(EngineRouteContext ctx)
    {
        string? refusal = RefusalForArguments(ctx, VersionListSpace);
        if (refusal != null) return EngineOutsideResult(ctx, refusal);
        if (!string.Equals(EngineArgString(ctx.Args ?? new JObject(), "action"), "list", StringComparison.OrdinalIgnoreCase))
            return EngineOutsideResult(ctx, "only action=list is admitted for native KB version reads");

        return await ServeActiveScopeReadAsync(
            ctx, EngineKbVersionListContract.ToEngineArguments(ctx.KbPath), "kb-versions", (json, active) =>
                EngineKbVersionListContract.TryShape(json, active.KbVersionId, out JObject? payload, out string? shapeRefusal)
                    ? (payload, null)
                    : (null, shapeRefusal),
            payload => $"genexus_kb_version.list served by gx-engine: {payload["result"]?["versions"]?.Count() ?? 0} versions").ConfigureAwait(false);
    }

    private async Task<JObject?> DispatchEngineProcedureListAsync(EngineRouteContext ctx)
    {
        JObject args = ctx.Args ?? new JObject();
        string? refusal = RefusalForArguments(ctx, ProcedureListSpace)
            ?? EngineFieldSelection.TryRead(args, "list_objects", EngineProcedureListContract.SuppliedFields, out _);
        if (refusal != null) return EngineOutsideResult(ctx, refusal);
        refusal = EngineProcedureListContract.TryPlan(args, out EngineListPageWindow window);
        if (refusal != null) return EngineOutsideResult(ctx, refusal);

        return await ServeActiveScopeReadAsync(
            ctx, EngineProcedureListContract.ToEngineArguments(ctx.KbPath, window), "kb-list-objects", (json, _) =>
                EngineProcedureListContract.TryShapeListResponse(json, window, DateTime.UtcNow, out JObject? payload, out string? shapeRefusal)
                    ? (payload, null)
                    : (null, shapeRefusal),
            payload => $"genexus_list_objects served by gx-engine: {window.ExpectedType} offset={window.Offset} count={payload["count"]}/{payload["total"]}").ConfigureAwait(false);
    }

    private async Task<JObject?> DispatchEngineInspectAsync(EngineRouteContext ctx)
    {
        JObject arguments = ctx.Args ?? new JObject();
        string? spaceRefusal = InspectSpace.RefusalFor(arguments);
        if (spaceRefusal != null) return EngineOutsideResult(ctx, spaceRefusal);
        if (!EngineKbArgMatches(ctx, arguments))
            return EngineOutsideResult(ctx, $"`kb` selects a different Knowledge Base than the session's ('{ctx.KbAlias ?? ctx.KbPath}')");

        string? planError = EngineProcedureInspectContract.TryPlan(arguments, ctx.KbPath, out string name, out InspectTypeInfo typeInfo);
        if (planError != null) return EngineOutsideResult(ctx, planError);

        (JObject? output, string? err) ShapeResult(JObject raw) =>
            EngineProcedureInspectContract.TryShapeInspectResponse(raw, name, typeInfo, out JObject? p, out string? r)
                ? (p, null)
                : (null, r);

        var cliTokens = EngineProcedureInspectContract.ToEngineArguments(ctx.KbPath, name, typeInfo.CanonicalType);
        return await ServeActiveScopeReadAsync(
            ctx, cliTokens, "kb-inspect", (raw, _) => ShapeResult(raw),
            _ => $"genexus_inspect served by gx-engine: {typeInfo.CanonicalType} {name} (minimal)").ConfigureAwait(false);
    }

    /// <summary>
    /// Serves one active-scope read: <paramref name="shape"/> turns the engine JSON into the SDK-shaped
    /// payload (or a refusal that keeps the configured fallback), then provenance is attached.
    /// </summary>
    private async Task<JObject?> ServeActiveScopeReadAsync(
        EngineRouteContext ctx,
        List<string> arguments,
        string command,
        Func<JObject, EngineActiveModel, (JObject? Payload, string? Refusal)> shape,
        Func<JObject, string> served)
    {
        var (json, active, executable, failure) = await InvokeActiveScopeEngineJsonAsync(ctx, arguments, command).ConfigureAwait(false);
        if (json == null || active == null) return failure;
        var (payload, refusal) = shape(json, active);
        if (payload == null) return EngineOutsideResult(ctx, refusal ?? $"{command} could not be shaped");
        payload["engine"] = EngineInfoBlock(ctx, executable);
        _log("[Engine] " + served(payload));
        return _buildToolResult(payload, false, ctx.ToolName, ctx.Args);
    }

    /// <summary>
    /// Runs a read pinned to the Worker's active version and accepts it only if that version is
    /// still active afterwards. A null JSON returns <c>Failure</c> (null continues on the SDK).
    /// </summary>
    private async Task<(JObject? Json, EngineActiveModel? Active, string? Executable, JObject? Failure)> InvokeActiveScopeEngineJsonAsync(
        EngineRouteContext ctx, List<string> arguments, string command)
    {
        EngineActiveModel? before = _activeModel == null ? null : await _activeModel(ctx.KbPath, ctx.CancellationToken).ConfigureAwait(false);
        if (before == null || !before.Describes(ctx.KbPath) || before.DesignModelId <= 0 || before.KbVersionId < 0)
            return (null, null, null, EngineOutsideResult(ctx, $"a ready Worker active-version snapshot is required for native {command}"));
        var (json, invocation, failure) = await InvokeEngineJsonAsync(ctx, arguments, command, before.EngineArguments()).ConfigureAwait(false);
        if (json == null) return (null, null, invocation.Executable, failure);

        EngineActiveModel? after = _activeModel == null ? null : await _activeModel(ctx.KbPath, ctx.CancellationToken).ConfigureAwait(false);
        if (after == null || !after.Describes(ctx.KbPath) || before.DesignModelId != after.DesignModelId || before.KbVersionId != after.KbVersionId)
            return (null, null, invocation.Executable, EngineOutsideResult(ctx, $"the active version changed or became unknown during native {command}"));
        return (json, before, invocation.Executable, null);
    }
}
