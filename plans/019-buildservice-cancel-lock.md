# Plan 019: `BuildService.Cancel()` mutates task status under `status._lock`

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4885c1c..HEAD -- src/GxMcp.Worker/Services/BuildService.cs`
> If it changed since this plan was written, compare the "Current state"
> excerpts against the live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `4885c1c`, 2026-07-21

## Why this matters

`BuildService.Cancel(taskId)` writes the task to a terminal state
(`Status="Cancelled"`, `Phase="Done"`, `EndTime`) so the next status poll sees a
frozen, cancelled build — that's the stated intent of the "R9" comment. But it
writes those fields **without** taking `status._lock`, while the build's output
parser (`HandleLine`, running on a thread-pool thread from
`Process.OutputDataReceived`) is still processing buffered lines and mutating the
same fields **under** `status._lock`. MSBuild keeps emitting buffered output for
a window after cancel, so a late line can flip `Phase` back from `"Done"` to
`"Compiling"`/`"Generating"`, un-freezing a build the caller was told is
cancelled. `Cancel()` is the sole outlier — its sibling `CancelAllRunning()` does
exactly the same mutations correctly under the lock.

## Current state

- `src/GxMcp.Worker/Services/BuildService.cs` — the worker's build runner. Each
  task's mutable state is a `BuildTaskStatus` with a `_lock` field guarding its
  fields.

The bug — `Cancel()` writes the three fields with **no** lock:

```csharp
// BuildService.cs:1419-1443
try
{
    var p = status.Process;
    if (p != null && !p.HasExited)
    {
        // R9: write the cancelled state BEFORE we go kill children so
        // the next status poll sees "Cancelled" immediately ...
        status.Status = "Cancelled";
        status.Phase = "Done";
        EmitPhaseProgress(status.Phase);
        status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        try { status.StateChangeSignal.Set(); } catch { }
        // ... fire-and-forget KillProcessTree(p) ...
        System.Threading.Tasks.Task.Run(() =>
        {
            try { KillProcessTree(p); }
            catch (Exception ex) { Logger.Warn("[KILL-TREE-BG] " + ex.Message); }
        });
        return JsonConvert.SerializeObject(new { status = "Cancelled", taskId = taskId });
    }
    return JsonConvert.SerializeObject(new { status = status.Status, message = "Task already finished" });
}
```

The concurrent writer holds the lock for the same fields:

```csharp
// BuildService.cs:1895-1899
private void HandleLine(BuildTaskStatus status, string line, bool isError)
{
    if (line == null) return;
    lock (status._lock)
    {
        ... mutates status.Phase, status.ErrorCount, status.TailLines, etc. ...
```

The correct sibling to mirror — `CancelAllRunning()` reads `Process` and writes
the three fields **inside** `lock (status._lock)`, keeping only the
fire-and-forget `KillProcessTree` outside:

```csharp
// BuildService.cs:1460-1490
public int CancelAllRunning()
{
    int cancelled = 0;
    foreach (var status in _tasks.Values.ToArray())
    {
        try
        {
            Process p;
            lock (status._lock)
            {
                if (!string.Equals(status.Status, "Running", StringComparison.OrdinalIgnoreCase))
                    continue;
                p = status.Process;
                if (p == null || p.HasExited) continue;
                status.Status = "Cancelled";
                status.Phase = "Done";
                status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                try { status.StateChangeSignal.Set(); } catch { }
            }
            var pt = p;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { KillProcessTree(pt); }
                catch (Exception ex) { Logger.Warn("[KILL-TREE-BG] CancelAllRunning: " + ex.Message); }
            });
            cancelled++;
        }
        catch (Exception ex) { Logger.Warn("[CancelAllRunning] " + ex.Message); }
    }
    return cancelled;
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
- `src/GxMcp.Worker/Services/BuildService.cs` — the `Cancel(taskId)` method only.

**Out of scope**:
- `CancelAllRunning`, `HandleLine`, `GetStatus`, `GetStatusWait`, `GetResult` —
  already correct.
- The `KillProcessTree` call and its fire-and-forget `Task.Run` — must stay
  **outside** the lock (killing a process tree can block; holding `status._lock`
  across it would stall status polls).

## Git workflow

- Conventional Commits (e.g. `fix(build): ...`). No `Co-Authored-By` trailer.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Wrap the status mutations in `Cancel()` under `status._lock`

Restructure the `try` block so the `Process` read and the three field writes
happen inside `lock (status._lock)`, and the fire-and-forget `KillProcessTree`
stays outside — mirroring `CancelAllRunning`. Target shape:

```csharp
try
{
    Process p;
    lock (status._lock)
    {
        p = status.Process;
        if (p == null || p.HasExited)
            return JsonConvert.SerializeObject(new { status = status.Status, message = "Task already finished" });

        // R9: write the cancelled state under the lock so a late HandleLine
        // (still draining buffered MSBuild output) can't flip Phase back off
        // "Done" after we've reported the task cancelled.
        status.Status = "Cancelled";
        status.Phase = "Done";
        status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        try { status.StateChangeSignal.Set(); } catch { }
    }

    EmitPhaseProgress("Done");

    var pt = p;
    System.Threading.Tasks.Task.Run(() =>
    {
        try { KillProcessTree(pt); }
        catch (Exception ex) { Logger.Warn("[KILL-TREE-BG] " + ex.Message); }
    });
    return JsonConvert.SerializeObject(new { status = "Cancelled", taskId = taskId });
}
catch (Exception ex)
{
    return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
}
```

Note two deliberate details:
- `EmitPhaseProgress` is moved **outside** the lock (it was inside the unlocked
  block before; keep it off the lock so it can't introduce lock-ordering issues —
  it only emits a progress notification, it does not need to be atomic with the
  field writes). If `EmitPhaseProgress` reads/writes `status` fields, verify by
  reading its body; if it does touch `status`, keep the call but confirm it takes
  its own `status._lock` internally (grep it) — if it does NOT and it mutates
  `status`, move it back inside the lock instead.
- The "already finished" early-return is now inside the lock (reads `status.Status`
  consistently). This preserves the original two return shapes.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Confirm `EmitPhaseProgress` placement is safe

```
grep -n "void EmitPhaseProgress" src/GxMcp.Worker/Services/BuildService.cs
```

Read the method. If it does not mutate `status` fields (only emits a
notification), leaving it outside the lock (as in Step 1) is correct. If it
mutates `status`, move the call back inside the `lock` block. Record which case
applied in your report.

**Verify**: build still exits 0.

### Step 3: Full worker suite green

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass.

## Test plan

- The race is timing-dependent and not deterministically unit-testable without a
  live MSBuild process, so **do not** attempt to write a flaky timing test.
- Primary verification is the full worker suite staying green (proves no
  behavioral regression in the many existing build tests), plus a code review
  that `Cancel()` now matches `CancelAllRunning`'s locking discipline exactly.
- If an existing build test constructs a `BuildTaskStatus` and calls `Cancel`
  directly, add a non-timing assertion that after `Cancel` on a task with a live
  (fake) process, `Status=="Cancelled"` and `Phase=="Done"` — but only if such a
  seam already exists. Do not build a new harness.

## Done criteria

Machine-checkable. ALL must hold:

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] The three field writes (`status.Status = "Cancelled"`, `status.Phase = "Done"`, `status.EndTime = ...`) in `Cancel()` are inside a `lock (status._lock)` block (visual confirm).
- [ ] `KillProcessTree` and its `Task.Run` remain outside the lock.
- [ ] `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` exits 0.
- [ ] No files outside the in-scope list modified (`git status`).
- [ ] `plans/README.md` status row for 019 updated.

## STOP conditions

Stop and report back if:

- `BuildService.cs` no longer matches the "Current state" excerpts.
- `BuildTaskStatus` no longer exposes a `_lock` field, or `CancelAllRunning`'s
  locking pattern has changed (mirror the *current* sibling, not this excerpt).
- The full suite has failures after the change that pass on `git stash`
  (your change caused them) — report the failing test rather than reworking
  blindly.

## Maintenance notes

- Any new terminal-state mutation of `BuildTaskStatus` from outside `HandleLine`
  must also take `status._lock`. The invariant: **every** writer of
  `Status`/`Phase`/`EndTime`/counts holds `status._lock`; only cheap
  fire-and-forget process-killing runs outside it.
- Reviewer should diff `Cancel()` against `CancelAllRunning()` and confirm they
  now use identical locking structure.
