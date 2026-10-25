$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Issue #645: behavioral guard for the cleanup of scripts/workwith_persistence_smoke.ps1.
# The smoke runs as a child process against an in-process fake MCP Gateway that emulates the
# genexus_workwith filter/action tools (including the remove_filter_attribute refusals), so the
# success path, the failure paths and the ownership guard are exercised without a Knowledge
# Base. It is NOT live evidence: it proves the smoke's own logic, not the SDK.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$smokePath = Join-Path $root 'scripts\workwith_persistence_smoke.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ww-smoke-$([guid]::NewGuid().ToString('N'))")
New-Item -ItemType Directory -Path $tempRoot | Out-Null

$serverScript = {
    param($Prefix, $State)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add($Prefix)
    $listener.Start()
    $State.listener = $listener
    $State.ready = $true

    function New-Payload($Status, $Code, $Result, $ErrorCode, $ErrorMessage) {
        $payload = [ordered]@{ status = $Status }
        if ($Code) { $payload.code = $Code }
        if ($Result) { $payload.result = $Result }
        if ($ErrorCode) { $payload.error = [ordered]@{ code = $ErrorCode; message = $ErrorMessage } }
        return $payload
    }
    function Get-Source {
        if ($State.scenario -eq 'reader_changes' -and $State.reloads -ge 2) { return 'xml_projection' }
        return 'in_memory_instance_reread'
    }
    function Get-FiltersResult {
        $attributes = @($State.attributes | ForEach-Object { [ordered]@{ name = $_.name; description = $_.description; domain = $_.domain; default = $_.default; variableName = "&$($_.name)" } })
        return [ordered]@{ filters = [ordered]@{ level = 'Customer'; attributes = $attributes; conditions = @($State.conditions) }; observedSource = (Get-Source) }
    }
    function Invoke-Fake($Name, $Arguments) {
        if ($Name -eq 'genexus_whoami') { return [ordered]@{ status = 'ok'; worker = [ordered]@{ pid = $State.pid } } }
        if ($Name -eq 'genexus_worker_reload') {
            $State.pid = $State.pid + 1
            $State.reloads = $State.reloads + 1
            $State.calls.Add('reload')
            return [ordered]@{ status = 'Reloaded'; swappedAndReady = $true }
        }
        $action = [string]$Arguments.action
        $State.calls.Add($action)
        switch ($action) {
            'list_filters' {
                if ($State.scenario -eq 'baseline_filter_missing') { return (New-Payload 'error' $null $null 'WorkWithFilterNotPresent' 'no filter node') }
                if ($State.scenario -eq 'reread_fails_once' -and $State.reloads -ge 1 -and -not $State.rereadFailed) {
                    $State.rereadFailed = $true
                    return (New-Payload 'error' $null $null 'Boom' 'reread failed')
                }
                return (New-Payload 'ok' $null (Get-FiltersResult) $null $null)
            }
            'add_filter_attribute' {
                $State.addArguments = $Arguments
                $State.attributes.Add([ordered]@{ name = [string]$Arguments.attribute; description = ''; domain = ''; default = '' })
                foreach ($condition in @($Arguments.conditions)) { $State.conditions.Add([string]$condition) }
                if ($State.scenario -eq 'add_fails_after_persist') { return (New-Payload 'error' $null $null 'WorkWithFilterAuthoringFailed' 'boom after save') }
                return (New-Payload 'ok' 'WorkWithFilterAttributeAdded' ([ordered]@{ verified = $true; verificationScope = 'fake'; persistenceEvidence = [ordered]@{ provesDurability = $false } }) $null $null)
            }
            'remove_filter_attribute' {
                $State.removeArguments.Add($Arguments)
                $name = ([string]$Arguments.attribute).Trim().TrimStart('&')
                $existing = @($State.attributes | Where-Object { $_.name -ieq $name } | Select-Object -First 1)
                if ($existing.Count -eq 0) { return (New-Payload 'error' $null $null 'FilterAttributeNotFound' "no $name") }
                $listed = @($Arguments.PSObject.Properties.Name -contains 'removeConditions' ? @($Arguments.removeConditions) : @())
                foreach ($text in $listed) {
                    if (-not $State.conditions.Contains([string]$text)) { return (New-Payload 'error' $null $null 'FilterConditionNotFound' "no condition $text") }
                }
                $unlisted = @($State.conditions | Where-Object { $listed -cnotcontains $_ -and $_.Contains("&$name") })
                if ($unlisted.Count -gt 0) { return (New-Payload 'error' $null $null 'FilterAttributeReferencedByConditions' "still referenced by: $($unlisted -join ' | ')") }
                if ($State.scenario -ne 'removal_lies') {
                    $State.attributes.Remove($existing[0]) | Out-Null
                    foreach ($text in $listed) { while ($State.conditions.Remove([string]$text)) { } }
                }
                if ($State.scenario -eq 'unverified_receipt') { return (New-Payload 'ok' 'WorkWithFilterAttributeRemovedUnverified' ([ordered]@{ verified = $false }) $null $null) }
                return (New-Payload 'ok' 'WorkWithFilterAttributeRemoved' ([ordered]@{ verified = $true }) $null $null)
            }
            'add_action' {
                $State.actions.Add([string]$Arguments.actionName)
                return (New-Payload 'ok' $null ([ordered]@{ verified = $true; persistenceEvidence = [ordered]@{ provesDurability = $false } }) $null $null)
            }
            'remove_action' {
                if (-not $State.actions.Remove([string]$Arguments.actionName)) { return (New-Payload 'error' $null $null 'ActionNotFound' 'no action') }
                return (New-Payload 'ok' $null ([ordered]@{ verified = $true; persistenceEvidence = [ordered]@{ provesDurability = $false } }) $null $null)
            }
            'list_actions' {
                $listedActions = @($State.actions | ForEach-Object { [ordered]@{ name = $_ } })
                return (New-Payload 'ok' $null ([ordered]@{ tabs = @([ordered]@{ code = 'General'; name = 'General'; actions = $listedActions }) }) $null $null)
            }
        }
        return (New-Payload 'error' $null $null 'UnknownFakeAction' $action)
    }

    while ($true) {
        try { $context = $listener.GetContext() } catch { break }
        try {
            $reader = [System.IO.StreamReader]::new($context.Request.InputStream)
            $request = $reader.ReadToEnd() | ConvertFrom-Json
            $response = [ordered]@{ jsonrpc = '2.0'; id = $request.id }
            if ($request.method -eq 'initialize') {
                $context.Response.Headers.Add('MCP-Session-Id', 'fake-session')
                $response.result = [ordered]@{ protocolVersion = '2025-11-25'; capabilities = [ordered]@{} }
            } else {
                $payload = Invoke-Fake $request.params.name $request.params.arguments
                $response.result = [ordered]@{ content = @([ordered]@{ type = 'text'; text = ($payload | ConvertTo-Json -Depth 10 -Compress) }) }
            }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes(($response | ConvertTo-Json -Depth 12 -Compress))
            $context.Response.ContentType = 'application/json'
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        } catch {
            $State.serverErrors.Add($_.Exception.Message)
            $context.Response.StatusCode = 500
        } finally {
            $context.Response.Close()
        }
    }
}

