$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\Invoke-PrePushMechanicalChecks.ps1'
$source = Get-Content -LiteralPath $scriptPath -Raw

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "PowerShell syntax errors: $($errors -join '; ')" }

$definition = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-PrePushDecision'
    }, $true)
if (-not $definition) { throw 'Missing Get-PrePushDecision production function.' }
. ([scriptblock]::Create($definition.Extent.Text))

$ready = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $false
if ($ready.status -ne 'passed' -or
    $ready.pushReadiness -ne 'readyLocal' -or
    $ready.localReadiness -ne 'ready' -or
    $ready.exitCode -ne 0) {
    throw 'A clean, mechanically passing checkout must be readyLocal.'
}

$dirty = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $true
if ($dirty.status -ne 'incomplete' -or
    $dirty.pushReadiness -ne 'blocked' -or
    $dirty.exitCode -ne 3) {
    throw 'A dirty working tree must block push readiness without becoming a mechanical failure.'
}

$behind = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 1 -WorkingTreeDirty $false
if ($behind.status -ne 'incomplete' -or
    $behind.pushReadiness -ne 'blocked' -or
    $behind.reasons -notcontains 'The branch is behind its base by 1 commit(s).') {
    throw 'A branch behind its base must block push readiness.'
}

$failed = Get-PrePushDecision -MechanicalFailed $true -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $false
if ($failed.status -ne 'failed' -or
    $failed.pushReadiness -ne 'blocked' -or
    $failed.exitCode -ne 1) {
    throw 'A failed mechanical phase must produce a failed blocked result.'
}

$unavailable = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $true -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $false
if ($unavailable.status -ne 'incomplete' -or
    $unavailable.pushReadiness -ne 'blocked' -or
    $unavailable.exitCode -ne 3 -or
    $unavailable.reasons -notcontains 'One or more mechanical phases were unavailable.') {
    throw 'An unavailable local dependency must be incomplete and block push readiness.'
}

foreach ($name in @('Get-PrePushTempRoot', 'New-Phase', 'Invoke-NativeCapture', 'Format-Command', 'Redact-PrePushLogLine', 'Write-PhaseLog', 'Remove-StalePrePushArtifacts', 'Test-ReleasePhaseStatusUnexpected', 'Invoke-NativePhase', 'Write-TextSummary', 'Read-ReleasePhaseDetails')) {
    $functionDefinition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
        }, $true)
    if (-not $functionDefinition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($functionDefinition.Extent.Text))
}

$fixturePath = Join-Path $env:TEMP ('gxmcp-prepush-noise-' + [guid]::NewGuid().ToString('N') + '.ps1')
$fixtureBody = @'
Write-Output 'SENTINELA-AQUI'
Write-Output 'token=SUPERSECRET-12345'
for ($i = 1; $i -le 200; $i++) { Write-Output "line $i" }
exit 7
'@
[IO.File]::WriteAllText($fixturePath, $fixtureBody, [Text.UTF8Encoding]::new($false))

