param(
    [Parameter(Mandatory = $true)][string]$GxPath,
    [Parameter(Mandatory = $true)][string]$Manifest
)

$ErrorActionPreference = 'Stop'
function Fail([string]$message) {
    Write-Error $message
    exit 1
}

if (-not (Test-Path -LiteralPath $GxPath -PathType Container)) {
    Fail "GXMCP_SDK_PATH_MISSING path=<missing>"
}
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    Fail "GXMCP_SDK_MANIFEST_MISSING manifest=$Manifest"
}
try { $spec = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json }
catch { Fail "GXMCP_SDK_MANIFEST_INVALID manifest=$Manifest error=Json" }

function Read-SupportedMajors($entries) {
    $majors = @()
    foreach ($entry in @($entries)) {
        $major = if ($entry -is [string] -or $entry -is [int]) { [string]$entry } else { [string]$entry.major }
        if (-not [string]::IsNullOrWhiteSpace($major) -and $majors -notcontains $major) {
            $majors += $major
        }
    }
    return $majors
}

function Resolve-SupportedMajors($manifestObject, [string]$manifestPath) {
    $manifestDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $manifestPath).Path
    foreach ($catalogPath in @(
        (Join-Path $manifestDirectory 'gx-versions.json'),
        (Join-Path $manifestDirectory 'config/gx-versions.json')
    )) {
        try {
            if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) { continue }
            $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
            $catalogMajors = @(Read-SupportedMajors $catalog.supportedMajors)
            if ($catalogMajors.Count -gt 0) { return $catalogMajors }
        }
        catch {
            # Standalone/older manifests fall back to their declared major below.
        }
    }

    $manifestMajors = @(Read-SupportedMajors $manifestObject.supportedMajors)
    if ($manifestMajors.Count -gt 0) { return $manifestMajors }

    $expectedMajor = 0
    if ([int]::TryParse(([string]$manifestObject.supportedVersion -split '\.')[0], [ref]$expectedMajor) -and $expectedMajor -gt 0) {
        return @([string]$expectedMajor)
    }
    return @()
}

$anchor = Join-Path $GxPath $spec.anchor
if (-not (Test-Path -LiteralPath $anchor -PathType Leaf)) {
    Fail "GXMCP_SDK_ANCHOR_MISSING path=$($spec.anchor)"
}
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($anchor).ProductVersion
$actualMajor = 0
$supportedMajors = @(Resolve-SupportedMajors $spec $Manifest)
$sameMajor = [int]::TryParse(([string]$actualVersion -split '\.')[0], [ref]$actualMajor) -and
    $supportedMajors -contains ([string]$actualMajor)
$exactVersion = $actualVersion -eq $spec.supportedVersion
if (-not $sameMajor) {
    $expectedMajors = if ($supportedMajors.Count -gt 0) { $supportedMajors -join ',' } else { '<none>' }
    Fail "GXMCP_SDK_VERSION_MISMATCH expectedVersion=$($spec.supportedVersion) expectedMajors=$expectedMajors actualVersion=$actualVersion"
}
if (-not $spec.assemblies -or $spec.assemblies.Count -eq 0) {
    Fail "GXMCP_SDK_MANIFEST_INVALID manifest=$Manifest error=assemblies"
}
foreach ($assembly in $spec.assemblies) {
    $file = Join-Path $GxPath $assembly.path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        Fail "GXMCP_SDK_ASSEMBLY_MISSING path=$($assembly.path)"
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($file)
        $actualHash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
    if ($actualHash -ne $assembly.sha256) {
        Write-Output "GXMCP_SDK_FINGERPRINT_DRIFT path=$($assembly.path) expectedSha256=$($assembly.sha256) actualSha256=$actualHash"
    }
}
if ($exactVersion) { $versionDiagnostic = $spec.supportedVersion } else { $versionDiagnostic = "$($spec.supportedVersion) actualVersion=$actualVersion (compatible major; patch/build drift)" }
Write-Output "GXMCP_SDK_COMPATIBLE version=$versionDiagnostic major=$actualMajor supportedMajors=$($supportedMajors -join ',') assemblies=$($spec.assemblies.Count)"
exit 0
