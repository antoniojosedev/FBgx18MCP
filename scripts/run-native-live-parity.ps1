#requires -Version 7.0
<#
.SYNOPSIS
  Runs the packaged native read-only routing live tests under every Engine.Backend
  policy (sdk, auto, gxengine) against a verified disposable KB clone.

.DESCRIPTION
  Touches a Knowledge Base through scripts/test-live.ps1, so it is not a named
  check in .pi/checks.json. Refuses to start unless:
    - the KB state file reports state "isolated-copy-verified";
    - knowledgebase.connection still matches the recorded SHA256;
    - the KB path is under C:\KBs and its directory name contains "teste";
    - no log or read-oracle directory from a prior run with the same suffix exists.
  The gxengine policy runs with -RequireEngineServed, so a silent SDK fallback fails.
  SDK oracle inputs (inspect, versions, environment, procedure list) must already
  exist under -InspectOracleDir / -SdkOracleDir.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$Suffix = "1",
    [string]$KbStateFile = "C:/tmp/gxe-timestamp-controlled-current.json",
    [Parameter(Mandatory = $true)][string]$InspectOracleDir,
    [Parameter(Mandatory = $true)][string]$SdkOracleDir,
    [string]$GxPath = "C:/Program Files (x86)/GeneXus/GeneXus18"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$state = Get-Content -LiteralPath $KbStateFile -Raw | ConvertFrom-Json
if ($state.state -ne "isolated-copy-verified") { throw "Clone isolation is not verified" }
$connectionHash = (Get-FileHash (Join-Path $state.destination "knowledgebase.connection") -Algorithm SHA256).Hash
if ($connectionHash -ne $state.cloneConnectionSha256) { throw "Clone identity changed" }

$kbPath = [System.IO.Path]::GetFullPath($state.destination).TrimEnd('\', '/')
$kbRoot = [System.IO.Path]::GetFullPath("C:\KBs").TrimEnd('\', '/')
$kbParent = [System.IO.Path]::GetDirectoryName($kbPath)
$kbName = [System.IO.Path]::GetFileName($kbPath)
if ($kbParent -ne $kbRoot -and -not $kbParent.StartsWith($kbRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "KB path is not under C:\KBs: $kbPath"
}
if ($kbName -notmatch '(?i)teste') { throw "KB directory name does not contain 'teste': $kbPath" }

$runs = @(
    @{ Name = "inspect"; Filter = "PackagedInspectMinimal"; Var = "GXMCP_NATIVE_SDK_ORACLE_DIR"; Oracle = $InspectOracleDir },
    @{ Name = "versions"; Filter = "PackagedKbVersionList"; Var = "GXMCP_NATIVE_SDK_ORACLE_FILE"; Oracle = (Join-Path $SdkOracleDir "versions.json") },
    @{ Name = "env"; Filter = "PackagedCurrentEnvironment"; Var = "GXMCP_NATIVE_SDK_ORACLE_FILE"; Oracle = (Join-Path $SdkOracleDir "environment.json") },
    @{ Name = "list"; Filter = "PackagedProcedureList"; Var = "GXMCP_NATIVE_SDK_ORACLE_DIR"; Oracle = $SdkOracleDir }
)
$policies = @("sdk", "auto", "gxengine")

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($run in $runs) {
    if (-not (Test-Path -LiteralPath $run.Oracle)) { throw "SDK oracle input not found: $($run.Oracle)" }
    foreach ($policy in $policies) {
        $label = "packaged-$($run.Name)-$policy-v$Suffix"
        foreach ($existing in @((Join-Path $OutputDir "$label.log"), (Join-Path $OutputDir $label))) {
            if (Test-Path -LiteralPath $existing) { throw "Refusing to overwrite prior evidence: $existing" }
        }
    }
}

try {
    foreach ($run in $runs) {
        foreach ($policy in $policies) {
            $label = "packaged-$($run.Name)-$policy-v$Suffix"
            $logPath = Join-Path $OutputDir "$label.log"
            Remove-Item Env:GXMCP_NATIVE_SDK_ORACLE_FILE, Env:GXMCP_NATIVE_SDK_ORACLE_DIR -ErrorAction SilentlyContinue
            Set-Item "Env:$($run.Var)" $run.Oracle
            $env:GXMCP_NATIVE_EXPECT_BACKEND = $policy
            $env:GXMCP_NATIVE_READ_ORACLE_DIR = Join-Path $OutputDir $label
            $liveArgs = @{
                KbPath = $kbPath
                GxPath = $GxPath
                SkipBuild = $true
                GatewayOnly = $true
                EngineBackend = $policy
                TestFilter = "FullyQualifiedName~NativeReadOnlyRoutingLiveTests.$($run.Filter)"
                RpcTimeoutSeconds = 120
            }
            if ($policy -eq "gxengine") { $liveArgs.RequireEngineServed = $true }
            & ./scripts/test-live.ps1 @liveArgs *> $logPath
            $code = $LASTEXITCODE
            Write-Output "== $label exit=$code"
            Get-Content -LiteralPath $logPath |
                Select-String -Pattern "Passed!|Failed!|Observed routing|Live gate completed|Error Message|live-[0-9a-f]{32}" |
                ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) } |
                Select-Object -Unique
            if ($code -ne 0) { exit $code }
        }
    }
}
finally {
    Remove-Item Env:GXMCP_NATIVE_SDK_ORACLE_FILE, Env:GXMCP_NATIVE_SDK_ORACLE_DIR, Env:GXMCP_NATIVE_EXPECT_BACKEND, Env:GXMCP_NATIVE_READ_ORACLE_DIR -ErrorAction SilentlyContinue
}
