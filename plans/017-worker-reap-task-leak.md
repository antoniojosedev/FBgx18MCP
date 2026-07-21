# Plan 017: Self-initiated worker reaps cancel `_cts`, so no background tasks leak

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4885c1c..HEAD -- src/GxMcp.Gateway/WorkerProcess.cs`
> If `WorkerProcess.cs` changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `4885c1c`, 2026-07-21

## Why this matters

The gateway is a long-running process. Every time a worker is reaped for being
idle (`IdleTimeout`), for heap recycling (`HeapRecycle`), or for being wedged
(`Wedged`), its `WorkerProcess` instance is discarded — but its two background
tasks (`ProcessQueueAsync` writer loop and `RunHealthCheckAsync` health loop)
are **not** cancelled, because the reap path calls the private `StopProcess`
directly and `StopProcess` never cancels `_cts`. Only `StopWithReason` (used by
gateway shutdown / pool eviction) cancels it.

Result: after each reap, the health loop keeps ticking every 15 s forever
(`await Task.Delay(15000, ct)` with an un-cancelled `ct`), and the writer loop
stays blocked on `_commandChannel.Reader.WaitToReadAsync(_cts.Token)` forever.
Idle-timeout reaping is a normal, recurring event on the default config, so this
is a monotonically growing leak of tasks, timer registrations, and their closed-
over state for the life of the gateway process.

## Current state

- `src/GxMcp.Gateway/WorkerProcess.cs` — the per-KB worker wrapper. Owns `_cts`
  (private, line 38), the writer task `_writerTask` (`ProcessQueueAsync`), and
  the health-check task `_healthCheckTask` (`RunHealthCheckAsync`, started at
  line 852 as `Task.Run(() => RunHealthCheckAsync(_cts.Token))`).

The three reap call sites inside `RunHealthCheckAsync` (they call the private
`StopProcess`, which does **not** cancel `_cts`):

```csharp
// WorkerProcess.cs:155
StopProcess(WorkerStopReason.IdleTimeout);
// WorkerProcess.cs:162
StopProcess(WorkerStopReason.HeapRecycle);
// WorkerProcess.cs:195
StopProcess(WorkerStopReason.Wedged);
```

The health loop (`RunHealthCheckAsync`) exit condition and its only delay:

```csharp
// WorkerProcess.cs:145
while (!ct.IsCancellationRequested)
{
    ...
// WorkerProcess.cs:225
    await Task.Delay(15000, ct);
}
```

The writer loop (`ProcessQueueAsync`):

```csharp
// WorkerProcess.cs:231
while (!_cts.Token.IsCancellationRequested)
{
    ...
    if (await _commandChannel.Reader.WaitToReadAsync(_cts.Token))
    ...
}
```

`_cts.Cancel()` is called in **exactly one place** — `StopWithReason`, the sink
for `Stop()` (gateway shutdown) and pool-driven teardown:

```csharp
// WorkerProcess.cs:870
public void Stop() => StopWithReason(WorkerStopReason.GatewayShutdown);

// WorkerProcess.cs:896
public void StopWithReason(WorkerStopReason reason)
{
    _cts.Cancel();
    StopProcess(reason);
}
```

The private sink every stop path eventually funnels through — note it does NOT
touch `_cts` today:

```csharp
// WorkerProcess.cs:931
private void StopProcess(WorkerStopReason reason)
{
    lock (_processLock)
    {
        _stopReason = reason;
        ... disposes pipes, zeroes counters, kills/disposes _process ...
    }
    // Signal the exit deterministically ...
    FireWorkerExitedOnce(reason);
}
```

**Confirmed:** the only callers of `StopProcess` are the 3 reap sites above and
`StopWithReason` (verified by grep over the file). There is no path where
cancelling `_cts` inside `StopProcess` would be wrong — every `StopProcess` call
is a terminal teardown of that `WorkerProcess`.

### Repo conventions to match

- The test project already has `InternalsVisibleTo GxMcp.Gateway.Tests`
  (`src/GxMcp.Gateway/GxMcp.Gateway.csproj:27`), and existing tests drive
  `WorkerProcess` through `internal` test-seam members named `*ForTest`
  (e.g. `SeedInFlightForTest`, `InFlightStartTimesCountForTest`,
  `CompleteInFlightForTest` — see `src/GxMcp.Gateway.Tests/WorkerWedgedDetectionTests.cs`).
  Add the new seam the same way (an `internal` member with a `ForTest` suffix).
- Tests are xUnit (`[Fact]`, `Assert.*`). Model the new test file's structure on
  `WorkerWedgedDetectionTests.cs`: construct
  `new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake"))`
  (never started, no real process).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0, no errors |
