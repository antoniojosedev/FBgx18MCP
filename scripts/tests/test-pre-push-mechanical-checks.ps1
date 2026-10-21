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

foreach ($name in @('Get-PrePushTempRoot', 'New-Phase', 'Invoke-NativeCapture', 'Format-Command', 'Redact-PrePushLogLine', 'Write-PhaseLog', 'Remove-StalePrePushArtifacts', 'Test-ReleasePhaseStatusUnexpected', 'Invoke-NativePhase', 'Write-TextSummary', 'Read-ReleasePhaseDetails', 'New-WhitespaceAdvisory', 'Get-WhitespaceClassification', 'Get-WhitespaceDiffArguments', 'Test-PhaseMechanicalFailure', 'Invoke-GitWhitespaceCapture', 'Get-CapturedFieldValue', 'Invoke-GitWhitespacePhase')) {
    $functionDefinition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
        }, $true)
    if (-not $functionDefinition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($functionDefinition.Extent.Text))
}

. (Join-Path $root 'scripts\release-contract.ps1')

# --- Whitespace classifier (pure) -------------------------------------------
# The classifier is the security-relevant surface: an unknown or case-folded
# reason must fail closed, and a ':' in a path must not be mistaken for the line
# separator. Each pin below maps to the manuscript's numbered case.
function Assert-WhitespaceClassification {
    param(
        [Parameter(Mandatory = $true)][string]$CaseName,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][int]$Advisories,
        [Parameter(Mandatory = $true)][int]$Blocking,
        [Parameter(Mandatory = $true)][int]$Noise
    )
    $classification = Get-WhitespaceClassification -Text $Text
    $actualAdvisories = @($classification.Advisories).Count
    $actualBlocking = @($classification.Blocking).Count
    $actualNoise = @($classification.NoiseLines).Count
    if ($actualAdvisories -ne $Advisories -or $actualBlocking -ne $Blocking -or $actualNoise -ne $Noise) {
        throw "Whitespace case '$CaseName': expected A=$Advisories B=$Blocking N=$Noise, got A=$actualAdvisories B=$actualBlocking N=$actualNoise."
    }
    return $classification
}

$noiseLine = "warning: in the working copy of 'a.txt', LF will be replaced by CRLF the next time Git touches it"