$phaseLogRoot = Join-Path $env:TEMP ('gxmcp-prepush-test-' + [guid]::NewGuid().ToString('N'))
try {
    $noisy = Invoke-NativePhase -Name 'noise phase' -Executable 'pwsh' -Arguments @('-NoProfile', '-File', $fixturePath) -LogRoot $phaseLogRoot
    if ($noisy.status -ne 'failed' -or $noisy.exitCode -ne 7) {
        throw 'A nonzero phase exit code must be preserved as a failed phase.'
    }
    if (-not $noisy.outputPath -or -not (Test-Path -LiteralPath $noisy.outputPath -PathType Leaf)) {
        throw 'A phase that produced output must expose an existing outputPath log.'
    }
    if ((@($noisy.outputTail) -join "`n") -match 'SENTINELA-AQUI') {
        throw 'Fixture defect: the sentinel must fall outside the 60-line tail, or this guard proves nothing.'
    }
    $noisyLog = Get-Content -LiteralPath $noisy.outputPath -Raw
    if ($noisyLog -notmatch 'SENTINELA-AQUI') {
        throw 'The phase output log must retain the lines the 60-line tail discards.'
    }
    if ($noisyLog -match 'SUPERSECRET-12345') {
        throw 'The phase output log must redact a secret printed by the command.'
    }
    if ($noisyLog -notmatch 'token=\[REDACTED\]') {
        throw 'The phase output log must show the redaction marker for a masked secret.'
    }
} finally {
    foreach ($path in @($fixturePath, $phaseLogRoot)) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$quietPhase = New-Phase -Name 'git diff check (working tree)' -Command 'git -C <root> diff --check'
$noisyPhase = New-Phase -Name 'release preflight (without live KB)' -Command 'pwsh -File release-preflight.ps1'
$noisyPhase.outputPath = 'C:\temp\release-preflight.log'
$textSummary = [ordered]@{
    status           = 'failed'
    baseRef          = 'origin/main'
    headCommit       = 'deadbeef'
    commitsAhead     = 1
    commitsBehind    = 0
    workingTreeDirty = $false
    pushReadiness    = 'blocked'
    reasons          = @('One or more mechanical phases failed.')
    phases           = New-Object System.Collections.Generic.List[object]
}
[void]$textSummary.phases.Add($quietPhase)
[void]$textSummary.phases.Add($noisyPhase)

try {
    $renderedText = (Write-TextSummary -Summary $textSummary 6>&1 | Out-String)
} catch {
    throw "Write-TextSummary must render a summary whose phases list is a generic List[object]: $($_.Exception.Message)"
}
if ($renderedText -notmatch 'release preflight \(without live KB\)') {
    throw 'The text summary must list every phase.'
}
if ($renderedText -notmatch 'release-preflight\.log') {
    throw 'The text summary must surface the phase log path when the phase has one.'
}

$releaseSummaryFixture = Join-Path $env:TEMP ('gxmcp-prepush-release-' + [guid]::NewGuid().ToString('N') + '.json')
$corruptSummaryFixture = Join-Path $env:TEMP ('gxmcp-prepush-release-corrupt-' + [guid]::NewGuid().ToString('N') + '.json')
$missingSummaryFixture = Join-Path $env:TEMP ('gxmcp-prepush-release-missing-' + [guid]::NewGuid().ToString('N') + '.json')
$releaseFixtureBody = [ordered]@{
    schemaVersion = 'gxmcp-release-preflight/1'
    status        = 'failed'
    phases        = @(
        [ordered]@{ name = 'CLI tests'; status = 'failed'; command = 'npm test'; exitCode = 1; durationSeconds = 12.5; reason = 'Command exited with code 1.' }
        [ordered]@{ name = 'live KB gate'; status = 'skipped'; command = 'pwsh -File scripts/test-live.ps1 -SkipBuild'; exitCode = 0; durationSeconds = 0; reason = $null }
        [ordered]@{ name = 'CLI lint'; status = 'passed'; command = 'npm run lint'; exitCode = 0; durationSeconds = 9.1; reason = $null }
    )
}
[IO.File]::WriteAllText($releaseSummaryFixture, ($releaseFixtureBody | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($corruptSummaryFixture, '{ not json', [Text.UTF8Encoding]::new($false))

try {
    $failedOutcome = Read-ReleasePhaseDetails -SummaryPath $releaseSummaryFixture -PhaseStatus 'failed'
    if ($null -ne $failedOutcome.parseError) { throw "Unexpected parse error: $($failedOutcome.parseError)" }
    if (-not $failedOutcome.keepSummary -or $failedOutcome.details.summaryPath -ne $releaseSummaryFixture) {
        throw 'A non-passing release phase must retain the preflight summary path.'
    }
    if ($failedOutcome.details.status -ne 'failed') { throw 'Parsed details must carry the summary status.' }
    if (@($failedOutcome.details.failedPhases).Count -ne 1 -or $failedOutcome.details.failedPhases[0] -ne 'CLI tests') {
        throw 'Parsed details must list failed phase names.'
    }
    if (@($failedOutcome.details.skippedPhases).Count -ne 1 -or $failedOutcome.details.skippedPhases[0] -ne 'live KB gate') {
        throw 'Parsed details must list skipped phase names.'
    }
    $failedDetail = @($failedOutcome.details.failedPhaseDetails)[0]
    if ($failedDetail.name -ne 'CLI tests' -or $failedDetail.command -ne 'npm test' -or
        [int]$failedDetail.exitCode -ne 1 -or [double]$failedDetail.durationSeconds -ne 12.5 -or
        $failedDetail.reason -ne 'Command exited with code 1.') {
        throw 'failedPhaseDetails must carry name, command, exitCode, durationSeconds and reason.'
    }

    $passedOutcome = Read-ReleasePhaseDetails -SummaryPath $releaseSummaryFixture -PhaseStatus 'passed'
    if ($passedOutcome.keepSummary -or $passedOutcome.details.summaryPath) {
        throw 'A passing release phase must not retain the preflight summary.'
    }

    $missingOutcome = Read-ReleasePhaseDetails -SummaryPath $missingSummaryFixture -PhaseStatus 'failed'
    if ($null -ne $missingOutcome.parseError -or $missingOutcome.keepSummary -or $missingOutcome.details.summaryPath -or
        @($missingOutcome.details.failedPhaseDetails).Count -ne 0) {
        throw 'A missing summary must yield empty details and no retention.'
    }

    $corruptOutcome = Read-ReleasePhaseDetails -SummaryPath $corruptSummaryFixture -PhaseStatus 'passed'
    if ($null -eq $corruptOutcome.parseError -or -not $corruptOutcome.keepSummary -or $corruptOutcome.details.summaryPath -ne $corruptSummaryFixture) {
        throw 'A corrupt summary read for a passing phase must report the error and still be retained.'
    }
} finally {
    foreach ($cleanupPath in @($releaseSummaryFixture, $corruptSummaryFixture, $missingSummaryFixture)) {
        if (Test-Path -LiteralPath $cleanupPath) {
            Remove-Item -LiteralPath $cleanupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

$redactedSample = Redact-PrePushLogLine -Line 'token=abc123 user id=jdoe api_key=k-9 authorization:BearerT9'
if ($redactedSample -match 'abc123|jdoe|k-9|BearerT9') {
    throw 'Redact-PrePushLogLine must mask every secret-bearing key it recognizes.'
}
foreach ($marker in @('token=[REDACTED]', 'api_key=[REDACTED]')) {
    if ($redactedSample -notmatch [regex]::Escape($marker)) { throw "Redaction lost the marker: $marker" }
}
$githubToken = 'ghp_' + ('a' * 36)
if ((Redact-PrePushLogLine -Line $githubToken) -ne 'ghp_[REDACTED]') {
    throw 'Redact-PrePushLogLine must mask a literal GitHub token prefix.'
}
if ((Redact-PrePushLogLine -Line 'npm_A1b2C3d4') -ne 'npm_[REDACTED]') {
    throw 'Redact-PrePushLogLine must mask a literal npm token prefix.'
}
$longPlainLine = 'x' * 2000
if ((Redact-PrePushLogLine -Line $longPlainLine).Length -ne 2000) {
    throw 'Redact-PrePushLogLine must not truncate long lines, unlike Protect-GxMcpReleaseMessage.'
}
if ((Redact-PrePushLogLine -Line 'nothing sensitive here') -ne 'nothing sensitive here') {
    throw 'Redact-PrePushLogLine must leave ordinary text untouched.'
}

$retentionRoot = Join-Path $env:TEMP ('gxmcp-prepush-retention-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $retentionRoot -Force | Out-Null
$staleDir = Join-Path $retentionRoot ('gxmcp-pre-push-' + [guid]::NewGuid().ToString('N'))
$staleFile = Join-Path $retentionRoot ('gxmcp-pre-push-release-' + [guid]::NewGuid().ToString('N') + '.json')
$freshDir = Join-Path $retentionRoot ('gxmcp-pre-push-' + [guid]::NewGuid().ToString('N'))
$freshFile = Join-Path $retentionRoot ('gxmcp-pre-push-release-' + [guid]::NewGuid().ToString('N') + '.json')
$unrelatedOldDir = Join-Path $retentionRoot ('gxmcp-other-' + [guid]::NewGuid().ToString('N'))
foreach ($directory in @($staleDir, $freshDir, $unrelatedOldDir)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
foreach ($file in @($staleFile, $freshFile)) { [IO.File]::WriteAllText($file, 'x', [Text.UTF8Encoding]::new($false)) }
$staleTimestamp = [DateTime]::UtcNow.AddDays(-8)
foreach ($stalePath in @($staleDir, $staleFile, $unrelatedOldDir)) { (Get-Item -LiteralPath $stalePath -Force).LastWriteTimeUtc = $staleTimestamp }

try {
    $pruned = Remove-StalePrePushArtifacts -LogRoot $retentionRoot -RetentionDays 7
    if (Test-Path -LiteralPath $staleDir) { throw 'A stale pre-push directory older than the retention window must be pruned.' }
    if (Test-Path -LiteralPath $staleFile) { throw 'A stale pre-push release summary older than the retention window must be pruned.' }
    if (-not (Test-Path -LiteralPath $freshDir)) { throw 'A recent pre-push directory must be kept.' }
    if (-not (Test-Path -LiteralPath $freshFile)) { throw 'A recent pre-push release summary must be kept.' }
    if (-not (Test-Path -LiteralPath $unrelatedOldDir)) { throw 'The pruner must never touch old items outside the pre-push prefix.' }
    if (@($pruned).Count -ne 2) { throw 'The pruner must report exactly the two pruned artifacts.' }

    $nothingPruned = Remove-StalePrePushArtifacts -LogRoot $retentionRoot -RetentionDays -1
    if (@($nothingPruned).Count -ne 0 -or -not (Test-Path -LiteralPath $freshDir)) {
        throw 'A negative retention window must prune nothing.'
    }
} finally {
    if (Test-Path -LiteralPath $retentionRoot) {
        Remove-Item -LiteralPath $retentionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

foreach ($unexpectedStatus in @('running', 'timeout', 'weird')) {
    if (-not (Test-ReleasePhaseStatusUnexpected -Status $unexpectedStatus)) {
        throw "An unexpected release phase status must be treated as a failure: $unexpectedStatus"
    }
}
if (-not (Test-ReleasePhaseStatusUnexpected -Status $null)) {
    throw 'A missing release phase status must be treated as a failure.'
}
foreach ($expectedStatus in @('passed', 'failed', 'unavailable')) {
    if (Test-ReleasePhaseStatusUnexpected -Status $expectedStatus) {
        throw "A recognized release phase status must not be flagged: $expectedStatus"
    }
}

$collisionRoot = Join-Path $env:TEMP ('gxmcp-prepush-collision-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $collisionRoot -Force | Out-Null
try {
    $firstPhase = New-Phase -Name 'same name' -Command 'x'
    $secondPhase = New-Phase -Name 'same name' -Command 'x'
    $firstLog = Write-PhaseLog -Phase $firstPhase -Lines @('first payload') -LogRoot $collisionRoot
    $secondLog = Write-PhaseLog -Phase $secondPhase -Lines @('second payload') -LogRoot $collisionRoot
    if ((Split-Path -Leaf $firstLog) -ne 'same-name.log') { throw 'The first phase log must keep the plain slug name.' }
    if ($firstLog -eq $secondLog) { throw 'A same-name phase must not overwrite the earlier log.' }
    if (-not (Test-Path -LiteralPath $firstLog) -or -not (Test-Path -LiteralPath $secondLog)) { throw 'Both same-name phase logs must exist.' }
    if ((Get-Content -LiteralPath $firstLog -Raw) -notmatch 'first payload') { throw 'The first log must retain its own payload.' }
    if ((Get-Content -LiteralPath $secondLog -Raw) -notmatch 'second payload') { throw 'The second log must carry its own payload.' }
} finally {
    if (Test-Path -LiteralPath $collisionRoot) {
        Remove-Item -LiteralPath $collisionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ((Get-PrePushTempRoot -Temp 'C:\custom\temp') -ne 'C:\custom\temp') {
    throw 'Get-PrePushTempRoot must return an explicit temp path unchanged.'
}
$fallbackTemp = Get-PrePushTempRoot -Temp ''
if ([string]::IsNullOrWhiteSpace($fallbackTemp) -or $fallbackTemp -ne [IO.Path]::GetTempPath()) {
    throw 'Get-PrePushTempRoot must fall back to the system temp path when TEMP is blank.'
}
if ([string]::IsNullOrWhiteSpace((Get-PrePushTempRoot -Temp $null))) {
    throw 'Get-PrePushTempRoot must fall back to the system temp path when TEMP is null.'
}

if ($source -match '(?im)^\s*(?:&\s*)?git\s+push\b') {
    throw 'The mechanical checker must never call git push.'
}
foreach ($marker in @(
        "BaseRef = 'origin/main'",
        '[switch]$AsJson',
        'git diff check (base..HEAD)',
        'PowerShell AST parse',
        'release-preflight.ps1',
        "remoteReadiness = 'unverified'",
        'pushReadiness = if ($ready) { ''readyLocal'' }',
        'outputPath',
        'failedPhaseDetails',
        'Redact-PrePushLogLine',
        'Remove-StalePrePushArtifacts',
        'Test-ReleasePhaseStatusUnexpected',
        'gxmcp-pre-push-'
    )) {
    if ($source -notmatch [regex]::Escape($marker)) { throw "Checker is missing contract marker: $marker" }
}

Write-Host 'pre-push mechanical checker: syntax, decision, output-retention and no-push contract passed' -ForegroundColor Green
