# Plan 020: `design_system` (no `name`) uses the type index instead of a full-KB COM scan

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4885c1c..HEAD -- src/GxMcp.Worker/Services/DesignSystemService.cs`
> If it changed since this plan was written, compare the "Current state"
> excerpts against the live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `4885c1c`, 2026-07-21

## Why this matters

`genexus_layout action=design_system` **without** a `name` argument walks the
entire KB with `model.Objects.GetAll()` and does a COM property read
(`o.TypeDescriptor.Name`) on every object until it finds the first
`DesignSystem`. On a large KB that's a multi-second, COM-bound scan for what the
search index already answers in O(bucket-size): `index.TypeIndex["DesignSystem"]`
(typically 0–2 entries). `SearchService` and `ListService` already resolve
"objects of type X" this way. Lower-frequency call path than the other perf
findings, but the fix is small and removes an avoidable full-KB COM walk.

## Current state

- `src/GxMcp.Worker/Services/DesignSystemService.cs` — constructed with
  `(KbService kb, ObjectService objects)` (see
  `src/GxMcp.Worker/Services/CommandDispatcher.cs:248`:
  `_designSystemService = new DesignSystemService(_kbService, _objectService);`).
  It has no direct index reference but `ObjectService` exposes one.

The slow no-`name` path:

```csharp
// DesignSystemService.cs:42-62
if (!string.IsNullOrWhiteSpace(name))
{
    try { dso = _objects?.FindObject(name, "DesignSystem") as DSObject; } catch { }
    if (dso == null)
        return McpResponse.Err("ObjectNotFound", ...);
}
else
{
    // No name → use the first DesignSystem object in the KB.
    try
    {
        foreach (KBObject o in model.Objects.GetAll())
        {
            if (string.Equals(o?.TypeDescriptor?.Name, "DesignSystem", StringComparison.OrdinalIgnoreCase))
            { dso = o as DSObject; if (dso != null) { name = o.Name; break; } }
        }
    }
    catch { }
    if (dso == null)
        return McpResponse.Err("NoDesignSystem", ...);
}
```

The index accessor on `ObjectService` (already public):

```csharp
// ObjectService.cs:72
public SearchIndex GetIndex() { return _kbService.GetIndexCache().GetIndex(); }
// ObjectService.cs:78
public SearchIndex GetLoadedIndexOrNull() { return _kbService.GetIndexCache().TryGetLoadedIndex(); }
```

The `TypeIndex` shape to rely on — `SearchService` uses it like this
(`src/GxMcp.Worker/Services/SearchService.cs:174-189`): `index.TypeIndex` maps a
**type name key** → a set of composite object keys; each composite key looks up
into `index.Objects` to get an `IndexEntry` (which has `.Name`, `.Type`,
`.Guid`). Access the set under `lock (typeKeys)` as `SearchService` does:

```csharp
// SearchService.cs:182-188 (reference pattern)
foreach (var typeKey in index.TypeIndex.Keys)
{
    if (!IsTypeMatch(typeKey, criteria.TypeFilter)) continue;
    if (index.TypeIndex.TryGetValue(typeKey, out var typeKeys))
    {
        lock (typeKeys) { candidateKeys.UnionWith(typeKeys); }
    }
}
```

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Set GX ref path (once per shell) | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Full worker suite | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | all pass |
| Unlock build (only on MSB3027/3021 naming GxMcp.Worker.exe) | `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` | processes killed |

PowerShell, from repo root.

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/DesignSystemService.cs` — the `else` (no-`name`)
  branch only.

**Out of scope**:
- The `name`-supplied branch (already uses the typed `FindObject`).
- `SearchService`, `ListService`, `ObjectService`, `SearchIndex` — read only,
  do not modify. This plan only adds a *reader* of the existing index.

## Git workflow

- Conventional Commits (e.g. `perf(layout): ...`). No `Co-Authored-By` trailer.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Try the type index first, keep the scan as fallback

