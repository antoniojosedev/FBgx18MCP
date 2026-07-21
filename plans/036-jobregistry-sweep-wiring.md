# Plan 036: Wire `BackgroundJobRegistry.SweepExpired()` into the gateway cleanup loop

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Gateway/BackgroundJobRegistry.cs src/GxMcp.Gateway/Program.Notifications.cs`
> Mismatch vs the excerpts = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`BackgroundJobRegistry.SweepExpired()` exists to prune completed jobs older than
`_retentionSeconds` (600s), but it has **no production caller** — only a unit test calls
it. Every background job ever started (async build/rebuild/validate, async edit, async
gxserver update/commit) therefore stays in `_jobs` for the entire life of the gateway
process, including its full `Result` payload. `_seenBySession` (keyed by session, and
stdio sessions share the constant id `"stdio"`) accumulates one id per completed job
forever with no eviction path at all. On a long-lived gateway (hours/days, one per
IDE/agent session) this is unbounded heap growth proportional to total async-tool-call
volume. The fix is to call the existing sweep from the existing once-a-minute
maintenance loop, and add a companion prune for `_seenBySession`.

## Current state

- `src/GxMcp.Gateway/BackgroundJobRegistry.cs`:
  - `SweepExpired()` (line 158) removes `_jobs` entries whose `CompletedAt < now -
    _retentionSeconds`. Correct and unit-tested; just never called in prod.
  - `_seenBySession` is a `ConcurrentDictionary<string, HashSet<string>>` (line 16);
    `MarkSeen`/`SnapshotForSession` add ids but nothing ever removes them.
- `src/GxMcp.Gateway/Program.Notifications.cs`, `RunSessionCleanupLoop` (lines 37–61):
  a `PeriodicTimer` firing every minute; already calls `_httpSessions.CleanupExpired()`,
  `_operationTracker.CleanupExpired()`, `CleanupStalePendingRequests()`. This is where
  the job sweep belongs.
- `JobRegistry` is the process-lifetime static instance (`Program.cs:81`,
  `new BackgroundJobRegistry(600)`), reachable as `JobRegistry` from the `Program`
  partial classes.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~BackgroundJob"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Gateway/BackgroundJobRegistry.cs` — add a `PruneSeenBySession()` method
  (and optionally have `SweepExpired` return the count it removed, if convenient).
- `src/GxMcp.Gateway/Program.Notifications.cs` — call both from `RunSessionCleanupLoop`.
- `src/GxMcp.Gateway.Tests/` — extend `BackgroundJobRegistryTests.cs`.

**Out of scope:** the `Complete`/`Cancel` locking (done in plan 026), the retention
constant, `SaveTo`/`LoadFrom`.

## Git workflow

- Branch: `advisor/036-jobregistry-sweep-wiring`
- One commit: `fix(gateway): sweep expired background jobs + prune seen-set in cleanup loop`
- Do NOT push.

## Steps

### Step 1: Add `PruneSeenBySession()` to `BackgroundJobRegistry`

Add a method that, for each session's seen-set, removes ids no longer present in
`_jobs`. Guard each `HashSet` with the same `lock (seen)` pattern `SnapshotForSession`/
`MarkSeen` already use. Optionally remove a session's entry entirely if its set becomes
empty. Keep it allocation-light.

```csharp
public void PruneSeenBySession()
{
    foreach (var kvp in _seenBySession)
    {
        var seen = kvp.Value;
        lock (seen)
        {
            seen.RemoveWhere(id => !_jobs.ContainsKey(id));
        }
    }
}
```

### Step 2: Call both from the cleanup loop

In `Program.Notifications.cs` `RunSessionCleanupLoop`, inside the `while` tick (next to
the existing cleanups), add `JobRegistry.SweepExpired();` then `JobRegistry.PruneSeenBySession();`.
Order matters: sweep `_jobs` first, then prune the seen-set against the now-reduced
`_jobs`. Optionally log a count if you make `SweepExpired` return one (match the style
of the adjacent `Removed N ...` logs); not required.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 3: Test

Extend `BackgroundJobRegistryTests.cs`:
- **Sweep**: start a job, mark it completed with a `CompletedAt` in the past (use the
  existing test's mechanism — the current `SweepExpired` test at line ~76 shows how),
  call `SweepExpired`, assert `Count == 0`.
- **Prune seen-set**: mark a completed job seen for a session, `SweepExpired` it out of
  `_jobs`, call `PruneSeenBySession`, then assert the pruned id is gone (e.g. a
  subsequent `SnapshotForSession` for a re-added job with the same id would surface it,
  or expose a minimal `internal` accessor only if `InternalsVisibleTo` is already set
  up — if not, assert via observable behavior instead).

**Verify**: `dotnet test ...GxMcp.Gateway.Tests... --filter "FullyQualifiedName~BackgroundJob"` → all pass.

## Done criteria

- [ ] Gateway build exits 0
- [ ] `RunSessionCleanupLoop` calls `JobRegistry.SweepExpired()` and `PruneSeenBySession()`
- [ ] `PruneSeenBySession` removes seen-ids absent from `_jobs`, guarded by `lock (seen)`
- [ ] New tests pass; existing `BackgroundJobRegistry` tests still pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `RunSessionCleanupLoop` already sweeps jobs (drift).
- `JobRegistry` isn't reachable from `Program.Notifications.cs` under that name — locate
  the correct static accessor (grep `BackgroundJobRegistry` in `Program.cs`) rather than
  guessing; if it's not a static reachable from the partial, STOP and report.

## Maintenance notes

- Reviewer: confirm sweep runs before prune, and `PruneSeenBySession` locks each set.
- The retention window (600s) is unchanged; this plan only makes it actually enforced.
