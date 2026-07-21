# Plan 029: `CrossPlatformImpactAnalyzer` uses a name→entry map instead of a linear scan

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/CrossPlatformImpactAnalyzer.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P3
- **Effort**: S-M
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`genexus_analyze mode=cross_platform_impact` calls `TryFindEntry` once per caller and
again per BFS node inside `ClassifyCallerPlatform` (depth 3). `TryFindEntry` probes 14
`"Type:bareName"` keys, then falls back to a full linear scan of `index.Objects` on a
miss. For a modularized KB the index can be keyed `"Type:module.qualified.Name"`, so
the bare-name prefix probe misses and every lookup degrades to an O(indexSize) scan —
making the whole analysis O(callers × depth × indexSize) on modular KBs. Build a
name→entry map once and consult it before scanning.

## Current state

- `src/GxMcp.Worker/Services/CrossPlatformImpactAnalyzer.cs`, `TryFindEntry`
  (lines 83–101):

```csharp
private static bool TryFindEntry(SearchIndex index, string bareName, out SearchIndex.IndexEntry entry)
{
    entry = null;
    if (index?.Objects == null || string.IsNullOrEmpty(bareName)) return false;
    foreach (var prefix in new[] { "Procedure", "Transaction", "WebPanel", "SDPanel", "DataProvider", "WebComponent", "MasterPage", "SDT", "Domain", "Object", "Menu", "Panel", "WorkWithDevices", "Dashboard" })
    {
        if (index.Objects.TryGetValue(prefix + ":" + bareName, out var hit) && hit != null) { entry = hit; return true; }
    }
    foreach (var kv in index.Objects)   // <-- O(indexSize) fallback, hit on modular KBs
    {
        if (kv.Value != null && string.Equals(kv.Value.Name, bareName, StringComparison.OrdinalIgnoreCase))
        { entry = kv.Value; return true; }
    }
    return false;
}
```

Callers: `ClassifyCallerPlatform` (lines 46, 49, 64, 68) and `Analyze` (lines 133,
136–140). `TryFindEntry` is `private static` and takes `SearchIndex index`.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CrossPlatform"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/CrossPlatformImpactAnalyzer.cs` — `TryFindEntry`,
  `ClassifyCallerPlatform`, `Analyze` signatures/bodies as needed to thread a
  per-analysis name map.
- `src/GxMcp.Worker.Tests/` — a test.

**Out of scope:**
- `SearchIndex` / `IndexCacheService` — do NOT add a persistent field there; keep the
  map local to one `Analyze` invocation.
- The classification logic (Web/SmartDevices/Both) — unchanged.

## Git workflow

- Branch: `advisor/029-crossplatform-name-map`
- One commit: `perf(analyze): index cross-platform lookups by name to avoid O(n) scans`
- Do NOT push.

## Steps

### Step 1: Build a name→entry map once per `Analyze`

In `Analyze`, before bucketing callers, build a case-insensitive
`Dictionary<string, SearchIndex.IndexEntry>` from `index.Objects.Values`, keyed by
`entry.Name` (first-wins on duplicate bare names — matches the current
first-match-wins scan). Pass this map down to `ClassifyCallerPlatform` and
`TryFindEntry` as a new parameter.

### Step 2: Consult the map in `TryFindEntry` before the linear scan

Change `TryFindEntry` to accept the map. Keep the 14-prefix `TryGetValue` probe first
(cheap, exact when keys are bare). If it misses, look up `map[bareName]` instead of
the `foreach` linear scan. Only if the map also misses, return false (drop the linear
scan entirely — the map already covers every entry by name, so it's a strict superset
of what the scan found).

Thread the map parameter through `ClassifyCallerPlatform` too (it calls `TryFindEntry`
at lines 49, 64, 68). For the public `Analyze` entry point, build the map internally
so external callers' signatures are unaffected where possible; if any other caller of
`ClassifyCallerPlatform` exists (grep for it), update it to build/pass the map.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 3: Test

Create `src/GxMcp.Worker.Tests/CrossPlatformNameMapTests.cs` (or extend an existing
`CrossPlatform` test). Cover:
- **Bare-key KB** (keys `"Procedure:Foo"`): resolution is unchanged vs before.
- **Module-scoped KB** (keys `"Procedure:my.mod.Foo"` with `entry.Name == "Foo"`):
  `Analyze` still resolves `Foo` and classifies its callers — proving the map path
  works where the prefix probe misses.
- A caller reachable from both a Web type and an SD type still classifies as `Both`.

Model on the existing `CrossPlatform` tests (search the test project — the audit
noted `Analyze` accepts a `callerSourceResolver` you can pass `null` in tests).

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~CrossPlatform"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] `TryFindEntry` no longer contains a `foreach (var kv in index.Objects)` linear scan
- [ ] Module-scoped-key resolution test passes
- [ ] Existing cross-platform tests still pass (no classification regression)
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `TryFindEntry` differs from the excerpt (drift) — report.
- Removing the linear scan changes a result in the existing tests (means bare-name
  first-wins semantics differ from the map) — STOP and reconcile before proceeding.

## Maintenance notes

- The map is per-`Analyze` and short-lived; if `cross_platform_impact` is ever called
  in a tight loop, consider caching it — but not by mutating `SearchIndex`.
- Reviewer: confirm the map is first-match-wins on duplicate bare names, matching the
  scan it replaces.
