# Plan 024: `genexus_gam` define_api/deploy require `confirm=true`

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit in the worktree per Git workflow.
> SKIP updating `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/GamService.cs`
> On change, compare the excerpt below; mismatch = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`genexus_gam action=deploy` calls the GAM SDK's `Deploy`, which can **create or alter
security tables in the KB's configured datastore**; `action=define_api` calls
`DefineAPI`. Both are destructive. Every sibling destructive SDK action in the same
release batch — `DeployService`, `TransferService`, `CiPipelineService` — refuses to
run without an explicit `confirm=true`. `GamService` does not: it executes on the
first call, no preview, no confirmation. The class doc-comment (lines 23–26) and the
dispatcher comment both *assert* a guard exists, so this is also a code-vs-comment
contradiction that misleads reviewers. Add the same fail-fast `confirm` gate.

## Current state

- `src/GxMcp.Worker/Services/GamService.cs` — `Run(JObject args)` (lines 43–101)
  validates the action and resolves the service/KB, then dispatches:

```csharp
if (action == "status") return StatusEnvelope(svc, model);
if (action == "define_api") return DefineApi(svc, model, args);
return Deploy(svc, model, args);
```

There is **no** `confirm` check anywhere on the `define_api`/`deploy` path.
`action=status` is read-only and must stay unguarded.

Reference — the pattern to mirror, from `CiPipelineService.cs:51-57` (already in the
repo):

```csharp
if ((action == "pipeline_run" || action == "pipeline_abort")
    && !(args?["confirm"]?.ToObject<bool?>() ?? false))
    return McpResponse.Err("ConfirmRequired",
        "pipeline_run triggers a build; pass confirm=true.",
        "Set confirm=true.");
```

`McpResponse.Err(code, message, hint, ...)` and `McpResponse.NextStep(...)` are used
throughout `GamService`; match the existing envelope style in that file.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Gam"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/GamService.cs` — add the confirm gate; fix the
  doc-comment so it describes the real guard.
- `src/GxMcp.Worker.Tests/` — a test file for the gate.

**Out of scope:**
- `StatusEnvelope` and `action=status` — must remain callable with no `confirm`.
- The `DefineApi`/`Deploy` method bodies (the SDK calls themselves) — unchanged.
- `CommandDispatcher.cs` — no routing change needed.

## Git workflow

- Branch: `advisor/024-gam-confirm-gate`
- One commit: `fix(gam): require confirm=true for define_api/deploy`
- Do NOT push.

## Steps

### Step 1: Add the fail-fast confirm gate

In `GamService.Run`, after the action-validation block (the `if (action != "status"
&& action != "define_api" && action != "deploy")` return) and BEFORE the service/KB
resolution, add:

```csharp
if ((action == "define_api" || action == "deploy")
    && !(args?["confirm"]?.ToObject<bool?>() ?? false))
    return McpResponse.Err(
        code: "ConfirmRequired",
        message: action == "define_api"
            ? "define_api calls the GAM Define API and can alter security metadata; pass confirm=true."
            : "deploy can create or alter GAM security tables in the datastore; pass confirm=true.",
        hint: "Re-issue the call with confirm=true once you intend the change.");
```

Placing it before service/KB resolution makes it a static precondition (mirrors
`CiPipelineService`'s "a static precondition must not depend on KB state" comment).

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Fix the doc-comment

The class comment (lines 23–26) says the destructive actions are guarded only by
requiring the explicit action name. Update it to state that they also require
`confirm=true`. Keep it to one or two sentences.

### Step 3: Test

Create `src/GxMcp.Worker.Tests/GamConfirmGateTests.cs`. `GamService`'s ctor takes a
`KbService`. Assert, without needing an open KB (the gate returns before KB
resolution):
- `Run({action:"deploy"})` → parsed JSON has `error.code == "ConfirmRequired"`.
- `Run({action:"define_api"})` → `ConfirmRequired`.
- `Run({action:"status"})` → does NOT return `ConfirmRequired` (it may return an
  unavailable/KB-not-open error — assert only that the code is not `ConfirmRequired`).

Model the test on any existing worker-service test that constructs a service with a
`KbService` and parses the returned JSON string (search the test project for
`new KbService` or `JObject.Parse`). If `GamService` cannot be constructed without a
live KB, STOP and report.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~Gam"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] `Run` returns `ConfirmRequired` for define_api/deploy without confirm and executes with `confirm=true` present
- [ ] `action=status` still reaches its normal path (no ConfirmRequired)
- [ ] New tests pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `GamService.Run` already has a confirm check (drift / already fixed) — report.
- `GamService` can't be constructed in a test without a live KB — report the existing
  test pattern instead of building an SDK harness.

## Maintenance notes

- If a fourth destructive SDK service appears, consider a shared `ConfirmGate` helper
  (deliberately not built here — three-plus-one is still cheap inline).
- Reviewer: verify `status` remains unguarded; a confirm gate on a read-only action
  would be a regression.
