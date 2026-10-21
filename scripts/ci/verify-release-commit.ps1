[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Tag,
    # newline-delimited JSON check runs for $Commit (gh api .../check-runs --jq '.check_runs[]').
    [Parameter(Mandatory = $true)][string]$CheckRunsPath,
    [string]$MainRef = 'origin/main',
    [string]$RequiredCheck = 'test',
    # GitHub Actions; the same app the branch-protection rule pins for `test`.
    [int]$RequiredAppId = 15368,
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

# Publication gate: the release tag must point at the exact commit being published,
# that commit must already be on main, and GitHub Actions must have reported the
# required check green for that exact SHA. There is no skip input and no override.

$ErrorActionPreference = 'Stop'

function Fail-Release([string]$Message) {
    Write-Host "::error::Release commit verification failed: $Message"
    exit 1
}

function Invoke-Git([string[]]$Arguments) {
    $output = @(& git -C $Repository @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n").Trim() }
}

if ($Commit -notmatch '^[0-9a-f]{40}$') { Fail-Release "commit '$Commit' is not a full SHA." }

$tagCommit = Invoke-Git @('rev-parse', '--verify', "refs/tags/$Tag^{commit}")
if ($tagCommit.ExitCode -ne 0) { Fail-Release "tag '$Tag' does not resolve to a commit." }
if ($tagCommit.Output -ne $Commit) { Fail-Release "tag '$Tag' resolves to $($tagCommit.Output), not the checked-out $Commit." }

$ancestry = Invoke-Git @('merge-base', '--is-ancestor', $Commit, $MainRef)
if ($ancestry.ExitCode -ne 0) { Fail-Release "commit $Commit is not reachable from $MainRef; publish only commits that are on main." }

if (-not (Test-Path -LiteralPath $CheckRunsPath -PathType Leaf)) { Fail-Release "check-run evidence '$CheckRunsPath' was not produced." }
$runs = @(
    Get-Content -LiteralPath $CheckRunsPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json }
)
$candidates = @($runs | Where-Object { $_.name -eq $RequiredCheck -and $_.app.id -eq $RequiredAppId } |
    Sort-Object { [datetime]$_.started_at } -Descending)
if ($candidates.Count -eq 0) { Fail-Release "no '$RequiredCheck' check run from app $RequiredAppId exists for commit $Commit." }
$latest = $candidates[0]
if ($latest.status -ne 'completed' -or $latest.conclusion -ne 'success') {
    Fail-Release "latest '$RequiredCheck' check run for $Commit is $($latest.status)/$($latest.conclusion), not completed/success."
}
if ($latest.head_sha -ne $Commit) { Fail-Release "'$RequiredCheck' check run is bound to $($latest.head_sha), not $Commit." }

Write-Host "release commit $Commit verified: tag '$Tag', reachable from $MainRef, '$RequiredCheck' green for the exact SHA."
exit 0
