[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',

    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$selfPath = [System.IO.Path]::GetFullPath($PSCommandPath)
$startedAtUtc = [DateTime]::UtcNow

function New-Phase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Command
    )

    return [ordered]@{
        name = $Name
        command = $Command
        status = 'running'
        exitCode = $null
        durationSeconds = 0
        outputTail = @()
        reason = $null
    }
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $root
    )

    $output = @()
    $exitCode = 1
    $errorMessage = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        Push-Location $WorkingDirectory
        try {
            $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
            $exitCode = $LASTEXITCODE
        } finally {
            Pop-Location
        }
    } catch {
        $errorMessage = $_.Exception.Message
    }
    $watch.Stop()

    return [pscustomobject]@{
        output = @($output)
        exitCode = $exitCode
        durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        error = $errorMessage
    }
}

function Format-Command {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @()
    )

    $parts = @($Executable) + @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    return ($parts -join ' ')
}

function Invoke-NativePhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $root
    )

    $phase = New-Phase -Name $Name -Command (Format-Command -Executable $Executable -Arguments $Arguments)
    $result = Invoke-NativeCapture -Executable $Executable -Arguments $Arguments -WorkingDirectory $WorkingDirectory
    $phase.exitCode = $result.exitCode
    $phase.durationSeconds = $result.durationSeconds
    $phase.outputTail = @($result.output | Select-Object -Last 60)
    if ($null -ne $result.error) {
        $phase.reason = $result.error
        $phase.status = 'failed'
    } elseif ($result.exitCode -eq 0) {
        $phase.status = 'passed'
    } else {
        $phase.reason = "Command exited with code $($result.exitCode)."
        $phase.status = 'failed'
    }
    return $phase
}

function Invoke-GitCapture {
    param([string[]]$Arguments = @())
    return Invoke-NativeCapture -Executable 'git' -Arguments (@('-C', $root) + @($Arguments))
}

function Get-GitLines {
    param([string[]]$Arguments = @())
    $result = Invoke-GitCapture -Arguments $Arguments
    return [pscustomobject]@{
        lines = @($result.output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        exitCode = $result.exitCode
        error = $result.error
    }
}

function Get-TrackedPowerShellPaths {
    $result = Get-GitLines -Arguments @('ls-files', '--', '*.ps1')
    if ($result.exitCode -ne 0) {
        throw "Unable to enumerate tracked PowerShell scripts: $($result.error)"
    }

    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($relativePath in $result.lines) {
        $normalizedRelativePath = $relativePath.Replace('\', '/')
        if ($normalizedRelativePath -notmatch '^(scripts/|[^/]+\.ps1$|.*\.example\.ps1$)') {
            continue
        }
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $root $relativePath))
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            [void]$paths.Add($candidate)
        }
    }
    $selfExists = Test-Path -LiteralPath $selfPath -PathType Leaf
    if ($selfExists -and -not ($paths -contains $selfPath)) {
        [void]$paths.Add($selfPath)
    }
    return @($paths | Sort-Object -Unique)
}

function Invoke-PowerShellParsePhase {
    $phase = New-Phase -Name 'PowerShell AST parse' -Command 'Parser::ParseFile for quality scripts plus this checker'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $errorsFound = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($path in @(Get-TrackedPowerShellPaths)) {
            $tokens = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
            foreach ($parseError in @($parseErrors)) {
                [void]$errorsFound.Add("$($path):$($parseError.Extent.StartLineNumber): $($parseError.Message)")
            }
        }
        $phase.exitCode = if ($errorsFound.Count -eq 0) { 0 } else { 1 }
        $phase.status = if ($errorsFound.Count -eq 0) { 'passed' } else { 'failed' }
        $phase.outputTail = @($errorsFound | Select-Object -Last 60)
        if ($errorsFound.Count -gt 0) {
            $phase.reason = "PowerShell parse errors: $($errorsFound.Count)."
        }
    } catch {
        $phase.exitCode = 1
        $phase.status = 'failed'
        $phase.reason = $_.Exception.Message
    }
    $watch.Stop()
    $phase.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    return $phase
}

function Get-PrePushDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$MechanicalFailed,
        [Parameter(Mandatory = $true)][bool]$MechanicalUnavailable,
        [Parameter(Mandatory = $true)][bool]$BaseRefAvailable,
        [Parameter(Mandatory = $true)][int]$CommitsBehind,
        [Parameter(Mandatory = $true)][bool]$WorkingTreeDirty
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    if ($MechanicalFailed) { [void]$reasons.Add('One or more mechanical phases failed.') }
    if ($MechanicalUnavailable) { [void]$reasons.Add('One or more mechanical phases were unavailable.') }
    if (-not $BaseRefAvailable) { [void]$reasons.Add('The comparison base ref is unavailable.') }
    if ($CommitsBehind -gt 0) { [void]$reasons.Add("The branch is behind its base by $CommitsBehind commit(s).") }
    if ($WorkingTreeDirty) { [void]$reasons.Add('The working tree contains uncommitted changes.') }

    $ready = $reasons.Count -eq 0
    $status = if ($ready) { 'passed' } elseif ($MechanicalFailed) { 'failed' } else { 'incomplete' }
    $exitCode = if ($ready) { 0 } elseif ($MechanicalFailed) { 1 } else { 3 }
    $localReadiness = if ($ready) { 'ready' } else { 'blocked' }
    $pushReadiness = if ($ready) { 'readyLocal' } else { 'blocked' }

    return [pscustomobject]@{
        status = $status
        exitCode = $exitCode
        localReadiness = $localReadiness
        pushReadiness = $pushReadiness
        reasons = @($reasons)
    }
}