function Invoke-SmokeScenario {
    param(
        [string]$Scenario,
        [string[]]$SmokeArguments = @(),
        [string]$ScriptPath = $smokePath,
        [string[]]$BaselineConditions = @(),
        [string[]]$BaselineAttributes = @()
    )
    $port = Get-Random -Minimum 20000 -Maximum 60000
    $state = [hashtable]::Synchronized(@{
        scenario = $Scenario; ready = $false; pid = 100; reloads = 0; rereadFailed = $false
        calls = [System.Collections.Generic.List[string]]::new()
        removeArguments = [System.Collections.Generic.List[object]]::new()
        serverErrors = [System.Collections.Generic.List[string]]::new()
        attributes = [System.Collections.Generic.List[object]]::new()
        conditions = [System.Collections.Generic.List[string]]::new()
        actions = [System.Collections.Generic.List[string]]::new()
        addArguments = $null; listener = $null
    })
    foreach ($name in $BaselineAttributes) { $state.attributes.Add([ordered]@{ name = $name; description = "desc $name"; domain = 'Dom'; default = "def $name" }) }
    foreach ($text in $BaselineConditions) { $state.conditions.Add($text) }

    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $shell = [powershell]::Create()
    $shell.Runspace = $runspace
    $null = $shell.AddScript($serverScript).AddArgument("http://127.0.0.1:$port/").AddArgument($state)
    $handle = $shell.BeginInvoke()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not $state.ready -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        if (-not $state.ready) { throw "fake gateway did not start: $($shell.Streams.Error | Out-String)" }
        $summaryPath = Join-Path $tempRoot "$Scenario-$port.json"
        $output = & pwsh -NoProfile -File $ScriptPath -BaseUrl "http://127.0.0.1:$port/mcp" -Kb kbteste -Transaction Customer -SummaryPath $summaryPath -CleanupTimeoutSec 30 @SmokeArguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        $summary = if (Test-Path -LiteralPath $summaryPath) { Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{ Exit = $exitCode; Summary = $summary; State = $state; Output = $output }
    } finally {
        if ($null -ne $state.listener) { $state.listener.Stop() }
        $shell.Dispose()
        $runspace.Dispose()
    }
}

