# Plan 027: `MultiAgentLockService` writes the lock file atomically

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/MultiAgentLockService.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`MultiAgentLockService` exists to give agents mutual exclusion over an object/part.
The `acquire` path writes the lock JSON with a single `File.WriteAllText(lockPath,
...)` straight to the final file. A crash or forced worker kill mid-write leaves a
truncated/partial JSON at `lockPath`. `TryReadLock` catches the parse failure and
treats it as **expired** — so a genuinely-active lock, destroyed mid-write, lets any
other agent acquire it while the original holder still believes it holds the lock,
defeating the exact mutual-exclusion guarantee this service provides. Sibling stores
in the repo (e.g. `IndexCacheService`, `BackgroundJobRegistry.SaveTo`) already write
to a temp file then move into place; do the same here.

## Current state

- `src/GxMcp.Worker/Services/MultiAgentLockService.cs`, `acquire` case, line 136:

```csharp
File.WriteAllText(lockPath, entry.ToString(Newtonsoft.Json.Formatting.None));
```

`TryReadLock` (lines 209–231) parses the file and, on any exception, sets
`expired = true` and returns null (comment: "Corrupted lock file — treat as expired").

Reference pattern already in the repo — `BackgroundJobRegistry.SaveTo`
(`src/GxMcp.Gateway/BackgroundJobRegistry.cs:186-189`):

```csharp
string tmp = path + ".tmp";
File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
if (File.Exists(path)) File.Delete(path);
File.Move(tmp, path);
```

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~MultiAgentLock"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/MultiAgentLockService.cs` — the `acquire` write only.
- `src/GxMcp.Worker.Tests/` — a test.

**Out of scope:**
- `release` (`File.Delete(lockPath)`) — atomic enough; leave it.
- `TryReadLock`'s treat-corrupt-as-expired behavior — keep it (it's the safety net).
- The `Sanitize`/path logic — unchanged.

## Git workflow

- Branch: `advisor/027-multiagentlock-atomic-write`
- One commit: `fix(lock): write multi-agent lock file atomically via temp+move`
- Do NOT push.

## Steps

### Step 1: Stage the write through a temp file

Replace the single `File.WriteAllText(lockPath, ...)` in the `acquire` case with a
temp-then-move sequence in the same directory as `lockPath` (same volume, so the move
is atomic on NTFS):

```csharp
string tmp = lockPath + ".tmp";
File.WriteAllText(tmp, entry.ToString(Newtonsoft.Json.Formatting.None));
if (File.Exists(lockPath)) File.Delete(lockPath);
File.Move(tmp, lockPath);
```

The surrounding `try/catch (Exception ex)` in `Run` (lines 199–206,
`LockOperationFailed`) already covers IO failures; no new catch needed. If a stale
`.tmp` from a prior crash exists, the `File.WriteAllText(tmp, ...)` overwrites it —
fine.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Test

Create `src/GxMcp.Worker.Tests/MultiAgentLockAtomicWriteTests.cs`. `MultiAgentLock`
writes under the KB's `.gx/locks`; the test likely needs a temp KB path. Model on an
existing `MultiAgentLockService` test (search the test project). Cover:
- **Happy path unchanged**: `acquire` then `status`/read shows the lock held with the
  right owner (regression: temp+move didn't break normal acquire).
- **No leftover temp**: after a successful `acquire`, `lockPath + ".tmp"` does not
  exist.
- Re-acquire by the same owner still refreshes (regression).

If the existing tests already cover acquire/read against a temp dir, extend them
rather than duplicating the harness.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~MultiAgentLock"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] `grep -n "File.WriteAllText(lockPath" src/GxMcp.Worker/Services/MultiAgentLockService.cs` returns nothing (the direct write is gone)
- [ ] The acquire path writes to `lockPath + ".tmp"` then `File.Move`s onto `lockPath`
- [ ] Tests pass, including that no `.tmp` remains after a normal acquire
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `acquire` already stages through a temp file (drift) — report.
- Tests can't construct the service against a temp KB dir without a live SDK — report
  the existing test approach.

## Maintenance notes

- Reviewer: confirm temp and final path are in the same directory (cross-volume
  `File.Move` is not atomic).
