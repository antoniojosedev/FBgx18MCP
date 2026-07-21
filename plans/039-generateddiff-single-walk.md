# Plan 039: `GeneratedDiffService.FindGeneratedFiles` walks each root once, not once per extension

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Worker/Services/GeneratedDiffService.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`FindGeneratedFiles` does one recursive `Directory.GetFiles(root, target+ext,
SearchOption.AllDirectories)` **per extension** (`.cs .aspx .js .html`) — up to four
full recursive walks of the same generated-output tree per `genexus_diff_generated`
request (the last candidate root is the whole KB path). The walks differ only by the
extension filter, which can be applied in memory after a single walk. One walk per root
instead of four is a straightforward, behavior-preserving win.

## Current state

- `src/GxMcp.Worker/Services/GeneratedDiffService.cs`, `FindGeneratedFiles` (196–228):

```csharp
foreach (var c in candidates)
{
    if (!Directory.Exists(c)) continue;
    foreach (var ext in GeneratedExtensions)
    {
        string fileName = target + ext;
        try
        {
            foreach (var match in Directory.GetFiles(c, fileName, SearchOption.AllDirectories))
            {
                if (!found.Contains(match, StringComparer.OrdinalIgnoreCase))
                    found.Add(match);
            }
        }
        catch { }
    }
    if (found.Count > 0) break; // first matching root wins
}
```

`GeneratedExtensions` is the fixed set `.cs .aspx .js .html` (a field in the same
class). The `if (found.Count > 0) break;` (first matching root wins) must be preserved.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~GeneratedDiff"` | all pass (or none) |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:** `src/GxMcp.Worker/Services/GeneratedDiffService.cs` — `FindGeneratedFiles`
only.

**Out of scope:** the candidate-root list, the baseline/copy logic
(`CaptureBaseline`/`ResolveLatestBaselineDir`), `GeneratedExtensions` contents. Do NOT
introduce a cross-request cache (invalidation risk) — this plan is only the single-walk
optimization.

## Git workflow

- Branch: `advisor/039-generateddiff-single-walk`
- One commit: `perf(diff): walk each generated-output root once, filter extensions in memory`
- Do NOT push.

## Steps

### Step 1: One walk per root, filter extensions in memory

Replace the inner `foreach (ext)` loop so each root is walked once. Enumerate files
matching the target's base name once, then keep those whose extension is in
`GeneratedExtensions`:

```csharp
foreach (var c in candidates)
{
    if (!Directory.Exists(c)) continue;
    try
    {
        foreach (var match in Directory.EnumerateFiles(c, target + ".*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(match);
            if (GeneratedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(match), target + ext, StringComparison.OrdinalIgnoreCase)
                && !found.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(match);
            }
        }
    }
    catch { }
    if (found.Count > 0) break; // first matching root wins — preserved
}
```

The `Path.GetFileName(match) == target+ext` check keeps the match precise (the `target
+ ".*"` pattern is broad, so re-verify the filename equals the base name plus a
whitelisted extension — this excludes accidental matches like `target.cs.bak` or
`targetX.cs`). Result set must equal the old four-walk result for any tree.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Test

If a `GeneratedDiff` test exists, keep it green. If the method is only exercisable
against a real generated tree, this is a build-only, behavior-preserving change — note
it. Optionally add a temp-dir test: create files `Foo.cs`, `Foo.aspx`, `Foo.cs.bak`,
`FooBar.cs` under a temp root and assert `FindGeneratedFiles(root, "Foo")` returns
exactly `Foo.cs` and `Foo.aspx` (proves the precise-filename filter). `FindGeneratedFiles`
is `internal static` — use it directly if `InternalsVisibleTo` is configured; otherwise
skip the test and note it (do not add visibility plumbing).

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~GeneratedDiff"` → all pass (or none).

## Done criteria

- [ ] Build exits 0
- [ ] `FindGeneratedFiles` walks each root once (`grep -c "GetFiles\|EnumerateFiles" ...` shows a single enumeration inside the root loop, not one per extension)
- [ ] `if (found.Count > 0) break;` first-matching-root behavior preserved
- [ ] Precise-filename filter excludes `target.cs.bak`-style over-matches
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `FindGeneratedFiles` differs from the excerpt (drift).
- The `target + ".*"` pattern behaves surprisingly on the test tree (over/under-match)
  and the filename filter can't make it precise — STOP and report.

## Maintenance notes

- Reviewer: confirm the in-memory filter yields exactly the same set as the old
  per-extension walks (precise base-name + whitelisted extension), and that the
  first-matching-root break is intact.
- A cross-request cache was deliberately NOT added (invalidation risk); revisit only if
  profiling shows the single walk is still the dominant cost.
