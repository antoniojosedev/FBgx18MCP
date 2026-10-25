<#
.SYNOPSIS
Durability smoke for genexus_workwith writes (issue #590).

.DESCRIPTION
The genexus_workwith write receipts verify by rereading the WorkWith instance from
the same in-memory object that was just saved, and they say so in
persistenceEvidence (independentPersistedReread=false, provesDurability=false).
This smoke is the durability proof that the receipt deliberately does not claim:
on a DISPOSABLE Knowledge Base with a standard WorkWith instance it authors a filter
attribute (and optionally an action), restarts the Worker process with
genexus_worker_reload, and rereads the definition on a Worker that never held the
written instance in memory. It then removes what it created (issue #645) and verifies the
removal the same way.

CLEANUP (issue #645)
- The filter attribute name is normalized once (trim, leading `&` removed). If the
  normalized name already exists in the baseline the smoke stops with a validation failure
  (exit 1) BEFORE writing anything and never removes an attribute it did not create.
- `$filterWritten` is set before add_filter_attribute is called (the attempt can persist
  even when the answer is a failure or unreadable). The cleanup removes only what this run
  created: the attribute, and through removeConditions exactly the condition text this run
  sent (-Condition), read back from its own list_filters. A pre-existing condition that
  mentions the variable is never removed; an unlisted referencing condition makes the tool
  refuse with FilterAttributeReferencedByConditions, which is a cleanup failure.
- Success path: durability reread -> one cleanup (remove_filter_attribute, then
  remove_action when the action lane ran, then ONE Worker restart) -> reread that asserts the
  attribute, the condition and the action are absent -> comparison of the projection
  (level, attributes name/description/domain/default/variableName, conditions in order) with
  the baseline. The neighbour comparison of default/variableName is intentionally stronger
  than the tool's own verifier, which compares neighbours by name/description/domain and the
  conditions. If the reader changed between the two readings (native <-> XML,
  observedSource) values are not compared across readers and cleanup=unverified.
- Failure path: the single finalizer runs the cleanup once ($cleanupDone) after the primary
  error was captured, without an extra restart; its result is best-effort and reported as
  cleanup=unverified or cleanup=fail. The primary error is preserved, never replaced.
- Cleanup helpers never call `exit`; Exit-Fail/Exit-Unavailable throw. The summary is
  written once, after the cleanup, and the exit code is chosen last.

WHAT THIS SMOKE DEMONSTRATES
- In the tested scenario the written definition survives a Worker restart: the
  reread runs on a new Worker process (the whoami pid changes, which the smoke
  asserts), so the result cannot come from the in-memory instance the receipt
  verified against.
- The filter attribute and condition this run created are removed again with
  remove_filter_attribute and, after a second Worker restart, the reread shows both absent
  and the filter projection equal to the baseline (when the same reader answered both).
- The receipt contract of this build: every write receipt carries
  persistenceEvidence with provesDurability=false.

WHAT THIS SMOKE DOES NOT COVER
- The GeneXus IDE: it does not prove the IDE displays the attribute, nor that the
  derived objects regenerate (receipts declare derivedObjectRegeneration).
- Concurrent writers, or a KB that the IDE has open at the same time.
- Other Knowledge Bases or GeneXus majors than the one it ran against.
- Pristine restoration: the cleanup removes the attribute and the condition this run
  created; it is not a restoration of the KB and does not guarantee regeneration of derived
  objects (derivedObjectRegeneration: not_guaranteed). The `filter` node itself is kept.
- Without the restart, the verification of a removal is a reread of the same Worker after
  the save (see observedSource) and does not prove persistence; this is what the failure
  path can offer.
- The optional action lane: it can leave its action behind when the run fails, and
  cleanup=pass for the filter does not cover the action.
- A transport failure or a dead Worker can prevent the cleanup. Run it only against a
  disposable KB.

PREREQUISITES
- PowerShell 7+.
- A Gateway serving Streamable HTTP (default http://127.0.0.1:5000/mcp), for
  example publish\start_mcp.bat or the Gateway your MCP client started.
- A disposable KB (directory name or alias containing "teste", or pass
  -AllowKbMutation; the smoke writes and removes a filter attribute and restarts the Worker) with the standard WorkWith pattern applied to -Transaction and
  a `filter` node on the Selection of the target Level. The filter node is created
  in the GeneXus IDE; without it the smoke exits 2 (WorkWithFilterNotPresent).
- For -ReloadMode hard, -WorkerSourceDir must point at a built Worker output
  directory (for example src\GxMcp.Worker\bin\Debug); mode=soft restarts the
  Worker with the binaries it already runs. Both modes replace the process.

SUMMARY STATES
durability = pass | fail | not_attempted
cleanup    = pass | unverified | fail | not_attempted
outcome    = pass | fail | unavailable
outcome=pass only when durability=pass AND cleanup=pass. durability=pass with cleanup=fail
or cleanup=unverified is possible and is reported as outcome=fail.

EXIT CODES
0 = pass.
1 = fail: any validation rejection (including an attribute that already exists), a broken
receipt contract, a durability failure, or a cleanup that ended fail or unverified.
2 = unavailable: a precondition was missing BEFORE any write was attempted (no KB,
Transaction or filter node, PowerShell version, KB could not be opened); nothing about
durability was decided.

.EXAMPLE
pwsh -NoProfile -File scripts\workwith_persistence_smoke.ps1 -KbPath C:\KBs\KBTesteWorkWith -Transaction Customer

.EXAMPLE
pwsh -NoProfile -File scripts\workwith_persistence_smoke.ps1 -Kb kbtesteworkwith -Transaction Customer -Procedure procPrintCustomer -ActionParameters CustomerId -ReloadMode hard -WorkerSourceDir .\src\GxMcp.Worker\bin\Debug

.NOTES
-Procedure enables the optional action lane. genexus_workwith checks the number of
parameters against the Procedure's parm() rule (ArityMismatch), so pass one
-ActionParameters entry per parm() argument (attributes, &variables or expressions).
Without them the lane only works for a Procedure that declares no parameters.
#>
param(
    [string]$BaseUrl = 'http://127.0.0.1:5000/mcp',
    [string]$KbPath = '',
    [string]$Kb = '',
    [Parameter(Mandatory = $true)][string]$Transaction,
    [string]$Level = '',
    [string]$Attribute = '',
    [string]$Condition = '',
    [string]$Procedure = '',
    [string[]]$ActionParameters = @(),
    [string]$Tab = 'General',
    [ValidateSet('soft', 'hard')][string]$ReloadMode = 'soft',
    [string]$WorkerSourceDir = '',
    [switch]$AllowKbMutation,
    [int]$CleanupTimeoutSec = 180,
    [string]$SummaryPath = ''
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    # Before any summary exists: the one place that exits on its own.
    Write-Host '[WW-SMOKE] unavailable: PowerShell 7+ required. Run with pwsh.'
    exit 2
}

$script:Summary = [ordered]@{
    smoke        = 'workwith_persistence_smoke'
    issue        = 590
    cleanupIssue = 645
    demonstrates = 'The authored WorkWith definition is reread, unchanged, by a new Worker process after genexus_worker_reload; the filter attribute and condition this run created are then removed (remove_filter_attribute, exact condition text) and, after one more Worker restart, reread as absent with the filter projection equal to the baseline.'
    doesNotCover = @('GeneXus IDE visibility', 'derived-object regeneration', 'concurrent writers', 'other KBs or GeneXus majors', 'pristine restoration of the KB (the cleanup removes only what this run created; the filter node stays)', 'persistence of the removal when the run fails (the failure path rereads the same Worker without a restart)', 'cleanup of the optional action lane on failure paths (cleanup=pass for the filter does not cover the action)')
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    durability   = 'not_attempted'
    cleanup      = 'not_attempted'
    outcome      = 'unavailable'
    evidence     = [ordered]@{}
    errors       = @()
}

# Ownership and cleanup state. Written at script scope (inside the main try, which does not
# open a scope) or through $script: from the helpers.
$filterWritten = $false
$actionWritten = $false
$cleanupDone = $false
$script:CleanupFailures = [System.Collections.Generic.List[string]]::new()
$script:CleanupUnverified = [System.Collections.Generic.List[string]]::new()
$script:CleanupVerified = $false
$script:DurabilityRunning = $false
$script:RequestTimeoutSec = 0

function Write-Summary {
    $script:Summary.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $json = $script:Summary | ConvertTo-Json -Depth 10
    if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
        Set-Content -LiteralPath $SummaryPath -Value $json -Encoding utf8
        Write-Host "[WW-SMOKE] summary written to $SummaryPath"
    }
    Write-Host $json
}

# Exit-Fail / Exit-Unavailable never exit: the top of the script captures the primary error
# before the cleanup runs, then the single finalizer writes the summary and picks the exit code.
function New-SmokeStop([string]$Outcome, [string]$Reason) {
    $stop = [System.InvalidOperationException]::new($Reason)
    $stop.Data['WwSmokeOutcome'] = $Outcome
    return $stop
}

function Exit-Unavailable([string]$Reason) {
    throw (New-SmokeStop -Outcome 'unavailable' -Reason $Reason)
}

function Exit-Fail([string]$Reason) {
    throw (New-SmokeStop -Outcome 'fail' -Reason $Reason)
}

function Add-CleanupFailure([string]$Message) {
    $script:CleanupFailures.Add($Message)
    Write-Host "[WW-SMOKE] cleanup FAIL: $Message"
}

function Add-CleanupUnverified([string]$Message) {
    $script:CleanupUnverified.Add($Message)
    Write-Host "[WW-SMOKE] cleanup unverified: $Message"
}

# --- MCP transport (same contract as scripts/mcp_smoke.ps1) -----------------------
$script:SessionId = ''

function Invoke-Mcp([string]$Method, [hashtable]$Params) {
    $headers = @{
        'MCP-Protocol-Version' = '2025-11-25'
        'Content-Type'         = 'application/json'
        'Accept'               = 'application/json, text/event-stream'
    }
    if (-not [string]::IsNullOrWhiteSpace($script:SessionId)) { $headers['MCP-Session-Id'] = $script:SessionId }
    $bodyObj = @{ jsonrpc = '2.0'; id = [guid]::NewGuid().ToString('N'); method = $Method }
    if ($null -ne $Params) { $bodyObj['params'] = $Params }
    $body = $bodyObj | ConvertTo-Json -Depth 20 -Compress
    $resp = Invoke-WebRequest -Uri $BaseUrl -Method Post -Headers $headers -Body $body -UseBasicParsing -TimeoutSec $script:RequestTimeoutSec
    $sid = $resp.Headers['MCP-Session-Id']
    if ($sid) { $script:SessionId = [string]($sid | Select-Object -First 1) }
    return ($resp.Content | ConvertFrom-Json)
}

# Calls one tool and returns the Worker/Gateway envelope ({status, code, result, error}).
function Invoke-Tool([string]$Name, [hashtable]$Arguments, [string]$Label) {
    $envelope = Invoke-Mcp -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments }
    if ($null -ne $envelope.error) { Exit-Fail "${Label}: JSON-RPC error $($envelope.error.message)" }
    $text = $envelope.result.content[0].text
    if ([string]::IsNullOrWhiteSpace($text)) { Exit-Fail "${Label}: empty tool payload." }
    try { return ($text | ConvertFrom-Json) } catch { Exit-Fail "${Label}: payload is not JSON: $text" }
}

function Get-PayloadErrorCode($Payload) {
    if ($null -eq $Payload) { return '' }
    if ($Payload.error) { return [string]$Payload.error.code }
    return [string]$Payload.code
}

function Get-PayloadErrorMessage($Payload) {
    if ($null -eq $Payload) { return '' }
    if ($Payload.error) { return [string]$Payload.error.message }
    return [string]$Payload.detail
}

function Assert-Ok($Payload, [string]$Label) {
    if ($null -eq $Payload) { Exit-Fail "${Label}: no payload." }
    $status = [string]$Payload.status
    if ($status -eq 'ok' -or $status -eq 'partial' -or $status -eq 'Reloaded') { return }
    Exit-Fail "${Label}: status=$status code=$(Get-PayloadErrorCode $Payload) $(Get-PayloadErrorMessage $Payload)"
}

function Get-WorkerPid([string]$Label) {
    $who = Invoke-Tool -Name 'genexus_whoami' -Arguments @{ kb = $Kb } -Label $Label
    $workerPid = $null
    try { $workerPid = $who.worker.pid } catch { }
    if ($null -eq $workerPid) { try { $workerPid = $who.result.worker.pid } catch { } }
    if ($null -eq $workerPid) { Exit-Fail "${Label}: whoami did not expose worker.pid; the restart cannot be evidenced." }
    return [int]$workerPid
}

function Find-FilterAttribute($Filters, [string]$Name) {
    if ($null -eq $Filters -or $null -eq $Filters.attributes) { return $null }
    return @($Filters.attributes | Where-Object { [string]$_.name -ieq $Name } | Select-Object -First 1)[0]
}

function Find-TabAction($Tabs, [string]$TabCode, [string]$Name) {
    if ($null -eq $Tabs) { return $null }
    $tabObj = @($Tabs | Where-Object { [string]$_.code -ieq $TabCode -or [string]$_.name -ieq $TabCode } | Select-Object -First 1)[0]
    if ($null -eq $tabObj -or $null -eq $tabObj.actions) { return $null }
    return @($tabObj.actions | Where-Object { [string]$_.name -ieq $Name } | Select-Object -First 1)[0]
}

# The projection list_filters exposes and that the smoke compares: level, the attributes
# (name, description, domain, default, variableName) and the conditions, both in order.
function ConvertTo-FilterProjection($Filters) {
    $attributes = @()
    if ($null -ne $Filters -and $null -ne $Filters.attributes) {
        foreach ($item in @($Filters.attributes)) {
            if ($null -eq $item) { continue }
            $attributes += [ordered]@{
                name         = [string]$item.name
                description  = [string]$item.description
                domain       = [string]$item.domain
                default      = [string]$item.default
                variableName = [string]$item.variableName
            }
        }
    }
    $conditions = @()
    if ($null -ne $Filters -and $null -ne $Filters.conditions) {
        foreach ($item in @($Filters.conditions)) { $conditions += [string]$item }
    }
    return [ordered]@{ level = [string]$Filters.level; attributes = $attributes; conditions = $conditions }
}

# Differences between two projections. A $null expected field is not compared (the smoke does
# not know what the SDK materializes for the attribute it just added). Comparing the default
# and variableName of the neighbours is intentionally stronger than the tool's own verifier,
# which compares neighbours by name/description/domain and the conditions.
function Get-ProjectionDifferences($Expected, $Actual) {
    $differences = [System.Collections.Generic.List[string]]::new()
    if ([string]$Expected.level -cne [string]$Actual.level) {
        $differences.Add("level expected '$($Expected.level)' observed '$($Actual.level)'")
    }
    $expectedAttributes = @($Expected.attributes)
    $actualAttributes = @($Actual.attributes)
    if ($expectedAttributes.Count -ne $actualAttributes.Count) {
        $differences.Add("attribute count expected $($expectedAttributes.Count) observed $($actualAttributes.Count) (observed: $((@($actualAttributes | ForEach-Object { $_.name })) -join ', '))")
    } else {
        for ($i = 0; $i -lt $expectedAttributes.Count; $i++) {
            foreach ($field in 'name', 'description', 'domain', 'default', 'variableName') {
                $expectedValue = $expectedAttributes[$i][$field]
                if ($null -eq $expectedValue) { continue }
                $actualValue = [string]$actualAttributes[$i][$field]
                $different = if ($field -eq 'name') { $expectedValue -ine $actualValue } else { $expectedValue -cne $actualValue }
                if ($different) { $differences.Add("attribute[$i].$field expected '$expectedValue' observed '$actualValue'") }
            }
        }
    }
    $expectedConditions = @($Expected.conditions)
    $actualConditions = @($Actual.conditions)
    if ($expectedConditions.Count -ne $actualConditions.Count) {
        $differences.Add("condition count expected $($expectedConditions.Count) observed $($actualConditions.Count)")
    } else {
        for ($i = 0; $i -lt $expectedConditions.Count; $i++) {
            if ([string]$expectedConditions[$i] -cne [string]$actualConditions[$i]) {
                $differences.Add("condition[$i] expected '$($expectedConditions[$i])' observed '$($actualConditions[$i])'")
            }
        }
    }
    return $differences.ToArray()
}

function Invoke-WorkerRestart([string]$Label) {
    $before = Get-WorkerPid -Label "$Label whoami(before)"
    $reloadArgs = @{ mode = $ReloadMode; kb = $Kb; alias = $Kb }
    if ($ReloadMode -eq 'hard') { $reloadArgs['sourceDir'] = (Resolve-Path -LiteralPath $WorkerSourceDir).Path }
    Write-Host "[WW-SMOKE] genexus_worker_reload mode=$ReloadMode (worker pid before: $before)"
    $reload = Invoke-Tool -Name 'genexus_worker_reload' -Arguments $reloadArgs -Label "$Label genexus_worker_reload"
    Assert-Ok -Payload $reload -Label "$Label genexus_worker_reload"
    if ($reload.PSObject.Properties['swappedAndReady'] -and -not [bool]$reload.swappedAndReady) {
        Exit-Fail "${Label}: the replacement Worker did not become SDK-ready."
    }
    $after = Get-WorkerPid -Label "$Label whoami(after)"
    if ($after -eq $before) {
        Exit-Fail "${Label}: worker pid did not change ($before); the reread would not be independent of the written instance."
    }
    Write-Host "[WW-SMOKE] Worker restarted: pid $before -> $after"
    return [ordered]@{ pidBefore = $before; pidAfter = $after }
}

# The one cleanup. It removes only what THIS run created: the filter attribute (only if it is
# on the filter now) with exactly the condition text this run sent (only if that text is on the
# filter now), and, with -IncludeAction, the action this run added. It reads list_filters
# itself, never exits and never throws: every step records its own result so the finalizer
# decides the exit. -Restart (success path) restarts the Worker once after the removals and
# leaves the verification to the caller; without it (failure path) the removal is only
# reread on the same Worker, which cannot prove persistence.
function Invoke-Cleanup([switch]$Restart, [switch]$IncludeAction) {
    $script:cleanupDone = $true
    $script:RequestTimeoutSec = $CleanupTimeoutSec
    $detail = [ordered]@{ filterRemoval = 'not_attempted'; removedConditionText = $null; actionRemoval = 'not_attempted'; restart = 'not_requested' }
    $script:Summary.evidence.cleanup = $detail
    Write-Host "[WW-SMOKE] cleanup: removing filter attribute '$AttributeName'$(if ($IncludeAction -and $actionWritten) { " and action '$actionName'" })"

    try {
        $current = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'cleanup list_filters'
        Assert-Ok -Payload $current -Label 'cleanup list_filters'
        $presentNow = Find-FilterAttribute -Filters $current.result.filters -Name $AttributeName
        $conditionsNow = @()
        foreach ($item in @($current.result.filters.conditions)) { if ($null -ne $item) { $conditionsNow += [string]$item } }
        if ($null -eq $presentNow) {
            $detail.filterRemoval = 'nothing_to_remove'
        } else {
            $removeArgs = @{ action = 'remove_filter_attribute'; name = $Transaction; level = $Level; attribute = $AttributeName; kb = $Kb }
            if ($conditionsNow -ccontains $Condition) {
                $removeArgs['removeConditions'] = @($Condition)
                $detail.removedConditionText = $Condition
            }
            $removal = Invoke-Tool -Name 'genexus_workwith' -Arguments $removeArgs -Label 'remove_filter_attribute'
            if ([string]$removal.status -eq 'error') {
                $code = Get-PayloadErrorCode $removal
                if ($code -eq 'FilterAttributeNotFound') {
                    $detail.filterRemoval = 'nothing_to_remove'
                } else {
                    $detail.filterRemoval = "refused:$code"
                    Add-CleanupFailure "remove_filter_attribute refused: $code $(Get-PayloadErrorMessage $removal)"
                }
            } else {
                Assert-Ok -Payload $removal -Label 'remove_filter_attribute'
                if ([string]$removal.code -eq 'WorkWithFilterAttributeRemovedUnverified' -or $null -eq $removal.result -or -not [bool]$removal.result.verified) {
                    $detail.filterRemoval = 'removed_unverified'
                    Add-CleanupUnverified 'the remove_filter_attribute receipt is not verified (WorkWithFilterAttributeRemovedUnverified).'
                } else {
                    $detail.filterRemoval = 'removed'
                }
            }
        }
    } catch {
        $detail.filterRemoval = 'failed'
        Add-CleanupFailure "filter attribute cleanup: $($_.Exception.Message)"
    }

    if ($IncludeAction -and $actionWritten) {
        try {
            $removeAction = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'remove_action'; name = $Transaction; tab = $Tab; actionName = $actionName; kb = $Kb } -Label 'remove_action'
            if ([string]$removeAction.status -eq 'error') {
                $code = Get-PayloadErrorCode $removeAction
                if ($code -eq 'ActionNotFound') {
                    $detail.actionRemoval = 'nothing_to_remove'
                } else {
                    $detail.actionRemoval = "refused:$code"
                    Add-CleanupFailure "remove_action refused: $code $(Get-PayloadErrorMessage $removeAction)"
                }
            } else {
                Assert-Ok -Payload $removeAction -Label 'remove_action'
                if ($null -eq $removeAction.result.persistenceEvidence) {
                    $detail.actionRemoval = 'removed_without_persistence_evidence'
                    Add-CleanupFailure 'remove_action receipt carries no persistenceEvidence (issue #590 contract).'
                } else {
                    $detail.actionRemoval = 'removed'
                }
            }
        } catch {
            $detail.actionRemoval = 'failed'
            Add-CleanupFailure "action cleanup: $($_.Exception.Message)"
        }
    }

    if ($Restart) {
        if ($script:CleanupFailures.Count -gt 0) {
            $detail.restart = 'skipped_cleanup_failed'
        } else {
            try {
                $script:Summary.evidence.restartAfterCleanup = Invoke-WorkerRestart -Label 'after cleanup'
                $detail.restart = 'done'
            } catch {
                $detail.restart = 'failed'
                Add-CleanupUnverified "the Worker restart after the cleanup failed: $($_.Exception.Message)"
            }
        }
    } else {
        # Failure path: no extra restart. Read the removal back on the same Worker; this can
        # never be cleanup=pass because it does not prove persistence.
        try {
            $reread = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'cleanup reread'
            Assert-Ok -Payload $reread -Label 'cleanup reread'
            $rereadAbsent = ($null -eq (Find-FilterAttribute -Filters $reread.result.filters -Name $AttributeName))
            $detail.rereadOnSameWorker = [ordered]@{ attributeAbsent = $rereadAbsent; observedSource = [string]$reread.result.observedSource }
            if (-not $rereadAbsent) {
                Add-CleanupFailure "Filter attribute '$AttributeName' is still on the filter after the failure-path cleanup."
            } else {
                Add-CleanupUnverified 'the failure-path cleanup was reread on the same Worker without a restart; persistence of the removal is not proven.'
            }
        } catch {
            Add-CleanupUnverified "the failure-path cleanup could not be read back: $($_.Exception.Message)"
        }
    }
}

function Resolve-CleanupStatus {
    if (-not $filterWritten -or -not $cleanupDone) { return 'not_attempted' }
    if ($script:CleanupFailures.Count -gt 0) { return 'fail' }
    if ($script:CleanupUnverified.Count -gt 0) { return 'unverified' }
    if ($script:CleanupVerified) { return 'pass' }
    return 'unverified'
}

$primaryError = $null
try {
    # --- preconditions ----------------------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($KbPath) -and [string]::IsNullOrWhiteSpace($Kb)) {
        Exit-Unavailable 'Pass -KbPath (opened by this smoke) or -Kb (alias of a KB already open on the Gateway).'
    }
    if ($ReloadMode -eq 'hard' -and [string]::IsNullOrWhiteSpace($WorkerSourceDir)) {
        Exit-Unavailable '-ReloadMode hard requires -WorkerSourceDir (a built Worker output directory).'
    }
    if (-not [string]::IsNullOrWhiteSpace($KbPath) -and -not (Test-Path -LiteralPath $KbPath -PathType Container)) {
        Exit-Unavailable "KB path not found: $KbPath"
    }
    $kbLabel = if (-not [string]::IsNullOrWhiteSpace($KbPath)) { Split-Path -Leaf $KbPath } else { $Kb }
    if (-not $AllowKbMutation -and $kbLabel -notmatch 'teste') {
        # Fail closed: the smoke writes (and removes) a filter attribute and restarts the Worker.
        Exit-Unavailable "Refusing to mutate '$kbLabel': the KB name does not contain 'teste'. Pass -AllowKbMutation only for a disposable KB."
    }

    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    if ([string]::IsNullOrWhiteSpace($Attribute)) { $Attribute = "SmokeFilter$stamp" }
    # Normalize once, like the tool does: trim and drop a leading `&`. Every later use (the
    # baseline guard, the write, the cleanup) takes this value.
    $AttributeName = $Attribute.Trim().TrimStart('&')
    if ([string]::IsNullOrWhiteSpace($AttributeName)) { Exit-Fail "-Attribute '$Attribute' is empty after normalization." }
    if ([string]::IsNullOrWhiteSpace($Condition)) { $Condition = "&$AttributeName.IsEmpty();" }
    $actionName = "SmokeAction$stamp"
    $script:Summary.evidence.kb = $kbLabel
    $script:Summary.evidence.transaction = $Transaction
    $script:Summary.evidence.attribute = $AttributeName
    $script:Summary.evidence.condition = $Condition
    $script:Summary.evidence.reloadMode = $ReloadMode

    # --- 1. session -------------------------------------------------------------------------
    Write-Host '[WW-SMOKE] initialize'
    try {
        $init = Invoke-Mcp -Method 'initialize' -Params @{
            protocolVersion = '2025-11-25'
            capabilities    = @{}
            clientInfo      = @{ name = 'workwith-persistence-smoke'; version = '1.0.0' }
        }
    } catch {
        Exit-Unavailable "No Gateway answered at $BaseUrl ($($_.Exception.Message)). Start publish\start_mcp.bat or pass -BaseUrl."
    }
    if ([string]::IsNullOrWhiteSpace($script:SessionId)) { Exit-Unavailable 'initialize returned no MCP-Session-Id.' }
    if ($null -eq $init.result) { Exit-Unavailable 'initialize returned no result.' }

    # --- 2. KB ------------------------------------------------------------------------------
    if (-not [string]::IsNullOrWhiteSpace($KbPath)) {
        if ([string]::IsNullOrWhiteSpace($Kb)) { $Kb = (Split-Path -Leaf $KbPath).ToLowerInvariant() }
        Write-Host "[WW-SMOKE] genexus_kb open $KbPath as '$Kb'"
        $open = Invoke-Tool -Name 'genexus_kb' -Arguments @{ action = 'open'; path = $KbPath; alias = $Kb } -Label 'genexus_kb open'
        if ([string]$open.status -eq 'error') { Exit-Unavailable "genexus_kb open failed: $($open.error.code) $($open.error.message)" }
    }
    $script:Summary.evidence.kbAlias = $Kb

    # --- 3. baseline ------------------------------------------------------------------------
    Write-Host "[WW-SMOKE] list_filters baseline ($Transaction)"
    $filterArgs = @{ action = 'list_filters'; name = $Transaction; kb = $Kb }
    if (-not [string]::IsNullOrWhiteSpace($Level)) { $filterArgs['level'] = $Level }
    $baseline = Invoke-Tool -Name 'genexus_workwith' -Arguments $filterArgs -Label 'list_filters baseline'
    if ([string]$baseline.status -eq 'error') {
        $code = [string]$baseline.error.code
        if ($code -in @('ObjectNotFound', 'WorkWithInstanceNotFound', 'WorkWithPatternMismatch', 'WorkWithFilterNotPresent', 'WorkWithSelectionMissing', 'FilterLevelAmbiguous', 'FilterLevelNotFound', 'KB_CONTEXT_REQUIRED', 'KB_AMBIGUOUS')) {
            Exit-Unavailable "list_filters baseline: $code $($baseline.error.message)"
        }
        Exit-Fail "list_filters baseline: $code $($baseline.error.message)"
    }
    $resolvedLevel = [string]$baseline.result.filters.level
    if ([string]::IsNullOrWhiteSpace($Level)) { $Level = $resolvedLevel }
    $script:Summary.evidence.level = $resolvedLevel
    $baselineProjection = ConvertTo-FilterProjection -Filters $baseline.result.filters
    $baselineSource = [string]$baseline.result.observedSource
    $baselineCount = @($baselineProjection.attributes).Count
    $script:Summary.evidence.baseline = [ordered]@{ attributeCount = $baselineCount; conditionCount = @($baselineProjection.conditions).Count; observedSource = $baselineSource }
    # Ownership guard: never write over, and later never remove, something this run did not create.
    if ($null -ne (Find-FilterAttribute -Filters $baseline.result.filters -Name $AttributeName)) {
        Exit-Fail "Filter attribute '$AttributeName' already exists before the write; pick another -Attribute. Nothing was written and nothing will be removed."
    }
    if (@($baselineProjection.conditions) -ccontains $Condition) {
        Exit-Fail "Condition '$Condition' already exists on the filter before the write; pick another -Condition. Nothing was written and nothing will be removed."
    }
    Write-Host "[WW-SMOKE] baseline: level=$resolvedLevel attributes=$baselineCount (source: $baselineSource)"

    # --- 4. write ---------------------------------------------------------------------------
    # Set BEFORE the call: the attempt can persist even if the answer is a failure or unreadable.
    $filterWritten = $true
    $script:DurabilityRunning = $true
    Write-Host "[WW-SMOKE] add_filter_attribute $AttributeName"
    $add = Invoke-Tool -Name 'genexus_workwith' -Arguments @{
        action     = 'add_filter_attribute'
        name       = $Transaction
        level      = $Level
        attribute  = $AttributeName
        conditions = @($Condition)
        kb         = $Kb
    } -Label 'add_filter_attribute'
    Assert-Ok -Payload $add -Label 'add_filter_attribute'
    if (-not [bool]$add.result.verified) { Exit-Fail 'add_filter_attribute receipt is not verified.' }
    $evidenceBlock = $add.result.persistenceEvidence
    if ($null -eq $evidenceBlock) { Exit-Fail 'add_filter_attribute receipt carries no persistenceEvidence (issue #590 contract).' }
    if ([bool]$evidenceBlock.provesDurability) { Exit-Fail 'add_filter_attribute receipt claims provesDurability=true; the receipt must not claim what only this smoke proves.' }
    $script:Summary.evidence.filterReceipt = [ordered]@{
        verified            = [bool]$add.result.verified
        verificationScope   = [string]$add.result.verificationScope
        persistenceEvidence = $evidenceBlock
    }

    $actionLane = -not [string]::IsNullOrWhiteSpace($Procedure)
    if ($actionLane) {
        $actionWritten = $true
        Write-Host "[WW-SMOKE] add_action $actionName -> $Procedure on tab $Tab"
        $addActionArgs = @{
            action     = 'add_action'
            name       = $Transaction
            tab        = $Tab
            actionName = $actionName
            caption    = 'Smoke action'
            procedure  = $Procedure
            kb         = $Kb
        }
        # The tool refuses a call whose parameter count differs from the Procedure's parm()
        # rule (ArityMismatch), so a Procedure with parameters needs matching -ActionParameters.
        if ($ActionParameters.Count -gt 0) { $addActionArgs['parameters'] = @($ActionParameters) }
        $addAction = Invoke-Tool -Name 'genexus_workwith' -Arguments $addActionArgs -Label 'add_action'
        Assert-Ok -Payload $addAction -Label 'add_action'
        if (-not [bool]$addAction.result.verified) { Exit-Fail 'add_action receipt is not verified.' }
        if ($null -eq $addAction.result.persistenceEvidence) { Exit-Fail 'add_action receipt carries no persistenceEvidence (issue #590 contract).' }
        $script:Summary.evidence.actionName = $actionName
    }

    # --- 5. restart + independent reread ----------------------------------------------------
    $restart = Invoke-WorkerRestart -Label 'after write'
    $script:Summary.evidence.restartAfterWrite = $restart

    Write-Host '[WW-SMOKE] list_filters on the new Worker'
    $after = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'list_filters after restart'
    Assert-Ok -Payload $after -Label 'list_filters after restart'
    $found = Find-FilterAttribute -Filters $after.result.filters -Name $AttributeName
    if ($null -eq $found) { Exit-Fail "Filter attribute '$AttributeName' is absent after the Worker restart: the write did not persist." }
    $conditions = @($after.result.filters.conditions | ForEach-Object { [string]$_ })
    if ($conditions -notcontains $Condition) { Exit-Fail "Condition '$Condition' is absent after the Worker restart (observed: $($conditions -join ' | '))." }
    $afterProjection = ConvertTo-FilterProjection -Filters $after.result.filters
    $afterCount = @($afterProjection.attributes).Count
    if ($afterCount -lt ($baselineCount + 1)) { Exit-Fail "Filter attributes were truncated: baseline $baselineCount, after restart $afterCount." }
    $afterSource = [string]$after.result.observedSource
    # Expected: the baseline plus the new attribute and condition at the end (the SDK appends).
    # The new attribute is compared by name only: the SDK decides what else it materializes.
    $expectedAfterWrite = [ordered]@{
        level      = $baselineProjection.level
        attributes = @($baselineProjection.attributes) + @([ordered]@{ name = $AttributeName; description = $null; domain = $null; default = $null; variableName = $null })
        conditions = @($baselineProjection.conditions) + @($Condition)
    }
    $writeComparison = 'equal'
    if ($afterSource -ne $baselineSource) {
        $writeComparison = 'skipped_reader_changed'
        Write-Host "[WW-SMOKE] reader changed between baseline ($baselineSource) and the reread after the restart ($afterSource); values are not compared across readers."
    } else {
        $writeDifferences = @(Get-ProjectionDifferences -Expected $expectedAfterWrite -Actual $afterProjection)
        if ($writeDifferences.Count -gt 0) { Exit-Fail "The filter after the Worker restart differs from the baseline plus the new attribute and condition: $($writeDifferences -join '; ')" }
    }
    $script:Summary.evidence.filterAfterRestart = [ordered]@{
        attributeFound              = $true
        conditionFound              = $true
        attributeCount              = $afterCount
        observedSource              = $afterSource
        projectionVsBaselinePlusNew = $writeComparison
    }
    Write-Host "[WW-SMOKE] filter attribute '$AttributeName' and its condition survived the restart (attributes: $baselineCount -> $afterCount)"

    if ($actionLane) {
        Write-Host '[WW-SMOKE] list_actions on the new Worker'
        $actionsAfter = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_actions'; name = $Transaction; kb = $Kb } -Label 'list_actions after restart'
        Assert-Ok -Payload $actionsAfter -Label 'list_actions after restart'
        if ($null -eq (Find-TabAction -Tabs $actionsAfter.result.tabs -TabCode $Tab -Name $actionName)) {
            Exit-Fail "Action '$actionName' is absent after the Worker restart: the write did not persist."
        }
        Write-Host "[WW-SMOKE] action '$actionName' survived the restart"
    }
    $script:DurabilityRunning = $false
    $script:Summary.durability = 'pass'

    # --- 6. cleanup, success path -------------------------------------------------------------
    # remove_filter_attribute -> remove_action (lane) -> ONE restart, then the reread below.
    Invoke-Cleanup -Restart -IncludeAction
    if ($script:CleanupFailures.Count -gt 0) { Exit-Fail "Cleanup failed: $($script:CleanupFailures -join ' | ')" }

    Write-Host '[WW-SMOKE] list_filters after the cleanup'
    $final = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'list_filters after cleanup'
    Assert-Ok -Payload $final -Label 'list_filters after cleanup'
    $finalProjection = ConvertTo-FilterProjection -Filters $final.result.filters
    $finalSource = [string]$final.result.observedSource
    $attributeAbsent = ($null -eq (Find-FilterAttribute -Filters $final.result.filters -Name $AttributeName))
    $conditionAbsent = (@($finalProjection.conditions) -cnotcontains $Condition)
    $actionAbsent = $true
    if ($actionLane) {
        $actionsFinal = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_actions'; name = $Transaction; kb = $Kb } -Label 'list_actions after cleanup'
        Assert-Ok -Payload $actionsFinal -Label 'list_actions after cleanup'
        $actionAbsent = ($null -eq (Find-TabAction -Tabs $actionsFinal.result.tabs -TabCode $Tab -Name $actionName))
    }
    $script:Summary.evidence.afterCleanup = [ordered]@{
        attributeAbsent = $attributeAbsent
        conditionAbsent = $conditionAbsent
        actionAbsent    = $actionAbsent
        observedSource  = $finalSource
    }
    # Absence is asserted before the full comparison, so a missing cleanup fails here.
    if (-not $attributeAbsent) {
        Add-CleanupFailure "Filter attribute '$AttributeName' is still present after the cleanup."
        Exit-Fail "Filter attribute '$AttributeName' is still present after the cleanup and a Worker restart: the removal did not persist."
    }
    if (-not $conditionAbsent) {
        Add-CleanupFailure "Condition '$Condition' is still present after the cleanup."
        Exit-Fail "Condition '$Condition' is still present after the cleanup and a Worker restart."
    }
    if (-not $actionAbsent) {
        Add-CleanupFailure "Action '$actionName' is still present after the cleanup."
        Exit-Fail "Action '$actionName' is still present after the cleanup and a Worker restart: the removal did not persist."
    }

    if ($finalSource -ne $baselineSource) {
        $script:Summary.evidence.afterCleanup.projectionVsBaseline = 'skipped_reader_changed'
        Add-CleanupUnverified "the reader changed between baseline ($baselineSource) and the reread after the cleanup ($finalSource); values are not compared across readers."
    } else {
        $restoreDifferences = @(Get-ProjectionDifferences -Expected $baselineProjection -Actual $finalProjection)
        if ($restoreDifferences.Count -gt 0) {
            $script:Summary.evidence.afterCleanup.projectionVsBaseline = 'different'
            Add-CleanupFailure "The filter after the cleanup differs from the baseline: $($restoreDifferences -join '; ')"
            Exit-Fail "The filter after the cleanup differs from the baseline: $($restoreDifferences -join '; ')"
        }
        $script:Summary.evidence.afterCleanup.projectionVsBaseline = 'equal'
        $script:CleanupVerified = $true
    }
    Write-Host "[WW-SMOKE] cleanup read back: attribute '$AttributeName' and its condition are absent after a Worker restart"
} catch {
    # Capture the primary error BEFORE the cleanup so a cleanup problem can never replace it.
    $primaryError = $_
}

