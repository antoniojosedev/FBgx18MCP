# Plan 012: MSBuild "guaranteed reap" cleanup operates on a live PID, not a disposed Process

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report — do not improvise. When done, update this
> plan's status row in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 9fe6817..HEAD -- src/GxMcp.Worker/Services/BuildService.cs`
> If `BuildService.cs` changed since this plan was written, compare the "Current
> state" excerpt against the live code before proceeding; on a mismatch, treat it
> as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `9fe6817`, 2026-07-20

## Why this matters

Commit `92a5d28` ("feat(build): guaranteed MSBuild reap on every build completion")
added a `finally` block meant to kill a lingering MSBuild process tree on *any*
build exit path. But it reads `status.Process` — the same `Process` instance that
the enclosing `using` block has **already disposed** by the time `finally` runs.
Accessing `p.HasExited` on a disposed `Process` throws ("No process is associated
with this object."), which the surrounding `catch` swallows and logs as a spurious
`[BUILD-CLEANUP]` warning. Net effect: the safety net never fires, and it logs a
misleading warning on essentially every build that reaches the `msbuild-exe`
fallback path. The commit's stated goal — a hung child can never linger — is not
achieved for this code path.

## Current state

- `src/GxMcp.Worker/Services/BuildService.cs` — background build runner. The
  relevant method spawns MSBuild in a `using (var process = ...)` block
  (line ~1767), closes the block (disposing `process`) at line ~1835, then the
  outer `finally` (lines ~1842–1861) tries to reap via `status.Process`.

Excerpt as it exists today (`BuildService.cs:1767-1861`):

```csharp
using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
{
    status.Process = process;
    ...
    process.Start();
    ...
    if (!process.WaitForExit(timeoutSec * 1000))
    {
        ...
        KillProcessTree(process);
        try { process.WaitForExit(5000); } catch { }
    }
    ...
}                       // <-- process.Dispose() runs HERE (line ~1835)
...
finally
{
    try { watchdog?.Dispose(); } catch { }
    // Guaranteed MSBuild cleanup ...
    try
    {
        var p = status.Process;                          // disposed instance
        if (p != null && !p.HasExited) KillProcessTree(p);   // HasExited throws
    }
    catch (Exception ex) { Logger.Warn("[BUILD-CLEANUP] MSBuild reap: " + ex.Message); }
    try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
    status.Process = null;
    Helpers.Logger.CurrentPhase = null;
}
```

- `KillProcessTree(Process)` already exists in this file (used at line ~1785 on the
  timeout path). Find its exact signature with
  `grep -n "void KillProcessTree" src/GxMcp.Worker/Services/BuildService.cs`.
- Convention: this file is defensive — every cleanup step is wrapped in
  `try { } catch { }`. Match that. Logging uses `Logger.Warn/Info/Error`.

## Commands you will need

| Purpose   | Command | Expected |
|-----------|---------|----------|
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` (with `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'`) | Build succeeded, 0 errors |
| Test | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Build"` | all pass |

If the build fails with `MSB3027`/`MSB3021` citing `GxMcp.Worker.exe` locked, a dev
worker is holding the binary — `Stop-Process -Name GxMcp.Worker -Force` then retry.

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/BuildService.cs`

**Out of scope**:
- The timeout path's existing `KillProcessTree(process)` call inside the `using`
  block — it works; do not touch it.
- `KillProcessTree`'s implementation — reuse it as-is.
- Any change to what `status.Process` is used for elsewhere — grep first
  (`grep -n "status.Process\|\.Process =" src/GxMcp.Worker/Services/BuildService.cs`)
  and leave other uses alone.

## Steps

### Step 1: Capture the PID (and start time) while the process is alive

Inside the `using` block, right after `process.Start();` (line ~1773), capture the
identity into locals that survive past disposal:

```csharp
process.Start();
int reapPid = process.Id;
DateTime reapStart;
try { reapStart = process.StartTime; } catch { reapStart = DateTime.MinValue; }
```

`Process.Id` and `StartTime` are safe to read after `Start()` and their captured
*values* remain valid after the `Process` object is disposed (they are value types).

### Step 2: Reap by PID in the `finally`, not via the disposed object

Replace the disposed-object dereference in the `finally` block with a PID-based
reap. Add a helper that kills a still-alive process tree by PID, verifying identity
via start time to avoid PID reuse:

```csharp
finally
{
    try { watchdog?.Dispose(); } catch { }
    // Guaranteed MSBuild cleanup: reap by captured PID (the Process object is
    // already disposed by the using-block, so we cannot inspect it here).
    try { ReapByPidIfAlive(reapPid, reapStart); }
    catch (Exception ex) { Logger.Warn("[BUILD-CLEANUP] MSBuild reap: " + ex.Message); }
    try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
    status.Process = null;
    Helpers.Logger.CurrentPhase = null;
}
```

`reapPid`/`reapStart` must be declared *outside* the `using` block (e.g. at the top
of the method as `int reapPid = 0; DateTime reapStart = DateTime.MinValue;`) so they
are in scope in `finally`. Assign them in Step 1.

### Step 3: Add the `ReapByPidIfAlive` helper

Add a private method (near `KillProcessTree`) that resolves the PID to a live
`Process`, confirms it is the same process (start-time match, tolerant when
`reapStart == DateTime.MinValue`), and calls the existing `KillProcessTree`:

```csharp
private void ReapByPidIfAlive(int pid, DateTime expectedStart)
{
    if (pid <= 0) return;
    Process p = null;
    try { p = Process.GetProcessById(pid); }
    catch { return; } // ArgumentException => not running; nothing to reap
    using (p)
    {
        try
        {
            if (p.HasExited) return;
            // Guard against PID reuse: only kill if the start time matches
            // (skip the guard when we failed to capture it).
            if (expectedStart != DateTime.MinValue)
            {
                try { if (p.StartTime != expectedStart) return; } catch { }
            }
            Logger.Info("[BUILD-CLEANUP] reaping lingering MSBuild tree pid=" + pid);
            KillProcessTree(p);
        }
        catch { /* best-effort */ }
    }
}
```

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → Build
succeeded, 0 errors.

## Test plan

- Add `src/GxMcp.Worker.Tests/BuildReapByPidTests.cs` (model after any existing
  small test in `src/GxMcp.Worker.Tests/BuildServiceTests.cs` for namespace/usings).
  `ReapByPidIfAlive` is private; expose it for test either by making it
  `internal` + adding `[assembly: InternalsVisibleTo("GxMcp.Worker.Tests")]` **only
  if that attribute is not already present** (grep:
  `grep -rn "InternalsVisibleTo" src/GxMcp.Worker`), or by testing the observable
  behavior through a thin internal wrapper. Prefer `internal` if the attribute
  already exists.
- Cases:
  - `ReapByPidIfAlive(0, ...)` and a definitely-dead PID (e.g. an int that
    `Process.GetProcessById` throws on) → no throw, no-op.
  - Spawn a short child process (`Process.Start("cmd.exe","/c ping 127.0.0.1 -n 30")`
    or similar), capture its pid+start, call `ReapByPidIfAlive(pid, start)`, then
    assert the process is gone within a short wait. Kill it in a `finally` so the
    test never leaks a process.
- **Verification**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~BuildReapByPid"` → all pass.

## Done criteria

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj` exits 0
- [ ] The `finally` block no longer references `status.Process` (except the
      `status.Process = null;` reset). Verify: `grep -n "status.Process" src/GxMcp.Worker/Services/BuildService.cs` shows only the assignment sites, not a `.HasExited` read.
- [ ] New reap test file exists and passes
- [ ] Full worker suite still green: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` (allow the two documented flakies in `EdgeCaseRegressionTests`/`PatternApplyServiceTests` — re-run in isolation to confirm)
- [ ] `plans/README.md` status row updated

## STOP conditions

- `BuildService.cs` around lines 1767–1861 does not match the "Current state"
  excerpt (drift).
- `KillProcessTree` has a different signature than `KillProcessTree(Process)` —
  report what you found.
- The build spawns MSBuild through a path *other* than the `using (var process ...)`
  block shown (e.g. a second spawn site) — report; this plan only covers the
  shown site.

## Maintenance notes

- If a future change moves the reap logic back inside the `using` block, the
  PID-capture locals become unnecessary — but the PID approach is harmless there
  too.
- Reviewer: confirm `reapPid`/`reapStart` are declared at method scope (not inside
  `using`) and assigned right after `Start()`.
