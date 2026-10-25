#Requires -Version 7.3
<#
.SYNOPSIS
Reapplies the local Engine.Backend choice to the generated publish config.

.DESCRIPTION
Fork-local helper. A plain build.ps1 (without -PreserveEngineConfig) wipes
publish/ and regenerates publish/config.json from a neutral template that has no
Engine section, so a local Engine.Backend choice (for example "sdk" to keep the
engine out of the way) is lost when the build runs directly; install.ps1 passes
the switch and keeps the section. This helper merges exactly one setting -
Engine.Backend - into the freshly generated file and leaves every other key
untouched.

Engine environment variables are not an alternative for strict documents: the
Gateway rejects GXMCP_ENGINE_* at load time there, so the file is the only
authority.

.PARAMETER Backend
sdk (the engine is never invoked), auto (admitted builds only) or gxengine
(upstream default).

.PARAMETER Path
Config file to patch. Defaults to <repo>\publish\config.json.

.EXAMPLE
pwsh -NoProfile -File scripts/Set-GxMcpLocalEngineBackend.ps1 -Backend sdk

.EXAMPLE
pwsh -NoProfile -File scripts/Set-GxMcpLocalEngineBackend.ps1 -Backend gxengine -Path C:\tmp\config.json
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('sdk', 'auto', 'gxengine')]
    [string]$Backend = 'sdk',

    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = Join-Path $root 'publish\config.json'
}

$fullPath = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "Config not found: $fullPath. Run pwsh -NoProfile -File scripts\Invoke-GxMcpLocalBuild.ps1 first (it generates publish\config.json)."
}

$raw = [System.IO.File]::ReadAllText($fullPath)
try {
    $config = $raw | ConvertFrom-Json
}
catch {
    throw "Config is not valid JSON: $fullPath ($($_.Exception.Message))"
}

if ($null -eq $config -or $config -isnot [pscustomobject]) {
    throw "Config root is not a JSON object: $fullPath"
}

if ($config.PSObject.Properties.Name -contains 'Engine' -and $config.Engine -is [pscustomobject]) {
    $config.Engine | Add-Member -NotePropertyName 'Backend' -NotePropertyValue $Backend -Force
}
else {
    $config | Add-Member -NotePropertyName 'Engine' -NotePropertyValue ([pscustomobject]@{ Backend = $Backend }) -Force
}

$json = $config | ConvertTo-Json -Depth 10

if ($PSCmdlet.ShouldProcess($fullPath, "Set Engine.Backend=$Backend")) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($fullPath, $json.TrimEnd() + [Environment]::NewLine, $utf8NoBom)

    $readBack = [System.IO.File]::ReadAllText($fullPath) | ConvertFrom-Json
    if ($readBack.Engine.Backend -ne $Backend) {
        throw "Write verification failed: Engine.Backend is '$($readBack.Engine.Backend)', expected '$Backend'."
    }
}

[pscustomobject]@{
    Path          = $fullPath
    EngineBackend = $Backend
}
