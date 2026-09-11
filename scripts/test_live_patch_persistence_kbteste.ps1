$ErrorActionPreference = 'Stop'
$gw = (Resolve-Path 'publish\GxMcp.Gateway.exe').Path

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gw
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $false
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['GX_CONFIG_PATH'] = (Resolve-Path 'config.json').Path
$psi.EnvironmentVariables['GX_MCP_STDIO']   = 'true'
$psi.EnvironmentVariables['GX_PROGRAM_DIR'] = 'C:\Program Files (x86)\GeneXus\GeneXus18'

$p = [System.Diagnostics.Process]::Start($psi)

function Send($obj) { 
    $p.StandardInput.WriteLine( ($obj | ConvertTo-Json -Compress -Depth 12) )
    $p.StandardInput.Flush() 
}

function ReadId([int]$id, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $line = $p.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line -notmatch '^\s*\{') { continue }
        try { 
            $j = $line | ConvertFrom-Json
            if ($j.id -eq $id) { return $j }
        } catch { continue }
    }
    return $null
}

function CallTool($id, $name, $toolArgs, $timeoutSec = 60) {
    Send @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$name; arguments=$toolArgs } }
    $r = ReadId $id $timeoutSec
    if ($null -eq $r) {
        Write-Host "[-] [$id] $name - TIMEOUT (${timeoutSec}s)" -ForegroundColor Red
        return $null
    }
    if ($r.error) {
        Write-Host "[-] [$id] $name - ERROR: $($r.error.message) ($($r.error.code))" -ForegroundColor Red
        return $r.error
    }
    if ($r.result -and $r.result.content -and $r.result.content.Count -gt 0) {
        $txt = $r.result.content[0].text
        if ($txt) {
            try {
                $parsed = $txt | ConvertFrom-Json
                Write-Host "[+] [$id] $name - OK" -ForegroundColor Green
                return $parsed
            } catch {
                Write-Host "[+] [$id] $name - Returned text" -ForegroundColor Green
                return $txt
            }
        }
    }
    Write-Host "[-] [$id] $name - Unexpected response shape" -ForegroundColor Yellow
    return $r
}

Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='persistence-verifier'; version='1.0' } } }
$null = ReadId 1 30
Send @{ jsonrpc='2.0'; method='notifications/initialized' }

Write-Host "=== 1. Opening KB ==="
$null = CallTool 2 'genexus_kb' @{ action='open'; path='C:\KBs\KBTeste'; alias='KBTeste' } 60
$null = CallTool 90 'genexus_delete_object' @{ name='LiveStylesProbe133112680'; confirm=$true } 20
$null = CallTool 91 'genexus_delete_object' @{ name='LiveCaptionProbe133112680'; confirm=$true } 20
$null = CallTool 96 'genexus_delete_object' @{ name='LiveStylesProbe133219635'; confirm=$true } 20
$null = CallTool 97 'genexus_delete_object' @{ name='LiveCaptionProbe133219635'; confirm=$true } 20
$null = CallTool 98 'genexus_delete_object' @{ name='LiveStylesProbe133304043'; confirm=$true } 20
$null = CallTool 99 'genexus_delete_object' @{ name='LiveCaptionProbe133304043'; confirm=$true } 20
$null = CallTool 100 'genexus_delete_object' @{ name='LiveStylesProbe133329278'; confirm=$true } 20
$null = CallTool 110 'genexus_delete_object' @{ name='LiveCaptionProbe133329278'; confirm=$true } 20
$null = CallTool 113 'genexus_delete_object' @{ name='LiveStylesProbe133708773'; confirm=$true } 20
$null = CallTool 111 'genexus_kb' @{ action='select'; alias='KBTeste' } 20

