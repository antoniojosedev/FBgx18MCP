[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',

    [int]$ArtifactRetentionDays = 7,

    [switch]$AsJson,

    [switch]$IncludeRustLane,

    [string]$CargoExecutable
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

# Raw tails are kept out of the serialized summary. They exist only so the
# release-phase retag can look for "command not found" after outputTail has
# already been redacted.
$rawPhaseTails = @{}

function Set-RawPhaseTail {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][object[]]$Tail
    )

    if ($null -eq (Get-Variable -Name 'rawPhaseTails' -Scope Script -ErrorAction SilentlyContinue)) {
        $script:rawPhaseTails = @{}
    }
    $script:rawPhaseTails[$Name] = @($Tail)
}

function Get-RawPhaseTail {
    param([Parameter(Mandatory = $true)][string]$Name)

    $variable = Get-Variable -Name 'rawPhaseTails' -Scope Script -ErrorAction SilentlyContinue
    if ($null -eq $variable) { return @() }
    return @($variable.Value[$Name])
}

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
        advisories = @()
        noiseLines = @()
        blocking = @()
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
    $rawTail = @($result.output | Select-Object -Last 60)
    Set-RawPhaseTail -Name $Name -Tail $rawTail
    $phase.outputTail = @($rawTail | ForEach-Object { Redact-PrePushLogLine -Line ([string]$_) })
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

    $result = if ($null -ne $CapturedResult) { $CapturedResult } else { Invoke-GitWhitespaceCapture -Arguments $Arguments }
    $output = @(Get-CapturedFieldValue -Result $result -Name 'output' -Default @())
    $exitCode = Get-CapturedFieldValue -Result $result -Name 'exitCode' -Default 1
    $captureError = Get-CapturedFieldValue -Result $result -Name 'error' -Default $null
    $durationSeconds = Get-CapturedFieldValue -Result $result -Name 'durationSeconds' -Default 0

    $phase.exitCode = $exitCode
    $phase.durationSeconds = $durationSeconds
    $rawTail = @($output | Select-Object -Last 60)
    Set-RawPhaseTail -Name $Name -Tail $rawTail
    $phase.outputTail = @($rawTail | ForEach-Object { Redact-PrePushLogLine -Line ([string]$_) })
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
    param(
        [string]$LogRoot,
        [AllowNull()][string[]]$Paths
    )

    $phase = New-Phase -Name 'PowerShell AST parse' -Command 'Parser::ParseFile for quality scripts plus this checker'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $errorsFound = New-Object System.Collections.Generic.List[string]
    try {
        $targetPaths = if ($null -ne $Paths) { @($Paths) } else { @(Get-TrackedPowerShellPaths) }
        foreach ($path in $targetPaths) {
            $tokens = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
            foreach ($parseError in @($parseErrors)) {
                [void]$errorsFound.Add("$($path):$($parseError.Extent.StartLineNumber): $($parseError.Message)")
            }
        }
        $phase.exitCode = if ($errorsFound.Count -eq 0) { 0 } else { 1 }
        $phase.status = if ($errorsFound.Count -eq 0) { 'passed' } else { 'failed' }
        $rawTail = @($errorsFound | Select-Object -Last 60)
        Set-RawPhaseTail -Name $phase.name -Tail $rawTail
        $phase.outputTail = @($rawTail | ForEach-Object { Redact-PrePushLogLine -Line ([string]$_) })
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
        # Read keys defensively: phases built outside New-Phase (the tests) may
        # not carry every key, and under Set-StrictMode -Version Latest a bare
        # property touch would throw. Phases are ordered dictionaries (New-Phase)
        # whose keys are not exposed through PSObject.Properties, so the
        # dictionary path is the one that matches. Hygiene findings are surfaced
        # even when the phase passed, which is the whole point of the downgrade.
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

        $aggregate = $null
        if ($phase -is [System.Collections.IDictionary] -and $phase.Contains('details') -and $null -ne $phase['details']) {
            $details = $phase['details']
            if ($details -is [System.Collections.IDictionary]) {
                if ($details.Contains('aggregateFailure')) { $aggregate = $details['aggregateFailure'] }
            } else {
                $aggregateProperty = $details.PSObject.Properties['aggregateFailure']
                if ($null -ne $aggregateProperty) { $aggregate = $aggregateProperty.Value }
            }
        }
        if ($null -ne $aggregate) {
            $aggregateStatus = if ([string]::IsNullOrEmpty([string]$aggregate.status)) { 'status unavailable' } else { [string]$aggregate.status }
            Write-Host ("    preflight: {0} (no failed phase)" -f $aggregateStatus) -ForegroundColor Red
            foreach ($line in @(([string]$aggregate.detail) -split "`r?`n")) {
                if (-not [string]::IsNullOrWhiteSpace($line)) { Write-Host ("      " + $line) -ForegroundColor DarkRed }
            }
        }
    }

    $rustLaneSpecs = @(
        [ordered]@{ key = 'rustLane'; label = 'gx-engine' }
        [ordered]@{ key = 'gxwireRustLane'; label = 'gxwire' }
    )
    foreach ($laneSpec in $rustLaneSpecs) {
        $rustLane = $null
        if ($Summary -is [System.Collections.IDictionary]) {
            if ($Summary.Contains($laneSpec.key)) { $rustLane = $Summary[$laneSpec.key] }
        } else {
            $rustLaneProperty = $Summary.PSObject.Properties[$laneSpec.key]
            if ($null -ne $rustLaneProperty) { $rustLane = $rustLaneProperty.Value }
        }
        if ($null -ne $rustLane -and [string]$rustLane.status -ne 'not-requested') {
            $rustNote = if ($rustLane.note) { " ($($rustLane.note))" } else { '' }
            $rustColor = if ($rustLane.status -eq 'passed') { 'Green' } else { 'Yellow' }
            Write-Host ("  {0} Rust lane: {1}{2}" -f $laneSpec.label, $rustLane.status, $rustNote) -ForegroundColor $rustColor
            foreach ($check in @($rustLane.checks)) {
                if ($check.status -ne 'passed') {
                    $checkLog = if ($check.outputPath) { " (log: $($check.outputPath))" } else { '' }
                    Write-Host ("    {0}: exit {1}{2}" -f $check.name, $check.exitCode, $checkLog) -ForegroundColor Yellow
                }
            }
        }
    }
}