Assert-WhitespaceClassification -CaseName '1 empty' -Text '' -Advisories 0 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '2 trailing' -Text "ws.txt:1: trailing whitespace.`n+a   " -Advisories 1 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '3 blank-at-eof' -Text 'eof.txt:2: new blank line at EOF.' -Advisories 1 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '4 space-before-tab' -Text "tab.txt:4: space before tab in indent.`n+`t z" -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '5 conflict marker' -Text 'cm.txt:2: leftover conflict marker' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '6 conflict marker with period (unknown, fail-closed)' -Text 'cm.txt:2: leftover conflict marker.' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '7 unknown reason' -Text 'x.txt:1: some unknown reason.' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '8 advisory alternation' -Text "a.txt:1: trailing whitespace.`n+a`nb.txt:1: trailing whitespace.`n+b`nc.txt:1: trailing whitespace.`n+c" -Advisories 3 -Blocking 0 -Noise 0 | Out-Null
$drivePath = Assert-WhitespaceClassification -CaseName '9 drive path' -Text "C:/x/y.md:9: trailing whitespace.`n+" -Advisories 1 -Blocking 0 -Noise 0
if (@($drivePath.Advisories)[0].path -ne 'C:/x/y.md' -or @($drivePath.Advisories)[0].line -ne 9) {
    throw "A drive-letter path must be preserved verbatim; got '$(@($drivePath.Advisories)[0].path)'."
}
$quotedPath = Assert-WhitespaceClassification -CaseName '10 quoted path' -Text "`"a b/c d.md`":3: trailing whitespace.`n+ " -Advisories 1 -Blocking 0 -Noise 0
if (@($quotedPath.Advisories)[0].path -ne 'a b/c d.md') {
    throw "A quoted path must drop only its outer quotes; got '$(@($quotedPath.Advisories)[0].path)'."
}
Assert-WhitespaceClassification -CaseName '11 advisory plus blocking' -Text "a.txt:1: trailing whitespace.`n+b`nc.txt:2: space before tab in indent.`n+`t z" -Advisories 1 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '12 content consumed' -Text "ws.txt:1: trailing whitespace.`n+function Send(`$obj) { " -Advisories 1 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '13 two advisories no leak' -Text 'eof.txt:2: new blank line at EOF.' -Advisories 1 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '14 fatal then stray content' -Text "ws.txt:1: trailing whitespace.`nfatal: bad revision`n+conteudo" -Advisories 1 -Blocking 2 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '15 noise plus advisory' -Text "$noiseLine`nws.txt:1: trailing whitespace.`n+a" -Advisories 1 -Blocking 0 -Noise 1 | Out-Null
Assert-WhitespaceClassification -CaseName '16 two noises' -Text "$noiseLine`n$noiseLine" -Advisories 0 -Blocking 0 -Noise 2 | Out-Null
Assert-WhitespaceClassification -CaseName '17 other warning' -Text 'warning: something else' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '18 record-shaped content after advisory' -Text "ws.txt:1: trailing whitespace.`n+weird.txt:9: indented with space." -Advisories 1 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '19 CRLF' -Text "ws.txt:1: trailing whitespace.`r`n+a" -Advisories 1 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '20 quoted space-before-tab' -Text "`"a b.md`":2: space before tab in indent.`n+`t z" -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '21 record after advisory record' -Text "eof.txt:2: new blank line at EOF.`nws.txt:1: trailing whitespace." -Advisories 2 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '22 advisory without content then fatal' -Text "ws.txt:1: trailing whitespace.`nfatal: x" -Advisories 1 -Blocking 1 -Noise 0 | Out-Null
$caseFold = Assert-WhitespaceClassification -CaseName '23 case sensitivity' -Text 'ws.txt:1: Trailing whitespace.' -Advisories 0 -Blocking 1 -Noise 0
if (@($caseFold.Blocking).Count -ne 1) { throw 'A case-folded reason must fail closed (m-case).' }
Assert-WhitespaceClassification -CaseName '24 blank line between records' -Text "ws.txt:1: trailing whitespace.`n+a`n`neof.txt:2: new blank line at EOF." -Advisories 2 -Blocking 0 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '25 note' -Text 'note: whatever' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '26 hint' -Text 'hint: whatever' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
$absolutePath = Assert-WhitespaceClassification -CaseName '27 absolute path' -Text "/tmp/a b.md:1: trailing whitespace.`n+a" -Advisories 1 -Blocking 0 -Noise 0
if (@($absolutePath.Advisories)[0].path -ne '/tmp/a b.md') {
    throw "An absolute path with a space must be preserved; got '$(@($absolutePath.Advisories)[0].path)'."
}
Assert-WhitespaceClassification -CaseName '28 leading content line' -Text '+just content' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '29 consumed then stray' -Text "ws.txt:1: trailing whitespace.`n+ok`n+stray`n+stray2" -Advisories 1 -Blocking 2 -Noise 0 | Out-Null
Assert-WhitespaceClassification -CaseName '30 non-numeric line' -Text 'a.txt:x: trailing whitespace.' -Advisories 0 -Blocking 1 -Noise 0 | Out-Null
$cQuoted = Assert-WhitespaceClassification -CaseName '31 C-quoted path' -Text '"a\tb.md":1: trailing whitespace.' -Advisories 1 -Blocking 0 -Noise 0
if (@($cQuoted.Advisories)[0].path -ne 'a\tb.md') {
    throw "A C-quoted path must keep its escaped payload byte for byte; got '$(@($cQuoted.Advisories)[0].path)'."
}
$ambiguousPath = Assert-WhitespaceClassification -CaseName '32 colon-digit path' -Text 'a.md:1:1: trailing whitespace.' -Advisories 1 -Blocking 0 -Noise 0
if (@($ambiguousPath.Advisories)[0].path -ne 'a.md:1' -or @($ambiguousPath.Advisories)[0].line -ne 1) {
    throw "A path containing ':digit:' must split on the last ':digit: '; got path='$(@($ambiguousPath.Advisories)[0].path)' line=$(@($ambiguousPath.Advisories)[0].line)."
}

# --- Whitespace phase (seam CapturedResult) ---------------------------------
function New-CapturedGitResult {
    param(
        [string[]]$Output = @(),
        [int]$ExitCode = 0,
        [AllowNull()][object]$ErrorMessage = $null
    )
    return [pscustomobject]@{ output = @($Output); exitCode = $ExitCode; error = $ErrorMessage; durationSeconds = 0.01 }
}

