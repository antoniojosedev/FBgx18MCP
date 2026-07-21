# Plan 031: `worker_reload mode=hard` keeps the drain window closed to concurrent spawns

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`. This touches core worker-pool synchronization — read STOP
> conditions.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Gateway/WorkerPool.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`DrainAndReplaceAsync` (the `worker_reload mode=hard` path) removes the pool entry
(`_entries.TryRemove`) **before** running the binary-swap hook (`CopyWorkerBinaries`,
a multi-second blocking file copy) and re-spawning. During that window `_entries` has
no entry for the alias, so a concurrent MCP request on the same KB (both HTTP and
stdio dispatch each request via `Task.Run`, so this is genuinely concurrent) calls
`AcquireAsync`, `GetOrAdd`s a fresh entry that is **not** marked `Draining`, and spawns
a worker using the **old, not-yet-swapped** binary. When `DrainAndReplaceAsync`'s own
`AcquireAsync` then runs, `GetOrAdd` returns that already-spawned entry and hands it
back as "the reloaded worker" — reporting `swappedAndReady: true` while the running
process is the pre-swap binary. The entire point of the drain window is silently
defeated. Keep the entry present (with `Draining=true`) across the whole operation so
concurrent `AcquireAsync` callers take the existing `Draining` wait-path instead of
creating a fresh entry.

## Current state

- `src/GxMcp.Gateway/WorkerPool.cs`, `DrainAndReplaceAsync` (lines 227–274):

```csharp
if (!_entries.TryGetValue(handle.NormalizedAlias, out var entry))
    throw new InvalidOperationException($"No pool entry for alias '{handle.Alias}'.");

entry.Draining = true;                         // <-- concurrent AcquireAsync should wait on this

WorkerProcess? oldWorker = entry.Worker;
if (oldWorker != null) { oldWorker.StopWithReason(...); await oldWorker.WaitForExitAsync(...); }

_entries.TryRemove(handle.NormalizedAlias, out _);   // <-- BUG: removes the entry, so the
                                                     //     Draining flag no longer protects the window

if (afterDrainBeforeSpawn != null)                   // multi-second binary copy runs here
{ try { await afterDrainBeforeSpawn(oldWorker)...; } catch (...) {...} }

try { var newWorker = await AcquireAsync(handle, ct)...; return newWorker; }   // GetOrAdd may
finally { entry.DrainComplete.TrySetResult(true); }                            // return a
                                                                               // concurrently-spawned entry
```

You need to understand how `AcquireAsync` uses `Entry`, `Draining`, and
`DrainComplete` — read the whole method and the `Entry` class in this file, and how
`AcquireAsync` waits when `Draining` is set, before changing anything.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerPool"` | all pass |
| Drain/reload tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~Drain|FullyQualifiedName~Reload"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Gateway/WorkerPool.cs` — `DrainAndReplaceAsync` and, if strictly
  required, the `AcquireAsync` wait-path (only to make it keep waiting on the existing
  `Draining` entry rather than the removed-entry assumption).
- `src/GxMcp.Gateway.Tests/` — a concurrency regression test.

**Out of scope:**
- `Close` / `DropLiveEntry` (lines 276–299) — different lifecycle; leave them.
- `SuppressEagerRespawn` and the eager-respawn-on-exit handler — leave as-is.
- `CopyWorkerBinaries` / `Program.WorkerLifecycle.cs` — the hook body is fine; the bug
  is the entry-removal ordering in the pool.

## Git workflow

- Branch: `advisor/031-worker-reload-drain-window`
- One commit: `fix(gateway): keep pool entry Draining across worker_reload binary swap`
- Do NOT push.

## Steps

### Step 1: Understand the current wait-path

Read `AcquireAsync` fully. Determine exactly how a caller behaves when it finds an
existing entry with `Draining == true` (it should await `entry.DrainComplete`). Confirm
that today `DrainAndReplaceAsync` relies on removing the entry so its OWN `AcquireAsync`
spawns fresh. That reliance is what must change.

