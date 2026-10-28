#requires -Version 7
<#
.SYNOPSIS
  Generates an Events save audit manifest (config/events-*-audit.json) from an installed GeneXus bundle.

.DESCRIPTION
  Read-only on the installation: it enumerates and hashes the Artech.*, GeneXus.* and DVelop.* managed assemblies that sit
  in the directories listed by an existing manifest (the scope), and copies every non-assembly section from a template
  manifest, so the generated file has the same shape the Worker reads (config/events-u16-wwp16.2-audit.json is the reference).
  It never opens a Knowledge Base, loads an assembly into this process or contacts a service.

  A manifest generated here does NOT authorize anything by itself: unless the nativeVerification section says `verified`
  with the digest of a native proof, the Worker only allows the authorized synthetic object to be saved.

  Regenerating the U16 manifest from its own installation, with itself as scope and template, reproduces every entry (name,
  hash, versions, path ignoring case) and every other section, in the same order. The only differences are the 20 culture
  satellites that were loaded when that manifest was created (the reference leaves them unflagged) and the letter case of
  one file name; neither is read by the Worker, which checks `name`, `relativePath` and `sha256` only.

.EXAMPLE
  pwsh -NoProfile -File scripts/new-events-bundle-audit-manifest.ps1 -InstallationRoot 'C:\Program Files (x86)\GeneXus\GeneXus18' `
    -ScopeManifest config\events-u16-wwp16.2-audit.json -Template config\events-u16-wwp16.2-audit.json `
    -BundleId 18.0.11.54344+wwp16.1.2.8167 `
    -NativeVerificationJson '{"status":"pending","syntheticObject":"McpU11AuditProbe01","scope":"kb-without-pattern-instances"}' -Output config\events-u11-wwp16.1-audit.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallationRoot,
    [Parameter(Mandatory)][string]$ScopeManifest,
    [Parameter(Mandatory)][string]$Template,
    [string]$BundleId,
    [string]$NativeVerificationJson,
    [string]$RuntimeInspectionFile,
    [Parameter(Mandatory)][string]$Output
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $InstallationRoot).Path.TrimEnd('\')
$scope = Get-Content -LiteralPath $ScopeManifest -Raw | ConvertFrom-Json -Depth 30
$templateManifest = Get-Content -LiteralPath $Template -Raw | ConvertFrom-Json -Depth 30

$directories = @($scope.assemblies | ForEach-Object {
    $directory = [IO.Path]::GetDirectoryName($_.relativePath.Replace('/', '\'))
    if ($null -eq $directory) { '' } else { $directory }
} | Sort-Object -Unique)

$entries = [System.Collections.Generic.List[object]]::new()
foreach ($directory in $directories) {
    $fullDirectory = if ($directory) { Join-Path $root $directory } else { $root }
    if (-not (Test-Path -LiteralPath $fullDirectory -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $fullDirectory -File -Filter '*.dll') {
        if ($file.Name -notmatch '^(Artech|GeneXus|DVelop)\.') { continue }
        try { $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $relativePath = ([IO.Path]::GetRelativePath($root, $file.FullName)).Replace('\', '/')
        $sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $version = $assemblyName.Version.ToString()
        $product = $file.VersionInfo.ProductVersion
        # Culture satellites are flagged, with the property order of the reference manifest.
        if ($assemblyName.Name.EndsWith('.resources', [StringComparison]::OrdinalIgnoreCase)) {
            $entries.Add([ordered]@{ name = $assemblyName.Name; sha256 = $sha256; relativePath = $relativePath
                assemblyVersion = $version; productVersion = $product; resourceSatellite = $true })
        }
        else {
            $entries.Add([ordered]@{ name = $assemblyName.Name; relativePath = $relativePath; sha256 = $sha256
                assemblyVersion = $version; productVersion = $product })
        }
    }
}
# Entries are ordered by relative path, ignoring case. List.Sort is used on purpose: [Array]::Sort(keys, items, comparer)
# orders the keys and leaves the items array as it was when PowerShell binds it.
$entries.Sort([Comparison[object]] { param($left, $right) [string]::Compare($left['relativePath'], $right['relativePath'], [StringComparison]::OrdinalIgnoreCase) })
$items = $entries.ToArray()

foreach ($critical in $templateManifest.criticalAssemblies) {
    if (@($items | Where-Object { $_.name -ceq $critical }).Count -ne 1) {
        throw "Critical assembly '$critical' must appear exactly once in the scope of this installation."
    }
}

$manifest = [ordered]@{}
foreach ($property in $templateManifest.PSObject.Properties) {
    switch ($property.Name) {
        'bundleId' { $manifest['bundleId'] = $(if ($BundleId) { $BundleId } else { $property.Value }) }
        'assemblies' { $manifest['assemblies'] = $items }
        'nativeVerification' { $manifest['nativeVerification'] = $(if ($NativeVerificationJson) { $NativeVerificationJson | ConvertFrom-Json -Depth 10 } else { $property.Value }) }
        'runtimeInspection' { $manifest['runtimeInspection'] = $(if ($RuntimeInspectionFile) { Get-Content -LiteralPath $RuntimeInspectionFile -Raw | ConvertFrom-Json -Depth 10 } else { $property.Value }) }
        default { $manifest[$property.Name] = $property.Value }
    }
}
$json = ($manifest | ConvertTo-Json -Depth 12) -replace "`r`n", "`n"
[IO.File]::WriteAllText($Output, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ('{0}: {1} assemblies, bundle {2}' -f $Output, $items.Count, $manifest['bundleId'])
