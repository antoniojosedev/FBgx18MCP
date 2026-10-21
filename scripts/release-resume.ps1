# Safe resume of an interrupted release. Reads scripts\release-doctor.ps1 -Remote (the
# read-only status: it reconciles tag/release/npm from GitHub and npm and the local
# preflight evidence) and runs the canonical release.ps1 with exactly the arguments the
# doctor derives - same version, never a new bump, gates skipped only when a complete
# passed certificate matches this commit and these artifacts.
#
#   pwsh -NoProfile -File scripts\release-resume.ps1 [-Version X.Y.Z] [-Detach] [-PrintOnly]
#
# Exit codes: release.ps1's own code when it runs; 0 when there is nothing to resume or
# with -PrintOnly; 3 when the doctor reports blockers (external state unknown, version
# would be re-bumped, tag or main mismatch); 4 when the doctor itself failed.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$StatusFile,
    [string]$LiveKbPath,
    [string]$LiveFixtureManifest,
    [switch]$RequireLive,
    [switch]$RequireBuildAll,
    [string]$Repository,
    [string]$Root,
    [switch]$Detach,
    [switch]$PrintOnly
)

$ErrorActionPreference = 'Stop'

# Write-Error would throw under Stop and replace the documented exit code with 1.
function Stop-Resume([int]$Code, [string]$Message) {
    [Console]::Error.WriteLine($Message)
    exit $Code
}
$repositoryRoot = if ([string]::IsNullOrWhiteSpace($Root)) { Split-Path -Parent $PSScriptRoot } else { [IO.Path]::GetFullPath($Root) }
$doctor = Join-Path $PSScriptRoot 'release-doctor.ps1'

$doctorArguments = @('-NoProfile', '-File', $doctor, '-Root', $repositoryRoot, '-Remote', '-Json')
foreach ($pair in @(
    @('-Version', $Version), @('-StatusFile', $StatusFile), @('-LiveKbPath', $LiveKbPath),
    @('-LiveFixtureManifest', $LiveFixtureManifest), @('-Repository', $Repository))) {
    if (-not [string]::IsNullOrWhiteSpace($pair[1])) { $doctorArguments += $pair }
}
if ($RequireLive) { $doctorArguments += '-RequireLive' }
if ($RequireBuildAll) { $doctorArguments += '-RequireBuildAll' }

$doctorOutput = @(& pwsh @doctorArguments)
$doctorExit = $LASTEXITCODE
if ($doctorExit -ne 0) {
    Stop-Resume 4 "release-doctor failed with exit code $doctorExit; refusing to resume without reconciled release state."
}
try { $state = ($doctorOutput -join [Environment]::NewLine) | ConvertFrom-Json }
catch {
    Stop-Resume 4 "release-doctor did not return valid JSON; refusing to resume."
}

Write-Host ("version={0} head={1} nextAction={2} resumeDecision={3}" -f $state.version, $state.head, $state.nextAction, $state.resumeDecision)
foreach ($gate in @($state.gates)) { Write-Host ("gate {0}: {1} exit={2}" -f $gate.name, $gate.status, $gate.exitCode) }

if ($state.resumeDecision -eq 'nothing-to-resume') {
    Write-Host "Release v$($state.version) is already published and verified; nothing to resume."
    exit 0
}
if ($state.resumeDecision -ne 'safe') {
    foreach ($blocker in @($state.resumeBlockers)) { Write-Host "blocker: $blocker" -ForegroundColor Yellow }
    Stop-Resume 3 "Resume is blocked for v$($state.version); resolve the blockers above (read-only status: scripts\release-doctor.ps1 -Remote)."
}

$releaseArguments = @($state.retryArguments)
$canonical = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'release.ps1'))
if ($releaseArguments.Count -lt 3 -or [IO.Path]::GetFullPath([string]$releaseArguments[0]) -ne $canonical -or
    [string]$releaseArguments[1] -cne '-Version' -or [string]$releaseArguments[2] -cne [string]$state.version) {
    Stop-Resume 4 "release-doctor returned an unexpected resume command; refusing to run it."
}
if ($Detach) { $releaseArguments += '-Detach' }

Write-Host ("resume: pwsh -NoProfile -File {0}" -f (($releaseArguments | ForEach-Object { "'$_'" }) -join ' '))
if ($PrintOnly) { exit 0 }
Push-Location $repositoryRoot
try {
    & pwsh -NoProfile -File @releaseArguments
    $releaseExit = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $releaseExit
