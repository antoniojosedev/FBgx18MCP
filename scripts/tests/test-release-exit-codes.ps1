$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# A failing release must surface as a failing exit code (or, for -Detach, as a failed status)
# through every launcher: the canonical script, the legacy wrapper, and the detached child.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$temp = Join-Path $env:TEMP ('gxmcp-release-exit-codes-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $temp 'scripts') -Force | Out-Null
try {
    # Wrapper against a stub canonical script: the stub's exit status is the only variable.
    Copy-Item -LiteralPath (Join-Path $root 'scripts\release.ps1') -Destination (Join-Path $temp 'scripts\release.ps1')
    $stub = Join-Path $temp 'release.ps1'
    foreach ($case in @(
        @{ label = 'exit 17'; body = 'exit 17'; expect = 17 },
        @{ label = 'exit 0'; body = 'exit 0'; expect = 0 },
        @{ label = 'terminating error'; body = "throw 'stub release failure'"; expect = 1 },
        @{ label = 'native failure then success'; body = "& pwsh -NoProfile -Command 'exit 5'; exit `$LASTEXITCODE"; expect = 5 }
    )) {
        Set-Content -LiteralPath $stub -Value $case.body -Encoding utf8
        & pwsh -NoProfile -File (Join-Path $temp 'scripts\release.ps1') 3.0.1 *> $null
        if ($LASTEXITCODE -ne $case.expect) { throw "Legacy wrapper lost the canonical exit status for '$($case.label)': expected $($case.expect), got $LASTEXITCODE." }
    }
    $wrapperSource = Get-Content -LiteralPath (Join-Path $root 'scripts\release.ps1') -Raw
    if ($wrapperSource -match '\|\s*(Tee-Object|Out-File|Select-Object)') { throw 'Legacy wrapper must not pipe the release through a logging/filter stage.' }

    # Canonical script: an invalid version fails fast, offline, with exit 1 and a failed status.
    $statusPath = Join-Path $temp 'direct-status.json'
    & pwsh -NoProfile -File (Join-Path $root 'release.ps1') -Version bad -SkipLabeledIssues -DryRun -StatusFile $statusPath *> $null
    if ($LASTEXITCODE -ne 1) { throw "Canonical release.ps1 must exit 1 for an invalid version, got $LASTEXITCODE." }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.state -ne 'failed' -or $status.exitCode -ne 1 -or $status.error -notmatch 'not strict semver') {
        throw 'Canonical failure was not recorded in the status file.'
    }

    # Detached child: the launcher returns 0 once the child started, so the failure must be
    # readable through the status command, which exits nonzero and names the cause.
    $detachedStatus = Join-Path $temp 'detached-status.json'
    $launch = @(& pwsh -NoProfile -File (Join-Path $root 'release.ps1') -Version bad -SkipLabeledIssues -DryRun -Detach -StatusFile $detachedStatus)
    if ($LASTEXITCODE -ne 0 -or ($launch -join "`n") -notmatch 'Detached release started') { throw "Detached launch failed: $($launch -join ' ')" }
    $report = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $detachedStatus -WaitSeconds 120)
    if ($LASTEXITCODE -ne 1 -or ($report -join "`n") -notmatch 'state=failed' -or ($report -join "`n") -notmatch 'not strict semver') {
        throw "A failing detached release must report failed with exit 1 (got $LASTEXITCODE): $($report -join ' ')"
    }
    $detached = Get-Content -LiteralPath $detachedStatus -Raw | ConvertFrom-Json
    if ($detached.exitCode -ne 1 -or -not (Test-Path -LiteralPath $detached.stdoutLog -PathType Leaf)) {
        throw 'Detached failure must record exitCode 1 and keep its stdout log.'
    }
    Remove-Item -LiteralPath $detached.stdoutLog, $detached.stderrLog -Force -ErrorAction SilentlyContinue
    Write-Host 'release-exit-codes: wrapper, canonical and detached failures stay failures' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
