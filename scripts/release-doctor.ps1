[CmdletBinding()]
param(
    [string]$Version,
    [string]$StatusFile,
    [string]$PreflightSummaryPath,
    [string]$Root,
    [string]$LiveKbPath,
    [string]$LiveFixtureManifest,
    [switch]$RequireLive,
    [switch]$RequireBuildAll,
    [string]$Repository,
    [switch]$Remote,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$root = if ([string]::IsNullOrWhiteSpace($Root)) { Split-Path -Parent $PSScriptRoot } else { [IO.Path]::GetFullPath($Root) }
. (Join-Path $PSScriptRoot 'release-contract.ps1')

function Read-OptionalJson([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "Invalid JSON state file: $Path" }
}

function Get-OptionalValue([object]$Object, [string]$PropertyName) {
    return Get-GxMcpReleaseFieldValue -Object $Object -Name $PropertyName
}

function Get-Sha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Protect-DoctorMessage([object]$Value) {
    return Protect-GxMcpReleaseMessage $Value
}

$packagePath = Join-Path $root 'package.json'
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "package.json not found: $packagePath" }
$package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = [string]$package.version }
$Version = $Version -replace '^v', ''
$tag = "v$Version"

if ([string]::IsNullOrWhiteSpace($StatusFile)) {
    $candidates = @(
        Get-ChildItem -LiteralPath $env:TEMP -Filter "gxmcp-release-status-$Version-*.json" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending
    )
    $exactStatus = Join-Path $env:TEMP "gxmcp-release-status-$Version.json"
    if (Test-Path -LiteralPath $exactStatus -PathType Leaf) { $candidates = @($candidates) + @(Get-Item -LiteralPath $exactStatus) }
    if ($candidates.Count -gt 0) { $StatusFile = $candidates[0].FullName }
}
if (-not [string]::IsNullOrWhiteSpace($StatusFile)) { $StatusFile = [IO.Path]::GetFullPath($StatusFile) }
$status = Read-OptionalJson -Path $StatusFile
$preflightPath = if ([string]::IsNullOrWhiteSpace($PreflightSummaryPath)) { Join-Path $env:TEMP "gxmcp-release-preflight-$Version.json" } else { [IO.Path]::GetFullPath($PreflightSummaryPath) }
$preflight = Read-OptionalJson -Path $preflightPath
$publicationPathValue = Get-OptionalValue $status 'publicationEvidencePath'
$publicationPath = if ($publicationPathValue) { [IO.Path]::GetFullPath([string]$publicationPathValue) } elseif ($StatusFile) { "$StatusFile.publication.json" } else { $null }
$publication = Read-OptionalJson -Path $publicationPath

$head = $null
$branch = $null
$workingTree = @()
try {
    $head = ((& git -C $root rev-parse HEAD 2>$null) -join '').Trim()
    $branch = ((& git -C $root rev-parse --abbrev-ref HEAD 2>$null) -join '').Trim()
    $workingTree = @(& git -C $root status --porcelain --untracked-files=no 2>$null)
} catch { }

