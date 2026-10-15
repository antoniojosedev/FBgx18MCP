[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',

    [int]$ArtifactRetentionDays = 7,

    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-PrePushTempRoot {
    param([AllowNull()][string]$Temp)

    if ([string]::IsNullOrWhiteSpace($Temp)) { return [IO.Path]::GetTempPath() }
    return $Temp
}

$root = Split-Path -Parent $PSScriptRoot
$selfPath = [System.IO.Path]::GetFullPath($PSCommandPath)
$startedAtUtc = [DateTime]::UtcNow
$tempRoot = Get-PrePushTempRoot -Temp $env:TEMP
$logRoot = Join-Path $tempRoot ('gxmcp-pre-push-' + [guid]::NewGuid().ToString('N'))

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
        outputPath = $null
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

function Redact-PrePushLogLine {
    param([AllowNull()][string]$Line)

    if ([string]::IsNullOrEmpty($Line)) { return '' }
    return (Protect-GxMcpReleaseText $Line)
}

function Write-PhaseLog {
    param(
        [Parameter(Mandatory = $true)][object]$Phase,
        [AllowEmptyCollection()][object[]]$Lines = @(),
        [string]$LogRoot
    )

    $cleanLines = @($Lines | ForEach-Object { Redact-PrePushLogLine -Line ([string]$_) })
    if ($cleanLines.Count -eq 0 -or [string]::IsNullOrWhiteSpace($LogRoot)) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $LogRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $LogRoot -Force | Out-Null
    }
    $slug = ([string]$Phase.name) -replace '[^A-Za-z0-9_.-]', '-'
    $path = Join-Path $LogRoot "$slug.log"
    $suffix = 2
    while (Test-Path -LiteralPath $path -PathType Leaf) {
        $path = Join-Path $LogRoot "$slug-$suffix.log"
        $suffix++
    }
    [IO.File]::WriteAllText($path, (($cleanLines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $Phase.outputPath = $path
    return $path
}

function Remove-StalePrePushArtifacts {
    param(
        [string]$LogRoot = $env:TEMP,
        [int]$RetentionDays = 7
    )

    if ([string]::IsNullOrWhiteSpace($LogRoot) -or $RetentionDays -lt 0) { return @() }
    if (-not (Test-Path -LiteralPath $LogRoot -PathType Container)) { return @() }
    $cutoff = [DateTime]::UtcNow.AddDays(-1 * $RetentionDays)
    $removed = New-Object System.Collections.Generic.List[string]
    foreach ($item in @(Get-ChildItem -LiteralPath $LogRoot -Filter 'gxmcp-pre-push-*' -Force -ErrorAction SilentlyContinue)) {
        if ($item.LastWriteTimeUtc -ge $cutoff) { continue }
        try {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
            [void]$removed.Add($item.FullName)
        } catch { }
    }
    return @($removed)
}

function Test-ReleasePhaseStatusUnexpected {
    param([AllowNull()][string]$Status)

    return ($Status -notin @('passed', 'failed', 'unavailable'))
}

function Invoke-NativePhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $root,
        [string]$LogRoot
    )

    $phase = New-Phase -Name $Name -Command (Format-Command -Executable $Executable -Arguments $Arguments)
    $result = Invoke-NativeCapture -Executable $Executable -Arguments $Arguments -WorkingDirectory $WorkingDirectory
    $phase.exitCode = $result.exitCode
    $phase.durationSeconds = $result.durationSeconds
    $phase.outputTail = @($result.output | Select-Object -Last 60)
    [void](Write-PhaseLog -Phase $phase -Lines $result.output -LogRoot $LogRoot)
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

function New-WhitespaceAdvisory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $normalizedPath = $Path
    if ($Path.Length -ge 2 -and $Path.StartsWith('"') -and $Path.EndsWith('"')) {
        # A quoted path keeps its C-quoted payload byte for byte; only the outer
        # quotes are dropped.
        $normalizedPath = $Path.Substring(1, $Path.Length - 2)
    } else {
        $normalizedPath = $Path.Replace('\', '/')
    }

    return [pscustomobject]@{
        path = $normalizedPath
        line = [int]$Line
        reason = $Reason
    }
}

function Get-WhitespaceClassification {
    param([AllowNull()][string]$Text)

    $advisories = New-Object System.Collections.Generic.List[object]
    $blocking = New-Object System.Collections.Generic.List[string]
    $noiseLines = New-Object System.Collections.Generic.List[string]
    $pending = $false

    if (-not [string]::IsNullOrEmpty($Text)) {
        foreach ($line in @($Text -split '\r?\n')) {
            # Rule 0: a blank or whitespace-only line carries no finding and does
            # not disturb the pending-content state. It is required because the
            # whitespace phase feeds this text straight from Invoke-NativeCapture,
            # so it has NOT been filtered the way Get-GitLines filters its lines;
            # without this rule a blank line would fall to rule 4 and block.
            if ([string]::IsNullOrWhiteSpace($line)) { continue }

            # Rule 1: a git whitespace record. It always closes the previous
            # pending state before it can open a new one.
            $record = [regex]::Match($line, '^(?<path>"?.+?"?):(?<line>\d+): (?<reason>.*)$')
            if ($record.Success) {
                $pending = $false
                $reason = $record.Groups['reason'].Value
                # -ceq, never -eq: a case-folded comparison would silently
                # reclassify 'Trailing whitespace.' (mutation m-case).
                if ($reason -ceq 'trailing whitespace.') {
                    [void]$advisories.Add((New-WhitespaceAdvisory -Path $record.Groups['path'].Value -Line $record.Groups['line'].Value -Reason 'trailing whitespace'))
                    $pending = $true
                } elseif ($reason -ceq 'new blank line at EOF.') {
                    [void]$advisories.Add((New-WhitespaceAdvisory -Path $record.Groups['path'].Value -Line $record.Groups['line'].Value -Reason 'new blank line at EOF'))
                } elseif ($reason -ceq 'space before tab in indent.') {
                    [void]$blocking.Add($line)
                    $pending = $true
                } elseif ($reason -ceq 'leftover conflict marker') {
                    [void]$blocking.Add($line)
                } else {
                    # Unknown reason: fail closed.
                    [void]$blocking.Add($line)
                }
                continue
            }

            # Rule 2: the '+' content line of the record just seen. Consuming it
            # closes the pending state, so a later '+' with no record above it is
            # a finding, not content (mutation m2b).
            if ($pending -and $line -cmatch '^\+') {
                $pending = $false
                continue
            }

            # Rule 3: the single enumerated EOL-conversion warning. It is noise,
            # never a finding, and leaves pending state untouched.
            if ($line -cmatch "^(?i)warning: in the working copy of '.+', (LF will be replaced by CRLF|CRLF will be replaced by LF) the next time Git touches it$") {
                [void]$noiseLines.Add($line)
                continue
            }

            # Rule 4: anything else is a finding (fail closed) and closes pending.
            [void]$blocking.Add($line)
            $pending = $false
        }
    }

    return [pscustomobject]@{
        Advisories = $advisories.ToArray()
        Blocking   = $blocking.ToArray()
        NoiseLines = $noiseLines.ToArray()
    }
}

function Get-WhitespaceDiffArguments {
    param([AllowNull()][string]$Range)

    $arguments = @(
        '-C', $root
        '-c', 'color.ui=never'
        '-c', 'core.quotePath=false'
        '-c', 'core.whitespace=blank-at-eol,blank-at-eof,space-before-tab'
        'diff', '--check', '--no-color'
    )
    if (-not [string]::IsNullOrWhiteSpace($Range)) {
        $arguments += $Range
    }
    return $arguments
}

function Test-PhaseMechanicalFailure {
    param([Parameter(Mandatory = $true)][object]$Phase)

    return ($Phase.status -ne 'passed')
}

function Invoke-GitWhitespaceCapture {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    # Pin the C locale for the capture so the reasons git prints are stable, then
    # restore the caller's environment exactly: an absent variable must stay
    # absent, which is not the same as setting it to an empty string.
    $names = @('LC_ALL', 'LANGUAGE', 'LANG', 'LC_MESSAGES')
    $saved = @{}
    foreach ($name in $names) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        Set-Item -Path "Env:$name" -Value 'C' -ErrorAction SilentlyContinue
    }
    try {
        return Invoke-NativeCapture -Executable 'git' -Arguments $Arguments
    } finally {
        foreach ($name in $names) {
            $value = $saved[$name]
            if ($null -eq $value) {
                Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
            } else {
                Set-Item -Path "Env:$name" -Value $value -ErrorAction SilentlyContinue
            }
        }
    }
}

function Get-CapturedFieldValue {
    param(
        [Parameter(Mandatory = $true)][object]$Result,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][object]$Default = $null
    )

    if ($Result -is [System.Collections.IDictionary]) {
        if ($Result.Contains($Name)) { return $Result[$Name] }
        return $Default
    }
    $property = $Result.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Invoke-GitWhitespacePhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [AllowNull()][object]$CapturedResult,
        [string]$LogRoot
    )

    $phase = New-Phase -Name $Name -Command (Format-Command -Executable 'git' -Arguments $Arguments)
    $phase.advisories = @()
    $phase.noiseLines = @()
    $phase.blocking = @()

    $result = if ($null -ne $CapturedResult) { $CapturedResult } else { Invoke-GitWhitespaceCapture -Arguments $Arguments }
    $output = @(Get-CapturedFieldValue -Result $result -Name 'output' -Default @())
    $exitCode = Get-CapturedFieldValue -Result $result -Name 'exitCode' -Default 1
    $captureError = Get-CapturedFieldValue -Result $result -Name 'error' -Default $null
    $durationSeconds = Get-CapturedFieldValue -Result $result -Name 'durationSeconds' -Default 0

    $phase.exitCode = $exitCode
    $phase.durationSeconds = $durationSeconds
    $phase.outputTail = @($output | Select-Object -Last 60)
    [void](Write-PhaseLog -Phase $phase -Lines $output -LogRoot $LogRoot)

    $classification = Get-WhitespaceClassification -Text ($output -join "`n")
    $phase.advisories = @($classification.Advisories)
    $phase.noiseLines = @($classification.NoiseLines)
    $phase.blocking = @($classification.Blocking)

    if (-not [string]::IsNullOrWhiteSpace([string]$captureError)) {
        $phase.reason = [string]$captureError
        $phase.status = 'failed'
        return $phase
    }

    # An exit code outside the documented git --check set means the command did
    # not run to completion; the classification is not trusted, so the phase
    # fails closed with no advisories and no blocking evidence.
    if ($exitCode -notin @(0, 1, 2)) {
        $phase.advisories = @()
        $phase.blocking = @()
        $phase.reason = "git diff --check exited with $exitCode"
        $phase.status = 'failed'
        return $phase
    }

    if ($phase.blocking.Count -gt 0) {
        $phase.reason = "Whitespace defects detected: $($phase.blocking.Count)."
        $phase.status = 'failed'
        return $phase
    }

    if ($phase.advisories.Count -gt 0) {
        # Hygiene findings never block: the phase passes with advisories exposed.
        $phase.reason = $null
        $phase.status = 'passed'
        return $phase
    }

    if ($phase.noiseLines.Count -gt 0) {
        # Noise-only output is tolerant at exit 0 and fails closed otherwise.
        if ($exitCode -eq 0) {
            $phase.status = 'passed'
        } else {
            $phase.reason = "Command exited with code $exitCode."
            $phase.status = 'failed'
        }
        return $phase
    }

    if ($exitCode -ne 0) {
        $phase.reason = "Command exited with code $exitCode."
        $phase.status = 'failed'
        return $phase
    }

    $phase.status = 'passed'
    return $phase
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
    param([string]$LogRoot)

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
        [void](Write-PhaseLog -Phase $phase -Lines $errorsFound -LogRoot $LogRoot)
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
    foreach ($phase in $Summary.phases) {
        $suffix = if ($phase.outputPath) { " (log: $($phase.outputPath))" } else { '' }
        Write-Host ("  [{0}] {1}{2}" -f $phase.status, $phase.name, $suffix)
        # Read 'advisories' defensively: not every phase carries the key, and under
        # Set-StrictMode -Version Latest a bare property touch on a phase built by
        # New-Phase would throw. Phases are ordered dictionaries (New-Phase) whose
        # keys are not exposed through PSObject.Properties, so the dictionary path
        # is the one that matches. Hygiene findings are surfaced even when the
        # phase passed, which is the whole point of the downgrade.
        $advisories = @()
        if ($phase -is [System.Collections.IDictionary]) {
            if ($phase.Contains('advisories')) { $advisories = @($phase['advisories']) }
        } else {
            $advisoryProperty = $phase.PSObject.Properties['advisories']
            if ($null -ne $advisoryProperty -and $null -ne $advisoryProperty.Value) { $advisories = @($advisoryProperty.Value) }
        }
        foreach ($advisory in $advisories) {
            Write-Host ("    advisory: {0}:{1}: {2}" -f $advisory.path, $advisory.line, $advisory.reason) -ForegroundColor DarkYellow
        }
    }
}

