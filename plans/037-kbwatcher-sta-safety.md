# Plan 037: `KbWatcherService` must not touch the SDK from a second STA thread

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`. This is a MED-risk concurrency change — the STOP conditions are
> load-bearing; prefer stopping over a risky improvisation.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Worker/Services/KbWatcherService.cs src/GxMcp.Worker/Services/CommandDispatcher.cs`
> Mismatch vs the excerpts = STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

The worker's core invariant (`CommandDispatcher.cs:392`): "Any operation interacting
with GeneXus SDK (COM objects) MUST run in the STA thread" — the dispatcher's single
STA queue thread. `KbWatcherService` violates it: `Start()` spins its **own** STA thread
(`KbWatcherService.cs:68–75`) and its loop calls SDK members directly
(`kb.DesignModel.Objects.GetKeys/Get`, `GetActiveEnvironment`). The only guard is
`IsWriteInProgress` (a write-only counter, `KbWatcherService.cs:16–34`), so watcher
polling can still interleave with any dispatcher-issued **read/index/analysis** SDK
call on the other STA thread. STA COM objects aren't safe across apartments without
marshaling — and the file's own comment (lines 14–15) documents that concurrent access
here already produced intermittent generic "Erro" messages for the write case they did
guard. The underlying hazard is empirically confirmed; the guard just covers a subset.

## Current state

- `src/GxMcp.Worker/Services/KbWatcherService.cs`:
  - `Start()` (68–78): creates `_watcherThread`, `SetApartmentState(STA)`, runs
    `WatcherLoop`.
  - `WatcherLoop` (85+): `Thread.Sleep`, then per tick calls `CheckForChanges` (~172)
    and `CheckForEnvironmentChange` (~129), both hitting the SDK directly.
  - Guard: `IsWriteInProgress` / `AcquireWriteGate` (16–34) — write transactions only.
- `src/GxMcp.Worker/Services/CommandDispatcher.cs`: owns the single STA command queue
  (the sanctioned SDK thread). Read it to learn HOW work is posted onto that queue
  (the enqueue/dispatch mechanism) — that is the seam this fix should reuse.
- `src/GxMcp.Worker/Program.cs:~284`: instantiates + starts the watcher.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Full worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -v:quiet` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/KbWatcherService.cs` — make its SDK access run on the
  dispatcher's STA thread instead of its own.
- A minimal seam on `CommandDispatcher` (or wherever the STA queue lives) ONLY if
  needed to post a low-priority poll job — additive, no behavior change to command
  dispatch.
- `src/GxMcp.Worker.Tests/` — a test if achievable.

**Out of scope:** the poll cadence semantics (keep ~5s), the change-notification event
shape (`_onObjectChanged`, `OnEnvironmentChanged`), any WriteService interaction.

## Git workflow

- Branch: `advisor/037-kbwatcher-sta-safety`
- One commit: `fix(worker): run KbWatcher SDK polling on the dispatcher STA thread`
- Do NOT push.

## Steps

### Step 1: Understand the dispatcher's STA queue seam

Read `CommandDispatcher.cs` and find how SDK work is enqueued onto the single STA
thread (the mechanism the invariant comment at line 392 refers to). Determine whether
there is a clean way to post an arbitrary `Action`/job onto that queue from outside
(e.g. an existing "run on STA" helper). Write down what you find.

**If there is no clean, minimal seam to post work onto the existing STA queue, STOP and
report** — do not hand-roll a second synchronization scheme (a global lock around all
SDK access risks deadlock/serialization regressions and is out of scope for a safe
auto-applied fix). This plan is only safe to land if the watcher can reuse the existing
STA queue with a small additive seam.

### Step 2: Route watcher ticks onto the STA queue

Keep the watcher's timer/loop thread for *scheduling* (the `Thread.Sleep`/cadence), but
move the actual SDK-touching work (`CheckForChanges`, `CheckForEnvironmentChange`) so it
executes **on the dispatcher STA thread** — post it as a low-priority job and await/poll
its completion before the next tick. The watcher thread itself must no longer call any
`kb.DesignModel.*` / `GetActiveEnvironment` member directly. Preserve the ~5s cadence,
the `_notifiedInLastTick`/`_lastCheckTime` dedup logic, and the two events.

The `IsWriteInProgress` guard may become redundant once all SDK access is serialized on
one thread; you MAY leave it (harmless) or remove it — if removing, confirm no other
file reads it (`grep -rn "IsWriteInProgress\|AcquireWriteGate" src/`). Prefer leaving it
to keep the diff minimal unless removal is trivially safe.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 3: Full worker suite (this is a concurrency change — run all of it)

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -v:quiet` → all pass. If a `KbWatcher` test exists, ensure it still passes; add one only if the seam makes it deterministically testable (don't build a fake SDK).

## Done criteria

- [ ] Build exits 0
- [ ] The watcher thread no longer calls `kb.DesignModel.*` / `GetActiveEnvironment` directly — SDK work runs on the dispatcher STA thread
- [ ] Poll cadence (~5s) and both change events preserved
- [ ] Full worker suite passes
- [ ] `git status` shows only in-scope files

## STOP conditions

- No clean minimal seam to post work onto the existing STA queue (Step 1) — STOP and
  report; a lock-based alternative is explicitly out of scope here.
- The change would require restructuring `CommandDispatcher`'s queue beyond a small
  additive post-job seam — STOP.
- Any full-suite test regresses and the cause isn't obvious/localized — STOP.

## Maintenance notes

- This removes a real cross-apartment COM hazard; the reviewer must confirm the watcher
  no longer touches the SDK off-thread and that dispatch latency for normal commands is
  unaffected (the poll job must be low priority / not starve real commands).
- If Step 1 forces a STOP, this becomes a human-design task — that's an acceptable
  outcome; report what the seam analysis found.
