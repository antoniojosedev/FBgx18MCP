# GeneXus MCP - Corporate Installer (fixed-path)
# ==============================================
# Installs `genexus-mcp` to a stable directory so corporate ASR / Defender
# policies can whitelist exact paths without needing wildcards over the
# npm cache (`%LOCALAPPDATA%\npm-cache\_npx\<hash>\...`).
#
# Default install dir:
#   - Admin           -> C:\Tools\GenexusMCP
#   - Non-admin       -> %LOCALAPPDATA%\Programs\GenexusMCP
#
# One-liner (latest release, runs init too):
#   iex (irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1)
#
# With params:
#   $script = irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1
#   & ([scriptblock]::Create($script)) -Kb "C:\KBs\MyKB" -Gx "C:\Program Files (x86)\GeneXus\GeneXus18"
#
# Re-run with the same args to upgrade. Use -Force to reinstall the same version.

[CmdletBinding()]
param(
    [string]$InstallDir,

    [string]$Version,

    [ValidateScript({ -not $_ -or (Test-Path -LiteralPath $_) })]
    [string]$Kb,

    [ValidateScript({ -not $_ -or (Test-Path -LiteralPath $_) })]
    [string]$Gx,

    [switch]$NoClient,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Repo = 'lennix1337/Genexus18MCP'
$ApiBase = 'https://api.github.com'

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step($msg) { Write-Host "[i] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Ok($msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }

if (-not $InstallDir) {
    if (Test-IsAdmin) {
        $InstallDir = 'C:\Tools\GenexusMCP'
    } else {
        $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\GenexusMCP'
        Write-Warn "Not running as admin — installing to per-user path: $InstallDir"
    }
}

if (-not $Version) {
    Write-Step 'Resolving latest release...'
    $rel = Invoke-RestMethod -Uri "$ApiBase/repos/$Repo/releases/latest" -UseBasicParsing
    $Version = $rel.tag_name
}
if ($Version -notmatch '^v') { $Version = "v$Version" }
Write-Step "Target version: $Version"

$versionFile = Join-Path $InstallDir 'version.txt'
$gatewayExe  = Join-Path $InstallDir 'GxMcp.Gateway.exe'
$workerExe   = Join-Path $InstallDir 'worker\GxMcp.Worker.exe'

if ((Test-Path $versionFile) -and -not $Force) {
    $current = (Get-Content $versionFile -Raw).Trim()
    if ($current -eq $Version) {
        Write-Ok "Already at $Version. Pass -Force to reinstall."
        exit 0
    }
    Write-Step "Upgrading $current -> $Version"
}

$zipUrl = "https://github.com/$Repo/releases/download/$Version/publish.zip"
$tmpZip = Join-Path ([IO.Path]::GetTempPath()) "genexus-mcp-$Version.zip"

Write-Step "Downloading $zipUrl"
try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $tmpZip -UseBasicParsing
} catch {
    if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue }
    throw "Failed to download $zipUrl. Check the version exists on Releases or your network/proxy. Error: $($_.Exception.Message)"
}

# Refuse to clean an unrelated directory — only wipe if it already looks like
# an install (version.txt or the gateway exe present), or is brand new.
if (Test-Path $InstallDir) {
    $looksOurs = (Test-Path $versionFile) -or (Test-Path $gatewayExe)
    $isEmpty = -not (Get-ChildItem -Path $InstallDir -Force | Select-Object -First 1)
    if (-not $looksOurs -and -not $isEmpty) {
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        throw "Refusing to clean '$InstallDir' — it exists but doesn't look like a previous GenexusMCP install. Pass -InstallDir to a dedicated directory."
    }
    Write-Step "Cleaning existing $InstallDir"
    Remove-Item -Path (Join-Path $InstallDir '*') -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

Write-Step "Extracting to $InstallDir"
try {
    Expand-Archive -Path $tmpZip -DestinationPath $InstallDir -Force
} finally {
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $gatewayExe)) { throw "Extraction failed: $gatewayExe not found." }
if (-not (Test-Path $workerExe))  { throw "Extraction failed: $workerExe not found." }

$Version | Out-File -FilePath $versionFile -Encoding ascii -NoNewline

if (-not $NoClient) {
    $npx = Get-Command npx.cmd -ErrorAction SilentlyContinue
    if (-not $npx) { $npx = Get-Command npx -ErrorAction SilentlyContinue }

    if (-not $npx) {
        Write-Warn 'npx not found — skipping AI client registration.'
        Write-Warn '  Install Node.js 18+ (https://nodejs.org/) and re-run, or pass -NoClient and configure clients manually.'
    } else {
        $initArgs = @('-y', 'genexus-mcp@latest', 'init', '--write-clients', '--no-smoke')
        if ($Kb) { $initArgs += @('--kb', $Kb) }
        if ($Gx) { $initArgs += @('--gx', $Gx) }

        Write-Step "Registering with AI clients (gateway = $gatewayExe)"
        # GENEXUS_MCP_GATEWAY_EXE is the contract that tells patchClientConfig
        # in cli/lib/config.js to write the direct exe path into the client
        # mcpServers entry instead of an npx invocation.
        $prev = $env:GENEXUS_MCP_GATEWAY_EXE
        $env:GENEXUS_MCP_GATEWAY_EXE = $gatewayExe
        try {
            & $npx.Source @initArgs
        } finally {
            if ($null -ne $prev) { $env:GENEXUS_MCP_GATEWAY_EXE = $prev }
            else { Remove-Item env:GENEXUS_MCP_GATEWAY_EXE -ErrorAction SilentlyContinue }
        }
    }
}

Write-Host ''
Write-Ok "genexus-mcp $Version installed to:"
Write-Host "     $InstallDir"
Write-Host ''
Write-Host 'Paths to whitelist with IT (ASR / Defender):' -ForegroundColor Cyan
Write-Host "  $gatewayExe"
Write-Host "  $workerExe"
Write-Host ''
Write-Host 'Restart your AI client (Claude Desktop / Cursor / Antigravity) to pick up the MCP.' -ForegroundColor Cyan
