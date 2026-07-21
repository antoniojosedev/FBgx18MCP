# Plan 021: Input-validation hardening for the v2.27–2.29 SDK-endpoint services

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat 4885c1c..HEAD -- src/GxMcp.Worker/Services/DeployService.cs src/GxMcp.Worker/Services/CiPipelineService.cs src/GxMcp.Worker/Services/TransferService.cs src/GxMcp.Worker/Services/UserControlsListService.cs src/GxMcp.Worker.Tests/NewServicesGuardTests.cs`
> If any of these changed since this plan was written, compare the "Current
> state" excerpts against the live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `4885c1c`, 2026-07-21

## Why this matters

Four small correctness papercuts in the recently-added SDK-endpoint services.
None is a crash, but each produces a misleading result that sends a caller (an AI
agent) down a wrong path:

1. **Misleading error ordering** (`DeployService`, `CiPipelineService`): a typo'd
   `action` combined with no-KB-open returns `NoKbOpen` ("open a KB first")
   instead of `BadAction` — the caller fixes the wrong thing, opens a KB, then
   still gets `BadAction`, wasting a round trip.
2. **Silent bad query** (`CiPipelineService` `pipeline_output`): a missing
   `buildId` silently queries build `0` instead of returning a clear `BadArgs`.
3. **Swallowed errors as "not found"** (`TransferService.Export`): any exception
   from `FindObject` is reported identically to a genuine "no such object", so a
   real SDK error becomes a debugging dead end.
4. **Off-by-one on `limit<=0`** (`UserControlsListService`): returns 1 control
   instead of 0.

## Current state

### 1a. `DeployService` — action validated after the KB guard

```csharp
// DeployService.cs:33-45
public string Run(JObject args)
{
    string action = (args?["action"]?.ToString() ?? "list_targets").Trim().ToLowerInvariant();

    // Fail-fast: a static precondition must not depend on KB state.
    if (action == "deploy" && !(args?["confirm"]?.ToObject<bool?>() ?? false))
        return McpResponse.Err(code: "ConfirmRequired", ...);

    if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
        return kbErr;                       // <-- runs before BadAction is reachable
    ...
}
```
The `BadAction` return is only at the bottom (`DeployService.cs:110-113`), after
the KB guard. Valid actions: `list_targets`, `deploy`.

### 1b. `CiPipelineService` — same ordering; also the `pipeline_output` buildId gap

```csharp
// CiPipelineService.cs:46-61  (KB guard + service resolve run before the switch's default:BadAction at 117)
if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
    return kbErr;
var svc = SdkServiceResolver.Resolve<Ci.IContinuousIntegrationService>();
...
TeamDevelopmentData data;
try { data = new TeamDevelopmentData(model); } catch (Exception ex) { return NotConnected(ex.Message); }
```
Valid actions (the `switch` cases): `pipeline_list`, `pipeline_runs`,
`pipeline_output`, `pipeline_run`, `pipeline_abort`.

The `pipeline_output` buildId gap:

```csharp
// CiPipelineService.cs:84-95
case "pipeline_output":
{
    if (string.IsNullOrWhiteSpace(project)) return NeedProject();
    int buildId = args?["buildId"]?.ToObject<int?>() ?? 0;   // <-- no guard; silently 0
    return McpResponse.Ok(code: "PipelineRunOutputRetrieved", result: new JObject
    {
        ["project"] = project,
        ["buildId"] = buildId,
        ["output"] = svc.GetPipelineRunOutput(data, project, buildId),
        ...
    });
}
```
Note the `action` variable is computed at the top of `Run` (lowercased) — read
the top of the method to find its exact name/spelling before adding the guard.

### 3. `TransferService.Export` — swallows FindObject exceptions as "missing"

```csharp
// TransferService.cs:80-92
var objs = new List<KBObject>();
var missing = new JArray();
foreach (var t in targets)
{
    string name = t?.ToString();
    if (string.IsNullOrWhiteSpace(name)) continue;
    KBObject o = null;
    try { o = _objects?.FindObject(name, typeFilter); } catch { }   // <-- exception == "not found"
    if (o == null) missing.Add(name); else objs.Add(o);
}
if (objs.Count == 0)
    return McpResponse.Err(code: "ObjectsNotFound", ..., target: string.Join(",", missing));
