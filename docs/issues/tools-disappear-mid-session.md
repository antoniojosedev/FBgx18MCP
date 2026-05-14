# MCP tools disappear from caller's schema mid-session (Connected but unreachable)

**Status:** Resolved in v2.2.0 via gateway-side `ResponseSizeGuard` (caps per-tool payloads at ~220KB before the harness truncation path runs) and lifecycle pagination (`status`/`result` accept `page`/`page_size`, default 50). The schema-invalidation root cause hypothesis (response > 80k tokens dropping tool registrations) remains unconfirmed pending one-release telemetry from the `[Gateway] OVERSIZE tool=X size=N` log line. See `docs/superpowers/specs/2026-05-13-mcp-perf-and-tool-stability-design.md` section (A).

**Original status:** Open — observed 2026-05-13 in a Claude Code session connected via stdio (`publish/start_mcp.bat`).

## Symptom

Mid-conversation, every `mcp__genexus18__*` tool stops being callable. Direct invocation returns:

```
Error: No such tool available: mcp__genexus18__genexus_read
```

`ToolSearch` for any of the names — exact (`select:mcp__genexus18__genexus_edit`), prefix (`mcp__genexus18__`), or keyword (`+genexus_edit`) — returns `No matching deferred tools found`. Meanwhile the harness still reports the server as healthy:

```
$ claude mcp list | grep genex
genexus18: C:\Projetos\Genexus18MCP\publish\start_mcp.bat  - ✓ Connected
```

Worker side likely fine too — a fresh session connects without intervention, and `/mcp` (user-triggered) immediately surfaces the full tool list again. The disconnect is between the harness and the caller's tool schema, not the server.

## Reproduction signal in the session that caught it

This session opened with the deferred tools listed via `<system-reminder>`. After ~30 turns of normal `genexus_read` / `genexus_edit` / `genexus_lifecycle` use (including a long-running build that returned a status payload too large for the 80k token budget and was reported as `isTruncated: true`), the next ToolSearch lookup for the same names came back empty. Server log showed no disconnect; harness `claude mcp list` still ✓ Connected.

Suspected triggers (any/all):

- A tool response that exceeded the 80k token budget (`status` for a batch build with many warnings). The truncation path may be evicting the tool's schema from the caller-side registry along with the oversized payload.
- Long session length / context compaction pruning the original tool-availability `<system-reminder>` without re-emitting it.
- Background-process notifications interleaving with deferred-tool registration.

## Workaround

User runs `/mcp` to force reconnect → `<system-reminder>` re-emits the deferred-tool list → tools callable again. No worker restart needed.

## Why this hurts

Loses the working tool set silently — model has to discover the gap by attempting a call and getting `No such tool available`, then ask the user to reconnect. Multi-step tasks that depend on `genexus_*` (everything that touches the KB) stall until the user notices the request.

## What to investigate

1. Does the truncation path (response > 80k tokens) drop tool registrations as a side effect? If yes, the fix is either capping per-tool response size at the gateway, paginating large `lifecycle/status` and `lifecycle/result` payloads by default, or making the truncation purely a payload concern that doesn't touch the schema registry.
2. Confirm whether the gateway logs anything around the moment the caller stops being able to discover tools — at the symptom moment in this session the relevant `worker_debug.log` lines were only routine `Build Action=Status` traces, no disconnect.
3. Check `McpRouter.cs` `tools/list_changed` broadcasting — if we emit it spuriously or at the wrong moment, the harness may invalidate the cached schema.
4. Consider emitting a sentinel response when a payload gets truncated (e.g. `"_meta": { "truncated": true, "follow_up_tool": "..." }`) that points the caller at a paginated alternative before they hit the failure.

## Related

- `src/GxMcp.Gateway/McpRouter.cs:162` — `tools/list` handler.
- `src/GxMcp.Gateway/Program.cs:169` — `notifications/tools/list_changed` broadcast.
- Build status payload size: `genexus_lifecycle action=status` for a batch build with 30+ warnings exceeds 80k tokens; `genexus_lifecycle action=result` same. Pagination already exists for `genexus_query`/`genexus_read` but not for `lifecycle` outputs.