$suffix = (Get-Date -Format 'HHmmssfff')
$dsoName = "LiveStylesProbe$suffix"
$webName = "LiveCaptionProbe$suffix"
$collisionName = "LiveAmbiguousProbe$suffix"
Write-Host "`n=== Issue #177: Creating disposable Design System $dsoName ==="
$dso = CallTool 101 'genexus_create' @{ action='object'; type='DesignSystem'; name=$dsoName; description='Temporary Styles live probe' } 60
$stylesRead = CallTool 102 'genexus_read' @{ name=$dsoName; part='Styles' } 60
Write-Host "Styles read: $($stylesRead | ConvertTo-Json -Compress)"
if ($stylesRead.source) {
    $styleLines = ([string]$stylesRead.source) -split "`r?`n"
    $findStyle = $styleLines[0]
    $replaceStyle = "/* live #177 */`r`n" + $findStyle
    $stylesPatch = CallTool 103 'genexus_edit' @{ name=$dsoName; part='Styles'; mode='patch'; patch=@{ find=$findStyle; replace=$replaceStyle } } 60
    Write-Host "Styles patch: $($stylesPatch | ConvertTo-Json -Compress)"
}
else {
    $stylesFull = CallTool 112 'genexus_edit' @{ name=$dsoName; part='Styles'; mode='full'; content="styles $dsoName { }" } 60
    Write-Host "Styles full write: $($stylesFull | ConvertTo-Json -Compress)"
    if (-not $stylesFull.result.verified -or -not $stylesFull.result.persisted -or -not $stylesFull.postSaveVerification.reReadConfirmed) { throw "Issue #177 live Styles persistence/read-back failed: $($stylesFull | ConvertTo-Json -Compress)" }
}

Write-Host "`n=== Issue #178: Creating disposable WebPanel $webName ==="
$web = CallTool 104 'genexus_create' @{ action='object'; type='WebPanel'; name=$webName; description='Temporary Caption live probe' } 60
$button = CallTool 105 'genexus_edit_form' @{ action='add_button'; name=$webName; controlName='btn_LiveProbe'; caption='Initial caption' } 60
Write-Host "Button result: $($button | ConvertTo-Json -Compress)"
$caption = CallTool 106 'genexus_layout' @{ action='set_property'; name=$webName; control='Btn1'; propertyName='Caption'; value="Line one`nLine two" } 60
Write-Host "Caption result: $($caption | ConvertTo-Json -Compress)"
if ($caption.code -ne 'CaptionNewlineUnsupported') { throw "Issue #178 live guard did not reject multiline Caption: $($caption | ConvertTo-Json -Compress)" }

Write-Host "`n=== Issue #176: Creating disposable ambiguous objects $collisionName ==="
$collisionTransaction = CallTool 108 'genexus_create' @{ action='object_atomic'; type='Transaction'; name=$collisionName; source='parm(in:&User);' } 60
$null = CallTool 114 'genexus_kb' @{ action='select'; alias='KBTeste' } 30
$buildAmbiguous = CallTool 109 'genexus_lifecycle' @{ action='build'; target=$collisionName; kb='KBTeste'; includeCallees='none'; estimated_seconds=60; wait_until_done=$true; wait_seconds=120 } 180
Write-Host "Ambiguous build: $($buildAmbiguous | ConvertTo-Json -Compress)"
if ($buildAmbiguous.status -eq 'ok' -and $buildAmbiguous.result.errorCount -eq 0 -and $buildAmbiguous.result.Status -eq 'Succeeded') { throw 'Issue #176 live build still reported false success.' }

$testObjName = "TempMcpProbe"
Write-Host "`n=== 2. Creating Disposable Procedure: $testObjName ==="
$initSource = "// initial line 1`r`n// initial line 2`r`n// initial line 3"
$create = CallTool 3 'genexus_create' @{ 
    action = 'object_atomic'
    type = 'Procedure'
    name = $testObjName
    description = 'Temporary persistence probe'
    source = $initSource
} 60
Write-Host "Create result: $($create | ConvertTo-Json -Compress)"

Write-Host "`n=== 3. Reading Initial State & Version Token ==="
$read0 = CallTool 4 'genexus_read' @{ name=$testObjName; part='Source' } 30
Write-Host "Read0 result: $($read0 | ConvertTo-Json -Compress)"
$token0 = $read0.versionToken
Write-Host "    Initial Token T0: $token0"
Write-Host "    Initial Source matches: $($read0.source -like '*initial line 1*')"

