$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../ci/sdk-validation.ps1'
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count) { throw $errors[0] }
$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($required in @(
    'GXMCP_SDK_CI_LICENSE_ACK',
    'validate-gx-sdk.ps1',
    'test-live.ps1',
    "Write-Status 'skip'",
    "Write-Status 'pass'",
    "Write-Status 'fail'",
    'finally',
    'Remove-Item -LiteralPath $runDirectory'
)) {
    if ($source -notmatch [regex]::Escape($required)) { throw "Missing SDK lane contract: $required" }
}

$outputRoot = Join-Path $env:TEMP ('gxmcp-sdk-contract-' + [guid]::NewGuid().ToString('N'))
$oldAck = $env:GXMCP_SDK_CI_LICENSE_ACK
$env:GXMCP_SDK_CI_LICENSE_ACK = $null
try {
    & pwsh -NoProfile -File $scriptPath -OutputRoot $outputRoot
    if ($LASTEXITCODE -ne 0) { throw "Missing license acknowledgement must skip, got exit code $LASTEXITCODE." }
    $statusPath = Join-Path $outputRoot 'status.json'
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw 'Skip status artifact was not written.' }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.state -ne 'skip') { throw "Expected skip state, got $($status.state)." }

    $sdk = Join-Path $outputRoot 'fake-sdk'
    $kb = Join-Path $outputRoot 'fake-kb'
    $manifest = Join-Path $outputRoot 'fake-fixture.json'
    New-Item -ItemType Directory -Path $sdk, $kb -Force | Out-Null
    '{}' | Set-Content -LiteralPath $manifest -Encoding utf8
    $env:GXMCP_SDK_CI_LICENSE_ACK = '1'
    & pwsh -NoProfile -File $scriptPath -GxPath $sdk -KbPath $kb -FixtureManifest $manifest -OutputRoot $outputRoot
    if ($LASTEXITCODE -ne 1) { throw "Validator failure must fail, got exit code $LASTEXITCODE." }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.state -ne 'fail') { throw "Expected fail state, got $($status.state)." }
    if (@(Get-ChildItem -LiteralPath $outputRoot -Filter 'run-*' -Directory).Count -ne 0) { throw 'Run directory was not cleaned after failure.' }
}
finally {
    $env:GXMCP_SDK_CI_LICENSE_ACK = $oldAck
    Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'PASS: SDK lane syntax, explicit pass/skip/fail states, preconditions, status artifact, and cleanup contract.'
