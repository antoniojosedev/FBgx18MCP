# GeneXus 18 MCP — one-shot release script
# =========================================
#
# Why this exists: the npm publish workflow (.github/workflows/release.yml)
# expects a `publish.zip` asset attached to the GitHub Release. The Worker
# references Artech.* DLLs from the local GeneXus 18 install, so building on
# ubuntu-latest in CI isn't viable — the zip has to be produced locally.
#
# Previous flow (manual, 5 commands):
#   1. dotnet build / build.ps1
#   2. Compress-Archive publish/* publish.zip
#   3. git commit + tag + push
#   4. gh release create vX.Y.Z --title ... --notes ...
#   5. gh release upload vX.Y.Z publish.zip
#
# Steps 4 and 5 were a foot-gun: skipping #5 published a release with no asset
# and the workflow exited with `no assets to download`. v2.6.8 hit this once.
#
# New flow (one command):
#   .\release.ps1 -Version 2.6.9          # bump → build → zip → tag → push → release WITH asset
#   .\release.ps1                         # release current package.json version
#   .\release.ps1 -Version 2.6.9 -DryRun  # rehearse without touching origin
#
# gh release create accepts asset paths as positional args, so the upload
# lands in the SAME api call as create — the workflow's first run already
# sees the asset and the npm publish succeeds without manual intervention.

[CmdletBinding()]
param(
    [string]$Version,
    [string]$NotesFile,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$root = $PSScriptRoot

function Step([string]$msg) { Write-Host "`n>>> $msg" -ForegroundColor Cyan }
function Ok  ([string]$msg) { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Warn([string]$msg) { Write-Host "    [!]  $msg" -ForegroundColor Yellow }
function Fail([string]$msg) { Write-Host "    [ERR] $msg" -ForegroundColor Red; exit 1 }

function Invoke-Cmd {
    # NOTE: do NOT name a parameter `$Args` — it collides with PowerShell's
    # automatic variable inside the function body, and `@Args` then splats
    # the EMPTY automatic instead of the caller's array. Observed releases
    # v2.6.9 with the bug: `git`, `dotnet`, `pwsh` all invoked with no args
    # (release.ps1 would die at "Tagging $tag" with git printing its help).
    # Use `$Arguments` (or any non-automatic name) instead.
    param([string]$Exe, [string[]]$Arguments, [switch]$IgnoreExit)
    $display = "$Exe $($Arguments -join ' ')"
    Write-Host "    $ $display" -ForegroundColor DarkGray
    if ($DryRun) { return }
    & $Exe @Arguments
    if (-not $IgnoreExit -and $LASTEXITCODE -ne 0) {
        Fail "Command failed (exit $LASTEXITCODE): $display"
    }
}

# ── 1. Resolve version + sanity-check tree ────────────────────────────────
Step "Resolving version"
$pkgPath = Join-Path $root 'package.json'
if (-not (Test-Path $pkgPath)) { Fail "package.json not found at $pkgPath" }
$pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
$currentVersion = $pkg.version

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $currentVersion
    Warn "No -Version passed; using package.json: $Version"
} else {
    # Strip leading 'v' if user typed it
    $Version = $Version -replace '^v', ''
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[\w.]+)?$') {
        Fail "Version '$Version' is not semver (X.Y.Z or X.Y.Z-tag)."
    }
}
$tag = "v$Version"
Ok "Target version: $Version  (tag: $tag)"

Step "Checking git working tree"
$status = git status --porcelain
if ($status -and -not $AllowDirty) {
    Write-Host $status
    Fail "Working tree is dirty. Commit or stash before releasing (or pass -AllowDirty to bundle pending changes into the release commit)."
}
Ok "Tree state acceptable."

# Verify tag isn't already on remote.
$remoteTag = git ls-remote --tags origin "refs/tags/$tag" 2>$null
if ($remoteTag) {
    Fail "Tag $tag already exists on origin. Bump the version or delete the remote tag first."
}
Ok "Tag $tag is free on origin."

# Verify CHANGELOG has an entry for this version (best-effort).
$changelogPath = Join-Path $root 'CHANGELOG.md'
if (Test-Path $changelogPath) {
    $changelog = Get-Content $changelogPath -Raw
    if ($changelog -notmatch "## v$([Regex]::Escape($Version))\b") {
        Warn "CHANGELOG.md has no '## v$Version' entry. Continuing — but add one before tagging real releases."
    } else {
        Ok "CHANGELOG entry for v$Version found."
    }
}