| Test (filter) | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerReapCancellationTests"` | all pass |
| Full gateway suite | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` | all pass |
| Unlock build (only if MSB3027/MSB3021 naming GxMcp.Gateway.exe) | `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` | processes killed |

Run these in PowerShell from the repo root `C:\Projetos\Genexus18MCP`.

## Scope

**In scope** (the only files you may modify):
- `src/GxMcp.Gateway/WorkerProcess.cs`
- `src/GxMcp.Gateway.Tests/WorkerReapCancellationTests.cs` (create)

**Out of scope** (do NOT touch):
- The unexpected-crash path (`Process.Exited` firing without a `StopProcess`
  call). Whether that path also leaks tasks is a *separate* question this plan
  does not resolve — see Maintenance notes. Do not try to fix it here.
- `WorkerPool.cs`, `Program.cs`, or any reaping/threshold logic. This plan only
  makes cancellation happen on the existing reap paths; it changes no timing,
  no thresholds, no reap decisions.

## Git workflow

- Commit style: Conventional Commits (see `git log`, e.g.
  `fix(gateway): ...`). Do **not** add any `Co-Authored-By` trailer.
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Make `StopProcess` cancel `_cts` (root-cause fix)

Move the cancellation into the common sink so every stop path — the three reaps
**and** `StopWithReason` — cancels by construction. Edit `StopProcess` to cancel
`_cts` at the very top, before taking `_processLock`:

```csharp
private void StopProcess(WorkerStopReason reason)
{
    // Every stop path (idle/heap/wedged reap from the health loop, and
    // StopWithReason for gateway shutdown / pool teardown) funnels here.
    // Cancel _cts so the writer loop (ProcessQueueAsync) and health loop
    // (RunHealthCheckAsync) actually exit instead of leaking for the life of
    // the gateway. Idempotent — safe when StopWithReason already cancelled.
    try { _cts.Cancel(); } catch (ObjectDisposedException) { }

    lock (_processLock)
    {
        _stopReason = reason;
        ... (rest unchanged) ...
```

Then simplify `StopWithReason` so it no longer double-cancels (optional but
tidy — `_cts.Cancel()` is idempotent, so leaving it would also be correct):

```csharp
public void StopWithReason(WorkerStopReason reason)
{
    StopProcess(reason);
}
```

Leave the three reap call sites (`StopProcess(WorkerStopReason.IdleTimeout)` etc.)
exactly as they are — they now cancel `_cts` via the sink.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 2: Add an `internal` test seam for the cancellation state

Add near the other `*ForTest` members in `WorkerProcess.cs`:

```csharp
// Test seam: lets a reap-path test assert the background loops were told to stop.
internal bool CancellationRequestedForTest => _cts.IsCancellationRequested;

// Test seam: invoke the private teardown sink directly (no real process needed).
internal void StopProcessForTest(WorkerStopReason reason) => StopProcess(reason);
```

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 3: Write the regression test

Create `src/GxMcp.Gateway.Tests/WorkerReapCancellationTests.cs`:

```csharp
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Plan 017: every WorkerStopReason teardown (idle/heap/wedged reap from the
    // health loop, and gateway-shutdown via StopWithReason) must cancel _cts so
    // the writer + health-check background loops exit instead of leaking.
    public class WorkerReapCancellationTests
    {
        private static WorkerProcess NewWorker() =>
            new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake"));

        [Theory]
        [InlineData(WorkerStopReason.IdleTimeout)]
        [InlineData(WorkerStopReason.HeapRecycle)]
        [InlineData(WorkerStopReason.Wedged)]
        public void StopProcess_Reap_CancelsCts(WorkerStopReason reason)
        {
            var worker = NewWorker();
            Assert.False(worker.CancellationRequestedForTest);

            worker.StopProcessForTest(reason);

            Assert.True(worker.CancellationRequestedForTest);
        }

        [Fact]
        public void StopWithReason_Shutdown_CancelsCts()
        {
            var worker = NewWorker();

            worker.StopWithReason(WorkerStopReason.GatewayShutdown);

            Assert.True(worker.CancellationRequestedForTest);
        }
    }
}
```

If `WorkerStopReason` does not contain one of the three member names above, STOP
(the enum drifted — see STOP conditions).

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerReapCancellationTests"` → all pass (4 test cases).

### Step 4: Confirm the full gateway suite still passes

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` → all pass.

## Test plan

- New file `src/GxMcp.Gateway.Tests/WorkerReapCancellationTests.cs`, modeled on
  `WorkerWedgedDetectionTests.cs`.
- Cases: the three reap reasons each cancel `_cts` (the bug), plus the shutdown
  path stays correct (regression guard so a future refactor can't silently drop
  cancellation from `StopWithReason`).
- Verification: the two `dotnet test` commands above, all green.

## Done criteria

Machine-checkable. ALL must hold:

- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` exits 0; the 4 new `WorkerReapCancellationTests` cases pass.
- [ ] `grep -n "_cts.Cancel" src/GxMcp.Gateway/WorkerProcess.cs` shows the cancel inside `StopProcess` (not only in `StopWithReason`).
- [ ] No files outside the in-scope list are modified (`git status`).
- [ ] `plans/README.md` status row for 017 updated to DONE.

## STOP conditions

Stop and report back (do not improvise) if:

- `WorkerProcess.cs` no longer matches the "Current state" excerpts (drifted).
- `WorkerStopReason` does not have `IdleTimeout`, `HeapRecycle`, and `Wedged`
  members.
- The full gateway suite has pre-existing failures unrelated to this change
  (note them; do not try to fix them here).
- Adding `_cts.Cancel()` to `StopProcess` breaks any existing test — that would
  mean some code path relies on `StopProcess` NOT cancelling; report it rather
  than working around it.

## Maintenance notes

- If a future change adds a new `WorkerStopReason` or a new teardown entry point,
  it should still funnel through `StopProcess` so cancellation stays automatic.
- **Deferred, out of scope, needs its own investigation:** the pure unexpected-
  crash path (worker process dies, `Process.Exited` fires, the pool drops the
  entry) may also leave `_cts` un-cancelled if it doesn't route through
  `StopProcess`. This plan does not address it because it wasn't confirmed to
  leak. A follow-up should trace the `OnWorkerExited`/pool-drop path and decide
  whether the discarded instance's `_cts` is cancelled there too.
- Reviewer should scrutinize: that no code depended on the old behavior where
  `StopProcess` left the loops running (there is none in the current tree, but
  the full-suite green run is the proof).
