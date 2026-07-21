# Plan 013: Confine `genexus_screenshot_publish` to image files under an allowed root

> **Executor instructions**: Follow step by step; run every verification command;
> honor STOP conditions. Update this plan's row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 9fe6817..HEAD -- src/GxMcp.Worker/Services/ScreenshotPublishService.cs`
> On any change, diff the "Current state" excerpt against live code; mismatch = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `9fe6817`, 2026-07-20

## Why this matters

`genexus_screenshot_publish` copies a caller-supplied `sourcePath` into the KB tree
(`<kbPath>/.gx/published-screenshots/<UTC>-<basename>`) after only a `File.Exists`
check — no extension allowlist, no root confinement, no size cap. An MCP caller can
therefore pass **any** path readable by the worker process (SSH keys, other KBs'
config/credential files, arbitrary documents) and have it copied into the KB working
tree, where every other file-listing/reading tool can see it and — if `.gx` isn't
fully gitignored — it can be committed and shared. The codebase already has the right
primitive for this (`PathSafety.TryResolveWithinRoot`, used by `AssetService`); this
tool just skips it. The fix applies defense-in-depth consistent with the rest of the
repo: restrict to image files coming from a directory screenshots actually originate
from.

## Current state

- `src/GxMcp.Worker/Services/ScreenshotPublishService.cs` — the whole service (~94
  lines). `Publish(sourcePath, kbPathOverride)` validates non-empty path + resolves
  KB path + `File.Exists`, then calls `PublishCore` which does the copy.

Excerpt (`ScreenshotPublishService.cs:38-58`):

```csharp
if (!File.Exists(sourcePath))
{
    return Error("SourceNotFound", "Screenshot file does not exist: " + sourcePath);
}
return PublishCore(sourcePath, kbPath);
...
public static string PublishCore(string sourcePath, string kbPath)
{
    try
    {
        string destDir = Path.Combine(kbPath, ".gx", "published-screenshots");
        Directory.CreateDirectory(destDir);
        string basename = Path.GetFileName(sourcePath);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        string destName = stamp + "-" + basename;
        string destPath = Path.Combine(destDir, destName);
        File.Copy(sourcePath, destPath, overwrite: false);
        ...
```

- The error helper `Error(code, message)` and `McpResponse.Err/Ok` are already in
  the file. Match their shape for the new rejection.
- Look at how `AssetService` confines paths for the established pattern:
  `grep -n "TryResolveWithinRoot\|PathSafety" src/GxMcp.Worker/Services/AssetService.cs`
  and read `PathSafety` (`grep -rln "class PathSafety" src/GxMcp.Worker`).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` (GX_PATH set) | Build succeeded |
| Test | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ScreenshotPublish"` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/ScreenshotPublishService.cs`
- `src/GxMcp.Worker.Tests/ScreenshotPublishServiceTests.cs` (create)

**Out of scope**:
- `AssetService.cs` / `PathSafety.cs` — read for the pattern, do not modify.
- The tool's registration/schema (`tool_definitions.json`, routers) — behavior
  contract for valid inputs is unchanged; only invalid inputs are newly rejected.
- Do NOT add a network/remote upload — this tool is local-only by design.

## Steps

### Step 1: Add an image-extension allowlist

In `PublishCore` (or in `Publish` before calling `PublishCore`), reject any
`sourcePath` whose extension is not in `{ .png, .jpg, .jpeg, .gif, .webp, .bmp }`
(case-insensitive):

```csharp
static readonly string[] AllowedExt = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
...
string ext = Path.GetExtension(sourcePath ?? "").ToLowerInvariant();
if (Array.IndexOf(AllowedExt, ext) < 0)
    return Error("SourceNotAllowed",
        "screenshot_publish only accepts image files (" + string.Join(", ", AllowedExt) + ").");
```

Place the check where it runs for the real entry point (`Publish`) AND is reachable
by `PublishCore`'s tests. Simplest: put it at the top of `PublishCore`, so both the
live path and the test path enforce it.

### Step 2: Confine the source to an allowed root

Screenshots the MCP produces land in the OS temp dir or a configured capture dir.
Require the resolved source path to sit under one of: `Path.GetTempPath()`, the KB
root (`kbPath`), or an override root from env `GXMCP_SCREENSHOT_DIR` if set. Reject
otherwise:

```csharp
string full;
try { full = Path.GetFullPath(sourcePath); } catch { return Error("SourceNotAllowed", "Invalid source path."); }