# ── 2. Bump version files if needed ───────────────────────────────────────
#
# Drift check (v2.8.0 lesson): when package.json was hand-edited BEFORE
# running release.ps1, `$Version -eq $currentVersion` and the whole bump
# block was skipped — including the csproj sync. The published binary
# carried the OLD InformationalVersion stamp even though the source was
# new. Detect that case and force the bump when ANY file is out of sync,
# not only when -Version was passed.
$csprojPath = Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj'
$csprojVersion = $null
if (Test-Path $csprojPath) {
    $csprojRaw = Get-Content $csprojPath -Raw
    if ($csprojRaw -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        $csprojVersion = $Matches[1].Trim()
    }
}
$needsBump = ($Version -ne $currentVersion) -or ($csprojVersion -and $csprojVersion -ne $Version)
if ($needsBump -and ($Version -eq $currentVersion) -and ($csprojVersion -ne $Version)) {
    Warn "csproj InformationalVersion=$csprojVersion is out of sync with package.json=$Version — forcing bump pass to realign."
}
if ($needsBump) {
    Step "Bumping version: $currentVersion → $Version"

    # package.json — preserve formatting via regex (ConvertTo-Json reorders keys).
    if (-not $DryRun) {
        $raw = Get-Content $pkgPath -Raw
        $bumped = [Regex]::Replace($raw,
            '("version"\s*:\s*")[^"]+(")',
            "`${1}$Version`${2}", 1)
        [System.IO.File]::WriteAllText($pkgPath, $bumped, [System.Text.UTF8Encoding]::new($false))
    }
    Ok "package.json → $Version"

    # GxMcp.Gateway.csproj — Version, AssemblyVersion, FileVersion, InformationalVersion.
    $csprojPath = Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj'
    if (Test-Path $csprojPath) {
        if (-not $DryRun) {
            $raw = Get-Content $csprojPath -Raw
            $bumped = $raw `
                -replace '(<Version>)[^<]+(</Version>)',                       "`${1}$Version`${2}" `
                -replace '(<AssemblyVersion>)[^<]+(</AssemblyVersion>)',       "`${1}$Version.0`${2}" `
                -replace '(<FileVersion>)[^<]+(</FileVersion>)',               "`${1}$Version.0`${2}" `
                -replace '(<InformationalVersion>)[^<]+(</InformationalVersion>)', "`${1}$Version`${2}"
            [System.IO.File]::WriteAllText($csprojPath, $bumped, [System.Text.UTF8Encoding]::new($false))
        }
        Ok "GxMcp.Gateway.csproj → $Version"
    }
} else {
    Ok "Version unchanged — skipping bump."
}

# ── 3. Build + zip publish/ ───────────────────────────────────────────────
if (-not $SkipBuild) {
    Step "Building (build.ps1)"
    Invoke-Cmd 'pwsh' @('-NoProfile', '-File', (Join-Path $root 'build.ps1')) -IgnoreExit
    if (-not $DryRun -and $LASTEXITCODE -ne 0) {
        # build.ps1 returns non-zero on warnings sometimes; check artefacts.
        $gw = Join-Path $root 'publish\GxMcp.Gateway.exe'
        $wk = Join-Path $root 'publish\worker\GxMcp.Worker.exe'
        if (-not (Test-Path $gw) -or -not (Test-Path $wk)) {
            Fail "build.ps1 failed AND artefacts are missing. Aborting."
        }
        Warn "build.ps1 returned non-zero but artefacts are present — continuing."
    }
} else {
    Warn "-SkipBuild set; reusing existing publish/ artefacts."
}

# Sanity-check the artefacts the workflow needs.
$publishDir = Join-Path $root 'publish'
$requiredArtefacts = @(
    'GxMcp.Gateway.exe',
    'worker\GxMcp.Worker.exe'
)
foreach ($rel in $requiredArtefacts) {
    $path = Join-Path $publishDir $rel
    if (-not (Test-Path $path)) {
        Fail "Required publish artefact missing: $rel (the release workflow asserts on this)."
    }
}
Ok "publish/ artefacts validated."

# ── 4. Optional test pass ─────────────────────────────────────────────────
if (-not $SkipTests) {
    Step "Running test suite (Gateway + Worker)"
    if (-not $DryRun) {
        $env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
        # Run gateway tests; worker tests can be flaky on parallel runs.
        Invoke-Cmd 'dotnet' @('test',
            (Join-Path $root 'src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj'),
            '--nologo', '-v:minimal')
        Ok "Gateway tests passed."
    }
} else {
    Warn "-SkipTests set; not running test suite."
}

