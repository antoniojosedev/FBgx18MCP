$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wrapperSource = Join-Path $root 'scripts\Invoke-GxMcpLocalBuild.ps1'
$setterSource = Join-Path $root 'scripts\Set-GxMcpLocalEngineBackend.ps1'
foreach ($source in @($wrapperSource, $setterSource)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing fork-local helper: $source"
    }
}

$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$tempRoot = Join-Path $env:TEMP ('gxmcp-fork-helpers-' + [guid]::NewGuid().ToString('N'))
$scriptsDir = Join-Path $tempRoot 'scripts'
$publishDir = Join-Path $tempRoot 'publish'
$configPath = Join-Path $publishDir 'config.json'
$installMarker = Join-Path $tempRoot 'install-args.txt'
$wrapper = Join-Path $scriptsDir 'Invoke-GxMcpLocalBuild.ps1'
$setter = Join-Path $scriptsDir 'Set-GxMcpLocalEngineBackend.ps1'
$previousFakeExit = $env:GXMCP_FAKE_EXIT

function Invoke-Child {
    param([string[]]$Arguments)

    $output = & $pwsh -NoProfile -File @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ exitCode = $LASTEXITCODE; output = $output }
}

$fakeBuildBody = @'
if ($env:GXMCP_FAKE_EXIT) { exit [int]$env:GXMCP_FAKE_EXIT }
$publish = Join-Path $PSScriptRoot 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$json = '{"Server":{"Name":"neutral"},"Engine":{"Backend":"gxengine","Mode":"keep"},"Extra":42}'
[IO.File]::WriteAllText((Join-Path $publish 'config.json'), $json, [Text.UTF8Encoding]::new($false))
exit 0
'@

$fakeInstallBody = @'
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'install-args.txt'), ($args -join '|'), [Text.UTF8Encoding]::new($false))
if ($env:GXMCP_FAKE_EXIT) { exit [int]$env:GXMCP_FAKE_EXIT }
$publish = Join-Path $PSScriptRoot 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$json = '{"Server":{"Name":"installed"},"Engine":{"Backend":"gxengine"},"Extra":7}'
[IO.File]::WriteAllText((Join-Path $publish 'config.json'), $json, [Text.UTF8Encoding]::new($false))
exit 0
'@

try {
    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    Copy-Item -LiteralPath $wrapperSource -Destination $wrapper
    Copy-Item -LiteralPath $setterSource -Destination $setter
    [IO.File]::WriteAllText((Join-Path $tempRoot 'build.ps1'), $fakeBuildBody, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $tempRoot 'install.ps1'), $fakeInstallBody, [Text.UTF8Encoding]::new($false))

    $sentinel = '{"Sentinel":"unchanged"}'
    [IO.File]::WriteAllText($configPath, $sentinel, [Text.UTF8Encoding]::new($false))
    $sentinelBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))

    $env:GXMCP_FAKE_EXIT = '7'
    $buildFailure = Invoke-Child -Arguments @($wrapper, '-EngineBackend', 'auto')
    if ($buildFailure.exitCode -ne 7) {
        throw "A failed build must propagate exit code 7 unchanged; got $($buildFailure.exitCode)."
    }
    $afterFailure = [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))
    if ($afterFailure -ne $sentinelBytes) {
        throw 'A failed build must not patch publish\config.json.'
    }

    $env:GXMCP_FAKE_EXIT = '5'
    $installFailure = Invoke-Child -Arguments @($wrapper, '-Install', '-InstallerArgs', 'InstallProbe', '-EngineBackend', 'auto')
    if ($installFailure.exitCode -ne 5) {
        throw "A failed install must propagate exit code 5 unchanged; got $($installFailure.exitCode)."
    }
    $afterInstallFailure = [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))
    if ($afterInstallFailure -ne $sentinelBytes) {
        throw 'A failed install must not patch publish\config.json.'
    }
    if (-not (Test-Path -LiteralPath $installMarker -PathType Leaf)) {
        throw 'The wrapper must forward -InstallerArgs to install.ps1.'
    }

    Remove-Item Env:\GXMCP_FAKE_EXIT -ErrorAction SilentlyContinue
    $buildSuccess = Invoke-Child -Arguments @($wrapper, '-EngineBackend', 'auto')
    if ($buildSuccess.exitCode -ne 0) {
        throw "A successful build must exit 0; got $($buildSuccess.exitCode). $($buildSuccess.output)"
    }
    $config = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
    if ($config.Engine.Backend -ne 'auto') {
        throw "A successful build must apply Engine.Backend=auto; got '$($config.Engine.Backend)'."
    }
    if ($config.Engine.Mode -ne 'keep' -or $config.Server.Name -ne 'neutral' -or $config.Extra -ne 42) {
        throw 'The setter must preserve every key other than Engine.Backend.'
    }
    $configBytes = [IO.File]::ReadAllBytes($configPath)
    if ($configBytes.Length -lt 1 -or $configBytes[0] -ne 0x7B) {
        throw 'The setter must write UTF-8 without BOM.'
    }
    $configText = [IO.File]::ReadAllText($configPath)
    if ([regex]::IsMatch($configText, "(?<!`r)`n")) {
        throw 'The setter must write uniform CRLF, with no lone LF.'
    }

    $firstRunBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))
    $setterRerun = Invoke-Child -Arguments @($setter, '-Backend', 'auto', '-Path', $configPath)
    if ($setterRerun.exitCode -ne 0) {
        throw "The setter rerun must exit 0; got $($setterRerun.exitCode). $($setterRerun.output)"
    }
    $secondRunBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))
    if ($firstRunBytes -ne $secondRunBytes) {
        throw 'The setter must be idempotent when the backend is already applied.'
    }

    $installSuccess = Invoke-Child -Arguments @($wrapper, '-Install', '-InstallerArgs', 'InstallProbe', '-EngineBackend', 'sdk')
    if ($installSuccess.exitCode -ne 0) {
        throw "A successful install must exit 0; got $($installSuccess.exitCode). $($installSuccess.output)"
    }
    $installedConfig = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
    if ($installedConfig.Engine.Backend -ne 'sdk' -or $installedConfig.Server.Name -ne 'installed' -or $installedConfig.Extra -ne 7) {
        throw 'A successful install must regenerate the config and then apply Engine.Backend=sdk.'
    }
    $marker = [IO.File]::ReadAllText($installMarker)
    if ($marker -ne 'InstallProbe') {
        throw "The wrapper must forward -InstallerArgs unchanged; got '$marker'."
    }
} finally {
    if ($null -eq $previousFakeExit) {
        Remove-Item Env:\GXMCP_FAKE_EXIT -ErrorAction SilentlyContinue
    } else {
        $env:GXMCP_FAKE_EXIT = $previousFakeExit
    }
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'fork-local backend helpers: exit-code, merge, byte-shape and idempotence contract passed' -ForegroundColor Green