$p2Log = @(
    'src/GxMcp.Worker.Tests/SemanticOpsMultilineFormulaTests.cs:45: trailing whitespace.'
    '+            '
    'src/GxMcp.Worker.Tests/SemanticOpsMultilineFormulaTests.cs:82: trailing whitespace.'
    '+            '
)
$phasePass = Invoke-GitWhitespacePhase -Name 'seam pass' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output $p2Log -ExitCode 2)
if ($phasePass.status -ne 'passed' -or @($phasePass.advisories).Count -ne 2 -or $null -ne $phasePass.reason) {
    throw 'A hygiene-only whitespace phase must pass with its advisories exposed and no reason.'
}
if (@($phasePass.advisories)[0].reason -ne 'trailing whitespace' -or @($phasePass.advisories)[0].line -ne 45) {
    throw 'An advisory must carry a canonical reason without a trailing period and its integer line.'
}
if (Test-PhaseMechanicalFailure -Phase $phasePass) { throw 'A passed advisory phase is not a mechanical failure.' }

$phaseFail = Invoke-GitWhitespacePhase -Name 'seam fail' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output @('tab.txt:4: space before tab in indent.', "+`t z") -ExitCode 2)
if ($phaseFail.status -ne 'failed' -or @($phaseFail.blocking).Count -ne 1) {
    throw 'A real whitespace defect must fail the phase.'
}
if (-not (Test-PhaseMechanicalFailure -Phase $phaseFail)) { throw 'A failed whitespace phase is a mechanical failure.' }

$phaseEmpty = Invoke-GitWhitespacePhase -Name 'seam empty' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output @() -ExitCode 0)
if ($phaseEmpty.status -ne 'passed' -or @($phaseEmpty.advisories).Count -ne 0) {
    throw 'Empty output at exit 0 must pass cleanly.'
}
$phaseEmptyNonZero = Invoke-GitWhitespacePhase -Name 'seam empty fail' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output @() -ExitCode 2)
if ($phaseEmptyNonZero.status -ne 'failed') { throw 'Empty output at a nonzero exit must fail.' }

$phaseNoiseOnly = Invoke-GitWhitespacePhase -Name 'seam noise' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output @($noiseLine) -ExitCode 0)
if ($phaseNoiseOnly.status -ne 'passed' -or @($phaseNoiseOnly.noiseLines).Count -ne 1) {
    throw 'Noise-only output at exit 0 must pass with the noise recorded.'
}
$phaseNoiseFail = Invoke-GitWhitespacePhase -Name 'seam noise fail' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output @($noiseLine) -ExitCode 2)
if ($phaseNoiseFail.status -ne 'failed') { throw 'Noise-only output at a nonzero exit must fail closed.' }

foreach ($shortCircuit in @(
        (New-CapturedGitResult -Output @('fatal: bad revision') -ExitCode 128),
        (New-CapturedGitResult -Output $p2Log -ExitCode 128))) {
    $phaseShort = Invoke-GitWhitespacePhase -Name 'seam 128' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult $shortCircuit
    if ($phaseShort.status -ne 'failed' -or $phaseShort.reason -ne 'git diff --check exited with 128' -or
        @($phaseShort.advisories).Count -ne 0 -or @($phaseShort.blocking).Count -ne 0) {
        throw 'An exit code outside {0,1,2} must short-circuit to a failed phase with no advisories and no blocking evidence (m-exit).'
    }
}

$phaseExitZeroBlocking = Invoke-GitWhitespacePhase -Name 'seam exit-0 blocking' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output @('tab.txt:4: space before tab in indent.', "+`t z") -ExitCode 0)
if ($phaseExitZeroBlocking.status -ne 'failed') {
    throw 'A recognized blocking record at exit 0 must fail closed (frontier F5 / m-exit0).'
}

$phaseKeepsExit = Invoke-GitWhitespacePhase -Name 'seam keeps exit' -Arguments (Get-WhitespaceDiffArguments) -CapturedResult (New-CapturedGitResult -Output $p2Log -ExitCode 2)
if ($phaseKeepsExit.exitCode -ne 2 -or $phaseKeepsExit.status -ne 'passed') {
    throw 'A passed advisory phase must preserve the real git exit code.'
}

