[CmdletBinding()]
param(
    # A VSTest TRX file (scripts/coverage/collect.ps1 writes gateway.trx).
    [Parameter(Mandatory = $true)][string]$TrxPath,
    [string]$BudgetPath = (Join-Path $PSScriptRoot 'test-time-budget.json'),
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,
    [int]$Top = 10
)

# CI time-budget guardrail for the dotnet lane. Fails when a test runs longer than
# dotnetTestSeconds and is not listed under exceptions.dotnet with a reason, and when
# an exception names a test that no longer exists (a stale entry would silently
# whitelist whatever later reuses the name). Always reports the slowest tests, so a
# suite creeping towards the budget is visible before it fails.

$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture

$budget = Get-Content -LiteralPath $BudgetPath -Raw | ConvertFrom-Json
$limit = [double]$budget.dotnetTestSeconds
if ($limit -le 0) { throw "Invalid dotnetTestSeconds in ${BudgetPath}: $($budget.dotnetTestSeconds)" }
$exceptions = @{}
foreach ($entry in @($budget.exceptions.dotnet.PSObject.Properties)) {
    if ([string]::IsNullOrWhiteSpace([string]$entry.Value)) { throw "Budget exception '$($entry.Name)' must give the reason it cannot be faster." }
    $exceptions[$entry.Name] = [string]$entry.Value
}

[xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
$results = @($trx.GetElementsByTagName('UnitTestResult') | ForEach-Object {
    # A Theory's cases share one entry: the name up to its argument list.
    $name = ([string]$_.testName -split '\(', 2)[0]
    [pscustomobject]@{ Name = $name; Seconds = [TimeSpan]::Parse([string]$_.duration).TotalSeconds }
})
if ($results.Count -eq 0) { throw "No test results in $TrxPath; the per-test budget cannot be checked." }

$lines = @("### Slowest tests ($(Split-Path $TrxPath -Leaf), budget ${limit}s per test)", '', '| Seconds | Test |', '| ---: | --- |')
foreach ($result in @($results | Sort-Object Seconds -Descending | Select-Object -First $Top)) {
    $lines += [string]::Format($invariant, '| {0:N1} | {1} |', $result.Seconds, $result.Name)
}
$total = ($results | Measure-Object Seconds -Sum).Sum
$lines += '', [string]::Format($invariant, '{0} tests, {1:N0}s in total.', $results.Count, $total)

$failures = @()
foreach ($result in @($results | Where-Object { $_.Seconds -gt $limit })) {
    if (-not $exceptions.ContainsKey($result.Name)) {
        $failures += [string]::Format($invariant, '{0} took {1:N1}s (budget {2}s)', $result.Name, $result.Seconds, $limit)
    }
}
$names = [System.Collections.Generic.HashSet[string]]::new([string[]]@($results.Name))
foreach ($name in $exceptions.Keys) {
    if (-not $names.Contains($name)) { $failures += "budget exception '$name' names no test in this run; remove it from $(Split-Path $BudgetPath -Leaf)" }
}

$lines | ForEach-Object { Write-Host $_ }
if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) { $lines | Out-File -LiteralPath $SummaryPath -Append -Encoding utf8 }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "::error::$_" }
    throw "CI time budget exceeded ($($failures.Count) finding(s) above). Make these tests faster (inject the clock or timeout, shrink iterations), or list them under exceptions.dotnet in $(Split-Path $BudgetPath -Leaf) with the reason they cannot be."
}
Write-Host "test-time-budget: every test is within ${limit}s"