// ... success envelope includes ["notFound"] = missing ...
```

### 4. `UserControlsListService` — adds before the limit check

```csharp
// UserControlsListService.cs:42, 47-59
int limit = args?["limit"]?.ToObject<int?>() ?? 200;
...
foreach (var def in svc.GetControlDefinitionCollection(model))
{
    if (def == null) continue;
    controls.Add(new JObject { ["name"] = ..., ["description"] = ... });
    if (controls.Count >= limit) break;      // <-- checked AFTER Add; limit=0 yields 1 item
}
```

### Existing test that encodes the current (buggy) ordering — MUST be updated

```csharp
// NewServicesGuardTests.cs:49-52
[Fact]
public void DeployService_BogusAction_ReturnsNoKbOpen()
{
    var svc = new DeployService(null);
    var jo = Parse(svc.Run(JObject.Parse("{\"action\":\"bogus\"}")));
    Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
}
```
This test asserts the buggy behavior. It must change to expect `BadAction`
(rename the test accordingly). Guard tests construct services with `null` KB and
read the canonical `error.code` field (`jo["error"]?["code"]`).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Set GX ref path (once per shell) | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Test (filter) | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~NewServicesGuardTests"` | all pass |
| Full worker suite | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | all pass |
| Unlock build (only on MSB3027/3021 naming GxMcp.Worker.exe) | `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` | processes killed |

PowerShell, from repo root.

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/DeployService.cs`
- `src/GxMcp.Worker/Services/CiPipelineService.cs`
- `src/GxMcp.Worker/Services/TransferService.cs`
- `src/GxMcp.Worker/Services/UserControlsListService.cs`
- `src/GxMcp.Worker.Tests/NewServicesGuardTests.cs` (update + add cases)

**Out of scope**:
- The confirm/dryRun gates themselves — they are correct; only the *ordering*
  relative to action-validation changes. Do NOT weaken any confirm gate.
- Any other service in the batch (Transfer's `inspect`/`import`, DesignSystem,
  KbStats, ReorgImpact, SecurityScan, TableRelations) — no change here.
- The gateway golden fixture / `tool_definitions.json` — no schema change.

## Git workflow

- Conventional Commits (e.g. `fix(sdk): ...`). No `Co-Authored-By` trailer.
- Do NOT push or open a PR unless instructed. One commit for the bundle is fine.

## Steps

### Step 1: `DeployService` — validate action first

Add an action-recognition check immediately after `action` is computed (line 35),
**before** the confirm check and the KB guard:

```csharp
string action = (args?["action"]?.ToString() ?? "list_targets").Trim().ToLowerInvariant();

if (action != "list_targets" && action != "deploy")
    return McpResponse.Err(
        code: "BadAction",
        message: "Unknown action '" + action + "'. Expected list_targets or deploy.",
        hint: "genexus_deploy action=list_targets|deploy.");
```
Leave the trailing `BadAction` return (now unreachable) or remove it — either is
fine; if you remove it, ensure the method still compiles (all paths return).

**Verify**: build exits 0.

### Step 2: `CiPipelineService` — validate action first, and guard `buildId`

2a. After the `action` is computed at the top of `Run` (use its exact variable
name/spelling from the code), before the confirm check / KB guard, add:

```csharp
switch (action) { case "pipeline_list": case "pipeline_runs": case "pipeline_output": case "pipeline_run": case "pipeline_abort": break;
    default: return McpResponse.Err("BadAction", "Unknown pipeline action '" + action + "'.", "Valid: pipeline_list|pipeline_runs|pipeline_output|pipeline_run|pipeline_abort."); }
