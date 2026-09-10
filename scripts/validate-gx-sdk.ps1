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

$anchor = Join-Path $GxPath $spec.anchor
if (-not (Test-Path -LiteralPath $anchor -PathType Leaf)) {
    Fail "GXMCP_SDK_ANCHOR_MISSING path=$($spec.anchor)"
}
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($anchor).ProductVersion
if ($actualVersion -ne $spec.supportedVersion) {
    Fail "GXMCP_SDK_VERSION_MISMATCH expectedVersion=$($spec.supportedVersion) actualVersion=$actualVersion"
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
    if ($actualHash -ne $assembly.sha256.ToLowerInvariant()) {
        Fail "GXMCP_SDK_FINGERPRINT_MISMATCH path=$($assembly.path) expectedSha256=$($assembly.sha256) actualSha256=$actualHash"
    }
}
Write-Output "GXMCP_SDK_COMPATIBLE version=$($spec.supportedVersion) assemblies=$($spec.assemblies.Count)"
exit 0
