[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha,
    # Test seam: bypasses git and classifies exactly these repository-relative paths.
    [string[]]$ChangedFile,
    # Test seam: the repository whose base...head diff is classified.
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$PathPolicy = (Join-Path $PSScriptRoot 'lane-paths.json'),
    [string]$GitHubOutput = $env:GITHUB_OUTPUT
)

# Decides which skippable CI lanes a pull request's changed files can affect.
# Every unknowable case (non-PR event, missing SHAs, failed or empty diff, a path
# no rule maps, a CI-definition change) runs every lane: the classifier fails
# closed rather than guessing that a lane is unaffected. contracts always runs.

$ErrorActionPreference = 'Stop'

$policy = Get-Content -LiteralPath $PathPolicy -Raw | ConvertFrom-Json
$lanes = [ordered]@{}
foreach ($lane in @($policy.lanes.PSObject.Properties)) { $lanes[$lane.Name] = [string]$lane.Value }
if ($policy.schemaVersion -ne 1 -or $lanes.Count -eq 0 -or @($policy.rules).Count -eq 0) {
    throw "Invalid lane path policy: $PathPolicy"
}

function Write-Decision([string[]]$Run, [string]$Reason) {
    $lines = @(foreach ($lane in $lanes.Keys) { "$($lanes[$lane])=$(($Run -contains $lane).ToString().ToLowerInvariant())" })
    $lines += "lanes_reason=$Reason"
    $lines | ForEach-Object { Write-Host $_ }
    if (-not [string]::IsNullOrWhiteSpace($GitHubOutput)) {
        $lines | Out-File -LiteralPath $GitHubOutput -Append -Encoding utf8
    }
    exit 0
}

$allLanes = @($lanes.Keys)

if (-not $PSBoundParameters.ContainsKey('ChangedFile')) {
    if ($EventName -ne 'pull_request') {
        Write-Decision $allLanes "event '$EventName' has no pull-request diff to classify"
    }
    if ([string]::IsNullOrWhiteSpace($BaseSha) -or [string]::IsNullOrWhiteSpace($HeadSha)) {
        Write-Decision $allLanes 'pull-request base/head SHA is unavailable'
    }
    # --no-renames lists both sides of a move; a rename would otherwise show only the new path.
    $ChangedFile = @(& git -C $Repository diff --name-only --no-renames "$BaseSha...$HeadSha" 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        Write-Decision $allLanes "git diff $BaseSha...$HeadSha failed: $($ChangedFile -join ' ')"
    }
}

$paths = @($ChangedFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim().Replace('\', '/') })
if ($paths.Count -eq 0) { Write-Decision $allLanes 'the diff lists no changed files' }

$run = [System.Collections.Generic.HashSet[string]]::new()
foreach ($path in $paths) {
    $rule = $policy.rules | Where-Object { $candidate = $_; @($candidate.patterns | Where-Object { $path -like $_ }).Count -gt 0 } | Select-Object -First 1
    if ($null -eq $rule) { Write-Decision $allLanes "no lane rule maps '$path'" }
    if (@($rule.lanes) -contains 'all') { Write-Decision $allLanes "'$path' changes how every lane runs ($($rule.reason))" }
    foreach ($lane in @($rule.lanes)) {
        if (-not $lanes.Contains([string]$lane)) { throw "Lane rule '$($rule.reason)' names unknown lane '$lane'." }
        [void]$run.Add([string]$lane)
    }
}

$skipped = @($allLanes | Where-Object { -not $run.Contains($_) })
$reason = if ($skipped.Count -eq 0) { "$($paths.Count) changed file(s) reach every lane" } else { "$($paths.Count) changed file(s) cannot affect: $($skipped -join ', ')" }
Write-Decision @($allLanes | Where-Object { $run.Contains($_) }) $reason
