#Requires -Version 7.3
<#
.SYNOPSIS
Builds (or installs) this checkout and reapplies the local Engine.Backend.

.DESCRIPTION
Fork-local wrapper. build.ps1 regenerates publish/ from scratch; since upstream
#649 it keeps the Engine section only with -PreserveEngineConfig (install.ps1
passes it, a plain build.ps1 warns and drops it). This wrapper runs the requested
entry point and then reapplies the choice through
Set-GxMcpLocalEngineBackend.ps1, so the published config ends up with the backend
you asked for whichever entry point ran.

The upstream entry point is invoked as a child process and its exit code is
propagated unchanged; a failed build never leaves a patched config behind.

.PARAMETER EngineBackend
sdk (default here: engine never invoked), auto or gxengine.

.PARAMETER Install
Runs install.ps1 (which also re-registers MCP clients) instead of build.ps1.

.PARAMETER InstallerArgs
Extra arguments forwarded to install.ps1, e.g. -InstallerArgs -SkipClientConfig.

.EXAMPLE
pwsh -NoProfile -File scripts/Invoke-GxMcpLocalBuild.ps1

.EXAMPLE
pwsh -NoProfile -File scripts/Invoke-GxMcpLocalBuild.ps1 -EngineBackend gxengine

.EXAMPLE
pwsh -NoProfile -File scripts/Invoke-GxMcpLocalBuild.ps1 -Install -InstallerArgs -SkipClientConfig
#>
[CmdletBinding()]
param(
    [ValidateSet('sdk', 'auto', 'gxengine')]
    [string]$EngineBackend = 'sdk',

    [switch]$Install,

    [string[]]$InstallerArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$entryPoint = if ($Install) { Join-Path $root 'install.ps1' } else { Join-Path $root 'build.ps1' }

if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
    throw "Entry point not found: $entryPoint"
}

$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$arguments = @('-NoProfile', '-File', $entryPoint) + @($InstallerArgs)

Write-Host ">>> $([System.IO.Path]::GetFileName($entryPoint))" -ForegroundColor Cyan
& $pwsh @arguments
if ($LASTEXITCODE -ne 0) {
    $childExitCode = $LASTEXITCODE
    Write-Error "$([System.IO.Path]::GetFileName($entryPoint)) failed with exit code $childExitCode; config not patched." -ErrorAction Continue
    exit $childExitCode
}

$result = & (Join-Path $PSScriptRoot 'Set-GxMcpLocalEngineBackend.ps1') -Backend $EngineBackend
Write-Host ">>> Engine.Backend=$($result.EngineBackend) applied to $($result.Path)" -ForegroundColor Green
Write-Host "    Upstream default is 'gxengine'; this choice is local and is lost on a plain .\build.ps1 or .\install.ps1." -ForegroundColor DarkGray
