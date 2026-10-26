# Helpers for the publish\config.json that build.ps1 regenerates.
#
# build.ps1 empties publish\ and writes a neutral config.json. A direct-exe
# launcher (GENEXUS_MCP_GATEWAY_EXE) and start_mcp.bat read that file, so an
# Engine section a developer added to it (for example Engine.Backend=sdk) must
# never disappear silently. These functions read the previous section before the
# clean step and decide, per key, what may be carried into the new file.

Set-StrictMode -Version Latest

$script:PublishEngineBackends = @('gxengine', 'auto', 'sdk')
$script:PublishEngineFallbacks = @('sdk', 'error')

function Read-PublishEngineSection {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath)) { return $null }
    try {
        $document = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    } catch {
        Write-Warning "Previous publish config.json is not valid JSON; any Engine section in it cannot be carried over: $($_.Exception.Message)"
        return $null
    }

    $property = $document.PSObject.Properties['Engine']
    if ($null -eq $property -or $null -eq $property.Value) { return $null }
    if ($property.Value -isnot [pscustomobject]) { return $null }
    if (@($property.Value.PSObject.Properties).Count -eq 0) { return $null }
    return $property.Value
}

# Returns @{ Section = <ordered hashtable or $null>; Warnings = <string[]> }.
# Without -Preserve the section is discarded and the warning says so; with
# -Preserve only the behavioral keys that still validate are kept. Executable is
# machine-specific, so it is never carried over. A release refuses any Engine
# section: scripts/verify-release-manifest.py only accepts the neutral keys
# written by New-PublishConfigJson (#682).
function Resolve-PublishEngineSection {
    [CmdletBinding()]
    param(
        $PreviousSection,
        [switch]$Preserve
    )

    $warnings = New-Object System.Collections.Generic.List[string]
    if ($null -eq $PreviousSection) {
        return @{ Section = $null; Warnings = @() }
    }

    $keys = @($PreviousSection.PSObject.Properties | ForEach-Object { $_.Name })
    if (-not $Preserve) {
        $warnings.Add("Discarding the Engine section ($($keys -join ', ')) of the previous publish\config.json; pass -PreserveEngineConfig to keep Engine.Backend/Fallback/TimeoutSeconds across rebuilds.")
        return @{ Section = $null; Warnings = $warnings.ToArray() }
    }

    $kept = [ordered]@{}
    foreach ($property in $PreviousSection.PSObject.Properties) {
        $name = $property.Name
        $value = $property.Value
        if ($null -eq $value) { continue }

        switch ($name) {
            'Backend' {
                $normalized = ([string]$value).Trim().ToLowerInvariant()
                if ($script:PublishEngineBackends -contains $normalized) { $kept['Backend'] = $normalized }
                else { $warnings.Add("Dropping Engine.Backend '$value': expected one of $($script:PublishEngineBackends -join ', ').") }
            }
            'Fallback' {
                $normalized = ([string]$value).Trim().ToLowerInvariant()
                if ($script:PublishEngineFallbacks -contains $normalized) { $kept['Fallback'] = $normalized }
                else { $warnings.Add("Dropping Engine.Fallback '$value': expected one of $($script:PublishEngineFallbacks -join ', ').") }
            }
            'TimeoutSeconds' {
                if (($value -is [int] -or $value -is [long]) -and [int]$value -gt 0) { $kept['TimeoutSeconds'] = [int]$value }
                else { $warnings.Add("Dropping Engine.TimeoutSeconds '$value': expected a positive integer.") }
            }
            'Executable' {
                $warnings.Add("Dropping Engine.Executable '$value': it is machine-specific and must not travel in the publish artifact.")
            }
            default {
                $warnings.Add("Dropping unknown Engine key '$name'.")
            }
        }
    }

    if ($kept.Count -eq 0) { return @{ Section = $null; Warnings = $warnings.ToArray() } }
    return @{ Section = $kept; Warnings = $warnings.ToArray() }
}

function New-PublishConfigJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactGxPath,
        $EngineSection
    )

    $config = [ordered]@{
        ConfigSchemaVersion = 2
        GatewayMode = 'stdio-isolated'
        GeneXus = [ordered]@{
            InstallationPath = $ArtifactGxPath
            WorkerExecutable = 'worker\\GxMcp.Worker.exe'
        }
        Server = [ordered]@{
            HttpPort = 0
            McpStdio = $true
            SessionIdleTimeoutMinutes = 10
            WorkerIdleTimeoutMinutes = 5
            EmitStructuredContent = $false
            TerseResponses = $true
        }
        Environment = [ordered]@{
            ResolutionPolicy = 'strict'
        }
    }
    if ($null -ne $EngineSection) { $config['Engine'] = $EngineSection }
    return ($config | ConvertTo-Json -Depth 4)
}