function Read-ReleasePhaseDetails {
    param(
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [AllowNull()][string]$PhaseStatus
    )

    $details = [ordered]@{
        status = $null
        failedPhases = @()
        failedPhaseDetails = @()
        skippedPhases = @()
        summaryPath = $null
    }
    $parseError = $null
    if (Test-Path -LiteralPath $SummaryPath -PathType Leaf) {
        try {
            $releaseSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
            # Every read below goes through Get-GxMcpReleaseFieldValue, envelope
            # included. Under Set-StrictMode -Version Latest, touching a property
            # the summary does not carry throws, and the catch would then discard
            # EVERY already-recoverable failedPhaseDetails entry and report
            # "unable to parse" instead - the evidence disappears exactly in the
            # scenario this function exists to make legible. A missing key reads
            # as $null, so a phase without 'status' is classified as a failure
            # with its name kept, and a summary without 'phases' yields an empty
            # set rather than nothing at all. release-doctor.ps1 carries the same
            # guard for the same reason (issue #348); this file must not diverge.
            $details.status = Get-GxMcpReleaseFieldValue -Object $releaseSummary -Name 'status'
            # release-preflight.ps1 decides failure by negation: any per-phase
            # status outside its allowed terminal set is a failure (see
            # $allowedTerminalStatuses there). Mirror that rule instead of
            # enumerating failure statuses in positive, which left 'running' - a
            # phase the preflight persists before it completes, so a preflight
            # killed mid-run retains it - and any future status out of the
            # retained detail. The checker never runs the preflight with -DryRun,
            # so 'dry-run' cannot appear, but it stays in the allowed set to match
            # the producer.
            #
            # A 'running' phase in a COMPLETED run is impossible: both the parallel
            # and the serial wave drive every phase to a terminal status before the
            # preflight aggregates (Invoke-PreflightParallel completes all states
            # before evaluating blocking). So mapping 'running' to failure carries
            # no false positive - 'running' in a summary means the preflight died.
            $nonFailurePhaseStatuses = @('passed', 'skipped', 'unavailable', 'dry-run')
            # A null element is not a phase; `@($null).Count` is 1, so without this
            # filter a single null would be reported as a failed phase with an
            # all-null detail - inventing evidence. release-doctor.ps1 filters the
            # same way (issue #348).
            $preflightPhases = @(Get-GxMcpReleaseFieldValue -Object $releaseSummary -Name 'phases' | Where-Object { $null -ne $_ })
            $failedEntries = @($preflightPhases | Where-Object { (Get-GxMcpReleaseFieldValue -Object $_ -Name 'status') -notin $nonFailurePhaseStatuses })
            # The detail is built once and failedPhases is projected from it, so the
            # two can never disagree about which phases failed. An entry with no
            # usable name - a phase whose 'name' key is absent, or a non-object
            # element such as a bare string that carries no keys at all - is
            # labelled rather than emitted as a $null, which would inflate the
            # count and read as no phase in the list.
            $details.failedPhaseDetails = @($failedEntries | ForEach-Object {
                    $phaseName = [string](Get-GxMcpReleaseFieldValue -Object $_ -Name 'name')
                    if ([string]::IsNullOrEmpty($phaseName)) { $phaseName = '(unnamed phase)' }
                    [ordered]@{
                        name = $phaseName
                        command = Get-GxMcpReleaseFieldValue -Object $_ -Name 'command'
                        exitCode = Get-GxMcpReleaseFieldValue -Object $_ -Name 'exitCode'
                        durationSeconds = Get-GxMcpReleaseFieldValue -Object $_ -Name 'durationSeconds'
                        reason = Get-GxMcpReleaseFieldValue -Object $_ -Name 'reason'
                    }
                })
            $details.failedPhases = @($details.failedPhaseDetails | ForEach-Object { $_.name })
            $details.skippedPhases = @($preflightPhases | Where-Object { (Get-GxMcpReleaseFieldValue -Object $_ -Name 'status') -eq 'skipped' } | ForEach-Object { Get-GxMcpReleaseFieldValue -Object $_ -Name 'name' })
        } catch {
            $parseError = $_.Exception.Message
        }
    }
    $effectiveStatus = $PhaseStatus
    if ($null -ne $parseError) { $effectiveStatus = 'failed' }
    $keepSummary = $false
    if ($effectiveStatus -ne 'passed' -and (Test-Path -LiteralPath $SummaryPath -PathType Leaf)) {
        $details.summaryPath = $SummaryPath
        $keepSummary = $true
    }
    return [pscustomobject]@{
        details = $details
        parseError = $parseError
        keepSummary = $keepSummary
    }
}

