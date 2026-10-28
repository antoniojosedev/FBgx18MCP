[CmdletBinding()]
param(
    # toJSON(needs) from the gate job.
    [Parameter(Mandatory = $true)][string]$NeedsJson,
    [string]$SdkEvidenceRequired,
    [string]$SdkEnabled,
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$HeadRepository,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ExpectedCommit = $env:GITHUB_SHA,
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,
    [string]$LanePolicy = (Join-Path $PSScriptRoot 'lane-paths.json')
)

# The required `test` check must be red unless every lane succeeded and, when the
# change alters Engine.Backend routing or defaults, the protected SDK lane proved
# `pass` for this exact commit. A skipped lane is never evidence: the only lane
# that may be skipped is one the `changes` job classified, by an explicit 'false'
# output, as unreachable from this diff (scripts/ci/classify-lanes.ps1).

$ErrorActionPreference = 'Stop'
$optionalLane = 'sdk-validation'
$skippableLanes = @{}
foreach ($lane in @((Get-Content -LiteralPath $LanePolicy -Raw | ConvertFrom-Json).lanes.PSObject.Properties)) {
    $skippableLanes[$lane.Name] = [string]$lane.Value
}

function Write-Summary([string]$Line) {
    Write-Host $Line
    if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
        $Line | Out-File -LiteralPath $SummaryPath -Append -Encoding utf8
    }
}

function Fail-Gate([string]$Message) {
    Write-Summary "**blocked** - $Message"
    Write-Host "::error::CI gate failed: $Message"
    exit 1
}

$needs = $NeedsJson | ConvertFrom-Json
$lanes = @($needs.PSObject.Properties)
if ($lanes.Count -eq 0) { Fail-Gate 'the gate job has no needs context.' }

$changes = $lanes | Where-Object { $_.Name -eq 'changes' } | Select-Object -First 1
$failedLanes = @()
$skippedLanes = @()
foreach ($lane in @($lanes | Where-Object { $_.Name -ne $optionalLane })) {
    $result = [string]$lane.Value.result
    if ($result -eq 'success') { continue }
    if ($result -eq 'skipped' -and $skippableLanes.ContainsKey($lane.Name) -and $null -ne $changes -and $changes.Value.result -eq 'success') {
        $decision = [string]$changes.Value.outputs.($skippableLanes[$lane.Name])
        if ($decision -eq 'false') { $skippedLanes += $lane.Name; continue }
        $failedLanes += "$($lane.Name)=skipped although $($skippableLanes[$lane.Name])='$decision'"
        continue
    }
    $failedLanes += "$($lane.Name)=$result"
}
if ($failedLanes.Count -gt 0) { Fail-Gate "required lanes did not succeed: $($failedLanes -join ', ')." }
if ($skippedLanes.Count -gt 0) {
    Write-Summary "Lanes skipped because this diff cannot affect them: $($skippedLanes -join ', ') ($([string]$changes.Value.outputs.lanes_reason))."
}

if ($SdkEvidenceRequired -notin @('true', 'false')) {
    Fail-Gate "sdk_evidence_required must be 'true' or 'false', got '$SdkEvidenceRequired'."
}
if ($SdkEvidenceRequired -eq 'false') {
    Write-Summary 'GeneXus SDK validation lane: not required - no Engine.Backend routing or default path changed; the lane stays optional.'
    exit 0
}

$isFork = $EventName -eq 'pull_request' -and ([string]::IsNullOrWhiteSpace($HeadRepository) -or $HeadRepository -ne $Repository)
if ($isFork) {
    Fail-Gate "this fork pull request changes Engine.Backend routing or defaults. The licensed self-hosted runner never executes fork code, so SDK/live evidence cannot be produced; a maintainer must re-run the change from a branch in $Repository."
}
if ($SdkEnabled -ne 'true') {
    Fail-Gate 'this change alters Engine.Backend routing or defaults and requires SDK/live evidence, but the protected self-hosted lane is not enabled (set repository variable GXMCP_SDK_CI_ENABLED=true and register a [self-hosted, windows, gx-sdk-18] runner; see docs/ci-sdk-validation.md).'
}

$sdk = $lanes | Where-Object { $_.Name -eq $optionalLane } | Select-Object -First 1
if ($null -eq $sdk) { Fail-Gate "the gate job does not depend on $optionalLane." }
if ($sdk.Value.result -ne 'success') { Fail-Gate "$optionalLane concluded '$($sdk.Value.result)'; successful SDK/live evidence is required." }
$state = [string]$sdk.Value.outputs.state
if ($state -ne 'pass') { Fail-Gate "$optionalLane reported state '$state'; only 'pass' is evidence (skip means the runner lacked the licensed SDK, acknowledgement or fixture)." }
$evidenceCommit = [string]$sdk.Value.outputs.commit
if ([string]::IsNullOrWhiteSpace($ExpectedCommit) -or $evidenceCommit -ne $ExpectedCommit) {
    Fail-Gate "SDK evidence is bound to commit '$evidenceCommit', expected '$ExpectedCommit'."
}

Write-Summary "GeneXus SDK validation lane: **pass** for commit $evidenceCommit (required by an Engine.Backend routing/default change)."
exit 0
