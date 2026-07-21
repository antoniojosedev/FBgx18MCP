# Plan 015: Guard tests for the new SDK-endpoint services + fail-fast confirm gates

> **Executor instructions**: Follow step by step; run every verification command;
> honor STOP conditions. Update this plan's row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 9fe6817..HEAD -- src/GxMcp.Worker/Services/DeployService.cs src/GxMcp.Worker/Services/CiPipelineService.cs`
> On any change, diff the "Current state" excerpts against live code; mismatch = STOP.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: LOW
- **Depends on**: none (but do this BEFORE plan 016 — it is the safety net for that refactor)
- **Category**: tests
- **Planned at**: commit `9fe6817`, 2026-07-20

## Why this matters

Ten of the eleven services added in the v2.27–2.29 SDK-endpoint expansion have zero
test coverage — including the two destructive, `confirm`-gated actions in the batch
(`genexus_deploy action=deploy`, `genexus_gxserver action=pipeline_run/pipeline_abort`).
A future refactor to the shared action-dispatch shape could silently drop a confirm
gate and nothing in CI would catch it. This plan adds a lightweight guard-test net
over the reachable pure-logic branches (missing-arg, no-KB, missing-confirm,
unknown-action). To make the confirm gate *reachable in a unit test without a live
KB*, it also moves the confirm precondition ahead of KB/model resolution in the two
destructive services — a fail-fast, safer-direction change (a static precondition
should not depend on KB state).

## Current state

- `src/GxMcp.Worker/Services/DeployService.cs` — `Run(JObject args)`. Today the
  order is: resolve `model` → if null `NoKbOpen` → `if (action=="deploy")` →
  `if (!confirm) ConfirmRequired`. So with no KB open, `action=deploy` returns
  `NoKbOpen` before the confirm check is ever seen.

Excerpt (`DeployService.cs:33-46, 80-86`):

```csharp
public string Run(JObject args)
{
    string action = (args?["action"]?.ToString() ?? "list_targets").Trim().ToLowerInvariant();
    KBModel model;
    try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; } catch { model = null; }
    if (model == null)
        return McpResponse.Err(code: "NoKbOpen", message: "No open KB / design model available.", hint: "Open a KB first (genexus_kb action=open).");
    if (action == "list_targets") { ... }
    if (action == "deploy")
    {
        if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
            return McpResponse.Err(code: "ConfirmRequired", message: "action=deploy builds and ships the application; pass confirm=true.", hint: ...);
        ...
    }
    return McpResponse.Err(code: "BadAction", ...);
}
```

- `src/GxMcp.Worker/Services/CiPipelineService.cs` — `Run(string action, JObject args)`.
  Same shape: `model` null → `NoKbOpen`; then resolve `svc`; then `new
  TeamDevelopmentData(model)`; then a `switch(action)` whose `pipeline_run` /
  `pipeline_abort` cases check `confirm`. Confirm is unreachable without a live,
  GXserver-linked KB.

- These services are constructed with a `KbService` (`_kb`). Passing `_kb == null`
  makes `_kb?.GetKB()` return null → `model == null` → `NoKbOpen`. That is how the
  no-KB branch is unit-testable today.
- Test convention: xUnit (`using Xunit;`), one test class per service or a shared
  class; parse envelopes with `JObject.Parse(...)` and assert on `jo["status"]` /
  `jo["error"]?["code"]` (canonical envelope). **Verify the exact envelope shape
  first** by reading how an existing test asserts an error code — e.g.
  `grep -n "error\"\]\|\"code\"\|status" src/GxMcp.Worker.Tests/DbDriftServiceTests.cs`
  and one that asserts an *error* envelope. Use whatever accessor the existing
  tests use (`jo["error"]?["code"]` vs top-level `jo["code"]`) so your asserts match
  the real `McpResponse.Err` shape.

The ten services with no coverage: `TransferService`, `DeployService`,
`SecurityScanService`, `CiPipelineService`, `ReorgImpactService`, `KbStatsService`,
`TableRelationsService`, `CurlProcService`, `DesignSystemService`,
`UserControlsListService`. (`DbDriftService` already has `DbDriftServiceTests`.)

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` (GX_PATH set) | Build succeeded |
| Test | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~NewServicesGuard"` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/DeployService.cs` — reorder confirm check only (Step 1).
- `src/GxMcp.Worker/Services/CiPipelineService.cs` — reorder confirm check only (Step 1).
- `src/GxMcp.Worker.Tests/NewServicesGuardTests.cs` (create) — Steps 2–3.

**Out of scope**:
- The SDK-calling branches of any service (they need a live KB — do not try to
  cover them; that's by-design per the repo's headless-service pattern).
- Any change to `list_targets` / read-only actions' behavior.
- The other eight services' production code — only add tests for them; do NOT
  change their logic here (plan 016 handles their shared refactor).
- Envelope-shape or message-string changes beyond the reorder.

## Steps

### Step 1: Move the confirm gate ahead of model resolution (2 destructive services)

**DeployService.Run** — before resolving `model`, if `action == "deploy"` and
`confirm` is not true, return `ConfirmRequired`. Keep the existing confirm check too
(harmless) or remove the now-redundant inner one — either is fine as long as
behavior for the *happy path* is unchanged. Minimal diff:

```csharp
string action = (args?["action"]?.ToString() ?? "list_targets").Trim().ToLowerInvariant();

