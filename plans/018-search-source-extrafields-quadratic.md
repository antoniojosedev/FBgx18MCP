# Plan 018: `search_source` metadata-field branch is linear, not quadratic, and honors `objectName` scope

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4885c1c..HEAD -- src/GxMcp.Worker/Services/SourceSearchService.cs`
> If it changed since this plan was written, compare the "Current state"
> excerpts against the live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW-MED
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `4885c1c`, 2026-07-21

## Why this matters

`genexus_search_source` with `fields=[caption|webForm|parmNames]` runs a second
pass over the index to search object metadata. That branch has two problems:

1. It resolves each candidate object with `_objectService.FindObject(e.Name)` —
   **without** the type argument. The untyped `FindObject` path is itself a full
   linear scan over `index.Objects.Values`. Called once per entry over the whole
   index, this is **O(n²)** in KB size.
2. It rebuilds its candidate list (`allEntries`) from the full index, ignoring
   the `objectName=` scope the caller supplied (parsed at line 133 as
   `objectNameSet`). So a caller who scopes with `objectName="Foo" fields=[caption]`
   expecting a cheap lookup still pays a full-KB scan.

The main source-scan loop already solved (1): it passes `e.Type` into
`FindObject` (line 218) precisely to take the O(1) typed index path, with a
comment explaining why. This plan applies the same two fixes to the metadata
branch: pass the type, and respect the `objectName` scope.

## Current state

- `src/GxMcp.Worker/Services/SourceSearchService.cs` — implements `search_source`.
  - `objectNameSet` (the parsed `objectName=` scope) is built at line 133:
    ```csharp
    // SourceSearchService.cs:133
    var objectNameSet = ParseObjectNames(c.ObjectName);
    ```
    and applied to the main scan via `ObjectNameMatches` (line 141).
  - The main scan resolves objects **with** the type (the pattern to copy):
    ```csharp
    // SourceSearchService.cs:214-218
    // Pass the type so FindObject takes the O(1) typed fast path ...
    try { obj = _objectService.FindObject(e.Name, e.Type); } catch { continue; }
    ```

The metadata branch to fix (note it rebuilds `allEntries` ignoring
`objectNameSet`, and calls `FindObject(e.Name)` untyped at line 286):

```csharp
// SourceSearchService.cs:263-287
if (extraFields.Count > 0 && rx != null)
{
    var allEntries = index.Objects.Values
        .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
        .ToList();
    foreach (var e in allEntries)
    {
        if (produced >= c.MaxResults) break;
        if (ct.IsCancellationRequested) break;
        if (swBudget.ElapsedMilliseconds > timeoutMs) break;

        foreach (var field in extraFields)
        {
            if (produced >= c.MaxResults) break;
            string fieldValue = null;
            if (string.Equals(field, "description", StringComparison.OrdinalIgnoreCase))
                fieldValue = e.Description;
            else if (string.Equals(field, "caption", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(field, "parmNames", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(field, "webForm", StringComparison.OrdinalIgnoreCase))
            {
                // Caption / parmNames / webForm require SDK access
                KBObject obj2 = null;
                try { obj2 = _objectService.FindObject(e.Name); } catch { }
                if (obj2 == null) continue;
                ...
```

Confirmed untyped-scan cost — the untyped `FindObject` branch is a full linear
scan (`src/GxMcp.Worker/Services/ObjectService.cs:1322`, the "Global search in
index" `foreach (var entry in index.Objects.Values)`).

### Why the universe stays broad (do NOT narrow it to the main scan's `entries`)

The metadata branch deliberately uses a **broader** candidate universe than the
main source scan: the main scan (unscoped) restricts to
`Procedure|DataProvider|WebPanel|Transaction` plus a literal pre-filter, because
those are the only types with searchable *source*. But `caption`/`description`/
`parmNames` metadata exists on many more object types, so the metadata branch
must NOT be limited to those four types. Keep the current
"all types, `TypeFilter` only" universe. The only changes are: (a) apply the
`objectNameSet` scope when the caller supplied one, and (b) pass `e.Type` to
`FindObject`.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Set GX ref path (once per shell) | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Test (filter) | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceSearch"` | all pass |
| Full worker suite | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | all pass |
| Unlock build (only on MSB3027/3021 naming GxMcp.Worker.exe) | `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` | processes killed |

PowerShell, from repo root `C:\Projetos\Genexus18MCP`.

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/SourceSearchService.cs` (the metadata branch only,
  lines ~263-287)

**Out of scope** (do NOT touch):
- The main source-scan loop (lines ~180-256) — already correct.
- `ObjectService.FindObject` — do not "fix" the untyped path; the caller must
  simply pass the type. Other callers rely on the untyped path.
- The `caption`/`webForm`/`parmNames` field-extraction logic below line 287 —
  leave the value-reading dynamic code exactly as-is.

## Git workflow

- Conventional Commits (e.g. `perf(search): ...`). No `Co-Authored-By` trailer.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Scope the metadata candidate list and pass the type

Replace the `allEntries` construction and the untyped `FindObject` call.

1a. Apply the `objectNameSet` scope to `allEntries` (falling back to the full
type-filtered set when no `objectName=` was supplied). Change lines 265-267 to:

```csharp
var allEntries = index.Objects.Values
    .Where(e => objectNameSet == null || ObjectNameMatches(objectNameSet, e.Name))
    .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
    .ToList();
```

1b. Pass `e.Type` into `FindObject` at line 286 exactly as the main loop does:

```csharp
KBObject obj2 = null;
try { obj2 = _objectService.FindObject(e.Name, e.Type); } catch { }
if (obj2 == null) continue;
```

Leave everything else in the branch unchanged.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Add a regression test for the scope fix

The metadata branch is exercisable without a live KB via the same index test seam
the existing search tests use. First confirm which seam/helper the existing
source-search tests use to load a fake index — search for the test file:

```
grep -rln "search_source\|SourceSearch\|Fields" src/GxMcp.Worker.Tests/
```

Open the matching test file and model the new test on it. If a test seam exists
that loads `SearchIndex` entries directly (e.g. `LoadFromEntries`) and drives the
`search_source` dispatch, add a test that:

- Seeds an index with, say, 3 objects (one named `Target`, two others), each with
  a `Description` containing a token like `NEEDLE`.
- Calls `search_source` with `objectName="Target"`, `fields=["description"]`,
  `pattern="NEEDLE"`.
- Asserts the only hit is `Target` (proving the `objectName` scope is now
  honored by the metadata branch, not scanning all 3).

If no such seam exists and the branch cannot be exercised without a live KB
(SDK-dependent `caption`/`webForm` paths need real objects), then:

- Use the `description` field (which reads `e.Description` straight from the index
  entry, **no** SDK/`FindObject` needed) for the test, since that path is
  index-only and unit-testable.
- If even that is not reachable through an existing seam, STOP and report that
  the branch lacks a unit-test seam (do not invent a new dispatch harness — flag
  it for the maintainer).

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceSearch"` → all pass, including the new test.

### Step 3: Full worker suite green

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass.

## Test plan

- New test in the existing source-search test file (found in Step 2), covering:
  the `objectName`-scoped metadata search returns only the scoped object.
- Model on whatever existing `search_source` test drives the dispatch with a
  seeded index.
- Verification: the two `dotnet test` commands above.

## Done criteria

Machine-checkable. ALL must hold:

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] `grep -n "FindObject(e.Name)" src/GxMcp.Worker/Services/SourceSearchService.cs` returns **no** matches (the untyped call is gone; only the typed `FindObject(e.Name, e.Type)` remains).
- [ ] `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` exits 0; a new scope test passes.
- [ ] No files outside the in-scope list modified (`git status`).
- [ ] `plans/README.md` status row for 018 updated.

## STOP conditions

Stop and report back if:

- `SourceSearchService.cs` no longer matches the "Current state" excerpts.
- `ObjectNameMatches` or `ParseObjectNames` no longer exist / changed signature.
- The metadata branch has no reachable unit-test seam (report; do not build a new
  harness).
- The fix would require changing `ObjectService.FindObject` — it must not; if you
  believe it does, you've misread the plan, re-read Step 1.

## Maintenance notes

- If a future field is added to the metadata branch that reads straight from the
  `IndexEntry` (like `description` does), no `FindObject` call is needed for it —
  keep SDK resolution only for fields that genuinely require the live object.
- Reviewer should confirm the candidate universe stays broad (all types, not the
  main scan's four source-bearing types) — narrowing it would silently drop
  legitimate metadata hits.
- The prior audit already added secondary type/domain indexes (plan 002); if a
  `caption` index is ever added, this branch could avoid the per-object SDK read
  entirely for captions — a future optimization, not part of this plan.
