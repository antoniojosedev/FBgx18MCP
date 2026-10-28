$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts\ci\start-sdk-runner.ps1'
$generator = Join-Path $root 'scripts\new-live-fixture-manifest.ps1'

# The preflight must stop on a stale manifest before it downloads, registers or runs anything:
# on 2026-10-25 a manifest made before the KB was last opened failed the job minutes in.
$kb = Join-Path $env:TEMP ('gxmcp-runner-preflight-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $kb -Force | Out-Null
try {
    $gxw = Join-Path $kb 'Synthetic.gxw'
    Set-Content -LiteralPath $gxw -Value '<KnowledgeBase><VersionNumber>a</VersionNumber><Name>Synthetic</Name></KnowledgeBase>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $kb 'knowledgebase.connection') -Value 'server=(localdb);database=synthetic' -Encoding utf8
    $manifest = Join-Path $kb 'synthetic.fixture.json'
    & pwsh -NoProfile -File $generator -KbPath $kb -FixtureId 'runner-preflight' -FixtureRevision 'r1' -Generator 'GeneXus18-net' `
        -KbDatabaseId 'isolated-kb' -ApplicationDatabaseId 'isolated-app' -Evidence 'script-test' -ProvisionedBy GeneXus `
        -ConfirmIsolated -OutputPath $manifest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Manifest generator failed with exit code $LASTEXITCODE." }

    function Invoke-Preflight([string]$SdkPath) {
        $output = @(& pwsh -NoProfile -File $script -RunnerDirectory $kb -KbPath $kb -FixturePath $manifest -SdkPath $SdkPath -PreflightOnly 2>&1)
        return @{ Exit = $LASTEXITCODE; Text = ($output -join "`n") }
    }

    # A matching manifest passes the fixture check and then stops at the missing SDK.
    $missingSdk = Invoke-Preflight (Join-Path $kb 'no-sdk')
    if ($missingSdk.Exit -eq 0 -or $missingSdk.Text -notmatch 'ok: fixture runner-preflight matches' -or $missingSdk.Text -notmatch 'No GeneXus\.exe') {
        throw "A matching manifest must pass the fixture check and stop at the missing SDK:`n$($missingSdk.Text)"
    }

    # The KB changes after the manifest was made: the preflight names the hash mismatch and the fix.
    # (VersionNumber is normalized away by Get-LiveFixtureHash -NormalizeGxw, so the change is elsewhere.)
    Set-Content -LiteralPath $gxw -Value '<KnowledgeBase><VersionNumber>a</VersionNumber><Name>Changed</Name></KnowledgeBase>' -Encoding utf8
    $stale = Invoke-Preflight (Join-Path $kb 'no-sdk')
    if ($stale.Exit -eq 0 -or $stale.Text -notmatch 'hash does not match' -or $stale.Text -notmatch 'regenerate it' -or $stale.Text -match 'ok: fixture') {
        throw "A stale manifest must fail the fixture check first:`n$($stale.Text)"
    }
} finally {
    Remove-Item -LiteralPath $kb -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'PASS: start-sdk-runner preflight accepts a matching fixture and stops on a stale one before any download or registration.'
