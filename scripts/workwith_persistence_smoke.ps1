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
written instance in memory.

WHAT THIS SMOKE DEMONSTRATES
- In the tested scenario the written definition survives a Worker restart: the
  reread runs on a new Worker process (the whoami pid changes, which the smoke
  asserts), so the result cannot come from the in-memory instance the receipt
  verified against.
- The receipt contract of this build: every write receipt carries
  persistenceEvidence with provesDurability=false.

WHAT THIS SMOKE DOES NOT COVER
- The GeneXus IDE: it does not prove the IDE displays the attribute, nor that the
  derived objects regenerate (receipts declare derivedObjectRegeneration).
- Concurrent writers, or a KB that the IDE has open at the same time.
- Other Knowledge Bases or GeneXus majors than the one it ran against.
- Cleanup: genexus_workwith has no remove_filter_attribute, so the smoke attribute
  and its condition stay in the KB (the optional action is removed again). Run it
  only against a scratch KB.

PREREQUISITES
- PowerShell 7+.
- A Gateway serving Streamable HTTP (default http://127.0.0.1:5000/mcp), for
  example publish\start_mcp.bat or the Gateway your MCP client started.
- A disposable KB (directory name or alias containing "teste", or pass
  -AllowKbMutation) with the standard WorkWith pattern applied to -Transaction and
  a `filter` node on the Selection of the target Level. The filter node is created
  in the GeneXus IDE; without it the smoke exits 2 (WorkWithFilterNotPresent).
- For -ReloadMode hard, -WorkerSourceDir must point at a built Worker output
  directory (for example src\GxMcp.Worker\bin\Debug); mode=soft restarts the
  Worker with the binaries it already runs. Both modes replace the process.

EXIT CODES
0 = pass, 1 = fail (durability or receipt contract broken), 2 = unavailable
(a precondition is missing; nothing about durability was decided).

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
    [string]$SummaryPath = ''
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host '[WW-SMOKE] unavailable: PowerShell 7+ required. Run with pwsh.'
    exit 2
}

$script:Summary = [ordered]@{
    smoke        = 'workwith_persistence_smoke'
    issue        = 590
    demonstrates = 'The authored WorkWith definition is reread, unchanged, by a new Worker process after genexus_worker_reload.'
    doesNotCover = @('GeneXus IDE visibility', 'derived-object regeneration', 'concurrent writers', 'other KBs or GeneXus majors')
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    outcome      = 'unavailable'
    evidence     = [ordered]@{}
    errors       = @()
}

function Write-Summary {
    $script:Summary.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $json = $script:Summary | ConvertTo-Json -Depth 10
    if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
        Set-Content -LiteralPath $SummaryPath -Value $json -Encoding utf8
        Write-Host "[WW-SMOKE] summary written to $SummaryPath"
    }
    Write-Host $json
}

function Exit-Unavailable([string]$Reason) {
    $script:Summary.outcome = 'unavailable'
    $script:Summary.errors += $Reason
    Write-Host "[WW-SMOKE] unavailable: $Reason"
    Write-Summary
    exit 2
}

function Exit-Fail([string]$Reason) {
    $script:Summary.outcome = 'fail'
    $script:Summary.errors += $Reason
    Write-Host "[WW-SMOKE] FAIL: $Reason"
    Write-Summary
    exit 1
}

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
    # Fail closed: the smoke leaves a filter attribute behind and restarts the Worker.
    Exit-Unavailable "Refusing to mutate '$kbLabel': the KB name does not contain 'teste'. Pass -AllowKbMutation only for a disposable KB."
}

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
if ([string]::IsNullOrWhiteSpace($Attribute)) { $Attribute = "SmokeFilter$stamp" }
if ([string]::IsNullOrWhiteSpace($Condition)) { $Condition = "&$Attribute.IsEmpty();" }
$actionName = "SmokeAction$stamp"
$script:Summary.evidence.kb = $kbLabel
$script:Summary.evidence.transaction = $Transaction
$script:Summary.evidence.attribute = $Attribute
$script:Summary.evidence.condition = $Condition
$script:Summary.evidence.reloadMode = $ReloadMode

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
    $resp = Invoke-WebRequest -Uri $BaseUrl -Method Post -Headers $headers -Body $body -UseBasicParsing
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

