# Plan 023: `ResolveWWPInstance` resolves the WWP host by name, not a full-KB scan

> **Executor instructions**: Follow step by step, run every verification command,
> confirm expected results. Touch only in-scope files. On any STOP condition, stop and
> report. Commit in the worktree per Git workflow. SKIP updating `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/PatternAnalysisService.cs`
> On any change, compare the excerpt below to the live code; mismatch = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`BuildPatternShadowWarningsIfAny` runs on **every** `genexus_edit`/`genexus_patch`
that touches a WebForm or Layout part (the common authoring path). It calls
`PatternAnalysisService.ResolveWWPInstance(obj)`, which — for any object that isn't
itself a `WorkWithPlus` (i.e. the usual Transaction/WebPanel being edited) — does
`model.Objects.GetAll().FirstOrDefault(...)`, a full-KB COM scan. The codebase's own
comments elsewhere clock that scan at 10s+ on a 50k-object KB. So a routine WebForm
edit pays a full-KB scan just to compute an optional "your hand edit may be
overwritten" warning that is usually null. The lookup is by an exact name
(`"WorkWithPlus" + obj.Name`), so a name-indexed lookup replaces the scan with an
O(1) hit.

## Current state

- `src/GxMcp.Worker/Services/PatternAnalysisService.cs` — `ResolveWWPInstance`
  (lines 125–151). The class already holds `private readonly ObjectService
  _objectService;` (line 15), injected via its constructor (line 17).

Method today:

```csharp
public KBObject ResolveWWPInstance(KBObject obj)
{
    if (obj == null) return null;
    if (obj.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase)) return obj;

    var model = obj.Model;
    if (model == null) return null;

    string instanceName = "WorkWithPlus" + obj.Name;
    var namedMatch = model.Objects.GetAll()                 // <-- full-KB COM scan
        .FirstOrDefault(o =>
            o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase) &&
            o.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase));
    if (namedMatch != null) return namedMatch;

    try
    {
        var childMatch = model.Objects.GetChildren(obj)
            .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
        if (childMatch != null) return childMatch;
    }
    catch { }

    return null;
}
```

`ObjectService.FindObject(string target, string typeFilter = null)`
(`ObjectService.cs:1281`) already does a fast lookup: index-typed key first, then the
SDK's own name index — it explicitly never does a full `GetAll()` scan. Its index key
format is `"Type:Name"` where Type is `TypeDescriptor.Name`, so
`FindObject("WorkWithPlus<Name>", "WorkWithPlus")` resolves exactly the object the
scan was looking for.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PatternAnalysis"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/PatternAnalysisService.cs` — `ResolveWWPInstance` only.

**Out of scope:**
- The `GetChildren(obj)` fallback — keep it unchanged (it covers the case where the
  host doesn't follow the `WorkWithPlus<Name>` naming rule).
- `PatchService.cs` / `WriteService.VisualWrite.cs` callers — do NOT change them; the
  fix is entirely inside `ResolveWWPInstance` so both callers benefit for free.
- `GetWWPStructure`'s existing `wwpEligible` guard — leave it.

## Git workflow

- Branch: `advisor/023-resolvewwp-name-lookup`
- One commit: `perf(pattern): resolve WWP host by name index instead of full-KB scan`
- Do NOT push.

## Steps

### Step 1: Replace the `GetAll()` scan with a name lookup

Replace the `namedMatch` block so it uses `_objectService.FindObject(instanceName,
"WorkWithPlus")` instead of `model.Objects.GetAll().FirstOrDefault(...)`. Guard
`_objectService` for null (return via the existing `GetChildren` fallback path if it's
ever null — do not throw). Keep `instanceName`, keep the early `WorkWithPlus`
self-return, keep the `GetChildren` fallback exactly. Remove the now-unused `System.Linq`
usage only if nothing else in the file needs it (it likely still does — check before
removing any `using`).

Target shape:

```csharp
string instanceName = "WorkWithPlus" + obj.Name;
var namedMatch = _objectService?.FindObject(instanceName, "WorkWithPlus");
if (namedMatch != null) return namedMatch;
```

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Add / extend a test

If a `PatternAnalysisService` test file exists, add a case; else create
`src/GxMcp.Worker.Tests/ResolveWwpNameLookupTests.cs`. Because `ResolveWWPInstance`
needs live SDK `KBObject`s, prefer a test that asserts the *contract* without a live
KB: e.g. a fake/stub `ObjectService` (if the tests already stub it) verifying that
`ResolveWWPInstance` calls `FindObject(instanceName, "WorkWithPlus")` and returns its
result, and returns the self-object for a `WorkWithPlus`-typed input. If the existing
tests cannot construct `KBObject`/`ObjectService` without a live KB, STOP and report —
do not stand up a new SDK harness.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~PatternAnalysis"` (or your new filter) → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] `grep -n "model.Objects.GetAll()" src/GxMcp.Worker/Services/PatternAnalysisService.cs` returns nothing
- [ ] `ResolveWWPInstance` calls `_objectService?.FindObject(instanceName, "WorkWithPlus")`
- [ ] Targeted worker tests pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `ResolveWWPInstance` doesn't match the excerpt (drift).
- `ObjectService.FindObject`'s signature isn't `(string, string=null)` as described.
- Tests cannot exercise this without a live KB (report the existing test approach).

## Maintenance notes

- The `GetChildren` fallback still does a bounded scan of the object's children — fine,
  it's small. If WWP host naming ever changes, revisit `instanceName`.
- Reviewer: confirm the fix is inside `ResolveWWPInstance` (so both edit callers
  benefit) and the `GetChildren` fallback is untouched.