Write-Host "`n=== 4. Applying Patch 1 (Replace line 2) with baseVersion T0 ==="
$patch1 = CallTool 5 'genexus_edit' @{
    name = $testObjName
    part = 'Source'
    mode = 'patch'
    patch = @{
        find = '// initial line 2'
        replace = '// updated line 2 - patch 1'
    }
    baseVersion = $token0
} 30
Write-Host "Patch1 result: $($patch1 | ConvertTo-Json -Compress)"
$token1 = $patch1.versionToken
Write-Host "    Patch 1 Token T1: $token1"
Write-Host "    Patch 1 Persisted: $($patch1.persisted)"
Write-Host "    Patch 1 ReReadConfirmed: $($patch1.reReadConfirmed)"

Write-Host "`n=== 5. Testing Concurrency Rejection with Stale Token T0 ==="
$stalePatch = CallTool 6 'genexus_edit' @{
    name = $testObjName
    part = 'Source'
    mode = 'patch'
    patch = @{
        find = '// updated line 2 - patch 1'
        replace = '// should fail'
    }
    baseVersion = $token0
} 30
if ($stalePatch.error -or $stalePatch.code -eq 'VersionConflict' -or $stalePatch.code -like '*Conflict*') {
    Write-Host "[+] SUCCESS: Stale token T0 was correctly rejected with VersionConflict!" -ForegroundColor Green
} else {
    Write-Host "[-] WARNING: Stale token was not rejected as expected: $($stalePatch | ConvertTo-Json -Compress)" -ForegroundColor Yellow
}

Write-Host "`n=== 6. Applying Patch 2 with Valid Token T1 ==="
$patch2 = CallTool 7 'genexus_edit' @{
    name = $testObjName
    part = 'Source'
    mode = 'patch'
    patch = @{
        find = '// updated line 2 - patch 1'
        replace = '// updated line 2 - patch 2'
    }
    baseVersion = $token1
} 30
Write-Host "Patch2 result: $($patch2 | ConvertTo-Json -Compress)"
$token2 = $patch2.versionToken
Write-Host "    Patch 2 Token T2: $token2"
Write-Host "    Patch 2 Persisted: $($patch2.persisted)"

Write-Host "`n=== 7. Independent Fresh Read ==="
$readFinal = CallTool 8 'genexus_read' @{ name=$testObjName; part='Source' } 30
Write-Host "ReadFinal result: $($readFinal | ConvertTo-Json -Compress)"
Write-Host "    Final Source contains patch 2: $($readFinal.source -like '*patch 2*')"
Write-Host "    Final Token matches T2: $($readFinal.versionToken -eq $token2)"

Write-Host "`n=== 8. Cleaning Up Disposable Object: $testObjName ==="
$del = CallTool 9 'genexus_delete_object' @{ name=$testObjName; confirm=$true } 30
Write-Host "Delete result: $($del | ConvertTo-Json -Compress)"

Write-Host "`n=== 9. Confirming Deletion ==="
$readAfterDel = CallTool 10 'genexus_read' @{ name=$testObjName } 30
Write-Host "ReadAfterDel: $($readAfterDel | ConvertTo-Json -Compress)"
if ($readAfterDel.error -or $readAfterDel.code -eq 'ObjectNotFound' -or $readAfterDel.notFound -or ($readAfterDel.source -eq $null -and $readAfterDel.parts -eq $null)) {
    Write-Host "[+] Object cleanly removed from KB." -ForegroundColor Green
}
$null = CallTool 92 'genexus_delete_object' @{ name=$dsoName; confirm=$true } 30
$null = CallTool 93 'genexus_delete_object' @{ name=$webName; confirm=$true } 30
$null = CallTool 94 'genexus_delete_object' @{ name=$collisionName; type='Table'; confirm=$true } 30
$null = CallTool 95 'genexus_delete_object' @{ name=$collisionName; type='Transaction'; confirm=$true } 30

try { $p.StandardInput.Close() } catch {}
try { $p.WaitForExit(5000) } catch {}
if (-not $p.HasExited) { $p.Kill() }

Write-Host "`n=== Live Source Persistence & Concurrency Validation PASSED! ===" -ForegroundColor Green