function New-AggregateFailureDetail {
    param(
        [AllowNull()][string]$PhaseStatus,
        [AllowNull()][object]$SummaryStatus,
        [AllowNull()][object[]]$OutputTail,
        [int]$AttributedFailureCount = 0
    )

    # A phase that failed without any failed phase in its own summary is an
    # aggregate failure: the preflight exited non-zero after its phases (for
    # example, the binary-fingerprint binding). The phase lists stay faithful to
    # real phases; this is a separate, explicitly unattributed detail. The
    # retag to 'unavailable' happens before this call, so an unavailable phase
    # never produces one.
    if ([string]::IsNullOrWhiteSpace($PhaseStatus)) { return $null }
    if ($PhaseStatus -in @('passed', 'skipped', 'unavailable', 'dry-run')) { return $null }
    if ($AttributedFailureCount -gt 0) { return $null }

    $tail = @($OutputTail | Where-Object { $null -ne $_ } | Select-Object -Last 20)
    $detail = @($tail | ForEach-Object { Redact-PrePushLogLine -Line ([string]$_) }) -join [Environment]::NewLine
    return [ordered]@{
        status = $SummaryStatus
        detail = $detail
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
        aggregateFailure = $null
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

function Get-ReleaseContractAllowlist {
    # The only top-level bindings the fork sanctions in the upstream contract.
    return @(
        'script:GxMcpReleaseWorkflowCheck'
        'script:GxMcpReleaseWorkflowAppId'
    )
}

function Test-ReleaseContractConstantBinding {
    param([Parameter(Mandatory = $true)][object]$Statement)

    if ($Statement -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { return $false }
    if ($Statement.Operator -ne [System.Management.Automation.Language.TokenKind]::Equals) { return $false }
    $left = $Statement.Left
    if ($left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { return $false }
    if ((Get-ReleaseContractAllowlist) -notcontains $left.VariablePath.UserPath) { return $false }
    $value = $Statement.Right
    if ($value -is [System.Management.Automation.Language.CommandExpressionAst]) { $value = $value.Expression }
    return $value -is [System.Management.Automation.Language.ConstantExpressionAst]
}

function Test-ReleaseContractTopLevelStatement {
    param([Parameter(Mandatory = $true)][object]$Statement)

    if ($Statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $true }
    return Test-ReleaseContractConstantBinding -Statement $Statement
}

function Assert-ReleaseContractTopLevelSafe {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$CheckerPath = $PSCommandPath
    )

    $contractTokens = $null; $contractParseErrors = $null
    $contractAst = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$contractTokens, [ref]$contractParseErrors)
    if (@($contractParseErrors).Count -gt 0) { throw "release-contract.ps1 has parse errors: $($contractParseErrors -join '; ')" }
    if ($contractAst.ParamBlock -or $contractAst.BeginBlock -or $contractAst.ProcessBlock -or
        $contractAst.DynamicParamBlock -or $contractAst.CleanBlock) {
        throw 'release-contract.ps1 must not declare param/begin/process/dynamicparam/clean blocks; the dot-source must not consume arguments or run top-level code.'
    }
    # using statements are resolved while the file is parsed, before this guard
    # can inspect them; the refusal is detection, not prevention. The fork
    # contract declares none.
    if (@($contractAst.UsingStatements).Count -gt 0) {
        throw 'release-contract.ps1 must not declare using statements; the contract is a function library, not a module import.'
    }

    foreach ($stmt in @($contractAst.EndBlock.Statements)) {
        if (Test-ReleaseContractTopLevelStatement -Statement $stmt) { continue }
        $snippet = $stmt.Extent.Text.Trim()
        if ($snippet.Length -gt 120) { $snippet = $snippet.Substring(0, 117) + '...' }
        throw "release-contract.ps1 must define only functions at top level or bind an allowed release constant (line $($stmt.Extent.StartLineNumber)); found $($stmt.GetType().Name): $snippet. If upstream added a sanctioned binding, update Get-ReleaseContractAllowlist in the checker."
    }

    $contractFunctions = @($contractAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
            }, $true) | ForEach-Object { $_.Name })
    if ($contractFunctions.Count -eq 0) { return }

    $checkerAst = $contractAst
    if (-not [string]::IsNullOrWhiteSpace($CheckerPath) -and (Test-Path -LiteralPath $CheckerPath -PathType Leaf)) {
        $checkerTokens = $null; $checkerParseErrors = $null
        $checkerAst = [System.Management.Automation.Language.Parser]::ParseFile($CheckerPath, [ref]$checkerTokens, [ref]$checkerParseErrors)
    }

    # A contract function shadows the checker's own functions and every command
    # it invokes, not just same-named functions. The two intentional exports are
    # exempt; anything else is a collision that would change behavior silently.
    $checkerFunctions = @($checkerAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
            }, $true) | ForEach-Object { $_.Name })
    $invokedCommands = @($checkerAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $expectedExports = @('Protect-GxMcpReleaseText', 'Get-GxMcpReleaseFieldValue')
    $collisions = @($contractFunctions | Where-Object {
            $name = $_
            ($checkerFunctions -contains $name) -or
            (($invokedCommands -contains $name) -and ($expectedExports -notcontains $name))
        })
    if ($collisions.Count -gt 0) {
        throw "release-contract.ps1 defines functions that shadow names used by the checker: $($collisions -join ', '). Rename them upstream or update the guard."
    }
}