function Write-TextSummary {
    param([Parameter(Mandatory = $true)][object]$Summary)

    Write-Host "Pre-push mechanical checks: $($Summary.status)" -ForegroundColor $(if ($Summary.status -eq 'passed') { 'Green' } else { 'Yellow' })
    Write-Host "  base: $($Summary.baseRef)"
    Write-Host "  head: $($Summary.headCommit)"
    Write-Host "  ahead/behind: $($Summary.commitsAhead)/$($Summary.commitsBehind)"
    Write-Host "  working tree dirty: $($Summary.workingTreeDirty)"
    Write-Host "  pushReadiness: $($Summary.pushReadiness)"
    if (@($Summary.reasons).Count -gt 0) {
        foreach ($reason in @($Summary.reasons)) { Write-Host "  reason: $reason" -ForegroundColor Yellow }
    }
    foreach ($phase in @($Summary.phases)) {
        Write-Host ("  [{0}] {1}" -f $phase.status, $phase.name)
    }
}

$summary = [ordered]@{
    schemaVersion = 'gxmcp-pre-push/1'
    startedAtUtc = $startedAtUtc.ToString('o')
    endedAtUtc = $null
    root = $root
    baseRef = $BaseRef
    branch = $null
    headCommit = $null
    baseCommit = $null
    baseRefAvailable = $false
    commitsAhead = 0
    commitsBehind = 0
    workingTreeDirtyBefore = $false
    workingTreeDirty = $false
    workingTreePaths = @()
    changedPaths = @()
    phases = New-Object System.Collections.Generic.List[object]
    reasons = New-Object System.Collections.Generic.List[string]
    manualRequired = @()
    incompleteReasons = @()
    notCovered = @(
        'Semantic review of the changed range'
        'GeneXus IDE and real-KB behavior'
        'Remote CI status and publication'
    )
    localReadiness = 'not-run'
    remoteReadiness = 'unverified'
    pushReadiness = 'blocked'
    status = 'running'
    exitCode = 1
}

$mechanicalFailed = $false
$mechanicalUnavailable = $false
$baseAvailable = $false
$commitsBehind = 0
$workingTreeDirty = $false