$artifactDefinitions = @(
    [ordered]@{ name = 'publish/GxMcp.Gateway.exe'; required = $true },
    [ordered]@{ name = 'publish/worker/GxMcp.Worker.exe'; required = $true },
    [ordered]@{ name = 'publish/tool_definitions.json'; required = $true },
    [ordered]@{ name = 'publish/nexus-ide.vsix'; required = $true },
    [ordered]@{ name = "nexus-ide-$Version.vsix"; required = $false },
    [ordered]@{ name = 'publish.zip'; required = $false },
    [ordered]@{ name = 'publish.zip.sha256'; required = $false }
)
$artifacts = @($artifactDefinitions | ForEach-Object {
    $path = Join-Path $root ($_.name -replace '/', '\')
    [ordered]@{
        name = $_.name
        required = [bool]$_.required
        exists = Test-Path -LiteralPath $path -PathType Leaf
        size = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-Item -LiteralPath $path).Length } else { $null }
        sha256 = Get-Sha256 -Path $path
    }
})
$requiredArtifactsReady = @($artifacts | Where-Object { $_.required -and -not $_.exists }).Count -eq 0
$currentArtifactFingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $root -Version $Version -AllowLegacy:(Test-GxMcpReleaseLegacyVersion $Version)
$gxCatalog = $null
try {
    . (Join-Path $root 'scripts\gx-version-catalog.ps1')
    $gxCatalog = Get-GxVersionCatalog -Root $root
} catch { }
$currentGxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) { $env:GX_PATH } elseif ($gxCatalog) { Get-GxPrimaryInstallPath -Catalog $gxCatalog } else { $null }
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $originUrl = ((& git -C $root remote get-url origin 2>$null) -join '').Trim()
    if ($originUrl -match 'github\.com[:/](?<repo>[^/]+/[^/.]+)(?:\.git)?$') {
        $Repository = $Matches.repo
    } else {
        $Repository = 'lennix1337/Genexus18MCP'
    }
}
$requestedLiveKbPath = if ([string]::IsNullOrWhiteSpace($LiveKbPath)) { $null } else { [IO.Path]::GetFullPath($LiveKbPath) }
$requestedFixtureManifest = if ([string]::IsNullOrWhiteSpace($LiveFixtureManifest)) { $null } else { [IO.Path]::GetFullPath($LiveFixtureManifest) }
$doctorCatalog = if ($gxCatalog) {
    $gxCatalog
} else {
    [pscustomobject]@{ primaryMajor = '18'; supportedMajors = @() }
}
$expectedDoctorInputs = Resolve-GxMcpReleasePreflightInputs `
    -Root $root -Catalog $doctorCatalog -GxPath $currentGxPath -Version $Version -SourceCommit $head `
    -LiveKbPath $requestedLiveKbPath -LiveFixtureManifest $requestedFixtureManifest `
    -RequireLive:$RequireLive -RequireBuildAll:$RequireBuildAll -ArtifactFingerprint $currentArtifactFingerprint

$remoteState = [ordered]@{
    checked = [bool]$Remote
    tagExists = $null
    tagCommit = $null
    originMainHead = $null
    releaseExists = $null
    npmPublished = $null
    releaseUrl = $null
    assets = @()
    assetVerification = $null
    workflowRunId = $null
    workflowStatus = $null
    workflowConclusion = $null
    npmVersion = $null
    npmGitHead = $null
    publicationValid = $false
    errors = @()
}
if ($Remote) {
    # Absence is concluded only from an explicit, successful negative answer. A failed
    # request leaves the field $null (unknown) and records an error, so a network or
    # auth failure can never be read as "tag/release/package is missing".
    try {
        $tagLines = @(& git -C $root ls-remote --tags origin "refs/tags/$Tag^{}" 2>$null)
        if ($LASTEXITCODE -ne 0) { throw "git ls-remote for tag $tag failed (exit $LASTEXITCODE); tag state is unknown." }
        if ($tagLines.Count -gt 0) {
            $remoteState.tagExists = $true
            $remoteState.tagCommit = (($tagLines[0].ToString().Trim()) -split '\s+')[0]
        } else { $remoteState.tagExists = $false }
    } catch { $remoteState.errors += Protect-DoctorMessage $_.Exception.Message }
    try {
        $mainLines = @(& git -C $root ls-remote --heads origin 'refs/heads/main' 2>$null)
        if ($LASTEXITCODE -ne 0 -or $mainLines.Count -ne 1) { throw "git ls-remote for origin/main failed (exit $LASTEXITCODE); base state is unknown." }
        $remoteState.originMainHead = (($mainLines[0].ToString().Trim()) -split '\s+')[0]
    } catch { $remoteState.errors += Protect-DoctorMessage $_.Exception.Message }
    try {
        $releaseOutput = @(& gh release view $tag --repo $Repository --json tagName,url,assets,isDraft,publishedAt 2>&1)
        $releaseExit = $LASTEXITCODE
        $releaseRaw = @($releaseOutput | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
        if ($releaseExit -ne 0 -and ($releaseOutput -join ' ') -match 'release not found') {
            $remoteState.releaseExists = $false
        } elseif ($releaseExit -ne 0) {
            throw "gh release view failed for $tag (exit $releaseExit); release state is unknown."
        } else {
            $remoteState.releaseExists = $true
            $release = (($releaseRaw -join [Environment]::NewLine) | ConvertFrom-Json)
            $remoteState.releaseUrl = [string]$release.url
            $remoteState.assets = @($release.assets | ForEach-Object { [string]$_.name } | Sort-Object)
            $remoteState.assetVerification = Get-GxMcpReleaseAssetVerification -Assets @($release.assets) -RepositoryRoot $root -Version $Version -AllowLegacy:(Test-GxMcpReleaseLegacyVersion $Version)
        }
    } catch { $remoteState.errors += Protect-DoctorMessage $_.Exception.Message }
    try {
        $runRaw = @(& gh run list --repo $Repository --workflow release.yml --limit 50 --json databaseId,status,conclusion,headSha,headBranch,event,createdAt,url 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'gh run list failed.' }
        $runs = @(($runRaw -join [Environment]::NewLine | ConvertFrom-Json) | Sort-Object createdAt -Descending)
        $expectedRemoteCommit = if ($remoteState.tagCommit) { [string]$remoteState.tagCommit } else { [string]$head }
        $minimumAssetUpdate = if ($remoteState.assetVerification -and $remoteState.assetVerification.latestUpdatedAtUtc) { [string]$remoteState.assetVerification.latestUpdatedAtUtc } else { $null }
        $run = @($runs | Where-Object {
            Test-GxMcpReleaseWorkflowRun -Run $_ -ExpectedCommit $expectedRemoteCommit -Tag $tag `
                -MinimumCreatedAtUtc $minimumAssetUpdate -AllowDispatch
        } | Select-Object -First 1)
        if ($run.Count -gt 0) {
            $remoteState.workflowRunId = [string]$run[0].databaseId
            $remoteState.workflowStatus = [string]$run[0].status
            $remoteState.workflowConclusion = [string]$run[0].conclusion
        }
    } catch { $remoteState.errors += Protect-DoctorMessage $_.Exception.Message }
    try {
        # Read-only registry query. stderr is captured (not discarded): npm reports "not
        # published" as E404 on stdout (--json) and stderr, while network/auth failures carry
        # other codes (ENOTFOUND, ETIMEDOUT, E401...) that must stay "unknown", never "absent".
        $npmOutput = @(& npm view "genexus-mcp@$Version" version gitHead --json --fetch-retries=0 --fetch-timeout=5000 2>&1)
        $npmExit = $LASTEXITCODE
        $npmRaw = @($npmOutput | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ })
        $npmText = ($npmOutput | ForEach-Object { [string]$_ }) -join "`n"
        if ($npmExit -eq 0 -and $npmRaw.Count -gt 0) {
            $npmRecord = (($npmRaw -join [Environment]::NewLine) | ConvertFrom-Json)
            if ($npmRecord -is [array]) { $npmRecord = @($npmRecord)[0] }
            $remoteState.npmPublished = $true
            $remoteState.npmVersion = [string]$npmRecord.version
            $remoteState.npmGitHead = [string]$npmRecord.gitHead
        } elseif ($npmExit -ne 0 -and $npmText -match '(^|\W)E404(\W|$)|404 No match found') {
            $remoteState.npmPublished = $false
        } else {
            $npmDetail = (($npmText -split "`n" | Where-Object { $_ -match '\S' } | Select-Object -First 2) -join ' | ')
            if ($npmDetail.Length -gt 300) { $npmDetail = $npmDetail.Substring(0, 300) }
            throw "npm view genexus-mcp@$Version failed (exit $npmExit): $npmDetail; registry state is unknown."
        }
    } catch { $remoteState.errors += Protect-DoctorMessage $_.Exception.Message }
}
$remoteAssetValid = $Remote -and $remoteState.assetVerification -and [bool]$remoteState.assetVerification.isValid
$remoteTagCommitValid = $Remote -and (Test-GxMcpReleaseCommitId $remoteState.tagCommit)
$remoteReleaseUrlValid = $Remote -and [string]$remoteState.releaseUrl -ceq "https://github.com/$Repository/releases/tag/$tag"
$remoteWorkflowValid = $Remote -and
    $remoteState.tagExists -eq $true -and
    -not [string]::IsNullOrWhiteSpace([string]$remoteState.tagCommit) -and
    -not [string]::IsNullOrWhiteSpace([string]$remoteState.workflowRunId) -and
    [string]$remoteState.workflowStatus -ceq 'completed' -and
    [string]$remoteState.workflowConclusion -ceq 'success' -and
    @($remoteState.errors).Count -eq 0