function Resolve-CargoExecutable {
    param([AllowNull()][string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (Test-Path -LiteralPath $Explicit -PathType Leaf) { return $Explicit }
        $explicitCommand = Get-Command $Explicit -CommandType Application -ErrorAction SilentlyContinue
        if ($null -ne $explicitCommand) { return $explicitCommand.Source }
        return $null
    }
    $command = Get-Command cargo -ErrorAction SilentlyContinue
    if ($null -eq $command) { return $null }
    return $command.Source
}

function Test-RustCratePathChanged {
    param(
        [AllowNull()][object[]]$Paths,
        [Parameter(Mandatory = $true)][string]$Prefix
    )

    $normalizedPrefix = $Prefix.Replace('\', '/').TrimEnd('/') + '/'
    foreach ($path in @($Paths)) {
        if ($null -eq $path) { continue }
        $normalizedPath = ([string]$path).Replace('\', '/')
        if ($normalizedPath.StartsWith($normalizedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Get-RustLaneRequestState {
    param(
        [bool]$Explicit,
        [AllowNull()][object[]]$ChangedPaths
    )

    # The explicit switch forces both lanes. Without it, a change under either
    # crate auto-enables only that lane; the lane stays advisory.
    $engineChanged = Test-RustCratePathChanged -Paths $ChangedPaths -Prefix 'src/gx-engine'
    $gxwireChanged = Test-RustCratePathChanged -Paths $ChangedPaths -Prefix 'src/gxwire'
    $autoNote = 'auto-enabled-by-changed-paths'

    $engineNote = $null
    if (-not $Explicit -and $engineChanged) { $engineNote = $autoNote }
    $gxwireNote = $null
    if (-not $Explicit -and $gxwireChanged) { $gxwireNote = $autoNote }

    return [ordered]@{
        engine = [ordered]@{
            requested = ($Explicit -or $engineChanged)
            note      = $engineNote
        }
        gxwire = [ordered]@{
            requested = ($Explicit -or $gxwireChanged)
            note      = $gxwireNote
        }
    }
}

function New-RustLaneSummary {
    param(
        [bool]$Requested,
        [string]$LaneName = 'gx-engine',
        [AllowNull()][string]$CargoPath,
        [string]$CratePath,
        [string]$LogRoot,
        [AllowNull()][string]$RequestNote
    )

    # Advisory by design: the fork policy reports upstream defects instead of
    # blocking on them, so nothing here feeds mechanicalFailed/unavailable and
    # nothing enters Summary.phases.
    if (-not $Requested) {
        return [ordered]@{ name = $LaneName; requested = $false; ran = $false; status = 'not-requested'; note = $null; checks = @() }
    }
    if ([string]::IsNullOrWhiteSpace($CargoPath)) {
        return [ordered]@{ name = $LaneName; requested = $true; ran = $false; status = 'not-run'; note = 'cargo-not-found'; checks = @() }
    }
    if (-not (Test-Path -LiteralPath $CratePath -PathType Container)) {
        $missingNote = if ($LaneName -eq 'gx-engine') { 'engine-missing' } else { ("{0}-missing" -f $LaneName) }
        return [ordered]@{ name = $LaneName; requested = $true; ran = $false; status = 'not-run'; note = $missingNote; checks = @() }
    }

    $checks = New-Object System.Collections.Generic.List[object]
    $specs = @(
        [ordered]@{ name = 'fmt'; arguments = @('fmt', '--check') }
        [ordered]@{ name = 'clippy'; arguments = @('clippy', '--all-targets', '--locked', '--', '-D', 'warnings') }
        [ordered]@{ name = 'test'; arguments = @('test', '--locked') }
    )
    foreach ($spec in $specs) {
        $phase = New-Phase -Name ("{0} {1}" -f $LaneName, $spec.name) -Command (Format-Command -Executable $CargoPath -Arguments $spec.arguments)
        $result = Invoke-NativeCapture -Executable $CargoPath -Arguments $spec.arguments -WorkingDirectory $CratePath
        $phase.exitCode = $result.exitCode
        $phase.durationSeconds = $result.durationSeconds
        $rawTail = @($result.output | Select-Object -Last 20)
        $phase.outputTail = @($rawTail | ForEach-Object { Redact-PrePushLogLine -Line ([string]$_) })
        [void](Write-PhaseLog -Phase $phase -Lines $result.output -LogRoot $LogRoot)
        if ($null -ne $result.error) {
            $phase.status = 'failed'
            $phase.reason = $result.error
        } elseif ($result.exitCode -eq 0) {
            $phase.status = 'passed'
        } else {
            $phase.status = 'failed'
            $phase.reason = "Command exited with code $($result.exitCode)."
        }
        [void]$checks.Add([ordered]@{
                name = $spec.name
                command = $phase.command
                status = $phase.status
                exitCode = $phase.exitCode
                durationSeconds = $phase.durationSeconds
                outputPath = $phase.outputPath
                outputTail = @($phase.outputTail)
            })
    }

    $failed = @($checks | Where-Object { $_['status'] -ne 'passed' }).Count -gt 0
    $laneStatus = if ($failed) { 'failed' } else { 'passed' }
    $checkArray = $checks.ToArray()
    return [ordered]@{
        name = $LaneName
        requested = $true
        ran = $true
        status = $laneStatus
        note = $RequestNote
        checks = $checkArray
    }
}

function Get-RustLaneNotCoveredEntry {
    param(
        [string]$LaneName = 'gx-engine',
        [bool]$Requested,
        [bool]$Ran,
        [AllowNull()][string]$Note
    )

    if ($Ran) { return $null }
    if (-not $Requested) { return "$LaneName Rust lane (opt-in)" }
    if ([string]::IsNullOrWhiteSpace($Note)) { return "$LaneName Rust lane" }
    return "$LaneName Rust lane ($Note)"
}

function Set-RustLaneCoverage {
    param(
        [Parameter(Mandatory = $true)][object]$Summary,
        [string]$LaneName = 'gx-engine',
        [bool]$Requested,
        [bool]$Ran,
        [AllowNull()][string]$Note
    )

    $prefix = "$LaneName Rust lane"
    $entries = @(@($Summary.notCovered) | Where-Object {
            $null -ne $_ -and -not ([string]$_).StartsWith($prefix, [System.StringComparison]::Ordinal)
        })
    $entry = Get-RustLaneNotCoveredEntry -LaneName $LaneName -Requested $Requested -Ran $Ran -Note $Note
    if ($null -ne $entry) { $entries += $entry }
    $Summary.notCovered = @($entries)
}

function Set-RustLaneSeedState {
    param(
        [Parameter(Mandatory = $true)][object]$Summary,
        [string]$LaneName = 'gx-engine',
        [string]$LaneKey = 'rustLane',
        [bool]$Requested
    )

    $status = 'not-requested'
    $note = $null
    if ($Requested) {
        $status = 'not-run'
        $note = 'aborted-before-lane'
    }
    $Summary[$LaneKey] = [ordered]@{
        name = $LaneName
        requested = $Requested
        ran = $false
        status = $status
        note = $note
        checks = @()
    }
    Set-RustLaneCoverage -Summary $Summary -LaneName $LaneName -Requested $Requested -Ran $false -Note $note
}

$rustLaneSeedRequested = [bool]$IncludeRustLane

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
    rustLane = [ordered]@{ name = 'gx-engine'; requested = $false; ran = $false; status = 'not-requested'; note = $null; checks = @() }
    gxwireRustLane = [ordered]@{ name = 'gxwire'; requested = $false; ran = $false; status = 'not-requested'; note = $null; checks = @() }
    localReadiness = 'not-run'
    remoteReadiness = 'unverified'
    pushReadiness = 'blocked'
    status = 'running'
    exitCode = 1
}
Set-RustLaneSeedState -Summary $summary -LaneName 'gx-engine' -LaneKey 'rustLane' -Requested $rustLaneSeedRequested
Set-RustLaneSeedState -Summary $summary -LaneName 'gxwire' -LaneKey 'gxwireRustLane' -Requested $rustLaneSeedRequested

$laneRequests = $null
$mechanicalFailed = $false
$mechanicalUnavailable = $false
$baseAvailable = $false
$commitsBehind = 0
$workingTreeDirty = $false

try {
    Assert-ReleaseContractTopLevelSafe -Path (Join-Path $root 'scripts\release-contract.ps1') -CheckerPath $selfPath
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

        # Refresh the seed as soon as the range is known: an abort from here on
        # must show the lane that auto-enable would have requested, not opt-in.
        $laneRequests = Get-RustLaneRequestState -Explicit ([bool]$IncludeRustLane) -ChangedPaths $summary.changedPaths
        Set-RustLaneSeedState -Summary $summary -LaneName 'gx-engine' -LaneKey 'rustLane' -Requested $laneRequests.engine.requested
        Set-RustLaneSeedState -Summary $summary -LaneName 'gxwire' -LaneKey 'gxwireRustLane' -Requested $laneRequests.gxwire.requested
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
            (@(Get-RawPhaseTail -Name $releasePhase.name) -join "`n") -match '(?i)(not recognized as an internal or external command|não é reconhecido como um comando|command not found|enoent)') {
            $releasePhase.status = 'unavailable'
            $releasePhase.reason = 'Release preflight could not run because a local tool or dependency was unavailable.'
        }
        if (Test-ReleasePhaseStatusUnexpected -Status $releasePhase.status) {
            $releasePhase.reason = "Release preflight phase ended with an unexpected status '$($releasePhase.status)'; treating it as failed."
            $releasePhase.status = 'failed'
        }
        $releasePhase.details = $releaseOutcome.details
        $aggregateFailure = New-AggregateFailureDetail `
            -PhaseStatus $releasePhase.status `
            -SummaryStatus $releaseOutcome.details.status `
            -OutputTail (Get-RawPhaseTail -Name $releasePhase.name) `
            -AttributedFailureCount @($releaseOutcome.details.failedPhaseDetails).Count
        if ($null -ne $aggregateFailure) {
            $releasePhase.details.aggregateFailure = $aggregateFailure
        }
        $keepReleaseSummary = $releaseOutcome.keepSummary
        [void]$summary.phases.Add($releasePhase)
        if ($releasePhase.status -eq 'failed') { $mechanicalFailed = $true }
        if ($releasePhase.status -eq 'unavailable') { $mechanicalUnavailable = $true }
    } finally {
        if (-not $keepReleaseSummary -and (Test-Path -LiteralPath $releaseSummaryPath -PathType Leaf)) {
            Remove-Item -LiteralPath $releaseSummaryPath -Force -ErrorAction SilentlyContinue
        }
    }

    if ($null -eq $laneRequests) {
        $laneRequests = Get-RustLaneRequestState -Explicit ([bool]$IncludeRustLane) -ChangedPaths $summary.changedPaths
    }
    $cargoPath = $null
    if ($laneRequests.engine.requested -or $laneRequests.gxwire.requested) {
        $cargoPath = Resolve-CargoExecutable -Explicit $CargoExecutable
    }
    $rustLane = New-RustLaneSummary -Requested $laneRequests.engine.requested -LaneName 'gx-engine' -RequestNote $laneRequests.engine.note -CargoPath $cargoPath -CratePath (Join-Path $root 'src\gx-engine') -LogRoot $logRoot
    $gxwireRustLane = New-RustLaneSummary -Requested $laneRequests.gxwire.requested -LaneName 'gxwire' -RequestNote $laneRequests.gxwire.note -CargoPath $cargoPath -CratePath (Join-Path $root 'src\gxwire') -LogRoot $logRoot
    $summary.rustLane = $rustLane
    $summary.gxwireRustLane = $gxwireRustLane
    Set-RustLaneCoverage -Summary $summary -LaneName 'gx-engine' -Requested $rustLane.requested -Ran $rustLane.ran -Note $rustLane.note
    Set-RustLaneCoverage -Summary $summary -LaneName 'gxwire' -Requested $gxwireRustLane.requested -Ran $gxwireRustLane.ran -Note $gxwireRustLane.note

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
# Belt and braces for the machine contract: the phase tails are redacted at
# capture, but reason/command/snippets are assembled elsewhere. When the
# contract did not load (a refused import), the redactor is unavailable and the
# summary must still be emitted.
if ($null -ne (Get-Command Protect-GxMcpReleaseText -ErrorAction SilentlyContinue)) {
    $json = Protect-GxMcpReleaseText $json
}
if ($AsJson) {
    [Console]::Out.WriteLine($json)
} else {
    Write-TextSummary -Summary $summary
}
exit ([int]$summary.exitCode)