```
(You may format it as a normal multi-line `switch`. Keep the existing `default:`
`BadAction` inside the lower switch too — harmless, now unreachable.)

2b. In `case "pipeline_output"`, add a `buildId` guard before reading it:

```csharp
case "pipeline_output":
{
    if (string.IsNullOrWhiteSpace(project)) return NeedProject();
    if (args?["buildId"] == null)
        return McpResponse.Err("BadArgs", "pipeline_output requires buildId.", "Pass buildId=<int> (see pipeline_runs).");
    int buildId = args["buildId"].ToObject<int?>() ?? 0;
    ...
}
```

**Verify**: build exits 0.

### Step 3: `TransferService.Export` — distinguish real errors from "not found"

Capture the swallowed exception message and surface it separately instead of
folding every failure into `missing`:

```csharp
var objs = new List<KBObject>();
var missing = new JArray();
var lookupErrors = new JArray();
foreach (var t in targets)
{
    string name = t?.ToString();
    if (string.IsNullOrWhiteSpace(name)) continue;
    KBObject o = null;
    try { o = _objects?.FindObject(name, typeFilter); }
    catch (Exception ex) { lookupErrors.Add(new JObject { ["name"] = name, ["error"] = ex.Message }); continue; }
    if (o == null) missing.Add(name); else objs.Add(o);
}
```
Then include `lookupErrors` in **both** exit envelopes when non-empty:
- In the `ObjectsNotFound` error return (add `["lookupErrors"] = lookupErrors` to
  its result, or append a note) — check the `McpResponse.Err` signature to see if
  it takes a result object; if not, append the error names to the `target`/`hint`
  string so the info isn't lost.
- In the success envelope (`TransferService.cs:97-107`), add
  `["lookupErrors"] = lookupErrors` alongside `["notFound"] = missing`.

Keep behavior otherwise identical (genuine not-founds still go to `missing`).

**Verify**: build exits 0.

### Step 4: `UserControlsListService` — handle `limit<=0`

Short-circuit before the loop (cleanest), preserving the default of 200:

```csharp
int limit = args?["limit"]?.ToObject<int?>() ?? 200;
...
var controls = new JArray();
if (limit > 0)
{
    try
    {
        foreach (var def in svc.GetControlDefinitionCollection(model))
        {
            if (def == null) continue;
            controls.Add(new JObject { ["name"] = Reflect(def, "Name"), ["description"] = Reflect(def, "Description") });
            if (controls.Count >= limit) break;
        }
    }
    catch (Exception ex) { return McpResponse.Err("ListControlsFailed", ex.Message, "Check the worker log."); }
}
```
(An empty `controls` for `limit<=0` is the correct "probe" response.)

**Verify**: build exits 0.

### Step 5: Update + extend the guard tests

In `NewServicesGuardTests.cs`:

- Change `DeployService_BogusAction_ReturnsNoKbOpen` to expect `BadAction` and
  rename it `DeployService_BogusAction_ReturnsBadAction`.
- Add `CiPipelineService_BogusAction_ReturnsBadAction` (construct with `null` KB,
  call with `{"action":"bogus"}`, assert `error.code == "BadAction"`).
- Add `CiPipelineService_PipelineOutput_NoBuildId_ReturnsBadArgs` — but note this
  needs a KB/project to reach the `buildId` guard *after* `NeedProject()`. Since
  the guard tests use a `null` KB, a call without a KB returns `NoKbOpen`/
  `BadAction` before reaching `pipeline_output`'s body. Therefore: assert what IS
  reachable without a KB (that `{"action":"pipeline_output"}` no longer silently
  proceeds — with the Step 2a change it still hits the KB guard, returning
  `NoKbOpen`, which is acceptable). Do NOT try to force the `buildId` path without
  a KB; if it can't be unit-tested at this layer, add a `// covered by ...` note
  and rely on the code review instead. Record this in your report.
- Add `UserControlsListService` cases only if the service is constructable with a
  `null` KB and reaches the `limit` logic without a live SDK; if it returns an
  "unavailable" guard before the loop (likely, since it needs the SDK service),
  the `limit<=0` fix is not unit-testable here — note that and rely on review.

Keep every assertion reading `jo["error"]?["code"]` (canonical shape), matching
the existing tests.

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~NewServicesGuardTests"` → all pass.

### Step 6: Full worker suite green

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass.

## Test plan

- Update `DeployService_BogusAction` to the corrected expectation; add a
  `CiPipelineService` bad-action case. These are the deterministically testable
  ones at the guard layer.
- The `buildId` and `limit<=0` fixes may not be reachable without a live KB/SDK
  at the unit layer — if so, verify by code review and note it (do not fabricate
  a KB harness).
- Model all new cases on the existing `NewServicesGuardTests` methods.
- Verification: the filtered + full `dotnet test` runs, all green.

## Done criteria

Machine-checkable. ALL must hold:

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] `DeployService` and `CiPipelineService` return `BadAction` for an unknown action even with no KB open (verified by the updated/added tests).
- [ ] `grep -n "requires buildId" src/GxMcp.Worker/Services/CiPipelineService.cs` matches (the guard exists).
- [ ] `grep -n "lookupErrors" src/GxMcp.Worker/Services/TransferService.cs` matches.
- [ ] `grep -n "limit > 0" src/GxMcp.Worker/Services/UserControlsListService.cs` matches.
- [ ] `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` exits 0; no `*_ReturnsNoKbOpen` test remains for `DeployService_BogusAction`.
- [ ] No files outside the in-scope list modified (`git status`).
- [ ] `plans/README.md` status row for 021 updated.

## STOP conditions

Stop and report back if:

- Any in-scope file no longer matches its "Current state" excerpt.
- `McpResponse.Err` has no way to carry the `lookupErrors` array and no
  `target`/`hint` string to append to (report; don't drop the data silently).
- Reordering the action check breaks a confirm-gate test in a way that suggests a
  confirm gate depended on the old ordering — that would be a real behavior change
  needing the maintainer's call.

## Maintenance notes

- These services follow a common shape: **validate action → check static
  preconditions (confirm) → resolve KB/SDK → act**. New actions/services should
  keep that order so error messages point at the caller's actual mistake.
- The `buildId`/`limit<=0` fixes are guarded-input hardening that the guard-test
  layer can't fully reach; if a future integration-test harness with a live KB is
  added, backfill real coverage for them.
- Reviewer should confirm no confirm/dryRun gate was weakened — only reordered
  relative to action validation.