# --- finalizer: one place, no exit inside a try ---------------------------------------------
$primaryOutcome = $null
if ($null -ne $primaryError) {
    $primaryOutcome = [string]$primaryError.Exception.Data['WwSmokeOutcome']
    if ([string]::IsNullOrWhiteSpace($primaryOutcome)) { $primaryOutcome = 'fail' }
    $primaryMessage = [string]$primaryError.Exception.Message
    # Unavailable means "nothing was written"; after a write attempt it is a failure.
    if ($primaryOutcome -eq 'unavailable' -and $filterWritten) { $primaryOutcome = 'fail' }
    $script:Summary.errors += $primaryMessage
    if ($script:DurabilityRunning) { $script:Summary.durability = 'fail' }
    Write-Host "[WW-SMOKE] $(if ($primaryOutcome -eq 'unavailable') { 'unavailable' } else { 'FAIL' }): $primaryMessage"
}

if ($filterWritten -and -not $cleanupDone) {
    try { Invoke-Cleanup } catch { Add-CleanupFailure "cleanup: $($_.Exception.Message)" }
}

$script:Summary.cleanup = Resolve-CleanupStatus
$script:Summary.evidence.cleanupFailures = @($script:CleanupFailures)
$script:Summary.evidence.cleanupUnverified = @($script:CleanupUnverified)

if ($null -ne $primaryOutcome) {
    $script:Summary.outcome = $primaryOutcome
} elseif ($script:Summary.durability -eq 'pass' -and $script:Summary.cleanup -eq 'pass') {
    $script:Summary.outcome = 'pass'
} else {
    $script:Summary.outcome = 'fail'
    $script:Summary.errors += "durability=$($script:Summary.durability) cleanup=$($script:Summary.cleanup): the smoke only passes when both pass."
}
if ($script:Summary.outcome -eq 'pass') {
    Write-Host "[WW-SMOKE] PASS: the definition survived a Worker restart on '$kbLabel' and the smoke filter attribute '$AttributeName' and its condition were removed again (read back after a Worker restart). Not covered: IDE visibility, derived-object regeneration, pristine KB restoration, cleanup of the optional action lane on failure paths."
} else {
    Write-Host "[WW-SMOKE] outcome=$($script:Summary.outcome) durability=$($script:Summary.durability) cleanup=$($script:Summary.cleanup)"
}

Write-Summary
$exitCode = switch ($script:Summary.outcome) { 'pass' { 0 } 'unavailable' { 2 } default { 1 } }
exit $exitCode