In the `else` branch, before the `model.Objects.GetAll()` loop, attempt the
index lookup. If the index isn't loaded or the bucket is empty/missing, fall
through to the existing full scan (do NOT delete it — it's the correct fallback
for a cold index, matching how `FindObject`'s fast path falls through).

Target shape for the `else` block:

```csharp
else
{
    // Fast path: the search index already buckets objects by type. Resolve the
    // first DesignSystem via TypeIndex["DesignSystem"] instead of a full-KB
    // COM scan (mirrors SearchService/ListService type-bucket lookups).
    try
    {
        var index = _objects?.GetLoadedIndexOrNull();
        if (index?.TypeIndex != null && index.Objects != null
            && index.TypeIndex.TryGetValue("DesignSystem", out var dsKeys))
        {
            string firstKey = null;
            lock (dsKeys) { foreach (var k in dsKeys) { firstKey = k; break; } }
            if (firstKey != null && index.Objects.TryGetValue(firstKey, out var entry)
                && !string.IsNullOrEmpty(entry?.Name))
            {
                dso = _objects.FindObject(entry.Name, "DesignSystem") as DSObject;
                if (dso != null) name = entry.Name;
            }
        }
    }
    catch { /* fall through to the full scan below */ }

    // Fallback: cold/absent index → the original full-KB scan.
    if (dso == null)
    {
        try
        {
            foreach (KBObject o in model.Objects.GetAll())
            {
                if (string.Equals(o?.TypeDescriptor?.Name, "DesignSystem", StringComparison.OrdinalIgnoreCase))
                { dso = o as DSObject; if (dso != null) { name = o.Name; break; } }
            }
        }
        catch { }
    }

    if (dso == null)
        return McpResponse.Err("NoDesignSystem", "This KB has no Design System Object.", "DSOs are created in the GeneXus IDE; nothing to read.");
}
```

Confirm the `TypeIndex` type key for design systems is exactly `"DesignSystem"`
by checking how entries get their `Type` (the `name`-branch already calls
`FindObject(name, "DesignSystem")`, and `index.Objects` keys are `"Type:Name"`,
so the type token is `"DesignSystem"`). If a grep shows the type is spelled
differently in `TypeIndex` keys, use that exact spelling (STOP condition below).

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Full worker suite green

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass.

## Test plan

- Behavior is unchanged (still returns the first DSO, or `NoDesignSystem`); the
  fallback preserves the exact old path for a cold index, so existing tests
  cover the contract.
- If `NewServicesGuardTests` (or a DesignSystem-specific test) already exercises
  the no-KB / no-name guards, run it and confirm it still passes. Do not add a
  timing/perf test.
- Verification: full worker suite green.

## Done criteria

Machine-checkable. ALL must hold:

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] `grep -n "TypeIndex" src/GxMcp.Worker/Services/DesignSystemService.cs` shows the new index lookup.
- [ ] The `model.Objects.GetAll()` fallback is still present (grep confirms).
- [ ] `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` exits 0.
- [ ] No files outside the in-scope list modified (`git status`).
- [ ] `plans/README.md` status row for 020 updated.

## STOP conditions

Stop and report back if:

- `DesignSystemService.cs` no longer matches the "Current state" excerpts.
- `ObjectService.GetLoadedIndexOrNull()` no longer exists or `SearchIndex` no
  longer has a `TypeIndex` / `Objects` with the `"Type:Name"` composite-key
  shape described.
- The `TypeIndex` type token for design systems is not `"DesignSystem"` and you
  cannot determine the correct spelling from the code — report it.

## Maintenance notes

- This is the same type-bucket-lookup pattern used across the worker; if a shared
  `ObjectService.FirstObjectOfType(string type)` helper is ever introduced, this
  branch (and the `SearchService`/`ListService` sites) should adopt it.
- Reviewer should confirm the full-scan fallback remains for the cold-index case
  (the `LoadFromEntries` test seam and a not-yet-indexed KB both hit it).