function Assert-Ok($Payload, [string]$Label) {
    if ($null -eq $Payload) { Exit-Fail "${Label}: no payload." }
    $status = [string]$Payload.status
    if ($status -eq 'ok' -or $status -eq 'partial' -or $status -eq 'Reloaded') { return }
    $code = if ($Payload.error) { [string]$Payload.error.code } else { [string]$Payload.code }
    $message = if ($Payload.error) { [string]$Payload.error.message } else { [string]$Payload.detail }
    Exit-Fail "${Label}: status=$status code=$code $message"
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

# --- 1. session ---------------------------------------------------------------------
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

# --- 2. KB ----------------------------------------------------------------------------
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
$baselineCount = @($baseline.result.filters.attributes).Count
if ($null -ne (Find-FilterAttribute -Filters $baseline.result.filters -Name $Attribute)) {
    Exit-Fail "Filter attribute '$Attribute' already exists before the write; pick another -Attribute."
}
Write-Host "[WW-SMOKE] baseline: level=$resolvedLevel attributes=$baselineCount"

# --- 4. write ---------------------------------------------------------------------------
Write-Host "[WW-SMOKE] add_filter_attribute $Attribute"
$add = Invoke-Tool -Name 'genexus_workwith' -Arguments @{
    action     = 'add_filter_attribute'
    name       = $Transaction
    level      = $Level
    attribute  = $Attribute
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
$found = Find-FilterAttribute -Filters $after.result.filters -Name $Attribute
if ($null -eq $found) { Exit-Fail "Filter attribute '$Attribute' is absent after the Worker restart: the write did not persist." }
$conditions = @($after.result.filters.conditions | ForEach-Object { [string]$_ })
if ($conditions -notcontains $Condition) { Exit-Fail "Condition '$Condition' is absent after the Worker restart (observed: $($conditions -join ' | '))." }
$afterCount = @($after.result.filters.attributes).Count
if ($afterCount -lt ($baselineCount + 1)) { Exit-Fail "Filter attributes were truncated: baseline $baselineCount, after restart $afterCount." }
$script:Summary.evidence.filterAfterRestart = [ordered]@{
    attributeFound  = $true
    conditionFound  = $true
    attributeCount  = $afterCount
    observedSource  = [string]$after.result.observedSource
}
Write-Host "[WW-SMOKE] filter attribute '$Attribute' and its condition survived the restart (attributes: $baselineCount -> $afterCount)"

if ($actionLane) {
    Write-Host '[WW-SMOKE] list_actions on the new Worker'
    $actionsAfter = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_actions'; name = $Transaction; kb = $Kb } -Label 'list_actions after restart'
    Assert-Ok -Payload $actionsAfter -Label 'list_actions after restart'
    if ($null -eq (Find-TabAction -Tabs $actionsAfter.result.tabs -TabCode $Tab -Name $actionName)) {
        Exit-Fail "Action '$actionName' is absent after the Worker restart: the write did not persist."
    }
    Write-Host "[WW-SMOKE] action '$actionName' survived the restart"

    Write-Host "[WW-SMOKE] remove_action $actionName"
    $remove = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'remove_action'; name = $Transaction; tab = $Tab; actionName = $actionName; kb = $Kb } -Label 'remove_action'
    Assert-Ok -Payload $remove -Label 'remove_action'
    if ($null -eq $remove.result.persistenceEvidence) { Exit-Fail 'remove_action receipt carries no persistenceEvidence (issue #590 contract).' }

    $restart2 = Invoke-WorkerRestart -Label 'after remove'
    $script:Summary.evidence.restartAfterRemove = $restart2
    $actionsFinal = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_actions'; name = $Transaction; kb = $Kb } -Label 'list_actions after second restart'
    Assert-Ok -Payload $actionsFinal -Label 'list_actions after second restart'
    if ($null -ne (Find-TabAction -Tabs $actionsFinal.result.tabs -TabCode $Tab -Name $actionName)) {
        Exit-Fail "Action '$actionName' is still present after remove_action and a Worker restart: the removal did not persist."
    }
    $script:Summary.evidence.actionAfterRestart = [ordered]@{ addedSurvived = $true; removalSurvived = $true }
    Write-Host "[WW-SMOKE] removal of '$actionName' survived the restart"
}

$script:Summary.outcome = 'pass'
Write-Host "[WW-SMOKE] PASS: the definition survived a Worker restart on '$kbLabel'. Not covered: IDE visibility, derived-object regeneration. The smoke filter attribute '$Attribute' remains in the KB."
Write-Summary
exit 0