$advisorySummary = [ordered]@{
    status           = 'passed'
    baseRef          = 'origin/main'
    headCommit       = 'deadbeef'
    commitsAhead     = 1
    commitsBehind    = 0
    workingTreeDirty = $false
    pushReadiness    = 'readyLocal'
    reasons          = @()
    phases           = New-Object System.Collections.Generic.List[object]
}
[void]$advisorySummary.phases.Add($phaseKeepsExit)
$advisoryText = (Write-TextSummary -Summary $advisorySummary 6>&1 | Out-String)
if ($advisoryText -notmatch 'advisory: src/GxMcp\.Worker\.Tests/SemanticOpsMultilineFormulaTests\.cs:45: trailing whitespace') {
    throw 'The text summary must print advisories even when the phase passed.'
}

# --- Command composition (pure) ---------------------------------------------
$expectedArguments = @(
    '-C', $root
    '-c', 'color.ui=never'
    '-c', 'core.quotePath=false'
    '-c', 'core.whitespace=blank-at-eol,blank-at-eof,space-before-tab'
    'diff', '--check', '--no-color'
)
if (((Get-WhitespaceDiffArguments) -join '|') -cne ($expectedArguments -join '|')) {
    throw 'The whitespace diff arguments must pin the locale-independent whitespace set and --no-color (m-arg).'
}
if (((Get-WhitespaceDiffArguments -Range 'origin/main..HEAD') -join '|') -cne (($expectedArguments + 'origin/main..HEAD') -join '|')) {
    throw 'An explicit range must append after the fixed arguments.'
}

# The pre-push checker delegates to Protect-GxMcpReleaseText, so the fork does not
# own the canonical redaction cases (the upstream test-release-contract.ps1 pins
# those for #326). These two guarantees are the fork's own and are pinned here:
# the canonical single marker survives the delegation, and a credential word
# embedded as a suffix (superuserid) is not over-masked by a word-boundary
# regression. Keeping them in the fork-owned test is what stops the fork from
# carrying an absolute copy of upstream's canonical that drifts on every sync.
foreach ($pin in @(
        [ordered]@{ input = 'user id=jdoe'; output = 'user id=[REDACTED]' },
        [ordered]@{ input = 'userid=x'; output = 'userid=[REDACTED]' },
        [ordered]@{ input = 'superuserid=leaked'; output = 'superuserid=leaked' })) {
    $actual = Protect-GxMcpReleaseText $pin.input
    if ([string]$actual -cne [string]$pin.output) {
        throw "Fork pre-push redaction pin regressed for '$($pin.input)': got '$actual'."
    }
}

