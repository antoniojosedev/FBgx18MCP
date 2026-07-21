# Plan 033: `KbValidationService.ValidateConditions` passes the known type to `FindObject`

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Worker/Services/KbValidationService.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`ValidateConditions` builds `candidates` = every index entry of type Transaction or
WebPanel (potentially most of the KB), then calls `_objectService.FindObject(entry.Name)`
**without a type filter** once per candidate. `ObjectService.FindObject` only takes its
O(1) typed fast path (`Type:Name` → GUID → `Objects.Get`) when a `typeFilter` is given;
without one it falls into a linear scan of all `idx.Objects.Values`. So this is
O(candidates × KB-size). `entry.Type` is already known (it's the filter used to build
`candidates`). This is the exact anti-pattern already fixed in `SourceSearchService`.

## Current state

- `src/GxMcp.Worker/Services/KbValidationService.cs`, lines 54–72:

```csharp
var candidates = index.Objects.Values
    .Where(e => string.Equals(e.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
             || string.Equals(e.Type, "WebPanel", StringComparison.OrdinalIgnoreCase))
    .ToList();
...
foreach (var entry in candidates)
{
    ...
    var obj = _objectService.FindObject(entry.Name);   // <-- no type filter → linear scan
    if (obj == null) continue;
    ...
}
```

`FindObject` signature: `FindObject(string target, string typeFilter = null)`
(`ObjectService.cs:1281`). Passing `entry.Type` only narrows to the correct object.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~KbValidation"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:** `src/GxMcp.Worker/Services/KbValidationService.cs` — the `FindObject` call
in `ValidateConditions` only.

**Out of scope:** everything else in the file; `ObjectService.FindObject` itself.

## Git workflow

- Branch: `advisor/033-kbvalidation-typefilter`
- One commit: `perf(validate): pass known type to FindObject to avoid O(n²) rescan`
- Do NOT push.

## Steps

### Step 1: Pass the type

Change `_objectService.FindObject(entry.Name)` to `_objectService.FindObject(entry.Name, entry.Type)`.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Confirm existing behavior

If a `KbValidation` test exists, it must still pass. If none exists and the method
needs a live KB to exercise, this is a build-only change (one-line, exact repo
precedent) — do not build an SDK harness. Note it in your report.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~KbValidation"` → all pass (or "no matching tests", which is acceptable for a build-only change).

## Done criteria

- [ ] Build exits 0
- [ ] `FindObject` in `ValidateConditions` passes `entry.Type` as the second arg
- [ ] Existing tests (if any) pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live code already passes a type filter (drift).
- `entry.Type` isn't the value `candidates` was filtered on (it is — Transaction/WebPanel).

## Maintenance notes

- Reviewer: trivial, but confirm `entry.Type` is the exact string `FindObject`'s index
  key expects (it is — the index key is `Type:Name` with `Type == entry.Type`).
