$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\Invoke-PrePushMechanicalChecks.ps1'
$source = Get-Content -LiteralPath $scriptPath -Raw

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "PowerShell syntax errors: $($errors -join '; ')" }

$definition = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-PrePushDecision'
    }, $true)
if (-not $definition) { throw 'Missing Get-PrePushDecision production function.' }
. ([scriptblock]::Create($definition.Extent.Text))

$ready = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $false
if ($ready.status -ne 'passed' -or
    $ready.pushReadiness -ne 'readyLocal' -or
    $ready.localReadiness -ne 'ready' -or
    $ready.exitCode -ne 0) {
    throw 'A clean, mechanically passing checkout must be readyLocal.'
}

$dirty = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $true
if ($dirty.status -ne 'incomplete' -or
    $dirty.pushReadiness -ne 'blocked' -or
    $dirty.exitCode -ne 3) {
    throw 'A dirty working tree must block push readiness without becoming a mechanical failure.'
}

$behind = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 1 -WorkingTreeDirty $false
if ($behind.status -ne 'incomplete' -or
    $behind.pushReadiness -ne 'blocked' -or
    $behind.reasons -notcontains 'The branch is behind its base by 1 commit(s).') {
    throw 'A branch behind its base must block push readiness.'
}

$failed = Get-PrePushDecision -MechanicalFailed $true -MechanicalUnavailable $false -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $false
if ($failed.status -ne 'failed' -or
    $failed.pushReadiness -ne 'blocked' -or
    $failed.exitCode -ne 1) {
    throw 'A failed mechanical phase must produce a failed blocked result.'
}

$unavailable = Get-PrePushDecision -MechanicalFailed $false -MechanicalUnavailable $true -BaseRefAvailable $true -CommitsBehind 0 -WorkingTreeDirty $false
if ($unavailable.status -ne 'incomplete' -or
    $unavailable.pushReadiness -ne 'blocked' -or
    $unavailable.exitCode -ne 3 -or
    $unavailable.reasons -notcontains 'One or more mechanical phases were unavailable.') {
    throw 'An unavailable local dependency must be incomplete and block push readiness.'
}

if ($source -match '(?im)^\s*(?:&\s*)?git\s+push\b') {
    throw 'The mechanical checker must never call git push.'
}
foreach ($marker in @(
        "BaseRef = 'origin/main'",
        '[switch]$AsJson',
        'git diff check (base..HEAD)',
        'PowerShell AST parse',
        'release-preflight.ps1',
        "remoteReadiness = 'unverified'",
        'pushReadiness = if ($ready) { ''readyLocal'' }'
    )) {
    if ($source -notmatch [regex]::Escape($marker)) { throw "Checker is missing contract marker: $marker" }
}

Write-Host 'pre-push mechanical checker: syntax, decision and no-push contract passed' -ForegroundColor Green