# --- release-contract.ps1 dot-source invariant ------------------------------
# A dot-source must not consume arguments and must not execute top-level code in
# the caller's scope, so the module stays a pure function library. The pre-push
# checker dot-sources the contract, so this fork-local guard lives with the
# checker rather than in the upstream test-release-contract.ps1.
# Upstream may bind a named constant at top level (v4.0.0 added
# $script:GxMcpReleaseWorkflowCheck / $script:GxMcpReleaseWorkflowAppId). A
# constant binding to a script-scoped variable emits no output, consumes no
# argument and invokes no command, so it keeps the invariant the guard exists
# to protect. Anything else at top level is still refused.
function Test-ReleaseContractConstantBinding {
    param([Parameter(Mandatory = $true)][object]$Statement)

    if ($Statement -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { return $false }
    if ($Statement.Operator -ne [System.Management.Automation.Language.TokenKind]::Equals) { return $false }
    $left = $Statement.Left
    if ($left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { return $false }
    if (-not $left.VariablePath.UserPath.StartsWith('script:', [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    $value = $Statement.Right
    if ($value -is [System.Management.Automation.Language.CommandExpressionAst]) { $value = $value.Expression }
    return $value -is [System.Management.Automation.Language.ConstantExpressionAst]
}

$contractPath = Join-Path $root 'scripts\release-contract.ps1'
$contractTokens = $null; $contractParseErrors = $null
$contractAst = [System.Management.Automation.Language.Parser]::ParseFile($contractPath, [ref]$contractTokens, [ref]$contractParseErrors)
if ($contractParseErrors.Count -gt 0) { throw "release-contract.ps1 has parse errors: $($contractParseErrors -join '; ')" }
if ($contractAst.ParamBlock -or $contractAst.BeginBlock -or $contractAst.ProcessBlock) {
    throw 'release-contract.ps1 must not declare param/begin/process blocks; the dot-source must not consume arguments.'
}
foreach ($stmt in $contractAst.EndBlock.Statements) {
    if ($stmt -is [System.Management.Automation.Language.FunctionDefinitionAst]) { continue }
    if (Test-ReleaseContractConstantBinding -Statement $stmt) { continue }
    throw "release-contract.ps1 must define only functions at top level (a constant binding to a script-scoped variable is tolerated); found: $($stmt.GetType().Name)"
}

$corpus = @(
    'user id=John,Doe;', 'userid=John,Doe', 'superuserid=leaked', 'user id=jdoe',
    'token=abc123', 'npm_A1b2C3d4', 'ghp_ABCDEF123456', 'authorization:BearerT9',
    'connection string=Server=x;Password=y'
)
foreach ($case in $corpus) {
    if ((Redact-PrePushLogLine -Line $case) -cne (Protect-GxMcpReleaseText $case)) {
        throw "Redact-PrePushLogLine diverged from the shared contract for: $case"
    }
}

$wrapperNode = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Redact-PrePushLogLine'
    }, $true)
if (-not $wrapperNode) { throw 'Missing production function: Redact-PrePushLogLine' }
$wrapperBody = $wrapperNode.Extent.Text -replace '(?s)<#.*?#>', ''
$wrapperBody = (($wrapperBody -split "`n") | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n"
if ($wrapperBody -notmatch 'Protect-GxMcpReleaseText\s+\$Line') {
    throw 'Redact-PrePushLogLine must call the shared contract function with $Line.'
}
if ($wrapperBody -match '-replace|\[regex\]::Replace') {
    throw 'Redact-PrePushLogLine must not re-inline redaction patterns; delegate to the shared contract.'
}

if ((Redact-PrePushLogLine -Line '   ') -ne '') {
    throw 'Redact-PrePushLogLine must normalize a whitespace-only line to empty.'
}

$topLevelTries = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })
if ($topLevelTries.Count -ne 1) { throw "Expected exactly one top-level try statement; found $($topLevelTries.Count)." }
$topLevelTry = $topLevelTries[0]

$dotContracts = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Dot -and
            $node.Extent.Text -match 'release-contract\.ps1'
        }, $true))
if ($dotContracts.Count -ne 1) { throw "Expected exactly one release-contract dot-source; found $($dotContracts.Count)." }
$dotContract = $dotContracts[0]

if ($dotContract.Extent.StartOffset -lt $topLevelTry.Body.Extent.StartOffset -or
    $dotContract.Extent.EndOffset -gt $topLevelTry.Body.Extent.EndOffset) {
    throw 'The contract dot-source must live inside the top-level try block, so a broken contract still emits the summary.'
}

