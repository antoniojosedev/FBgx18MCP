# Plan 035: `BrowserDriverInvoker.ResolveDriverPath` drains stderr to avoid a pipe deadlock

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Worker/Services/BrowserDriverInvoker.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`ResolveDriverPath` sets `RedirectStandardError = true` but only reads stdout
(`p.StandardOutput.ReadToEnd()`), then `WaitForExit`. Classic two-pipe deadlock: if the
child (`cmd /c where ...`) writes enough to stderr to fill the OS pipe buffer (~4KB)
before finishing, it blocks on stderr while the parent blocks in `ReadToEnd()` on
stdout — neither proceeds, and there's no timeout on `ReadToEnd`. `where`'s stderr is
small enough not to trip it today, but it's a latent landmine, and the sibling `Invoke()`
method in the same file already does it right (async `BeginOutputReadLine`/`BeginErrorReadLine`).

## Current state

- `src/GxMcp.Worker/Services/BrowserDriverInvoker.cs`, `ResolveDriverPath` (lines 48–71):

```csharp
var psi = new ProcessStartInfo("cmd.exe", "/c where " + name)
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,      // redirected but never read
    UseShellExecute = false,
    CreateNoWindow = true
};
using (var p = Process.Start(psi))
{
    string so = p.StandardOutput.ReadToEnd();   // blocks; stderr never drained
    p.WaitForExit(5000);
    if (p.ExitCode == 0) { ... }
}
```

- The correct pattern already exists in `Invoke()` (lines 97–113): async
  `OutputDataReceived`/`ErrorDataReceived` handlers + `BeginOutputReadLine()` +
  `BeginErrorReadLine()` + `WaitForExit(timeoutMs)`.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~BrowserDriver"` | all pass (or none — see Test) |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:** `src/GxMcp.Worker/Services/BrowserDriverInvoker.cs` — `ResolveDriverPath`
only.

**Out of scope:** `Invoke()` (already correct — use it as the reference, don't change
it); the probe target list; caching (`_probed`/`_cachedPath`).

## Git workflow

- Branch: `advisor/035-browserdriver-stderr-drain`
- One commit: `fix(browser): drain stderr in ResolveDriverPath to avoid pipe deadlock`
- Do NOT push.

## Steps

### Step 1: Drain both streams

Rewrite the `using (var p = Process.Start(psi))` body to read stdout AND stderr without
blocking on one while the other fills. Simplest: capture stdout into a `StringBuilder`
via `p.OutputDataReceived` + `p.BeginOutputReadLine()`, add a `p.ErrorDataReceived`
handler (contents can be discarded) + `p.BeginErrorReadLine()`, then `p.WaitForExit(5000)`,
then read `ExitCode` and parse the captured stdout as before. Mirror `Invoke()`'s
approach (lines 97–113). Keep the 5000ms wait and the "first non-empty line" parse.

Alternative acceptable minimal fix: keep synchronous stdout `ReadToEnd()` but start an
async stderr drain first (`p.BeginErrorReadLine()` with an empty handler, or
`p.StandardError.ReadToEndAsync()` captured before the stdout read). Either eliminates
the deadlock; prefer the `Invoke()`-style async pattern for consistency.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Test (best-effort)

Driver resolution shells out to the OS. If the worker test project has no way to
exercise `ResolveDriverPath` deterministically (it depends on PATH), this is a
build-verified change matching the proven `Invoke()` pattern — do not build a
process-mocking harness. Note it in your report. If a `BrowserDriver` test seam exists,
add a case that resolution still returns a path when the tool is present.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~BrowserDriver"` → all pass (or "no matching tests").

## Done criteria

- [ ] Build exits 0
- [ ] `ResolveDriverPath` drains stderr (no `RedirectStandardError=true` left with stderr unread before `WaitForExit`)
- [ ] stdout parsing behavior (first non-empty line, ExitCode==0) preserved
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `ResolveDriverPath` already drains stderr (drift).
- Switching to async reads changes the "first non-empty line" result in an existing
  test — STOP and reconcile.

## Maintenance notes

- Reviewer: confirm both pipes are drained before/while `WaitForExit`, and the parse of
  the resolved path is unchanged. The fix pattern is copied from `Invoke()` in the same
  file.