bool WithinRoot(string root)
{
    if (string.IsNullOrEmpty(root)) return false;
    string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
    return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
}

string envDir = Environment.GetEnvironmentVariable("GXMCP_SCREENSHOT_DIR");
if (!(WithinRoot(Path.GetTempPath()) || WithinRoot(kbPath) || WithinRoot(envDir)))
    return Error("SourceNotAllowed",
        "screenshot source must be under the OS temp dir, the open KB, or GXMCP_SCREENSHOT_DIR.");
```

If `PathSafety.TryResolveWithinRoot` exists and cleanly expresses "path under root"
(read it first), prefer reusing it over the hand-rolled `WithinRoot`. Keep the
temp-dir + KB + env-dir union either way.

### Step 3: Add a size cap (defensive)

Before `File.Copy`, cap the source size (e.g. 25 MB) so a hostile/huge file can't be
duplicated into the KB tree:

```csharp
const long MaxBytes = 25L * 1024 * 1024;
try { if (new FileInfo(sourcePath).Length > MaxBytes) return Error("SourceTooLarge", "Screenshot exceeds 25 MB."); }
catch { /* fall through; File.Copy will surface a real IO error */ }
```

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → Build succeeded.

## Test plan

- Create `src/GxMcp.Worker.Tests/ScreenshotPublishServiceTests.cs`. All tests call
  the static `PublishCore(sourcePath, kbPath)` (no live KB needed). Use xUnit
  (`using Xunit;`), model after `DbDriftServiceTests.cs` structure. Use a temp KB
  dir via `Path.GetTempPath()` + a GUID-ish unique name (do NOT use `Guid.NewGuid`
  if the test project forbids it — check an existing test; otherwise a timestamp is
  fine). Clean up created dirs in a `finally`.
- Cases:
  - **Rejects non-image**: write a temp `secret.txt`, call `PublishCore(txtPath, kb)`
    → envelope `code == "SourceNotAllowed"`; assert the file was NOT copied into
    `<kb>/.gx/published-screenshots/`.
  - **Rejects out-of-root image**: create a `.png` in a directory that is neither
    temp, KB, nor `GXMCP_SCREENSHOT_DIR` (e.g. a sibling temp root you then treat as
    "outside") — if every temp path counts as allowed, instead assert the negative
    via a path under a clearly-unrelated root like the repo dir; `code ==
    "SourceNotAllowed"`. (If reliably constructing an out-of-root path is awkward on
    the CI box, cover this branch by pointing `GXMCP_SCREENSHOT_DIR` at a temp
    subdir and passing a `.png` from a *different* subdir.)
  - **Accepts a valid temp image**: write a small `.png` (any bytes) under
    `Path.GetTempPath()`, call `PublishCore` → `code == "ScreenshotPublished"`,
    `publishedPath` exists, basename ends with the original name.
  - **Rejects oversize**: a `.png` > 25 MB under temp → `code == "SourceTooLarge"`.
    (Create a sparse/large file cheaply; if that's slow/awkward, skip this one case
    and note it — the two rejection cases above are the security-critical ones.)
- **Verification**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ScreenshotPublish"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] New test file exists; non-image and out-of-root cases prove the copy did NOT happen
- [ ] `grep -n "File.Copy" src/GxMcp.Worker/Services/ScreenshotPublishService.cs` — the copy is now preceded by extension + root checks
- [ ] Full worker suite green (allow documented flakies)
- [ ] `plans/README.md` row updated

## STOP conditions

- The service file doesn't match the "Current state" excerpt (drift).
- `PublishCore` turns out to be called from a path where confinement would break a
  legitimate documented workflow (grep callers:
  `grep -rn "PublishCore\|ScreenshotPublish" src/GxMcp.Worker src/GxMcp.Gateway`) —
  report before narrowing.

## Maintenance notes

- If a future feature captures screenshots into a new directory, add it to the
  allowed-root union (or set `GXMCP_SCREENSHOT_DIR`), don't loosen the check.
- Reviewer: confirm the reject paths return before `File.Copy`, and that the
  allowed-root check uses `Path.GetFullPath` (normalizes `..`) so traversal via
  `temp/../../secret` can't slip through.