if ($source -match '\[regex\]::Replace') {
    throw 'The checker must not re-implement the shared regex-replacement pattern.'
}
# Reuse the checker AST parsed above (F3); do not reparse.
$checkerAst = $ast
foreach ($op in $checkerAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.BinaryExpressionAst] -and
            $node.Operator -in [System.Management.Automation.Language.TokenKind]::Ireplace, [System.Management.Automation.Language.TokenKind]::Creplace
        }, $true)) {
    # $op.Right covers both pattern and replacement (ArrayLiteralAst); the markers live in the replacement.
    if ($op.Right.Extent.Text -match 'ghp_|github_pat_|api[_-]?key|connection\s*string|token|password|pwd|secret|authorization|\[REDACTED\]|<redacted>') {
        throw 'The checker must not re-inline a credential redaction pattern outside the shared contract.'
    }
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
        [ordered]@{ name = 'live SDK probe'; status = 'timeout'; command = 'pwsh -File scripts/test-live.ps1'; exitCode = 124; durationSeconds = 1200; reason = 'Phase exceeded 1200s and was terminated.' }
        [ordered]@{ name = 'live gate unavailable'; status = 'unavailable'; command = 'pwsh -File scripts/test-live.ps1'; exitCode = 2; durationSeconds = 1; reason = 'Command reported unavailable with exit code 2.' }
        [ordered]@{ name = 'dry-run phase (never produced by this caller)'; status = 'dry-run'; command = 'pwsh -File scripts/release-preflight.ps1 -DryRun'; exitCode = 0; durationSeconds = 0; reason = 'Dry-run requested.' }
        [ordered]@{ name = 'live SDK probe [in progress]'; status = 'running'; command = 'pwsh -File scripts/test-live.ps1'; exitCode = $null; durationSeconds = 0; reason = $null }
        [ordered]@{ name = 'phase with no status label'; command = 'pwsh -File scripts/test-live.ps1'; exitCode = $null; durationSeconds = 0; reason = $null }
        [ordered]@{ name = 'phase carrying only a name' }
        [ordered]@{ status = 'failed'; command = 'npm run test:one'; exitCode = 2; durationSeconds = 3; reason = 'Unnamed failing phase.' }
        'not-a-phase'
        $null
    )
}
[IO.File]::WriteAllText($releaseSummaryFixture, ($releaseFixtureBody | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($corruptSummaryFixture, '{ not json', [Text.UTF8Encoding]::new($false))
# A summary that parses but does not carry 'phases'. Under StrictMode a bare
# '$releaseSummary.phases' read throws, the catch discards every recoverable
# failedPhaseDetails entry, and the caller reports "unable to parse" - the
# evidence loss this guard exists to prevent. The same class release-doctor.ps1
# fixed under issue #348.
$phaselessSummaryFixture = Join-Path $env:TEMP ('gxmcp-prepush-release-phaseless-' + [guid]::NewGuid().ToString('N') + '.json')
[IO.File]::WriteAllText($phaselessSummaryFixture, ([ordered]@{ schemaVersion = 'gxmcp-release-preflight/1'; status = 'failed' } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
# A summary that parses but does not carry 'status'. The envelope read must be as
# defensive as the per-phase read, or the missing aggregate key throws under
# StrictMode and the catch takes every failedPhaseDetails entry with it.
$statuslessSummaryFixture = Join-Path $env:TEMP ('gxmcp-prepush-release-statusless-' + [guid]::NewGuid().ToString('N') + '.json')
[IO.File]::WriteAllText($statuslessSummaryFixture, ([ordered]@{ schemaVersion = 'gxmcp-release-preflight/1'; phases = @([ordered]@{ name = 'CLI tests'; status = 'failed'; command = 'npm test'; exitCode = 1; durationSeconds = 1.5; reason = 'Command exited with code 1.' }) } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

try {
    $failedOutcome = Read-ReleasePhaseDetails -SummaryPath $releaseSummaryFixture -PhaseStatus 'failed'
    if ($null -ne $failedOutcome.parseError) { throw "Unexpected parse error: $($failedOutcome.parseError)" }
    if (-not $failedOutcome.keepSummary -or $failedOutcome.details.summaryPath -ne $releaseSummaryFixture) {
        throw 'A non-passing release phase must retain the preflight summary path.'
    }
    if ($failedOutcome.details.status -ne 'failed') { throw 'Parsed details must carry the summary status.' }
    if (@($failedOutcome.details.failedPhases).Count -ne 7 -or
        $failedOutcome.details.failedPhases -notcontains 'CLI tests' -or
        $failedOutcome.details.failedPhases -notcontains 'live SDK probe' -or
        $failedOutcome.details.failedPhases -notcontains 'live SDK probe [in progress]' -or
        $failedOutcome.details.failedPhases -notcontains 'phase with no status label' -or
        $failedOutcome.details.failedPhases -notcontains 'phase carrying only a name' -or
        $failedOutcome.details.failedPhases -notcontains '(unnamed phase)' -or
        $failedOutcome.details.failedPhases -contains $null -or
        $failedOutcome.details.failedPhases -contains 'live gate unavailable' -or
        $failedOutcome.details.failedPhases -contains 'dry-run phase (never produced by this caller)') {
        throw 'failedPhases must list every non-terminal phase by negation (failed, timeout, running, missing status, name-only, unnamed, non-object), exclude every allowed terminal status (unavailable, dry-run), and never report a null entry.'
    }
    if (@($failedOutcome.details.failedPhaseDetails).Count -ne @($failedOutcome.details.failedPhases).Count) {
        throw 'failedPhases and failedPhaseDetails must describe the same set: they are projected from one another and their counts may not drift.'
    }
    if (@($failedOutcome.details.skippedPhases).Count -ne 1 -or $failedOutcome.details.skippedPhases[0] -ne 'live KB gate') {
        throw 'Parsed details must list skipped phase names.'
    }
    $failedDetail = @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'CLI tests')[0]
    if ($failedDetail.command -ne 'npm test' -or
        [int]$failedDetail.exitCode -ne 1 -or [double]$failedDetail.durationSeconds -ne 12.5 -or
        $failedDetail.reason -ne 'Command exited with code 1.') {
        throw 'failedPhaseDetails must carry name, command, exitCode, durationSeconds and reason.'
    }
    $timeoutDetail = @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'live SDK probe')[0]
    if ($timeoutDetail.command -ne 'pwsh -File scripts/test-live.ps1' -or
        [int]$timeoutDetail.exitCode -ne 124 -or [double]$timeoutDetail.durationSeconds -ne 1200 -or
        $timeoutDetail.reason -ne 'Phase exceeded 1200s and was terminated.') {
        throw 'A timed-out phase must travel in failedPhaseDetails with its reason, exit code and command.'
    }
    $runningDetail = @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'live SDK probe [in progress]')[0]
    if ($null -ne $runningDetail.exitCode -or $null -ne $runningDetail.reason -or
        @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'live gate unavailable').Count -ne 0 -or
        @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'dry-run phase (never produced by this caller)').Count -ne 0) {
        throw 'A running phase (null exit code/reason) must travel in failedPhaseDetails, and no terminal-ok phase (unavailable, dry-run) may travel.'
    }
    $unlabelledDetail = @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'phase with no status label')[0]
    if ($null -eq $unlabelledDetail -or $unlabelledDetail.command -ne 'pwsh -File scripts/test-live.ps1') {
        throw 'A phase without a status label must be reported as a failure with its name preserved: under StrictMode a bare property read throws and discards every failedPhaseDetails entry.'
    }
    $nameOnlyDetail = @($failedOutcome.details.failedPhaseDetails | Where-Object name -eq 'phase carrying only a name')[0]
    if ($null -eq $nameOnlyDetail -or $null -ne $nameOnlyDetail.command -or
        $null -ne $nameOnlyDetail.exitCode -or $null -ne $nameOnlyDetail.durationSeconds -or $null -ne $nameOnlyDetail.reason) {
        throw 'A phase carrying only a name must be reported as a failure with every absent detail field reading as $null, not dropped by a StrictMode property access.'
    }
    if (@($failedOutcome.details.failedPhaseDetails | Where-Object { $null -eq $_.name }).Count -ne 0) {
        throw 'A null element of phases must be filtered out rather than reported as an all-null failed phase.'
    }
    $unnamedDetail = @($failedOutcome.details.failedPhaseDetails | Where-Object command -eq 'npm run test:one')[0]
    if ($null -eq $unnamedDetail -or $unnamedDetail.name -ne '(unnamed phase)' -or
        [int]$unnamedDetail.exitCode -ne 2 -or $unnamedDetail.reason -ne 'Unnamed failing phase.') {
        throw 'A failing phase with no name must be reported under a placeholder name with its other fields intact, not emitted as a null entry.'
    }
    if (@($failedOutcome.details.failedPhaseDetails | Where-Object name -eq '(unnamed phase)').Count -ne 2) {
        throw 'Both a name-less phase object and a non-object phases element must be labelled with the placeholder name.'
    }

    # --- Fork-local mirror is pinned to the producer's failure rule ---------
    # The whole value of the negation filter is that it MIRRORS the producer, and
    # the mirror is a hand-copied literal. A fixture pins the fork's own behaviour,
    # but nothing here would turn red if upstream changed $allowedTerminalStatuses:
    # the fork would keep accepting a status the producer now treats as failure,
    # which is precisely the silent divergence this change exists to remove. So
    # parse the producer's literal and require the checker's set to equal it.
    # Note this pins the literal declaration of the aggregate rule
    # ($allowedTerminalStatuses, line 698), not the positive hint block below it
    # or Test-PreflightPhaseStatus (different rules with different callers), and
    # not a later re-derivation of the same variable.
    $preflightSource = Get-Content -LiteralPath (Join-Path $root 'scripts\release-preflight.ps1') -Raw
    $producerLiteral = [regex]::Match($preflightSource, '\$allowedTerminalStatuses\s*=\s*@\(([^)]*)\)')
    if (-not $producerLiteral.Success) {
        throw 'The producer no longer declares $allowedTerminalStatuses; the fork checker mirrors that rule and the pin has lost its anchor.'
    }
    $producerSet = @([regex]::Matches($producerLiteral.Groups[1].Value, "'([^']*)'") | ForEach-Object { $_.Groups[1].Value })
    if ($preflightSource -notmatch '\+=\s*''dry-run''') {
        throw 'The producer no longer adds dry-run to the allowed terminal statuses; update $nonFailurePhaseStatuses in the checker in lockstep.'
    }
    $producerSet = @($producerSet + 'dry-run' | Sort-Object)
    $checkerSource = Get-Content -LiteralPath (Join-Path $root 'scripts\Invoke-PrePushMechanicalChecks.ps1') -Raw
    $checkerNonFailure = [regex]::Match($checkerSource, '\$nonFailurePhaseStatuses\s*=\s*@\(([^)]*)\)')
    if (-not $checkerNonFailure.Success) { throw 'The fork checker no longer declares $nonFailurePhaseStatuses; the mirror pin has lost its anchor.' }
    $checkerSet = @([regex]::Matches($checkerNonFailure.Groups[1].Value, "'([^']*)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
    if (($checkerSet -join ',') -ne ($producerSet -join ',')) {
        throw "The fork checker's non-failure status set '$($checkerSet -join ',')' has drifted from the producer's allowed terminal statuses '$($producerSet -join ',')'."
    }

    $phaselessOutcome = Read-ReleasePhaseDetails -SummaryPath $phaselessSummaryFixture -PhaseStatus 'failed'
    if ($null -ne $phaselessOutcome.parseError -or $phaselessOutcome.details.status -ne 'failed' -or
        @($phaselessOutcome.details.failedPhases).Count -ne 0 -or
        @($phaselessOutcome.details.failedPhaseDetails).Count -ne 0 -or
        @($phaselessOutcome.details.skippedPhases).Count -ne 0 -or -not $phaselessOutcome.keepSummary) {
        throw 'A summary without a phases key must read as an empty phase set with its own status and retention intact, not as an unparsable summary that discards the evidence.'
    }

    $statuslessOutcome = Read-ReleasePhaseDetails -SummaryPath $statuslessSummaryFixture -PhaseStatus 'failed'
    if ($null -ne $statuslessOutcome.parseError -or $null -ne $statuslessOutcome.details.status -or
        @($statuslessOutcome.details.failedPhases).Count -ne 1 -or
        $statuslessOutcome.details.failedPhases[0] -ne 'CLI tests' -or -not $statuslessOutcome.keepSummary) {
        throw 'A summary without a status key must read that field as $null and still list its phases, not throw under StrictMode and discard the evidence.'
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

$sentinelRoot = Join-Path $env:TEMP ('gxmcp-prepush-sentinel-' + [guid]::NewGuid().ToString('N'))
$sentinelScripts = Join-Path $sentinelRoot 'scripts'
try {
    New-Item -ItemType Directory -Path $sentinelScripts -Force | Out-Null
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $sentinelScripts 'Invoke-PrePushMechanicalChecks.ps1')
    [IO.File]::WriteAllText(
        (Join-Path $sentinelScripts 'release-contract.ps1'),
        "throw 'GXMCP_PREPUSH_CONTRACT_SENTINEL'" + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $out = & pwsh -NoProfile -File (Join-Path $sentinelScripts 'Invoke-PrePushMechanicalChecks.ps1') -AsJson 2>$null
    $sentinelSummary = $out | ConvertFrom-Json
    if ($sentinelSummary.reasons -notcontains 'GXMCP_PREPUSH_CONTRACT_SENTINEL') { throw 'A broken contract must surface its error in the summary reasons.' }
    if ($sentinelSummary.exitCode -ne 1) { throw 'A broken contract must exit 1.' }
    if (@($sentinelSummary.phases).Count -ne 0) { throw 'A broken contract must abort before any phase runs.' }
} finally {
    Remove-Item -LiteralPath $sentinelRoot -Recurse -Force -ErrorAction SilentlyContinue
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
        'Protect-GxMcpReleaseText',
        'release-contract.ps1',
        'Remove-StalePrePushArtifacts',
        'Test-ReleasePhaseStatusUnexpected',
        'gxmcp-pre-push-',
        'gxmcp-pre-push/2',
        'advisories',
        'noiseLines',
        'Test-PhaseMechanicalFailure',
        'Get-WhitespaceClassification',
        'Get-WhitespaceDiffArguments',
        'Invoke-GitWhitespacePhase',
        'CapturedResult',
        'core.whitespace',
        '--no-color'
    )) {
    if ($source -notmatch [regex]::Escape($marker)) { throw "Checker is missing contract marker: $marker" }
}

Write-Host 'pre-push mechanical checker: syntax, decision, output-retention and no-push contract passed' -ForegroundColor Green
