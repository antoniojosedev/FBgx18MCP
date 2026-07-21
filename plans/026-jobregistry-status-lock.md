# Plan 026: `BackgroundJobRegistry` guards job status transitions with a lock

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Gateway/BackgroundJobRegistry.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`Complete` and `Cancel` mutate the same `JobEntry.Status`/`CompletedAt`/`Summary`
fields with no lock. `Complete` does a non-atomic read-then-write: it checks
`job.Status != "cancelled"` and, several statements later, assigns
`succeeded`/`failed`. `Cancel` runs on a live request thread (`genexus_lifecycle
action=cancel`); `Complete` runs on a separate background poller `Task.Run` when the
worker's build/edit finishes. If `Cancel` lands between `Complete`'s status check and
its assignment, `Complete` overwrites `"cancelled"` back to `"succeeded"`/`"failed"` —
the exact clobber the comment on lines 83–85 says it prevents, but the guard isn't
atomic. The client that requested cancellation then sees the job resurrect as
completed. `OperationTracker.OperationRecord` already carries a `SyncRoot` for exactly
this pattern; mirror it.

## Current state

- `src/GxMcp.Gateway/BackgroundJobRegistry.cs`. `JobEntry` (lines 231–251) is a plain
  mutable class with no lock object.

```csharp
public void Complete(string jobId, bool success, string? summary, JObject? result = null)
{
    if (!_jobs.TryGetValue(jobId, out var job)) return;
    if (!string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
        job.Status = success ? "succeeded" : "failed";
    job.CompletedAt = DateTime.UtcNow;
    if (job.Summary == null) job.Summary = summary;
    if (job.Result == null) job.Result = result;
    if (success && job.Kind != null && job.Kind.IndexOf("build", ...) >= 0)
    {
        int elapsed = ...;
        RecordBuildDuration(job.Kind, elapsed);
    }
    DisposeCts(jobId);
}

public bool Cancel(string jobId, string? reason = null)
{
    if (!_jobs.TryGetValue(jobId, out var job)) return false;
    if (_cts.TryGetValue(jobId, out var cts)) { try { cts.Cancel(); } catch { } }
    job.Status = "cancelled";
    job.CompletedAt = DateTime.UtcNow;
    job.Summary = reason ?? "Cancelled by client";
    return true;
}
```

`_jobs` is a `ConcurrentDictionary` (lookups are safe); the unguarded part is the
read-modify-write of the shared `JobEntry` object.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~BackgroundJob"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Gateway/BackgroundJobRegistry.cs` — add a lock object to `JobEntry` and
  guard the status/summary/result/completedAt writes in `Complete` and `Cancel` (and
  any other `JobEntry` field-mutator, if present).
- `src/GxMcp.Gateway.Tests/` — a race/regression test.

**Out of scope:**
- The `_durationLock`-guarded duration methods — already correctly locked.
- Serialization (`SaveTo`/`LoadFrom`): the new lock field must not be serialized — add
  `[JsonIgnore]` so `JsonConvert` skips it (JobEntry is round-tripped to `jobs.json`).
- `_cts` handling / `DisposeCts` — leave as-is; `cts.Cancel()` can stay outside the
  per-entry lock (it's on the concurrent `_cts` dictionary).

## Git workflow

- Branch: `advisor/026-jobregistry-status-lock`
- One commit: `fix(gateway): guard JobEntry status transitions with a per-job lock`
- Do NOT push.

## Steps

### Step 1: Add a `SyncRoot` to `JobEntry`

Add to `JobEntry`:

```csharp
[Newtonsoft.Json.JsonIgnore]
public readonly object SyncRoot = new object();
```

(Confirm `Newtonsoft.Json` is already used in this file — it is.)

### Step 2: Guard `Complete` and `Cancel`

Wrap the field read-modify-write in each method in `lock (job.SyncRoot) { ... }`:
- In `Complete`: the whole block from the `if (!string.Equals(job.Status,
  "cancelled"...))` check through `if (job.Result == null) job.Result = result;`
  (the elapsed-duration `RecordBuildDuration` call and `DisposeCts` can stay outside
  the lock — `RecordBuildDuration` takes its own `_durationLock`, and calling it under
  `job.SyncRoot` too would be a nested lock; keep it outside). Read `job.Status`,
  `job.CompletedAt`, `job.StartedAt` you need for the elapsed calc *inside* the lock
  into locals, then call `RecordBuildDuration` outside.
- In `Cancel`: wrap the three field writes (`Status`/`CompletedAt`/`Summary`) in
  `lock (job.SyncRoot)`. The `cts.Cancel()` stays before/outside the lock.

The invariant to preserve: once `Status == "cancelled"`, `Complete` must never
overwrite it. Keep `Complete`'s existing `if (!string.Equals(... "cancelled"))` guard
— now it's atomic because both sides take `job.SyncRoot`.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 3: Test

Create `src/GxMcp.Gateway.Tests/JobStatusRaceTests.cs`. Cover:
- **Ordering invariant**: `Start` a job, call `Cancel(id)`, then `Complete(id,
  success:true, ...)`. Assert `Get(id).Status == "cancelled"` (Complete must not
  clobber). This is deterministic and proves the guard.
- Optional stress: spawn many `(Cancel || Complete)` pairs across `Task`s on distinct
  job ids and assert no exception and every job ends in a terminal status. Keep it
  bounded (e.g. 200 jobs) so it's fast and not flaky.

Model on an existing gateway test that news up `BackgroundJobRegistry` (search the
test project for `new BackgroundJobRegistry`).

**Verify**: `dotnet test ...GxMcp.Gateway.Tests... --filter "FullyQualifiedName~JobStatusRace"` → all pass.

### Step 4: Confirm serialization still round-trips

The `SaveTo`/`LoadFrom` path serializes `JobEntry`. Run:

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~BackgroundJob"` → all pass (any existing save/load test must still pass with the `[JsonIgnore]` field present).

## Done criteria

- [ ] Gateway build exits 0
- [ ] `Cancel` then `Complete` leaves `Status == "cancelled"` (test proves it)
- [ ] `JobEntry.SyncRoot` is `[JsonIgnore]` and existing save/load tests pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live methods already lock a per-entry object (drift) — report.
- Adding the lock field breaks an existing serialization test in a way the
  `[JsonIgnore]` attribute doesn't resolve — STOP and report.

## Maintenance notes

- Any future `JobEntry` mutator must also take `job.SyncRoot`.
- Reviewer: check that `RecordBuildDuration` is not called while holding `job.SyncRoot`
  (it takes `_durationLock` — nesting two locks in one order here is fine, but keeping
  them un-nested is cleaner and is what this plan specifies).
