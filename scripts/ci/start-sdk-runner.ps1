[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunnerDirectory,
    [Parameter(Mandatory = $true)][string]$KbPath,
    [Parameter(Mandatory = $true)][string]$FixturePath,
    [string]$SdkPath = 'C:\Program Files (x86)\GeneXus\GeneXus18',
    [string]$Repository = 'lennix1337/Genexus18MCP',
    [string]$RunnerName = "gxmcp-sdk-$($env:COMPUTERNAME.ToLowerInvariant())",
    [string]$DotnetChannel = '10.0',
    # Checks only; registers nothing and downloads nothing.
    [switch]$PreflightOnly
)
# Prepares and runs one ephemeral `gx-sdk-18` job for docs/ci-sdk-validation.md. Each preflight below is a way the
# lane failed on a real runner (2026-10-25): it fails here, in seconds, with the fix, instead of minutes into the job.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\live-fixture.ps1')

function Write-Check([string]$Text) { Write-Host "ok: $Text" -ForegroundColor Green }

# 1. The manifest must match the KB on disk. Opening a KB in GeneXus can rewrite its .gxw, so a manifest made before
#    that fails in the job's live gate; regenerate it with scripts/new-live-fixture-manifest.ps1.
$resolvedKb = (Resolve-Path -LiteralPath $KbPath).Path
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
try { Assert-LiveFixture $fixture $resolvedKb }
catch { throw "Fixture $FixturePath does not match $resolvedKb`: $($_.Exception.Message) Pick the manifest made for this KB revision, or regenerate it." }
Write-Check "fixture $($fixture.fixtureId) matches $resolvedKb"

if (-not (Test-Path -LiteralPath (Join-Path $SdkPath 'GeneXus.exe') -PathType Leaf)) { throw "No GeneXus.exe under $SdkPath." }
Write-Check "GeneXus SDK at $SdkPath"

# 2. The live harness binds a port in 55100..55199. Windows can reserve that range with nothing listening on it.
$port = 55100..55199 | Where-Object {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $_)
    try { $listener.Start(); $true } catch { $false } finally { $listener.Stop() }
} | Select-Object -First 1
if (-not $port) { throw 'No bindable port in 55100..55199. See: netsh int ipv4 show excludedportrange protocol=tcp' }
Write-Check "live HTTP port $port is bindable"

# 3. setup-dotnet installs into DOTNET_INSTALL_DIR, and the runner user usually cannot write C:\Program Files\dotnet.
#    Its PowerShell download of the SDK zip can stall for half an hour, so the SDK is fetched here with curl.exe first;
#    setup-dotnet then finds it installed and skips the download.
$dotnetDirectory = Join-Path $RunnerDirectory 'dotnet-ci'
$installer = Join-Path ([System.IO.Path]::GetTempPath()) 'gxmcp-dotnet-install.ps1'
if ($PreflightOnly) {
    Write-Host "preflight only: would install .NET $DotnetChannel into $dotnetDirectory and register $RunnerName"
    return
}
& curl.exe -fsSL --retry 3 -o $installer https://dot.net/v1/dotnet-install.ps1
if ($LASTEXITCODE -ne 0) { throw "Could not download dotnet-install.ps1 (curl exit $LASTEXITCODE)." }
$dryRun = (& $installer -Channel $DotnetChannel -InstallDir $dotnetDirectory -DryRun *>&1 | Out-String)
$url = [regex]::Match($dryRun, 'https://\S+/dotnet-sdk-[^/\s]+-win-x64\.zip').Value
$version = [regex]::Match($url, 'dotnet-sdk-(.+)-win-x64\.zip').Groups[1].Value
if (-not $version) { throw "dotnet-install.ps1 -DryRun named no SDK zip for channel $DotnetChannel." }
if (-not (Test-Path -LiteralPath (Join-Path $dotnetDirectory "sdk\$version"))) {
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) "dotnet-sdk-$version-win-x64.zip"
    & curl.exe -fSL --retry 3 --max-time 1800 -o $zip $url
    if ($LASTEXITCODE -ne 0) { throw "Could not download $url (curl exit $LASTEXITCODE)." }
    Expand-Archive -LiteralPath $zip -DestinationPath $dotnetDirectory -Force
    Remove-Item -LiteralPath $zip -Force
}
Write-Check ".NET SDK $version in $dotnetDirectory"

# 4. One ephemeral job: the runner unregisters itself when the job ends.
Push-Location $RunnerDirectory
try {
    if (-not (Test-Path -LiteralPath '.runner')) {
        $token = & gh api -X POST "repos/$Repository/actions/runners/registration-token" --jq .token
        if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'Could not get a runner registration token (gh auth?).' }
        & .\config.cmd --unattended --url "https://github.com/$Repository" --token $token --name $RunnerName `
            --labels gx-sdk-18 --ephemeral --work _work --replace
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath '.runner')) { throw "Runner registration failed (exit $LASTEXITCODE)." }
    }
    Write-Check "runner $RunnerName registered for one job"
    $env:DOTNET_INSTALL_DIR = $dotnetDirectory
    $env:GXMCP_SDK_CI_LICENSE_ACK = '1'
    $env:GXMCP_SDK_PATH = $SdkPath
    $env:GXMCP_TEST_KB = $resolvedKb
    $env:GXMCP_TEST_FIXTURE = (Resolve-Path -LiteralPath $FixturePath).Path
    & .\run.cmd
    exit $LASTEXITCODE
} finally { Pop-Location }
