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

    # Comma-separated client ids: claude-desktop-win, claude-desktop-mac,
    # antigravity, claude-code, gemini-cli, cursor, opencode, codex-cli.
    # Default: all detected installed agents.
    [string]$Clients,

    # Show interactive y/N prompt per detected agent (overrides -Clients).
    [switch]$InteractiveClients,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:initFailed = $false
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

# Spawn probe: if AppLocker/SRP blocks execution from $InstallDir, fail HERE with a
# clear actionable message instead of letting the user discover it via "Failed to
# connect" from their MCP client hours later.
Write-Step 'Probing gateway exe (AppLocker / execution policy check)...'
$probeError = $null
try {
    $proc = Start-Process -FilePath $gatewayExe -ArgumentList '--axi-spawn-probe' `
        -PassThru -WindowStyle Hidden -ErrorAction Stop
    Start-Sleep -Milliseconds 600
    if (-not $proc.HasExited) {
        try { $proc.Kill() } catch { }
    }
    Write-Ok 'Gateway exe is launchable from the install path.'
} catch {
    $probeError = $_.Exception.Message
}

if ($probeError) {
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    $msg = $probeError
    $accessDenied = $msg -match 'Access is denied|Access denied|Acesso negado|0x80070005|UnauthorizedAccess'
    Write-Host ''
    if ($accessDenied) {
        Write-Host '[X] AppLocker / SRP / Defender blocked execution of the gateway from this path.' -ForegroundColor Red
        Write-Host "    Path: $gatewayExe" -ForegroundColor Red
        Write-Host ''
        Write-Host 'Remediations:' -ForegroundColor Yellow
        Write-Host '  - Run installer as admin so it defaults to C:\Tools\GenexusMCP (admin-writable, usually whitelisted).'
        Write-Host '  - Or pass -InstallDir to a path your IT policy allows execution from'
        Write-Host '    (e.g. C:\Apps\GenexusMCP). AppLocker default rules deny exec from'
        Write-Host '    %APPDATA%, %LOCALAPPDATA%, and %TEMP%.'
        Write-Host '  - If you got here via -InstallDir under AppData, retry with a path outside AppData.'
        Write-Host '  - Get-AppLockerPolicy -Effective -Xml  # to inspect current policy'
        Write-Host '  - Event log: Microsoft-Windows-AppLocker/EXE and DLL, IDs 8003/8004'
    } else {
        Write-Host '[X] Gateway exe failed to launch from the install path.' -ForegroundColor Red
        Write-Host "    Path: $gatewayExe" -ForegroundColor Red
        Write-Host "    Error: $msg" -ForegroundColor Red
    }
    throw "Aborting: gateway exe is not runnable from '$InstallDir'."
}

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
        if ($InteractiveClients) {
            # Full interactive flow: prompts per detected agent (plus KB/GX prompts if not provided).
            $initArgs = @('-y', 'genexus-mcp@latest', 'init', '--interactive')
            if ($Kb) { $initArgs += @('--kb', $Kb) }
            if ($Gx) { $initArgs += @('--gx', $Gx) }
        } elseif ($Clients) {
            $initArgs += @('--clients', $Clients)
        }

        Write-Step "Registering with AI clients (gateway = $gatewayExe)"
        # GENEXUS_MCP_GATEWAY_EXE is the contract that tells patchClientConfig
        # in cli/lib/config.js to write the direct exe path into the client
        # mcpServers entry instead of an npx invocation.
        $prev = $env:GENEXUS_MCP_GATEWAY_EXE
        $env:GENEXUS_MCP_GATEWAY_EXE = $gatewayExe
        try {
            & $npx.Source @initArgs
            if ($LASTEXITCODE -ne 0) {
                $script:initFailed = $true
            }
        } finally {
            if ($null -ne $prev) { $env:GENEXUS_MCP_GATEWAY_EXE = $prev }
            else { Remove-Item env:GENEXUS_MCP_GATEWAY_EXE -ErrorAction SilentlyContinue }
        }
    }
}

Write-Host ''
if ($script:initFailed) {
    Write-Warn "genexus-mcp $Version files installed to:"
    Write-Host "     $InstallDir"
    Write-Host ''
    Write-Warn 'Client registration (init) FAILED — see error output above.'
    Write-Warn 'Files are extracted, but no AI client config was written and no config.json was created.'
    Write-Host ''
    Write-Host 'Common causes:' -ForegroundColor Yellow
    Write-Host '  - GeneXus installed in a non-standard path (auto-discovery missed it)'
    Write-Host '  - Not running from inside a KB folder'
    Write-Host ''
    Write-Host 'Fix by re-running with explicit paths:' -ForegroundColor Cyan
    Write-Host '  $s = irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1'
    Write-Host '  & ([scriptblock]::Create($s)) -Kb "C:\KBs\YourKB" -Gx "C:\Path\To\GeneXus18" -Force'
    Write-Host ''
    exit 1
}

Write-Ok "genexus-mcp $Version installed to:"
Write-Host "     $InstallDir"
Write-Host ''
Write-Host 'Paths to whitelist with IT (ASR / Defender):' -ForegroundColor Cyan
Write-Host "  $gatewayExe"
Write-Host "  $workerExe"
Write-Host ''
Write-Host 'Restart your AI client (Claude Desktop / Cursor / Antigravity) to pick up the MCP.' -ForegroundColor Cyan