$remoteNpmValid = $Remote -and [string]$remoteState.npmVersion -ceq $Version -and
    ((Test-GxMcpReleaseLegacyVersion $Version) -or [string]$remoteState.npmGitHead -ceq [string]$remoteState.tagCommit)
$remotePublicationValid = $Remote -and $remoteAssetValid -and $remoteTagCommitValid -and $remoteReleaseUrlValid -and $remoteWorkflowValid -and $remoteNpmValid
$remoteState.publicationValid = $remotePublicationValid

# Nulls are dropped at materialisation, not after it. `@($null).Count` is 1, not 0, so a
# summary whose `phases` key was absent entered the check below and then evaluated
# `$_.status` on that single $null element, which throws under StrictMode - and the doctor
# is the script docs/RELEASE.md tells an operator to run first when a preflight is
# interrupted. Initialising to @() rather than $null fixes the other half: an empty
# `phases: []` left the sentinel null and the `.Count` in the report threw. See issue #348.
$failedPhase = @()
$preflightPhases = @(Get-OptionalValue $preflight 'phases' | Where-Object { $null -ne $_ })
if ($preflight -and $preflightPhases.Count -gt 0) {
    $failedPhase = @($preflightPhases | Where-Object { $_.status -in @('failed', 'timeout', 'running') } | Select-Object -First 1)
}
$publicationState = if ($publication -and (Get-OptionalValue $publication 'state')) { [string](Get-OptionalValue $publication 'state') } elseif ($status -and (Get-OptionalValue $status 'publicationState')) { [string](Get-OptionalValue $status 'publicationState') } else { 'not-started' }
$publicationEvidenceValid = $false
$publicationEvidenceError = $null
if ($publication -and $status) {
    $publicationCheck = Test-GxMcpReleasePublicationEvidence -Status $status -Publication $publication -RepositoryRoot $root -Repository $Repository -AllowLegacy:(Test-GxMcpReleaseLegacyVersion $Version)
    $publicationEvidenceValid = [bool]$publicationCheck.Valid
    $publicationEvidenceError = Protect-DoctorMessage $publicationCheck.Error
    if (-not $publicationEvidenceValid -and ([string](Get-OptionalValue $status 'state') -eq 'succeeded' -or [string](Get-OptionalValue $publication 'state') -eq 'failed')) {
        $publicationState = 'failed'
    }
}
$preflightSourceCommit = if ($remoteState.tagCommit) { [string]$remoteState.tagCommit } else { [string]$head }
$expectedDoctorInputs = Resolve-GxMcpReleasePreflightInputs `
    -Root $root -Catalog $doctorCatalog -GxPath $currentGxPath -Version $Version -SourceCommit $preflightSourceCommit `
    -LiveKbPath $requestedLiveKbPath -LiveFixtureManifest $requestedFixtureManifest `
    -RequireLive:$RequireLive -RequireBuildAll:$RequireBuildAll -ArtifactFingerprint $currentArtifactFingerprint
$preflightMatchesInputs = $preflight -and $requiredArtifactsReady -and
    (Test-GxMcpReleasePreflightCertificate -Summary $preflight -Expected $expectedDoctorInputs)
function Quote-DoctorArgument([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}
$retryParts = @('pwsh', '-NoProfile', '-File', (Join-Path $root 'release.ps1'), '-Version', $Version)
$retryCommand = ($retryParts | ForEach-Object { Quote-DoctorArgument $_ }) -join ' '
$nextAction = 'start-release'
# A publication that GitHub and npm both confirm is complete whether or not this machine still
# has the local status file (it lives in %TEMP%); remote truth must not depend on it. A local
# status that says the run failed or is still running is left to the branches below, since an
# interrupted issue-closure step is invisible to the remote checks.
$statusState = if ($status) { [string](Get-OptionalValue $status 'state') } else { $null }
if ($Remote -and $remotePublicationValid -and (-not $status -or $statusState -eq 'succeeded')) {
    $nextAction = 'complete'
} elseif ($status -and [string](Get-OptionalValue $status 'state') -eq 'succeeded' -and $publicationState -eq 'verified' -and
    $publicationEvidenceValid -and (-not $Remote -or $remotePublicationValid)) {
    $nextAction = 'complete'
} elseif ($status -and [string](Get-OptionalValue $status 'state') -eq 'failed') {
    $nextAction = if ($failedPhase) {
        'fix-preflight-and-retry'
    } elseif ($publicationState -in @('pending', 'failed')) {
        'retry-publication'
    } else {
        'retry-release'
    }
} elseif ($preflight -and [string](Get-OptionalValue $preflight 'status') -eq 'failed') {
    $nextAction = 'fix-preflight-and-retry'
} elseif ($remoteState.tagExists -eq $true -and $publicationState -eq 'pending') {
    $nextAction = 'wait-for-publication'
} elseif ($remoteState.tagExists -eq $true) {
    $nextAction = 'resume-release'
} elseif ($preflight -and [string](Get-OptionalValue $preflight 'status') -eq 'passed' -and $preflightMatchesInputs) {
    $nextAction = 'retry-release'
}
if ($preflightMatchesInputs) {
    if ($expectedDoctorInputs.liveKbPath) { $retryParts += @('-LiveKbPath', [string]$expectedDoctorInputs.liveKbPath) }
    if ($expectedDoctorInputs.liveFixtureManifest) { $retryParts += @('-LiveFixtureManifest', [string]$expectedDoctorInputs.liveFixtureManifest) }
    if ($expectedDoctorInputs.requireLive) { $retryParts += '-RequireLive' }
    if ($expectedDoctorInputs.requireBuildAll) { $retryParts += '-RequireBuildAll' }
    $retryParts += @('-SkipBuild', '-SkipTests')
    $retryCommand = ($retryParts | ForEach-Object { Quote-DoctorArgument $_ }) -join ' '
}

# Local commit facts and resume safety. Resume is offered only when every external fact it
# depends on was actually read (-Remote, no failed request) and nothing would re-bump the
# version, move a tag, or publish twice. Unknown is a blocker, never "missing".
$headSubject = ((& git -C $root log -1 --format=%s 2>$null) -join '').Trim()
$headParent = ((& git -C $root rev-parse -q --verify 'HEAD^' 2>$null) -join '').Trim()
$localTagCommit = ((& git -C $root rev-parse -q --verify "refs/tags/$tag^{commit}" 2>$null) -join '').Trim()
$commitVersion = if ($headSubject -match '^release: v(?<v>\S+)$') { $Matches.v } else { $null }
$resumeBlockers = New-Object System.Collections.Generic.List[string]
if ($commitVersion -and $commitVersion -cne $Version) {
    [void]$resumeBlockers.Add("HEAD is the release commit for v$commitVersion; resuming v$Version would bump the version again.")
}
if (-not $Remote) {
    [void]$resumeBlockers.Add('External tag/release/npm state was not read; rerun with -Remote.')
}
foreach ($remoteError in @($remoteState.errors)) { [void]$resumeBlockers.Add("External state unknown: $remoteError") }
if ($localTagCommit -and $localTagCommit -cne $head) {
    [void]$resumeBlockers.Add("Local tag $tag points at $localTagCommit, not HEAD $head.")
}
if ($Remote -and $remoteState.tagExists -eq $true -and $remoteState.tagCommit -cne $head) {
    [void]$resumeBlockers.Add("Remote tag $tag is pinned to $($remoteState.tagCommit), not HEAD $head.")
}
if ($Remote -and $remoteState.tagExists -eq $false -and $remoteState.originMainHead) {
    $baseAligned = $head -ceq $remoteState.originMainHead -or
        ($headParent -ceq $remoteState.originMainHead -and $headSubject -ceq "release: $tag")
    if (-not $baseAligned) {
        [void]$resumeBlockers.Add("HEAD is not origin/main ($($remoteState.originMainHead)) or the single unpublished release commit on top of it.")
    }
}
$resumeBlockerList = @($resumeBlockers)
$resumeDecision = if ($nextAction -eq 'complete') { 'nothing-to-resume' } elseif ($resumeBlockerList.Count -gt 0) { 'blocked' } else { 'safe' }
$gateRecords = @($preflightPhases | ForEach-Object { [ordered]@{ name = $_.name; status = $_.status; exitCode = Get-OptionalValue $_ 'exitCode' } })

$result = [ordered]@{
    schemaVersion = 'gxmcp-release-doctor/1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    version = $Version
    tag = $tag
    root = $root
    head = $head
    branch = $branch
    workingTreeDirty = $workingTree.Count -gt 0
    statusFile = $StatusFile
    status = if ($status) { [ordered]@{ state = Get-OptionalValue $status 'state'; phase = Get-OptionalValue $status 'phase'; error = Protect-DoctorMessage (Get-OptionalValue $status 'error') } } else { $null }
    preflight = if ($preflight) { [ordered]@{ path = $preflightPath; status = Get-OptionalValue $preflight 'status'; failedPhase = if ($failedPhase.Count -gt 0) { $failedPhase.name } else { $null }; artifactFingerprint = Get-OptionalValue $preflight 'artifactFingerprint' } } else { $null }
    publication = if ($publication) { [ordered]@{ path = $publicationPath; state = Get-OptionalValue $publication 'state'; workflowRunId = Get-OptionalValue $publication 'workflowRunId'; workflowStatus = Get-OptionalValue $publication 'workflowStatus'; workflowConclusion = Get-OptionalValue $publication 'workflowConclusion'; npmVersion = Get-OptionalValue $publication 'npmVersion'; error = Protect-DoctorMessage (Get-OptionalValue $publication 'error') } } else { $null }
    publicationEvidenceValid = [bool]$publicationEvidenceValid
    publicationEvidenceError = $publicationEvidenceError
    repository = $Repository
    artifacts = $artifacts
    artifactFingerprint = $currentArtifactFingerprint
    requiredArtifactsReady = $requiredArtifactsReady
    preflightMatchesInputs = [bool]$preflightMatchesInputs
    headSubject = $headSubject
    commitVersion = $commitVersion
    localTagCommit = if ($localTagCommit) { $localTagCommit } else { $null }
    gates = $gateRecords
    remote = $remoteState
    nextAction = $nextAction
    resumeDecision = $resumeDecision
    resumeBlockers = $resumeBlockerList
    retryCommand = $retryCommand
    retryArguments = @($retryParts | Select-Object -Skip 3)
}

if ($Json) {
    $result | ConvertTo-Json -Depth 12
    exit 0
}

Write-Host "version=$Version tag=$tag branch=$branch head=$head" -ForegroundColor Cyan
Write-Host "statusFile=$StatusFile"
if ($status) { Write-Host "status=$(Get-OptionalValue $status 'state') phase=$(Get-OptionalValue $status 'phase')" }
if ($publication) { Write-Host "publicationEvidenceValid=$publicationEvidenceValid" }
if ($preflight) { Write-Host "preflight=$(Get-OptionalValue $preflight 'status') failedPhase=$(if ($failedPhase.Count -gt 0) { $failedPhase.name } else { $null })" }
Write-Host "requiredArtifactsReady=$requiredArtifactsReady artifactFingerprint=$currentArtifactFingerprint"
Write-Host "preflightMatchesInputs=$preflightMatchesInputs"
foreach ($artifact in $artifacts) { Write-Host ("artifact {0}: exists={1} size={2}" -f $artifact.name, $artifact.exists, $artifact.size) }
if ($Remote) {
    Write-Host "remoteTagExists=$($remoteState.tagExists) remoteTagCommit=$($remoteState.tagCommit)"
    Write-Host "remoteAssets=$($remoteState.assets -join ',')"
    Write-Host "workflow=$($remoteState.workflowStatus)/$($remoteState.workflowConclusion) run=$($remoteState.workflowRunId)"
    Write-Host "npmVersion=$($remoteState.npmVersion) npmGitHead=$($remoteState.npmGitHead) remotePublicationValid=$($remoteState.publicationValid)"
    foreach ($errorMessage in @($remoteState.errors)) { Write-Host "remoteDiagnostic=$errorMessage" -ForegroundColor Yellow }
}
foreach ($gate in $gateRecords) { Write-Host ("gate {0}: {1} exit={2}" -f $gate.name, $gate.status, $gate.exitCode) }
Write-Host "nextAction=$nextAction resumeDecision=$resumeDecision" -ForegroundColor Green
foreach ($blocker in $resumeBlockerList) { Write-Host "resumeBlocker=$blocker" -ForegroundColor Yellow }
Write-Host "retryCommand=$retryCommand" -ForegroundColor Green
exit 0