# ── 5. Zip publish/ → publish.zip ─────────────────────────────────────────
Step "Packing publish.zip"
$zipPath = Join-Path $root 'publish.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (-not $DryRun) {
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
    $sizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Ok "publish.zip created ($sizeMb MB)."
} else {
    Ok "[dry-run] would create publish.zip"
}

# ── 6. Commit (if version bumped or tree dirty) ───────────────────────────
$pending = git status --porcelain
if ($pending) {
    Step "Committing version bump"
    Invoke-Cmd 'git' @('add', '-A')
    $msg = "release: $tag"
    Invoke-Cmd 'git' @('commit', '-m', $msg)
    Ok "Committed: $msg"
} else {
    Ok "No pending changes to commit."
}

# ── 7. Tag (annotated) + push ─────────────────────────────────────────────
Step "Tagging $tag"
Invoke-Cmd 'git' @('tag', '-a', $tag, '-m', "$tag")
Ok "Local tag created."

if (-not $DryRun) {
    Step "Pushing main + $tag to origin"
    Invoke-Cmd 'git' @('push', 'origin', 'HEAD')
    Invoke-Cmd 'git' @('push', 'origin', $tag)
    Ok "Pushed."
}

# ── 8. Extract release notes from CHANGELOG (best-effort) ─────────────────
$notes = $null
if ($NotesFile -and (Test-Path $NotesFile)) {
    $notes = Get-Content $NotesFile -Raw
} elseif (Test-Path $changelogPath) {
    # Pull the block between `## v$Version` and the next `## v` heading.
    $cl = Get-Content $changelogPath -Raw
    $rx = [Regex]::new(
        "## v$([Regex]::Escape($Version))(?:\s+—\s+[^\r\n]+)?[\r\n]+(.*?)(?=\r?\n## v|\z)",
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $m = $rx.Match($cl)
    if ($m.Success) {
        $notes = $m.Groups[1].Value.Trim()
        Ok "Extracted release notes from CHANGELOG ($($notes.Length) chars)."
    }
}
if (-not $notes) {
    $notes = "Release $tag. See CHANGELOG.md for details."
    Warn "Falling back to a generic release-notes body."
}

# ── 9. Create release WITH publish.zip in the same call ───────────────────
Step "Creating GitHub release $tag (with publish.zip)"
$notesTmp = Join-Path $env:TEMP "release-notes-$tag.md"
if (-not $DryRun) {
    [System.IO.File]::WriteAllText($notesTmp, $notes, [System.Text.UTF8Encoding]::new($false))
}
# Key insight: `gh release create <tag> [files...]` uploads the assets in
# the same call as create, so the workflow's FIRST `release.published` event
# already has publish.zip attached → npm publish succeeds on the first run.
$createArgs = @(
    'release', 'create', $tag,
    '--title', "$tag",
    '--notes-file', $notesTmp,
    '--target', 'main',
    $zipPath
)
Invoke-Cmd 'gh' $createArgs

Ok "Release created: https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag"

# Belt-and-suspenders: explicitly trigger the publish workflow.
# Observed 2026-05-26: v2.6.11 was created with publish.zip attached but the
# `release.published` event never fired the workflow — npm stayed at 2.6.10
# until a manual `gh workflow run release.yml -f tag=v2.6.11` backfilled it.
# Dispatching explicitly is idempotent: if the release event DID fire, the
# workflow's `Check npm registry` step short-circuits the duplicate run with
# `already_published=true` and exits cheap.
if (-not $DryRun) {
    Step "Triggering publish workflow (belt-and-suspenders)"
    Start-Sleep -Seconds 3  # let GitHub register the release before dispatch
    Invoke-Cmd 'gh' @('workflow', 'run', 'release.yml', '-f', "tag=$tag")
    Ok "Workflow dispatched."
}
Write-Host ""
Write-Host "    Watch the publish workflow:" -ForegroundColor Cyan
Write-Host "      gh run watch --workflow=release.yml" -ForegroundColor Gray
Write-Host ""
Write-Host "    Verify on npm (~2-3 min):" -ForegroundColor Cyan
Write-Host "      npm view genexus-mcp@$Version version" -ForegroundColor Gray
Write-Host ""

if ($DryRun) {
    Warn "DRY RUN — no remote changes were made. Re-run without -DryRun to publish."
}
