# Plan 022: `CallerGraphService.GetCallers` resolves callers in a single index pass

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. Touch only in-scope files.
> If a STOP condition occurs, stop and report — do not improvise. Commit in the
> worktree per the Git workflow section. SKIP updating `plans/README.md` — the
> reviewer maintains the index.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/CallerGraphService.cs`
> If it changed since this plan was written, compare the excerpts below to the live
> code before proceeding; on a mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S-M
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`genexus_analyze` impact analysis is the primary blast-radius tool. Its BFS
(`GetCallersTransitive`) calls `GetCallers(name)` once per visited node (up to
`maxNodes`, default 200). `GetCallers` currently walks `idx.Objects.Values` **four
times** per call — including one loop that does literally nothing — so a single
impact analysis pays `O(4 × visitedNodes × indexSize)`. On a KB with tens of
thousands of objects this turns a should-be-milliseconds BFS into multiple seconds,
on every call. Collapsing to a single pass is behavior-preserving and removes ~75%
of the scan work.

## Current state

- `src/GxMcp.Worker/Services/CallerGraphService.cs` — index-backed caller/callee
  graph. `GetCallers` (lines 44–88) is the hot method.

The method as it exists today (lines 44–88):

```csharp
public List<string> GetCallers(string targetName)
{
    if (string.IsNullOrEmpty(targetName) || _index == null) return new List<string>();
    var idx = _index.GetIndex();
    if (idx == null) return new List<string>();

    var callers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Fast path: inverted index already populated by IndexCacheService.
    foreach (var e in idx.Objects.Values)                       // PASS 1 — dead no-op
    {
        if (e == null || string.Equals(e.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
        if (e.CalledBy != null && e.Name != null)
        {
            // (no-op here; CalledBy is on the target, not the caller)
        }
    }

    if (idx.Objects.Values.Any(v => v != null && string.Equals(v.Name, targetName, StringComparison.OrdinalIgnoreCase)))  // PASS 2
    {
        var target = idx.Objects.Values.FirstOrDefault(v => v != null && string.Equals(v.Name, targetName, StringComparison.OrdinalIgnoreCase));  // PASS 3
        if (target != null && target.CalledBy != null)
        {
            foreach (var c in target.CalledBy) callers.Add(c);
        }
    }

    // Slow path / augmentation: regex over SourceSnippet.
    var pattern = new Regex(@"\b" + Regex.Escape(targetName) + @"\s*\(", RegexOptions.IgnoreCase);
    foreach (var e in idx.Objects.Values)                       // PASS 4
    {
        if (e == null) continue;
        if (string.Equals(e.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.IsNullOrEmpty(e.SourceSnippet) && pattern.IsMatch(e.SourceSnippet))
            callers.Add(e.Name);

        if (e.Calls != null && e.Calls.Any(c => string.Equals(c, targetName, StringComparison.OrdinalIgnoreCase)))
            callers.Add(e.Name);
    }

    return callers.ToList();
}
```

Conventions: this codebase favors plain `foreach` over LINQ in hot loops, uses
`StringComparison.OrdinalIgnoreCase` for name comparison, and returns
`new List<string>()` (never null). Match that.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Set GX path (once per shell) | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CallerGraph"` | all pass |

If the build fails with `MSB3027`/`MSB3021` naming `GxMcp.Worker.exe` locked, run
`Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` then rebuild.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/CallerGraphService.cs` (method `GetCallers` only)
- `src/GxMcp.Worker.Tests/` (add/extend a test file — see Test plan)

**Out of scope** (do NOT touch):
- `GetCallees`, `GetBcVariantTargets`, `GetCallersTransitive`, `GetCalleesTransitive`
  — leave exactly as-is.
- `AnalyzeService.cs` and its `TryFindByBareName` — a separate concern, not this plan.
- The regex augmentation *semantics*: the fallback regex/`Calls` scan must STILL run
  on every call (it catches callers the SDK reference walker missed). This plan only
  removes the redundant passes, it does NOT gate the regex behind fast-path success.

## Git workflow

- Branch: `advisor/022-callergraph-single-pass`
- One commit; conventional-commits style, e.g.
  `perf(analyze): collapse GetCallers to a single index pass`
- Do NOT push or open a PR.

## Steps

### Step 1: Rewrite `GetCallers` as a single pass

Replace the body from the first `var callers = ...` through the end so it walks
`idx.Objects.Values` exactly once. In that single loop, for each entry `e`:
- if `e.Name` equals `targetName` (case-insensitive): if `e.CalledBy != null`, add
  every name in `e.CalledBy` to `callers`, then `continue` (a target is not its own
  caller);
- otherwise, apply the two augmentation checks that PASS 4 did: regex match on
  `e.SourceSnippet`, and the `e.Calls` contains-target check.

Compile the `Regex` once, before the loop (as today). The resulting method must
produce the identical `callers` set as the four-pass version for any index — the
target's `CalledBy` plus every entry whose snippet matches or whose `Calls` lists the
target. Delete PASS 1, PASS 2, PASS 3 entirely.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Add regression tests

See Test plan. Write them, then:

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CallerGraph"` → all pass.

## Test plan

Find an existing test that constructs a `SearchIndex` / `IndexCacheService` with
in-memory entries (search the test project for `LoadFromEntries` or
`CallerGraphService`). Model new tests on it. Cover:
- **CalledBy path**: target entry has `CalledBy = ["A","B"]`, no snippets → returns
  {A,B}.
- **Regex-only path**: entries have no `CalledBy`/`Calls` populated but a caller's
  `SourceSnippet` contains `Target(` → that caller is returned (proves the
  augmentation still runs — this is the behavior the single-pass rewrite must keep).
- **Forward-Calls path**: an entry with `Calls = ["Target"]` is returned as a caller.
- **Union**: an index mixing all three yields the union with no duplicates.

If no `CallerGraphService` test file exists, create
`src/GxMcp.Worker.Tests/CallerGraphSinglePassTests.cs`.

## Done criteria

ALL must hold:
- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0
- [ ] The three-plus-one redundant passes are gone: `grep -n "no-op here" src/GxMcp.Worker/Services/CallerGraphService.cs` returns nothing, and `GetCallers` contains exactly one `foreach (... in idx.Objects.Values)`.
- [ ] New tests exist and pass (`--filter "FullyQualifiedName~CallerGraph"`)
- [ ] `git status` shows only in-scope files modified

## STOP conditions

- The live `GetCallers` doesn't match the excerpt above (drift).
- You cannot construct a `SearchIndex` in a test without live SDK/KB access — report
  what the existing tests do instead of inventing a harness.
- Making the single pass identical to the four-pass output requires changing the
  augmentation semantics — STOP; that means the plan's assumption is wrong.

## Maintenance notes

- If a future change adds a genuine "index has complete edges" flag, the regex
  augmentation *could* then be gated behind it — but that's a separate, higher-risk
  change deliberately excluded here.
- Reviewer: confirm the regex/`Calls` augmentation still runs unconditionally (the
  point of this plan is speed, not dropping the fallback).