$summary = [ordered]@{
    schemaVersion = 'gxmcp-pre-push/2'
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
    . (Join-Path $root 'scripts\release-contract.ps1')
    [void](Remove-StalePrePushArtifacts -LogRoot $tempRoot -RetentionDays $ArtifactRetentionDays)

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
        $rangeDiffPhase = Invoke-GitWhitespacePhase -Name 'git diff check (base..HEAD)' -Arguments (Get-WhitespaceDiffArguments -Range ("{0}..HEAD" -f $BaseRef)) -LogRoot $logRoot
        [void]$summary.phases.Add($rangeDiffPhase)
        if (Test-PhaseMechanicalFailure -Phase $rangeDiffPhase) { $mechanicalFailed = $true }
    }

    $workingDiffPhase = Invoke-GitWhitespacePhase -Name 'git diff check (working tree)' -Arguments (Get-WhitespaceDiffArguments) -LogRoot $logRoot
    [void]$summary.phases.Add($workingDiffPhase)
    if (Test-PhaseMechanicalFailure -Phase $workingDiffPhase) { $mechanicalFailed = $true }

    $parsePhase = Invoke-PowerShellParsePhase -LogRoot $logRoot
    [void]$summary.phases.Add($parsePhase)
    if (Test-PhaseMechanicalFailure -Phase $parsePhase) { $mechanicalFailed = $true }

    $releaseSummaryPath = Join-Path $tempRoot ('gxmcp-pre-push-release-' + [guid]::NewGuid().ToString('N') + '.json')
    $keepReleaseSummary = $false
    try {
        $releasePhase = Invoke-NativePhase `
            -Name 'release preflight (without live KB)' `
            -Executable 'pwsh' `
            -Arguments @('-NoProfile', '-File', (Join-Path $root 'scripts/release-preflight.ps1'), '-SkipLive', '-SummaryPath', $releaseSummaryPath) `
            -LogRoot $logRoot
        $releaseOutcome = Read-ReleasePhaseDetails -SummaryPath $releaseSummaryPath -PhaseStatus $releasePhase.status
        if ($null -ne $releaseOutcome.parseError) {
            $releasePhase.reason = "Unable to parse release preflight summary: $($releaseOutcome.parseError)"
            $releasePhase.status = 'failed'
        }
        if ($releasePhase.status -eq 'failed' -and
            (@($releasePhase.outputTail) -join "`n") -match '(?i)(not recognized as an internal or external command|não é reconhecido como um comando|command not found|enoent)') {
            $releasePhase.status = 'unavailable'
            $releasePhase.reason = 'Release preflight could not run because a local tool or dependency was unavailable.'
        }
        if (Test-ReleasePhaseStatusUnexpected -Status $releasePhase.status) {
            $releasePhase.reason = "Release preflight phase ended with an unexpected status '$($releasePhase.status)'; treating it as failed."
            $releasePhase.status = 'failed'
        }
        $releasePhase.details = $releaseOutcome.details
        $keepReleaseSummary = $releaseOutcome.keepSummary
        [void]$summary.phases.Add($releasePhase)
        if ($releasePhase.status -eq 'failed') { $mechanicalFailed = $true }
        if ($releasePhase.status -eq 'unavailable') { $mechanicalUnavailable = $true }
    } finally {
        if (-not $keepReleaseSummary -and (Test-Path -LiteralPath $releaseSummaryPath -PathType Leaf)) {
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