### Step 2: Don't remove the entry mid-drain; swap the worker on the existing entry

Restructure `DrainAndReplaceAsync` so the entry stays in `_entries` with
`Draining = true` for the entire duration (drain → hook → spawn). Instead of
`TryRemove` + `AcquireAsync` (which round-trips through `GetOrAdd`), spawn the
replacement worker directly and assign it onto the existing `entry.Worker`, then clear
`Draining` and signal `DrainComplete`. Concrete shape:
- Remove the `_entries.TryRemove(handle.NormalizedAlias, out _)` line.
- After the hook, spawn a fresh worker the same way `AcquireAsync` spawns one
  (extract/reuse the spawn logic — do NOT call `AcquireAsync` for the same alias while
  the entry is still present, or `GetOrAdd` will just return the draining entry with a
  null worker and you'll deadlock/misreport). If `AcquireAsync` has an internal
  "spawn a worker process" helper, call that; if the spawn logic is inline, factor a
  private `SpawnWorkerAsync(handle, ct)` and use it in both places.
- Assign the new worker to `entry.Worker`, set `entry.Draining = false`, and complete
  `entry.DrainComplete`. Any concurrent `AcquireAsync` that was awaiting
  `DrainComplete` now proceeds and sees the freshly-swapped worker.

Preserve: the drain timeout behavior (don't rethrow on `oldWorker` exit timeout), the
`finally`-signal of `DrainComplete` even on spawn failure, and the eager-respawn
suppression contract.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 3: Concurrency regression test

Create `src/GxMcp.Gateway.Tests/DrainWindowConcurrencyTests.cs`. Model on the existing
drain/reload tests (search for `DrainAndReplaceAsync` in the test project). Cover:
- With a mode=hard drain in progress (an `afterDrainBeforeSpawn` hook that blocks on a
  gate you control), a concurrent `AcquireAsync(handle)` for the same alias does NOT
  create a second entry / spawn against a stale state — it awaits and returns the
  post-swap worker. Assert the pool ends with exactly one entry for the alias and the
  hook ran exactly once before any worker the concurrent caller observes.
- Existing drain-then-spawn happy path still works (single caller).

If the pool can't be exercised without spawning real worker processes, use whatever
fake/stub the existing drain tests use (they must inject a spawn seam — find and reuse
it). If no such seam exists, STOP and report — do not stand up real worker processes in
a unit test.

**Verify**: `dotnet test ...GxMcp.Gateway.Tests... --filter "FullyQualifiedName~Drain|FullyQualifiedName~WorkerPool|FullyQualifiedName~Reload"` → all pass.

## Done criteria

- [ ] Gateway build exits 0
- [ ] `DrainAndReplaceAsync` no longer calls `_entries.TryRemove` for the alias mid-drain
- [ ] The entry stays `Draining=true` from before the hook until the new worker is assigned
- [ ] A concurrent `AcquireAsync` during the drain returns the swapped worker (test proves it), never spawns against the removed-entry window
- [ ] Existing drain/reload/worker-pool tests still pass
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `DrainAndReplaceAsync` no longer removes the entry mid-drain (drift / already
  fixed) — report.
- `AcquireAsync` has no reusable spawn seam and factoring one would ripple beyond
  `WorkerPool.cs` — STOP and report; this fix must stay within `WorkerPool.cs`.
- The existing drain tests depend on the entry being removed mid-drain (i.e. they
  assert `_entries` is empty during the window) — STOP; changing that is a contract
  change needing review.
- Reproducing the concurrent-spawn requires real worker processes — STOP and report
  the test seam you found (or didn't).

## Maintenance notes

- This is core pool synchronization. Reviewer must confirm no new deadlock: the
  concurrent `AcquireAsync` waits on `DrainComplete`, which is always signaled in the
  `finally`, even on spawn failure — trace that path explicitly.
- The prior "Unknown KB after recycle" bug (issue #26 P3) came from erasing the durable
  `_known` record; this plan must not touch `_known`, only `_entries` ordering.