// Fail-fast: a static precondition must not depend on KB state.
if (action == "deploy" && !(args?["confirm"]?.ToObject<bool?>() ?? false))
    return McpResponse.Err(
        code: "ConfirmRequired",
        message: "action=deploy builds and ships the application; pass confirm=true.",
        hint: "Review the target with action=list_targets, then set confirm=true.");

KBModel model;
try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; } catch { model = null; }
if (model == null) return McpResponse.Err(code: "NoKbOpen", ...);   // unchanged
```

Leave the inner `if (!confirm) ConfirmRequired` inside the `action=="deploy"` block
as-is (defensive redundancy) OR delete it — your call; if you delete it, make sure
nothing else falls through.

**CiPipelineService.Run** — same idea for the two destructive actions. Before the
`model` resolution, add:

```csharp
action = (action ?? "").Trim().ToLowerInvariant();

if ((action == "pipeline_run" || action == "pipeline_abort")
    && !(args?["confirm"]?.ToObject<bool?>() ?? false))
    return McpResponse.Err("ConfirmRequired",
        action == "pipeline_run"
            ? "pipeline_run triggers a build; pass confirm=true."
            : "pipeline_abort cancels a running build; pass confirm=true.",
        "Set confirm=true.");

KBModel model;
...
```

Note CiPipeline's destructive actions also require `project` (`NeedProject`). Order:
confirm-first is fine even if project is missing — a caller fixing confirm then hits
NeedProject on the next call. Do NOT reorder `project` handling; only add the
confirm pre-check.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → Build succeeded.

### Step 2: Guard tests for the two destructive services

Create `src/GxMcp.Worker.Tests/NewServicesGuardTests.cs`. Construct each service with
`_kb = null` (e.g. `new DeployService(null)`). Cases:

- `DeployService`:
  - `Run(JObject.Parse("{\"action\":\"deploy\"}"))` → `ConfirmRequired` (proves the
    gate fires *without* a KB — this is the regression lock).
  - `Run(JObject.Parse("{\"action\":\"deploy\",\"confirm\":true}"))` → `NoKbOpen`
    (confirm satisfied, now KB is required).
  - `Run(JObject.Parse("{\"action\":\"bogus\"}"))` → `NoKbOpen` (unknown action
    still needs a KB in the current flow) — OR `BadAction` if your reorder makes it
    reachable; assert whichever your final control flow produces and note it.
  - `Run(JObject.Parse("{\"action\":\"list_targets\"}"))` → `NoKbOpen`.
- `CiPipelineService` (constructor is `new CiPipelineService(null)`, `Run(action, args)`):
  - `Run("pipeline_run", JObject.Parse("{\"project\":\"p\"}"))` → `ConfirmRequired`.
  - `Run("pipeline_abort", JObject.Parse("{\"project\":\"p\"}"))` → `ConfirmRequired`.
  - `Run("pipeline_list", new JObject())` → `NoKbOpen`.

### Step 3: `NoKbOpen` guard test for the remaining eight services

For `TransferService`, `SecurityScanService`, `ReorgImpactService`, `KbStatsService`,
`TableRelationsService`, `CurlProcService`, `DesignSystemService`,
`UserControlsListService`: construct each with `null` KbService and invoke its public
entry point with minimal/default args, asserting the envelope is a `NoKbOpen` error
(or the service's documented no-KB code — read each service's top-of-method guard to
get the exact code; most return `NoKbOpen`). One `[Fact]` (or `[Theory]`) per service.

- Find each entry point + its no-KB code:
  `grep -n "public string \|NoKbOpen\|code:" src/GxMcp.Worker/Services/<Service>.cs`.
- Some entry points take a `JObject args`, some take `(string action, JObject args)`,
  some take specific params — match the real signature. If a service needs a
  required arg *before* the KB check (e.g. `CurlProcService` may validate `curl`
  first), pass that arg so you reach the KB check, OR assert the arg-validation code
  instead and note it. The goal is one green guard assertion per service, not forcing
  a specific code.

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~NewServicesGuard"` → all pass.

## Test plan

Covered by Steps 2–3. Total: ~7 destructive-gate asserts + 8 no-KB asserts. Model
the file after `DbDriftServiceTests.cs` for structure/usings.

## Done criteria

- [ ] Build exits 0
- [ ] `DeployService`/`CiPipelineService` return `ConfirmRequired` for a destructive
      action with `_kb=null` and no `confirm` (asserted in tests)
- [ ] One guard test per each of the 10 services exists and passes
- [ ] Full worker suite green (allow documented flakies)
- [ ] `plans/README.md` row updated

## STOP conditions

- `DeployService.cs`/`CiPipelineService.cs` don't match the "Current state"
  excerpts (drift).
- A service's public entry-point signature can't be invoked without a live KB even
  for its guard branch (e.g. it dereferences the SDK before any guard) — skip that
  one service, note it in your report and in the `plans/README.md` row, do NOT
  refactor it here.
- The canonical error envelope accessor you asserted doesn't match real output
  (your first test fails on the assert path, not the code value) — fix the accessor
  by reading an existing passing error-envelope test; if still stuck after two
  tries, STOP and report the actual envelope JSON you observed.

## Maintenance notes

- These are characterization tests: they lock current guard behavior so plan 016's
  shared-helper extraction can't silently change it. Run this plan's tests green
  before and after 016.
- When an 11th destructive-action service is added, add its confirm-gate guard test
  here.
- Reviewer: confirm the confirm-gate reorder didn't change the happy path (a valid
  `deploy` with confirm + open KB still reaches `IDeploymentService.Deploy`).
