# Plan 032: `CallerGraphService.GetCallees` drops per-candidate regex compilation

> **Executor instructions**: Follow step by step, run every verification command and
> confirm the expected result before moving on. Touch only in-scope files. On any STOP
> condition, stop and report. Commit in the worktree per Git workflow. SKIP updating
> `plans/README.md` — the reviewer maintains the index.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Worker/Services/CallerGraphService.cs`
> If it changed, compare the excerpt below to the live code; mismatch = STOP.

## Status

- **Priority**: P1
- **Effort**: S-M
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`GetCallees`' fallback (taken whenever the entry's `Calls` list isn't populated —
new/unindexed objects or callsites the SDK walker missed) compiles a **fresh `Regex`
per object in the KB** and matches it against the caller's snippet. `GetCalleesTransitive`
(BFS) calls `GetCallees` once per visited node, and `BuildService` calls it per build
target for `IncludeCallees="direct"`. That's O(nodes × KB-size) regex *compilations*
per call. This is the exact mirror of the `GetCallers` multi-pass problem already fixed
last pass (plan 022) — `GetCallees` was left unfixed.

## Current state

- `src/GxMcp.Worker/Services/CallerGraphService.cs`, `GetCallees` (lines 93–123):

```csharp
public List<string> GetCallees(string objectName)
{
    if (string.IsNullOrEmpty(objectName) || _index == null) return new List<string>();
    var idx = _index.GetIndex();
    if (idx == null) return new List<string>();

    var entry = idx.Objects.Values.FirstOrDefault(
        v => v != null && string.Equals(v.Name, objectName, StringComparison.OrdinalIgnoreCase));
    if (entry == null) return new List<string>();

    var callees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (entry.Calls != null)
    {
        foreach (var c in entry.Calls)
            if (!string.IsNullOrEmpty(c)) callees.Add(c);
    }

    // Fallback for objects whose Calls hasn't been populated yet: scan
    // the snippet for identifiers that match known objects in the index.
    if (callees.Count == 0 && !string.IsNullOrEmpty(entry.SourceSnippet))
    {
        foreach (var other in idx.Objects.Values)
        {
            if (other == null || other == entry || string.IsNullOrEmpty(other.Name)) continue;
            var pat = new Regex(@"\b" + Regex.Escape(other.Name) + @"\s*\(", RegexOptions.IgnoreCase);
            if (pat.IsMatch(entry.SourceSnippet)) callees.Add(other.Name);
        }
    }

    return callees.ToList();
}
```

The `Calls`-populated fast path is fine — only the fallback is the problem.

Conventions: plain `foreach` in hot loops, `StringComparison.OrdinalIgnoreCase`,
never return null. See plan 022's `GetCallers` (already merged) for the same style of
single-pass rewrite.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CallerGraph"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/CallerGraphService.cs` — `GetCallees` only.
- `src/GxMcp.Worker.Tests/` — extend `CallerGraphServiceTests.cs` (exists).

**Out of scope:** `GetCallers`, `GetCallersTransitive`, `GetCalleesTransitive`,
`GetBcVariantTargets` — leave untouched.

## Git workflow

- Branch: `advisor/032-getcallees-single-pass`
- One commit: `perf(analyze): drop per-candidate regex compile in GetCallees fallback`
- Do NOT push.

## Steps

### Step 1: Replace the per-candidate regex fallback with one snippet tokenization

Rewrite ONLY the fallback block (`if (callees.Count == 0 && ...)`). Instead of
compiling a regex per object:
1. Extract every call-site identifier from `entry.SourceSnippet` in a single regex
   pass: `Regex.Matches(entry.SourceSnippet, @"\b(\w+)\s*\(", RegexOptions.IgnoreCase)`,
   collect group[1] values into a `HashSet<string>(StringComparer.OrdinalIgnoreCase)`.
2. Build a set of known object names once (`idx.Objects.Values` → `Name`), or iterate
   `idx.Objects.Values` once and add `other.Name` to `callees` when the extracted-identifier
   set contains it (and `other != entry`).

The result set must be equivalent to the old per-name `\b<name>\s*\(` matching for
identifier callees. Compile zero regexes inside any loop.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Add a regression test

In `CallerGraphServiceTests.cs`, add cases (model on the existing tests there):
- **Calls-populated**: entry with `Calls=["A","B"]` → returns {A,B} (fast path unchanged).
- **Fallback**: entry with empty `Calls` and `SourceSnippet` containing `DoThing(` where
  an object `DoThing` exists in the index → returns {DoThing}; an identifier in the
  snippet with NO matching object is not returned.
- **No false match**: a snippet mentioning `Foo` without `(` does not return `Foo`.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~CallerGraph"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] No `new Regex(` inside any loop in `GetCallees` (`grep -n "new Regex" src/GxMcp.Worker/Services/CallerGraphService.cs` shows the fallback compiles at most one regex, not one per candidate)
- [ ] New tests pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `GetCallees` doesn't match the excerpt (drift).
- The identifier-extraction set can't be made equivalent to the old matching for an
  existing test — STOP and report.

## Maintenance notes

- Reviewer: confirm behavior equivalence (the fallback still finds the same callees),
  and that the `Calls` fast path is untouched.
