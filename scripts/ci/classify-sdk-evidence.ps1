[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha,
    # Test seam: bypasses git and classifies exactly these repository-relative paths.
    [string[]]$ChangedFile,
    [string]$PathPolicy = (Join-Path $PSScriptRoot 'sdk-evidence-paths.json'),
    [string]$GitHubOutput = $env:GITHUB_OUTPUT
)

# Decides whether a change needs protected-runner SDK/live evidence. Every
# unknowable case (non-PR event, missing SHAs, failed diff) requires evidence:
# the classifier fails closed rather than guessing that a change is unrelated.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Write-Decision([bool]$Required, [string]$Reason, [string[]]$Matched = @()) {
    $lines = @(
        "sdk_evidence_required=$($Required.ToString().ToLowerInvariant())",
        "sdk_evidence_reason=$Reason",
        "sdk_evidence_matched=$($Matched -join ',')"
    )
    $lines | ForEach-Object { Write-Host $_ }
    if (-not [string]::IsNullOrWhiteSpace($GitHubOutput)) {
        $lines | Out-File -LiteralPath $GitHubOutput -Append -Encoding utf8
    }
    exit 0
}

$policy = Get-Content -LiteralPath $PathPolicy -Raw | ConvertFrom-Json
if ($policy.schemaVersion -ne 1 -or @($policy.patterns).Count -eq 0) {
    throw "Invalid SDK evidence path policy: $PathPolicy"
}

if (-not $PSBoundParameters.ContainsKey('ChangedFile')) {
    if ($EventName -ne 'pull_request') {
        Write-Decision $true "event '$EventName' has no pull-request diff to classify"
    }
    if ([string]::IsNullOrWhiteSpace($BaseSha) -or [string]::IsNullOrWhiteSpace($HeadSha)) {
        Write-Decision $true 'pull-request base/head SHA is unavailable'
    }
    $ChangedFile = @(& git -C $root diff --name-only "$BaseSha...$HeadSha" 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        Write-Decision $true "git diff $BaseSha...$HeadSha failed: $($ChangedFile -join ' ')"
    }
}

$matched = @(
    $ChangedFile |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Replace('\', '/') } |
        Where-Object { $path = $_; @($policy.patterns | Where-Object { $path -like $_ }).Count -gt 0 }
)
if ($matched.Count -gt 0) {
    Write-Decision $true 'change touches Engine.Backend routing or defaults' $matched
}
Write-Decision $false 'no Engine.Backend routing or default path changed'
