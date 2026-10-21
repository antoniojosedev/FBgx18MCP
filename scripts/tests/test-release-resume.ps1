$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# release-resume.ps1 acts only on the doctor's reconciled verdict and runs the canonical
# release.ps1 with the doctor's arguments. The doctor and release.ps1 are stubs here so the
# decision table and exit-code propagation are tested without any network or release.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$temp = Join-Path $env:TEMP ('gxmcp-release-resume-' + [guid]::NewGuid().ToString('N'))
$scripts = Join-Path $temp 'scripts'
New-Item -ItemType Directory -Path $scripts -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $root 'scripts\release-resume.ps1') -Destination $scripts
    $verdictPath = Join-Path $temp 'verdict.json'
    $ranMarker = Join-Path $temp 'release-ran.txt'
    $env:GXMCP_STUB_VERDICT = $verdictPath
    $env:GXMCP_STUB_RAN = $ranMarker
    $env:GXMCP_STUB_RELEASE_EXIT = '0'
    Set-Content -LiteralPath (Join-Path $scripts 'release-doctor.ps1') -Encoding utf8 -Value @'
param([string]$Root, [string]$Version, [switch]$Remote, [switch]$Json, [string]$StatusFile, [string]$LiveKbPath, [string]$LiveFixtureManifest, [string]$Repository, [switch]$RequireLive, [switch]$RequireBuildAll)
if ($env:GXMCP_STUB_DOCTOR_EXIT) { exit [int]$env:GXMCP_STUB_DOCTOR_EXIT }
if (-not $Remote -or -not $Json) { Write-Error 'resume must ask the doctor for -Remote -Json'; exit 9 }
Get-Content -LiteralPath $env:GXMCP_STUB_VERDICT -Raw
'@
    Set-Content -LiteralPath (Join-Path $temp 'release.ps1') -Encoding utf8 -Value @'
param([string]$Version, [switch]$Detach, [switch]$SkipBuild, [switch]$SkipTests)
Set-Content -LiteralPath $env:GXMCP_STUB_RAN -Value "version=$Version detach=$($Detach.IsPresent) skipTests=$($SkipTests.IsPresent)"
exit ([int]$env:GXMCP_STUB_RELEASE_EXIT)
'@
    $canonical = Join-Path $temp 'release.ps1'
    function Write-Verdict([string]$Decision, [string[]]$Blockers = @(), [object[]]$Arguments = $null) {
        if ($null -eq $Arguments) { $Arguments = @($canonical, '-Version', '4.0.0', '-SkipBuild', '-SkipTests') }
        [ordered]@{
            version = '4.0.0'; head = ('a' * 40); nextAction = 'retry-release'; resumeDecision = $Decision
            resumeBlockers = @($Blockers); gates = @([ordered]@{ name = 'CLI tests'; status = 'passed'; exitCode = 0 }); retryArguments = @($Arguments)
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $verdictPath -Encoding utf8
        Remove-Item -LiteralPath $ranMarker -Force -ErrorAction SilentlyContinue
    }
    function Invoke-Resume([string[]]$Extra = @()) {
        $text = (& pwsh -NoProfile -File (Join-Path $scripts 'release-resume.ps1') -Root $temp @Extra 2>&1 | Out-String)
        return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text }
    }

    # Safe: runs the canonical script with the doctor's exact arguments and returns its exit code.
    Write-Verdict 'safe'
    $env:GXMCP_STUB_RELEASE_EXIT = '23'
    $result = Invoke-Resume
    if ($result.Code -ne 23) { throw "Resume must return the release's exit code (23), got $($result.Code): $($result.Text)" }
    if ((Get-Content -LiteralPath $ranMarker -Raw) -notmatch 'version=4.0.0 detach=False skipTests=True') { throw 'Resume did not pass the doctor-derived arguments.' }
    $env:GXMCP_STUB_RELEASE_EXIT = '0'
    Remove-Item -LiteralPath $ranMarker -Force
    $result = Invoke-Resume @('-Detach')
    if ($result.Code -ne 0 -or (Get-Content -LiteralPath $ranMarker -Raw) -notmatch 'detach=True') { throw "-Detach must be forwarded: $($result.Text)" }
    Remove-Item -LiteralPath $ranMarker -Force
    $result = Invoke-Resume @('-PrintOnly')
    if ($result.Code -ne 0 -or (Test-Path -LiteralPath $ranMarker) -or $result.Text -notmatch 'resume:') { throw '-PrintOnly must show the command without running it.' }

    # Blocked: nothing runs, exit 3, blockers are shown.
    Write-Verdict 'blocked' @('External state unknown: gh release view failed')
    $result = Invoke-Resume
    if ($result.Code -ne 3 -or $result.Text -notmatch 'External state unknown' -or (Test-Path -LiteralPath $ranMarker)) { throw "Blocked resume must exit 3 without running release.ps1: $($result.Text)" }

    # Already published: nothing to do, exit 0, nothing runs (no republish).
    Write-Verdict 'nothing-to-resume'
    $result = Invoke-Resume
    if ($result.Code -ne 0 -or (Test-Path -LiteralPath $ranMarker) -or $result.Text -notmatch 'nothing to resume') { throw "A published release must not be resumed: $($result.Text)" }

    # A doctor verdict pointing anywhere but the canonical release.ps1 / the same version is refused.
    Write-Verdict 'safe' @() @((Join-Path $temp 'other.ps1'), '-Version', '4.0.0')
    $result = Invoke-Resume
    if ($result.Code -ne 4 -or (Test-Path -LiteralPath $ranMarker)) { throw "A foreign resume command must be refused (exit 4): $($result.Text)" }
    Write-Verdict 'safe' @() @($canonical, '-Version', '9.9.9')
    $result = Invoke-Resume
    if ($result.Code -ne 4 -or (Test-Path -LiteralPath $ranMarker)) { throw "A resume that would change the version must be refused (exit 4): $($result.Text)" }

    # A failing doctor means unreconciled state: refuse, never guess.
    Write-Verdict 'safe'
    $env:GXMCP_STUB_DOCTOR_EXIT = '5'
    $result = Invoke-Resume
    Remove-Item Env:GXMCP_STUB_DOCTOR_EXIT
    if ($result.Code -ne 4 -or (Test-Path -LiteralPath $ranMarker)) { throw "A failing doctor must block resume (exit 4): $($result.Text)" }

    # The resume script itself never mutates anything but through release.ps1.
    $source = Get-Content -LiteralPath (Join-Path $root 'scripts\release-resume.ps1') -Raw
    foreach ($forbidden in @('git push', 'git tag', 'gh release', 'npm publish', '-SkipTests', '-SkipBuild')) {
        if ($source -match [regex]::Escape($forbidden) -and $source -notmatch "#.*$([regex]::Escape($forbidden))") { throw "release-resume.ps1 must not decide or perform: $forbidden" }
    }
    Write-Host 'release-resume: doctor-driven decision table and exit-code propagation passed' -ForegroundColor Green
} finally {
    foreach ($name in @('GXMCP_STUB_VERDICT', 'GXMCP_STUB_RAN', 'GXMCP_STUB_RELEASE_EXIT', 'GXMCP_STUB_DOCTOR_EXIT')) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