try {
    $headResult = Get-GitLines -Arguments @('rev-parse', 'HEAD')
    if ($headResult.exitCode -eq 0 -and $headResult.lines.Count -gt 0) {
        $summary.headCommit = $headResult.lines[0]
    } else {
        throw 'Unable to resolve HEAD.'
    }

    $branchResult = Get-GitLines -Arguments @('branch', '--show-current')
    if ($branchResult.exitCode -eq 0 -and $branchResult.lines.Count -gt 0) {
        $summary.branch = $branchResult.lines[0]
    } else {
        [void]$summary.reasons.Add('The checkout is detached or has no named branch.')
        $mechanicalFailed = $true
    }

    $beforeStatusResult = Get-GitLines -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
    if ($beforeStatusResult.exitCode -ne 0) {
        [void]$summary.reasons.Add('Unable to inspect the working tree.')
        $mechanicalFailed = $true
    } else {
        $summary.workingTreeDirtyBefore = $beforeStatusResult.lines.Count -gt 0
    }

    $baseResult = Get-GitLines -Arguments @('rev-parse', '--verify', "$BaseRef`^{commit}")
    if ($baseResult.exitCode -eq 0 -and $baseResult.lines.Count -gt 0) {
        $baseAvailable = $true
        $summary.baseRefAvailable = $true
        $summary.baseCommit = $baseResult.lines[0]
    } else {
        [void]$summary.reasons.Add("Comparison base ref '$BaseRef' is unavailable.")
    }

    if ($baseAvailable) {
        $rangeResult = Get-GitLines -Arguments @('rev-list', '--left-right', '--count', ("{0}...HEAD" -f $BaseRef))
        if ($rangeResult.exitCode -eq 0 -and $rangeResult.lines.Count -gt 0) {
            $counts = @($rangeResult.lines[0] -split '\s+' | Where-Object { $_ -ne '' })
            if ($counts.Count -eq 2) {
                $commitsBehind = [int]$counts[0]
                $summary.commitsBehind = $commitsBehind
                $summary.commitsAhead = [int]$counts[1]
            } else {
                [void]$summary.reasons.Add('Unable to parse the base/head divergence.')
                $mechanicalFailed = $true
            }
        } else {
            [void]$summary.reasons.Add('Unable to calculate the base/head divergence.')
            $mechanicalFailed = $true
        }

        $changedResult = Get-GitLines -Arguments @('diff', '--name-only', ("{0}..HEAD" -f $BaseRef))
        if ($changedResult.exitCode -eq 0) {
            $summary.changedPaths = @($changedResult.lines | Sort-Object -Unique)
        } else {
            [void]$summary.reasons.Add('Unable to enumerate changed paths.')
            $mechanicalFailed = $true
        }
    }

    if ($beforeStatusResult.exitCode -eq 0) {
        $summary.workingTreePaths = @($beforeStatusResult.lines)
    }

    if ($baseAvailable) {
        $rangeDiffPhase = Invoke-NativePhase -Name 'git diff check (base..HEAD)' -Executable 'git' -Arguments @('-C', $root, 'diff', '--check', ("{0}..HEAD" -f $BaseRef))
        [void]$summary.phases.Add($rangeDiffPhase)
        if ($rangeDiffPhase.status -ne 'passed') { $mechanicalFailed = $true }
    }

    $workingDiffPhase = Invoke-NativePhase -Name 'git diff check (working tree)' -Executable 'git' -Arguments @('-C', $root, 'diff', '--check')
    [void]$summary.phases.Add($workingDiffPhase)
    if ($workingDiffPhase.status -ne 'passed') { $mechanicalFailed = $true }

    $parsePhase = Invoke-PowerShellParsePhase
    [void]$summary.phases.Add($parsePhase)
    if ($parsePhase.status -ne 'passed') { $mechanicalFailed = $true }

    $releaseSummaryPath = Join-Path $env:TEMP ('gxmcp-pre-push-release-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $releasePhase = Invoke-NativePhase `
            -Name 'release preflight (without live KB)' `
            -Executable 'pwsh' `
            -Arguments @('-NoProfile', '-File', (Join-Path $root 'scripts/release-preflight.ps1'), '-SkipLive', '-SummaryPath', $releaseSummaryPath)
        if (Test-Path -LiteralPath $releaseSummaryPath -PathType Leaf) {
            try {
                $releaseSummary = Get-Content -LiteralPath $releaseSummaryPath -Raw | ConvertFrom-Json
                $releasePhase.details = [ordered]@{
                    status = $releaseSummary.status
                    failedPhases = @($releaseSummary.phases | Where-Object status -eq 'failed' | ForEach-Object name)
                    skippedPhases = @($releaseSummary.phases | Where-Object status -eq 'skipped' | ForEach-Object name)
                }
            } catch {
                $releasePhase.reason = "Unable to parse release preflight summary: $($_.Exception.Message)"
                $releasePhase.status = 'failed'
            }
        }
        if ($releasePhase.status -eq 'failed' -and
            (@($releasePhase.outputTail) -join "`n") -match '(?i)(not recognized as an internal or external command|não é reconhecido como um comando|command not found|enoent)') {
            $releasePhase.status = 'unavailable'
            $releasePhase.reason = 'Release preflight could not run because a local tool or dependency was unavailable.'
        }
        [void]$summary.phases.Add($releasePhase)
        if ($releasePhase.status -eq 'failed') { $mechanicalFailed = $true }
        if ($releasePhase.status -eq 'unavailable') { $mechanicalUnavailable = $true }
    } finally {
        if (Test-Path -LiteralPath $releaseSummaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $releaseSummaryPath -Force -ErrorAction SilentlyContinue
        }
    }

    $afterStatusResult = Get-GitLines -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
    if ($afterStatusResult.exitCode -ne 0) {
        [void]$summary.reasons.Add('Unable to inspect the working tree after validation.')
        $mechanicalFailed = $true
    } else {
        $summary.workingTreePaths = @($afterStatusResult.lines)
        $workingTreeDirty = $afterStatusResult.lines.Count -gt 0
        $summary.workingTreeDirty = $workingTreeDirty
    }

    $decision = Get-PrePushDecision `
        -MechanicalFailed $mechanicalFailed `
        -MechanicalUnavailable $mechanicalUnavailable `
        -BaseRefAvailable $baseAvailable `
        -CommitsBehind $commitsBehind `
        -WorkingTreeDirty $workingTreeDirty

    foreach ($reason in @($decision.reasons)) {
        if (-not ($summary.reasons -contains $reason)) { [void]$summary.reasons.Add($reason) }
    }
    $summary.status = $decision.status
    $summary.exitCode = $decision.exitCode
    $summary.localReadiness = $decision.localReadiness
    $summary.pushReadiness = $decision.pushReadiness
    $summary.incompleteReasons = @($decision.reasons | Where-Object { $_ -match 'working tree|behind|base ref|unavailable' })
} catch {
    $mechanicalFailed = $true
    [void]$summary.reasons.Add($_.Exception.Message)
    $summary.status = 'failed'
    $summary.localReadiness = 'blocked'
    $summary.pushReadiness = 'blocked'
    $summary.exitCode = 1
}

$summary.endedAtUtc = [DateTime]::UtcNow.ToString('o')
$json = $summary | ConvertTo-Json -Depth 12
if ($AsJson) {
    [Console]::Out.WriteLine($json)
} else {
    Write-TextSummary -Summary $summary
}
exit ([int]$summary.exitCode)
