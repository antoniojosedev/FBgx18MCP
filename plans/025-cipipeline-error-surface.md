# Plan 025: `CiPipelineService` surfaces real run/abort failures instead of "not connected"

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/CiPipelineService.cs`
> Mismatch vs the excerpt below = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`pipeline_run` and `pipeline_abort` are confirm-gated, mutating CI operations
(trigger / cancel a build). Today a single outer `try` wraps the whole action switch,
and its `catch` unconditionally returns `NotConnected(...)` — a **success** envelope
(`McpResponse.Ok`, `connected:false`). So if `RunPipeline`/`AbortRunPipeline` throws
for any reason other than "KB not GXserver-linked" (transient network error, auth
token expiring after the connection was established, a build that partially triggered
before the SDK threw), the caller sees a benign "not connected" success instead of an
error. An agent acting on that will retry `pipeline_run` and can **double-trigger a
build**. Real destructive failures must surface as errors.

## Current state

- `src/GxMcp.Worker/Services/CiPipelineService.cs` — `Run(string action, JObject
  args)`. The outer `try` (line 77) wraps the full switch including `pipeline_run`
  (`svc.RunPipeline(...)`, line 119) and `pipeline_abort` (`svc.AbortRunPipeline(...)`,
  line 128). The shared catch (lines 137–141) routes everything to `NotConnected`:

```csharp
            catch (Exception ex)
            {
                // A KB that isn't GXserver-linked (or an unauthenticated session) surfaces here.
                return NotConnected(ex.Message);
            }
```

```csharp
        private static string NotConnected(string detail)
            => McpResponse.Ok(code: "PipelineNotConnected", result: new JObject
            {
                ["connected"] = false,
                ["detail"] = detail,
                ["hint"] = "This KB is not linked to a GXserver, or the CI session needs credentials (GXMCP_TEAMDEV_USER/PASSWORD)."
            });
```

The genuine "not connected" case is already caught earlier and correctly, at the
`new TeamDevelopmentData(model)` construction (lines 70–74) — that's the real
connectivity probe. The read actions (`pipeline_list`/`pipeline_runs`/`pipeline_output`)
tolerate the `NotConnected` fallback; the mutating ones must not.

`McpResponse.Err(code, message, hint)` is the error envelope used throughout the file.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CiPipeline"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/CiPipelineService.cs` — the `pipeline_run` and
  `pipeline_abort` cases and/or the catch structure.
- `src/GxMcp.Worker.Tests/` — a test.

**Out of scope:**
- The read actions' behavior (`pipeline_list`/`pipeline_runs`/`pipeline_output`) —
  they keep surfacing `NotConnected` on failure. Do not change them.
- The connectivity probe at `new TeamDevelopmentData(model)` (lines 70–74) — keep it.
- The confirm gates — already correct, leave them.

## Git workflow

- Branch: `advisor/025-cipipeline-error-surface`
- One commit: `fix(gxserver): surface pipeline_run/abort failures as errors, not "not connected"`
- Do NOT push.

## Steps

### Step 1: Give `pipeline_run` / `pipeline_abort` their own error handling

Wrap the two mutating SDK calls so an exception from them returns
`McpResponse.Err`, not `NotConnected`. The cleanest shape: inside each of the
`pipeline_run` and `pipeline_abort` cases, wrap the `svc.RunPipeline(...)` /
`svc.AbortRunPipeline(...)` call in its own `try/catch (Exception ex)` returning:

```csharp
return McpResponse.Err(
    code: "PipelineRunFailed",     // or "PipelineAbortFailed"
    message: ex.Message,
    hint: "The build trigger/cancel call failed. Verify the project name and GXserver session, then retry.");
```

Leave the outer `try`/`NotConnected` catch in place for the read actions and the rest
of the switch. Do not swallow — the `Err` must carry `ex.Message`.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Test

Create `src/GxMcp.Worker.Tests/CiPipelineErrorSurfaceTests.cs`. If the tests can
inject a fake `IContinuousIntegrationService` whose `RunPipeline` throws, assert the
returned JSON has `error.code == "PipelineRunFailed"` (not `code == "PipelineNotConnected"`
/ `connected:false`). If the service is resolved internally via `SdkServiceResolver`
and cannot be faked without a live SDK, STOP and report — do NOT rearchitect the
service just to make it testable in this plan; instead note the limitation and rely on
the build + a review of the diff.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~CiPipeline"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] An exception thrown by `RunPipeline`/`AbortRunPipeline` yields an `Err` envelope (code `PipelineRunFailed`/`PipelineAbortFailed`), verified by test OR — if untestable without live SDK — by the reviewer reading the diff
- [ ] Read actions still return `NotConnected` on failure (unchanged)
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live code already differentiates run/abort exceptions (drift) — report.
- The service cannot be unit-tested without a live SDK session — report the limitation
  and stop at build-verified; do not refactor for testability here.

## Maintenance notes

- Reviewer: the key invariant is that a *mutating* pipeline call never returns a 200-
  shaped success when it actually failed. Confirm the read-action fallback is intact.