function Assert-True([bool]$Condition, [string]$Message, $Run = $null) {
    if (-not $Condition) {
        $detail = if ($null -ne $Run) { "`n--- smoke output ---`n$($Run.Output)`n--- server errors: $($Run.State.serverErrors -join '; ')" } else { '' }
        throw "$Message$detail"
    }
}

try {
    # 1. Success path: removal with exactly the sent condition, ONE restart after it, outcome pass.
    $run = Invoke-SmokeScenario -Scenario 'success' -BaselineAttributes @('Existing') -BaselineConditions @('Existing.IsEmpty();')
    Assert-True ($run.Exit -eq 0) "success: expected exit 0, got $($run.Exit)" $run
    Assert-True ($run.Summary.outcome -eq 'pass' -and $run.Summary.durability -eq 'pass' -and $run.Summary.cleanup -eq 'pass') "success: expected pass/pass/pass, got $($run.Summary.outcome)/$($run.Summary.durability)/$($run.Summary.cleanup)" $run
    Assert-True ($run.State.attributes.Count -eq 1 -and $run.State.attributes[0].name -eq 'Existing' -and $run.State.conditions.Count -eq 1) 'success: the KB must end equal to the baseline' $run
    Assert-True ($run.State.removeArguments.Count -eq 1) 'success: exactly one remove_filter_attribute call' $run
    $sentCondition = @($run.State.removeArguments[0].removeConditions)
    Assert-True ($sentCondition.Count -eq 1 -and $sentCondition[0] -eq $run.Summary.evidence.condition) 'success: removeConditions must be exactly the condition this run sent' $run
    Assert-True ($run.State.reloads -eq 2) "success: expected 2 restarts (after write, after cleanup), got $($run.State.reloads)" $run
    $order = $run.State.calls -join ','
    Assert-True ($order -match 'add_filter_attribute,reload,list_filters,list_filters,remove_filter_attribute,reload,list_filters$') "success: unexpected call order: $order" $run
    Write-Host 'fake-gateway: success path removes what it created with one extra restart'

    # 2. Action lane: remove_filter_attribute -> remove_action -> ONE restart, both absent.
    $run = Invoke-SmokeScenario -Scenario 'action_lane' -SmokeArguments @('-Procedure', 'procPrint', '-ActionParameters', 'CustomerId')
    Assert-True ($run.Exit -eq 0 -and $run.Summary.cleanup -eq 'pass') "action_lane: expected pass, got exit $($run.Exit) cleanup $($run.Summary.cleanup)" $run
    Assert-True ($run.State.reloads -eq 2 -and $run.State.actions.Count -eq 0) 'action_lane: two restarts in total and no action left' $run
    Assert-True (($run.State.calls -join ',') -match 'remove_filter_attribute,remove_action,reload') "action_lane: removal order: $($run.State.calls -join ',')" $run

    # 3. Name normalization: ' &Name ' is normalized once and used by the write and the cleanup.
    $run = Invoke-SmokeScenario -Scenario 'normalize' -SmokeArguments @('-Attribute', ' &SmokeFilterAmp ')
    Assert-True ($run.Exit -eq 0) "normalize: expected exit 0, got $($run.Exit)" $run
    Assert-True ([string]$run.State.addArguments.attribute -ceq 'SmokeFilterAmp') "normalize: add received '$($run.State.addArguments.attribute)'" $run
    Assert-True ([string]$run.State.removeArguments[0].attribute -ceq 'SmokeFilterAmp') 'normalize: cleanup must use the normalized name' $run

    # 4. Ownership guard: an attribute that already exists stops the run before any write.
    $run = Invoke-SmokeScenario -Scenario 'preexisting_attribute' -SmokeArguments @('-Attribute', 'Mine') -BaselineAttributes @('mine')
    Assert-True ($run.Exit -eq 1 -and $run.Summary.outcome -eq 'fail') "preexisting_attribute: expected exit 1/fail, got $($run.Exit)" $run
    Assert-True (-not ($run.State.calls -contains 'add_filter_attribute') -and -not ($run.State.calls -contains 'remove_filter_attribute')) 'preexisting_attribute: nothing may be written or removed' $run
    Assert-True ($run.Summary.cleanup -eq 'not_attempted' -and $run.State.attributes.Count -eq 1) 'preexisting_attribute: cleanup must be not_attempted and the attribute kept' $run

    # 5. Failure path: the add persists but answers with an error. The finalizer removes it once,
    # without an extra restart, preserves the primary error and cannot claim cleanup=pass.
    $run = Invoke-SmokeScenario -Scenario 'add_fails_after_persist'
    Assert-True ($run.Exit -eq 1 -and $run.Summary.outcome -eq 'fail' -and $run.Summary.durability -eq 'fail') "add_fails_after_persist: expected fail/fail, got $($run.Exit) $($run.Summary.durability)" $run
    Assert-True ($run.State.attributes.Count -eq 0 -and $run.State.conditions.Count -eq 0) 'add_fails_after_persist: the persisted attribute and condition must be removed' $run
    Assert-True ($run.State.reloads -eq 0) 'add_fails_after_persist: the failure path must not restart the Worker' $run
    Assert-True ($run.Summary.cleanup -eq 'unverified') "add_fails_after_persist: expected cleanup=unverified, got $($run.Summary.cleanup)" $run
    Assert-True (@($run.Summary.errors)[0] -match 'WorkWithFilterAuthoringFailed') 'add_fails_after_persist: the primary error must stay first' $run
    Assert-True (([regex]::Matches($run.Output, '"outcome"')).Count -eq 1) 'add_fails_after_persist: the summary must be written once' $run

    # 6. Failure after the restart: the reread fails once; the primary error is preserved and the filter is removed.
    $run = Invoke-SmokeScenario -Scenario 'reread_fails_once'
    Assert-True ($run.Exit -eq 1 -and $run.Summary.durability -eq 'fail') "reread_fails_once: expected exit 1 and durability=fail, got $($run.Exit)/$($run.Summary.durability)" $run
    Assert-True (@($run.Summary.errors)[0] -match 'list_filters after restart') 'reread_fails_once: the primary error must be the failing reread' $run
    Assert-True ($run.State.attributes.Count -eq 0 -and $run.State.reloads -eq 1) 'reread_fails_once: removal without an extra restart' $run

    # 7. A pre-existing condition that references the variable is never removed: the tool refuses
    # and that is a cleanup failure, not something to work around.
    $run = Invoke-SmokeScenario -Scenario 'unlisted_reference' -SmokeArguments @('-Attribute', 'SmokeFixed') -BaselineConditions @('x = &SmokeFixed;')
    Assert-True ($run.Exit -eq 1 -and $run.Summary.cleanup -eq 'fail') "unlisted_reference: expected exit 1 and cleanup=fail, got $($run.Exit)/$($run.Summary.cleanup)" $run
    Assert-True ($run.State.conditions.Contains('x = &SmokeFixed;')) 'unlisted_reference: the pre-existing condition must be kept' $run
    Assert-True (($run.Summary.evidence.cleanupFailures -join ' ') -match 'FilterAttributeReferencedByConditions') 'unlisted_reference: the refusal code must be reported' $run

    # 8. A removal receipt that is not verified is cleanup=unverified, never pass.
    $run = Invoke-SmokeScenario -Scenario 'unverified_receipt'
    Assert-True ($run.Exit -eq 1 -and $run.Summary.cleanup -eq 'unverified' -and $run.Summary.durability -eq 'pass') "unverified_receipt: expected durability=pass cleanup=unverified exit 1, got $($run.Summary.durability)/$($run.Summary.cleanup)/$($run.Exit)" $run

    # 9. A removal that did not happen is caught by the absence assertion.
    $run = Invoke-SmokeScenario -Scenario 'removal_lies'
    Assert-True ($run.Exit -eq 1 -and $run.Summary.cleanup -eq 'fail' -and $run.Summary.durability -eq 'pass') "removal_lies: expected durability=pass cleanup=fail exit 1, got $($run.Summary.durability)/$($run.Summary.cleanup)/$($run.Exit)" $run
    Assert-True (@($run.Summary.errors)[0] -match 'still present after the cleanup') 'removal_lies: the absence assertion must be the primary error' $run

    # 10. The reader changed between the readings: values are not compared across readers.
    $run = Invoke-SmokeScenario -Scenario 'reader_changes'
    Assert-True ($run.Exit -eq 1 -and $run.Summary.cleanup -eq 'unverified') "reader_changes: expected cleanup=unverified exit 1, got $($run.Summary.cleanup)/$($run.Exit)" $run
    Assert-True ($run.Summary.evidence.afterCleanup.projectionVsBaseline -eq 'skipped_reader_changed') 'reader_changes: the comparison must be recorded as skipped' $run

    # 11. A precondition missing before any write is unavailable (exit 2) and writes nothing.
    $run = Invoke-SmokeScenario -Scenario 'baseline_filter_missing'
    Assert-True ($run.Exit -eq 2 -and $run.Summary.outcome -eq 'unavailable' -and $run.Summary.cleanup -eq 'not_attempted') "baseline_filter_missing: expected exit 2/unavailable, got $($run.Exit)" $run
    Assert-True (-not ($run.State.calls -contains 'add_filter_attribute')) 'baseline_filter_missing: nothing may be written' $run

    # 12. Mutation check: without the success-path cleanup call the smoke must fail on the
    # absence assertion (the finalizer then removes the residue, as on any failure path).
    $mutated = (Get-Content -LiteralPath $smokePath -Raw).Replace('Invoke-Cleanup -Restart -IncludeAction', '# mutation: cleanup call removed')
    if ($mutated -eq (Get-Content -LiteralPath $smokePath -Raw)) { throw 'mutation: the success-path cleanup call was not found in the smoke.' }
    $mutatedPath = Join-Path $tempRoot 'workwith_persistence_smoke.mutated.ps1'
    Set-Content -LiteralPath $mutatedPath -Value $mutated -Encoding utf8
    $run = Invoke-SmokeScenario -Scenario 'success' -ScriptPath $mutatedPath
    Assert-True ($run.Exit -eq 1) "mutation: removing the cleanup call must fail the smoke, got exit $($run.Exit)" $run
    Assert-True (@($run.Summary.errors)[0] -match 'still present after the cleanup') "mutation: it must fail on the absence assertion, got: $(@($run.Summary.errors)[0])" $run
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'workwith-persistence-smoke: fake-gateway scenarios (success, action lane, normalization, ownership guard, failure paths, refusals, unverified, reader change, unavailable, mutation)'
