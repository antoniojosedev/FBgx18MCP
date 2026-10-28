$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ciScripts = Join-Path $root 'scripts/ci'
$ci = Get-Content -LiteralPath (Join-Path $root '.github/workflows/ci.yml') -Raw
$release = Get-Content -LiteralPath (Join-Path $root '.github/workflows/release.yml') -Raw

function Assert-Policy([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "CI workflow policy failed: $Message" }
}

# ---- Static workflow policy -------------------------------------------------

function Get-JobNames([string]$Workflow) {
    $jobs = $Workflow.Substring($Workflow.IndexOf("`njobs:`n"))
    @([regex]::Matches($jobs, '(?m)^  ([A-Za-z0-9_-]+):\s*$') | ForEach-Object { $_.Groups[1].Value })
}

function Get-JobBlock([string]$Workflow, [string]$Job) {
    $match = [regex]::Match($Workflow, "(?ms)^  $([regex]::Escape($Job)):\s*\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)")
    if (-not $match.Success) { return $null }
    return $match.Groups['body'].Value
}

function Get-JobNeeds([string]$Workflow, [string]$Job) {
    $block = Get-JobBlock $Workflow $Job
    if ($null -eq $block) { return $null }
    $needs = [regex]::Match($block, '(?m)^    needs:\s*\[(?<list>[^\]]*)\]\s*$')
    if (-not $needs.Success) { return @() }
    @($needs.Groups['list'].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# Every lane except the gate itself must gate `test`; a lane added or dropped without
# updating needs is the defect this catches (rust (gx-engine) was once left out).
function Test-GateDependencies([string]$Workflow) {
    $errors = @()
    $jobs = Get-JobNames $Workflow
    if ($jobs -notcontains 'test') { return @('job test is missing') }
    $needs = Get-JobNeeds $Workflow 'test'
    foreach ($job in ($jobs | Where-Object { $_ -ne 'test' })) {
        if ($needs -notcontains $job) { $errors += "test does not depend on $job" }
    }
    foreach ($need in $needs) {
        if ($jobs -notcontains $need) { $errors += "test depends on unknown job $need" }
    }
    $block = Get-JobBlock $Workflow 'test'
    if ($block -notmatch '(?m)^    if:\s*\$\{\{\s*!cancelled\(\)\s*\}\}\s*$') {
        $errors += 'test must run after a failed lane (if: !cancelled()); a skipped required check passes branch protection'
    }
    if ($block -notmatch 'assert-ci-gate\.ps1' -or $block -notmatch 'needs\.changes\.outputs\.sdk_evidence_required' -or $block -notmatch 'toJSON\(needs\)') {
        $errors += 'test must assert lane results and SDK evidence through assert-ci-gate.ps1'
    }
    return $errors
}

Assert-Policy (@(Test-GateDependencies $ci).Count -eq 0) ("gate dependencies: " + (@(Test-GateDependencies $ci) -join '; '))
foreach ($required in @('changes', 'dotnet', 'toolchain', 'rust', 'rust-engine', 'contracts', 'sdk-validation')) {
    Assert-Policy ((Get-JobNeeds $ci 'test') -contains $required) "test must depend on $required."
}

# Mutations: the checks above must be able to fail.
$withoutRustEngine = $ci -replace '(?m)^(    needs: \[[^\]]*?)\brust-engine, ', '$1'
Assert-Policy ($withoutRustEngine -ne $ci) 'mutation setup: rust-engine was not removed.'
Assert-Policy (@(Test-GateDependencies $withoutRustEngine) -contains 'test does not depend on rust-engine') 'missing rust-engine dependency was not detected.'
$wrongDependency = $ci -replace '(?m)^(    needs: \[[^\]]*?)\bcontracts\b', '$1contract'
Assert-Policy (@(Test-GateDependencies $wrongDependency) -contains 'test depends on unknown job contract') 'wrong dependency name was not detected.'
Assert-Policy (@(Test-GateDependencies $wrongDependency) -contains 'test does not depend on contracts') 'dropped contracts dependency was not detected.'
$noAlways = $ci -replace '(?m)^    if: \$\{\{ !cancelled\(\) \}\}\r?\n', ''
Assert-Policy (@(Test-GateDependencies $noAlways).Count -gt 0) 'missing !cancelled() on the gate was not detected.'
$withoutGate = $ci -replace 'assert-ci-gate\.ps1', 'removed.ps1'
Assert-Policy (@(Test-GateDependencies $withoutGate).Count -gt 0) 'removed SDK evidence assertion was not detected.'

# A pwsh step exits with the LAST native command's code, so a failing cargo command followed by a passing one
# leaves the step (and the lane) green. Every cargo command of a Rust lane therefore gets its own step, unless
# each one is followed by a $LASTEXITCODE check. The gxwire lane gates fmt and clippy like the gx-engine lane.
function Get-WorkflowSteps([string]$JobBlock) {
    $chunks = @([regex]::Split($JobBlock, '(?m)^      - (?=\w)') | Select-Object -Skip 1)
    foreach ($chunk in $chunks) {
        $lines = @($chunk -split '\r?\n')
        $name = [regex]::Match($chunk, '(?m)^name:\s*(.+?)\s*$').Groups[1].Value
        $run = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $start = [regex]::Match($lines[$i], '^ {8}run:\s*(?<value>.*?)\s*$')
            if (-not $start.Success) { continue }
            if ($start.Groups['value'].Value -eq '|') {
                for ($j = $i + 1; $j -lt $lines.Count -and ($lines[$j] -match '^ {10}' -or $lines[$j] -match '^\s*$'); $j++) { $run += $lines[$j].Trim() }
            } else {
                $run += $start.Groups['value'].Value
            }
            break
        }
        [pscustomobject]@{ Name = $name; Run = @($run | Where-Object { $_ }) }
    }
}

function Test-RustLanesFailClosed([string]$Workflow) {
    $errors = @()
    foreach ($job in @('rust', 'rust-engine')) {
        $block = Get-JobBlock $Workflow $job
        if ($null -eq $block) { $errors += "job $job is missing"; continue }
        $commands = @()
        foreach ($step in @(Get-WorkflowSteps $block)) {
            $cargo = @($step.Run | Where-Object { $_ -match '^cargo\s' })
            $checks = @($step.Run | Where-Object { $_ -match '\$LASTEXITCODE' })
            if ($cargo.Count -gt 1 -and $checks.Count -lt $cargo.Count) {
                $errors += "$job step '$($step.Name)' runs $($cargo.Count) cargo commands without a `$LASTEXITCODE check after each; a failing one is masked by the last"
            }
            $commands += $cargo
        }
        foreach ($pattern in @('^cargo fmt .*--check\b', '^cargo clippy .*--all-targets .*-- -D warnings\s*$', '^cargo test ')) {
            if (-not @($commands | Where-Object { $_ -match $pattern }).Count) { $errors += "$job does not run a cargo command matching $pattern" }
        }
        foreach ($command in @($commands | Where-Object { $_ -match '^cargo (clippy|test|build) ' -and $_ -notmatch '\s--locked\b' })) {
            $errors += "$job runs '$command' without --locked (Cargo.lock must not drift in CI)"
        }
    }
    return $errors
}

Assert-Policy (@(Test-RustLanesFailClosed $ci).Count -eq 0) ('Rust lanes: ' + (@(Test-RustLanesFailClosed $ci) -join '; '))

# Mutations: the Rust-lane checks above must be able to fail. The first is the workflow as it was before the fix.
$legacyRustEngineJob = @'
  rust-engine:
    name: rust (gx-engine)
    runs-on: windows-latest
    steps:
      - name: Lint and format gx-engine
        shell: pwsh
        run: |
          cargo clippy --manifest-path src/gx-engine/Cargo.toml --all-targets -- -D warnings
          cargo fmt --manifest-path src/gx-engine/Cargo.toml --check

      - name: Build and test gx-engine
        shell: pwsh
        run: |
          cargo test --manifest-path src/gx-engine/Cargo.toml
          cargo build --release --manifest-path src/gx-engine/Cargo.toml
          New-Item -ItemType Directory -Path publish -Force | Out-Null
'@
$legacyWorkflow = "jobs:`n  rust:`n    steps:`n      - name: x`n        run: cargo fmt --check`n`n$legacyRustEngineJob`n  contracts:`n    steps: []`n"
$legacyErrors = @(Test-RustLanesFailClosed $legacyWorkflow)
Assert-Policy (@($legacyErrors -like "*rust-engine step 'Lint and format gx-engine' runs 2 cargo commands*").Count -eq 1) 'the pre-fix chained clippy/fmt step was not detected.'
Assert-Policy (@($legacyErrors -like "*rust-engine step 'Build and test gx-engine' runs 2 cargo commands*").Count -eq 1) 'the pre-fix chained test/build step was not detected.'
$chainedWithChecks = $legacyWorkflow -replace '(?m)^(          cargo .*)$', "`$1`n          if (`$LASTEXITCODE -ne 0) { throw 'cargo failed' }"
Assert-Policy (-not @(Test-RustLanesFailClosed $chainedWithChecks | Where-Object { $_ -like '*cargo commands without*' })) 'a chained step that checks $LASTEXITCODE after each command must be accepted.'
$chainedTestAndDoc = $ci -replace '(?ms)^        run: (cargo nextest run --manifest-path src/gx-engine/Cargo.toml [^\r\n]*)\r?\n.*?^      - name: Test gx-engine doctests\r?\n        shell: pwsh\r?\n        run: (cargo test --doc [^\r\n]*)', "        run: |`n          `$1`n          `$2"
Assert-Policy ($chainedTestAndDoc -ne $ci) 'mutation setup: the test and doctest commands were not chained.'
Assert-Policy (@(Test-RustLanesFailClosed $chainedTestAndDoc | Where-Object { $_ -like '*cargo commands without*' }).Count -gt 0) 'chaining the current test and doctest commands was not detected.'
$gxwireWithoutClippy = $ci -replace '(?m)^        run: cargo clippy --manifest-path src/gxwire/Cargo.toml[^\r\n]*', '        run: Write-Host skipped'
Assert-Policy ($gxwireWithoutClippy -ne $ci) 'mutation setup: gxwire clippy was not removed.'
Assert-Policy (@(@(Test-RustLanesFailClosed $gxwireWithoutClippy) -like 'rust does not run a cargo command matching*clippy*').Count -eq 1) 'a missing gxwire clippy gate was not detected.'
$gxwireWithoutFmt = $ci -replace '(?m)^        run: cargo fmt --manifest-path src/gxwire/Cargo.toml --check', '        run: Write-Host skipped'
Assert-Policy (@(@(Test-RustLanesFailClosed $gxwireWithoutFmt) -like 'rust does not run a cargo command matching*fmt*').Count -eq 1) 'a missing gxwire fmt gate was not detected.'
$unlocked = $ci -replace ' --locked', ''
Assert-Policy (@(@(Test-RustLanesFailClosed $unlocked) -like '*without --locked*').Count -gt 0) 'cargo commands without --locked were not detected.'

$sdkJob = Get-JobBlock $ci 'sdk-validation'
Assert-Policy ($sdkJob -match 'runs-on:\s*\[self-hosted, windows, gx-sdk-18\]') 'the SDK lane must stay on the protected gx-sdk-18 runner.'
Assert-Policy ($sdkJob -match "GXMCP_SDK_CI_ENABLED == 'true'") 'the SDK lane must exist only while a runner is declared (no indefinite queue).'
Assert-Policy ($sdkJob -match 'github\.event\.pull_request\.head\.repo\.full_name == github\.repository') 'the licensed runner must never execute fork code.'
Assert-Policy ($sdkJob -match 'state: \$\{\{ steps\.status\.outputs\.state \}\}' -and $sdkJob -match 'commit: \$\{\{ steps\.status\.outputs\.commit \}\}') 'the SDK lane must export state and commit for the gate.'

$contracts = Get-JobBlock $ci 'contracts'
foreach ($step in @('run-release-script-tests.ps1', 'check-line-endings.ps1', 'check-build-warning-baseline.ps1 -ValidateOnly')) {
    Assert-Policy ($contracts.Contains($step)) "contracts lane must run $step."
}
# This policy runs inside the release script suite, once; the contracts lane no longer repeats it.
$suite = Get-Content -LiteralPath (Join-Path $root 'scripts/tests/run-release-script-tests.ps1') -Raw
Assert-Policy ($suite -match "(?m)^\s*'test-ci-workflow-policy\.ps1',?\s*$") 'the release script suite must run test-ci-workflow-policy.ps1.'
Assert-Policy ($contracts -notmatch '(?m)^    (if|needs):') 'contracts must always run: its line-ending check covers every changed file.'

# Lane selection: each skippable lane waits for `changes` and runs unless the classifier said
# exactly 'false', so a missing output runs the lane. The coverage threshold is enforced whenever
# the dotnet lane was required.
$lanePolicy = Get-Content -LiteralPath (Join-Path $ciScripts 'lane-paths.json') -Raw | ConvertFrom-Json
$laneOutputs = [ordered]@{}
foreach ($lane in @($lanePolicy.lanes.PSObject.Properties)) { $laneOutputs[$lane.Name] = [string]$lane.Value }

function Test-LaneSelection([string]$Workflow) {
    $errors = @()
    $changesJob = Get-JobBlock $Workflow 'changes'
    if ($null -eq $changesJob -or $changesJob -notmatch 'classify-lanes\.ps1') { $errors += 'changes must run classify-lanes.ps1' }
    foreach ($lane in $laneOutputs.Keys) {
        $output = $laneOutputs[$lane]
        if ($null -ne $changesJob -and $changesJob -notmatch "(?m)^      $([regex]::Escape($output)): \`$\{\{ steps\.lanes\.outputs\.$([regex]::Escape($output)) \}\}\s*$") {
            $errors += "changes does not export $output"
        }
        $block = Get-JobBlock $Workflow $lane
        if ($null -eq $block) { $errors += "job $lane is missing"; continue }
        if ((Get-JobNeeds $Workflow $lane) -notcontains 'changes') { $errors += "$lane must need changes" }
        if ($block -notmatch "(?m)^    if: \`$\{\{ needs\.changes\.outputs\.$([regex]::Escape($output)) != 'false' \}\}\s*$") {
            $errors += "$lane must run unless needs.changes.outputs.$output is exactly 'false'"
        }
    }
    $gate = Get-JobBlock $Workflow 'test'
    $runDotnet = "if: \`$\{\{ needs\.changes\.outputs\.$($laneOutputs['dotnet']) != 'false' \}\}"
    foreach ($step in @('Download coverage artifacts', 'Assert combined coverage threshold')) {
        if ($null -eq $gate -or $gate -notmatch "(?m)^      - name: $([regex]::Escape($step))\s*\n        $runDotnet\s*$") {
            $errors += "gate step '$step' must run exactly when the dotnet lane was required"
        }
    }
    return $errors
}

Assert-Policy (@(Test-LaneSelection $ci).Count -eq 0) ('lane selection: ' + (@(Test-LaneSelection $ci) -join '; '))
Assert-Policy (@($laneOutputs.Keys) -join ',' -eq 'dotnet,toolchain,rust,rust-engine') "lane-paths.json must classify exactly the skippable lanes, got $(@($laneOutputs.Keys) -join ',')."
foreach ($rule in @($lanePolicy.rules)) {
    foreach ($lane in @($rule.lanes)) {
        Assert-Policy ($lane -eq 'all' -or $laneOutputs.Contains([string]$lane)) "lane rule '$($rule.reason)' names unknown lane '$lane'."
    }
}

# Mutations: the lane-selection checks above must be able to fail.
$dotnetRunsOnlyOnTrue = $ci -replace "needs\.changes\.outputs\.run_dotnet != 'false' \}\}(\s*\n    runs-on)", "needs.changes.outputs.run_dotnet == 'true' }}`$1"
Assert-Policy ($dotnetRunsOnlyOnTrue -ne $ci) 'mutation setup: the dotnet condition was not changed.'
Assert-Policy (@(@(Test-LaneSelection $dotnetRunsOnlyOnTrue) -like "dotnet must run unless*").Count -eq 1) "a dotnet lane that skips on a missing classification was not detected."
$toolchainWithoutNeeds = $ci -replace "(?m)^    needs: \[changes\]\r?\n(    if: \`$\{\{ needs\.changes\.outputs\.run_toolchain)", '$1'
Assert-Policy ($toolchainWithoutNeeds -ne $ci) 'mutation setup: the toolchain needs was not removed.'
Assert-Policy (@(@(Test-LaneSelection $toolchainWithoutNeeds) -like 'toolchain must need changes').Count -eq 1) 'a lane that does not wait for changes was not detected.'
$unexportedRust = $ci -replace '(?m)^      run_rust: [^\r\n]*\r?\n', ''
Assert-Policy (@(@(Test-LaneSelection $unexportedRust) -like 'changes does not export run_rust').Count -eq 1) 'an unexported lane decision was not detected.'
$unconditionalCoverage = $ci -replace "(?m)(^      - name: Assert combined coverage threshold\r?\n)        if: [^\r\n]*\r?\n", '$1'
Assert-Policy ($unconditionalCoverage -ne $ci) 'mutation setup: the coverage condition was not removed.'
Assert-Policy (@(@(Test-LaneSelection $unconditionalCoverage) -like "*Assert combined coverage threshold*").Count -eq 1) 'a coverage step not tied to the dotnet decision was not detected.'
$gatedContracts = Get-JobBlock ($ci -replace "(?m)^(    name: contracts \(python \+ docs\)\r?\n)", "`$1    if: `${{ needs.changes.outputs.run_dotnet != 'false' }}`n") 'contracts'
Assert-Policy ($gatedContracts -match '(?m)^    (if|needs):') 'mutation: a conditional contracts lane was not detected.'

# The lane map must agree with what each lane reads. A lane's sources must map to it, and for a
# scanned lane every repository file its sources name must map to it too, or a change to that file
# would skip a lane that reads it. Names are matched as whole tokens; a name several files share
# (README.md, Cargo.toml) counts only with its parent directory among the six tokens before it.
# This over-approximates on purpose: a false match costs a lane run, a missed one a skipped test.
function Get-LaneRule([object]$Policy, [string]$Path) {
    foreach ($rule in @($Policy.rules)) {
        if (@($rule.patterns | Where-Object { $Path -like $_ }).Count -gt 0) { return $rule }
    }
    return $null
}

$trackedFiles = @(& git -C $root ls-files)
Assert-Policy ($LASTEXITCODE -eq 0 -and $trackedFiles.Count -gt 1000) 'git ls-files must list the repository for the lane-map evidence check.'
$filesByName = $trackedFiles | Group-Object { (Split-Path $_ -Leaf).ToLowerInvariant() } -AsHashTable
$scanExtensions = '\.(cs|csproj|props|targets|sln|ps1|psm1|py|js|mjs|cjs|ts|json|runsettings|xml|config)$'

# Tokenizes a source and records, for every token that is a tracked file name, the six tokens
# before it. Compiled because the dotnet sources hold millions of tokens: the same loop in
# PowerShell took about 20 of this script's 50 seconds.
if (-not ('LaneEvidenceTokens' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public static class LaneEvidenceTokens
{
    private static readonly Regex Token = new Regex(@"[A-Za-z0-9_.\-]+", RegexOptions.Compiled);

    public static void Collect(string text, IDictionary fileNames, IDictionary context)
    {
        var window = new string[6];
        int seen = 0;
        foreach (Match match in Token.Matches(text))
        {
            string token = match.Value.TrimEnd('.').ToLowerInvariant();
            if (fileNames.Contains(token))
            {
                var before = context[token] as HashSet<string>;
                if (before == null) { before = new HashSet<string>(); context[token] = before; }
                for (int back = Math.Min(seen, window.Length); back >= 1; back--) before.Add(window[(seen - back) % window.Length]);
            }
            window[seen % window.Length] = token;
            seen++;
        }
    }
}
'@
}

function Get-LaneReads([object]$Policy, [string]$Lane) {
    $evidence = $Policy.evidence.$Lane
    $sources = @($trackedFiles | Where-Object { $file = $_; @($evidence.sources | Where-Object { $file -like $_ }).Count -gt 0 })
    $reads = [System.Collections.Generic.HashSet[string]]::new([string[]]$sources)
    # The leading comma keeps PowerShell from unrolling the set into an array.
    if (-not $evidence.scan) { return , $reads }
    $context = @{}
    foreach ($source in @($sources | Where-Object { $_ -match $scanExtensions })) {
        $text = [IO.File]::ReadAllText((Join-Path $root $source))
        # Relative references such as an MSBuild Include="..\..\.gemini\skills\nexa\references\*.md"
        # name no single file, so resolve them (wildcards included) against the source's directory.
        foreach ($match in [regex]::Matches($text, '(?:\.\.[\\/])+[A-Za-z0-9_.\-\\/*]+')) {
            $full = [IO.Path]::GetFullPath((Join-Path (Join-Path $root (Split-Path $source -Parent)) $match.Value))
            if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $full.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            foreach ($file in $trackedFiles) { if ($file -like $relative) { [void]$reads.Add($file) } }
        }
        [LaneEvidenceTokens]::Collect($text, $filesByName, $context)
    }
    foreach ($name in $context.Keys) {
        foreach ($file in @($filesByName[$name])) {
            $directory = Split-Path $file -Parent
            if (@($filesByName[$name]).Count -gt 1 -and $directory -and -not $context[$name].Contains((Split-Path $directory -Leaf).ToLowerInvariant())) { continue }
            [void]$reads.Add($file)
        }
    }
    return , $reads
}

$laneReads = @{}
foreach ($lane in $laneOutputs.Keys) {
    Assert-Policy ($null -ne $lanePolicy.evidence.$lane -and @($lanePolicy.evidence.$lane.sources).Count -gt 0) "lane-paths.json has no evidence sources for $lane."
    $laneReads[$lane] = Get-LaneReads $lanePolicy $lane
}
Assert-Policy ($laneReads['dotnet'].Contains('docs/kb-isolation-contract.md') -and $laneReads['toolchain'].Contains('docs/engine_backend.md')) 'the evidence scan must find documents the tests read.'
Assert-Policy (-not $laneReads['dotnet'].Contains('src/nexus-ide/package.json')) 'a shared file name must not match without its parent directory.'

function Test-LaneMap([object]$Policy) {
    $errors = @()
    foreach ($lane in $laneOutputs.Keys) {
        foreach ($file in $laneReads[$lane]) {
            $rule = Get-LaneRule $Policy $file
            if ($null -eq $rule -or @($rule.lanes) -contains 'all' -or @($rule.lanes) -contains $lane) { continue }
            $errors += "$file is read by $lane but its rule '$($rule.reason)' skips $lane"
        }
    }
    return $errors
}

Assert-Policy (@(Test-LaneMap $lanePolicy).Count -eq 0) ("lane-paths.json drops lanes that read a file:`n  " + (@(Test-LaneMap $lanePolicy) -join "`n  "))

# Mutations: the evidence check must catch a rule that drops a reading lane.
function Copy-LanePolicy { Get-Content -LiteralPath (Join-Path $ciScripts 'lane-paths.json') -Raw | ConvertFrom-Json }
$withoutDotnetDocs = Copy-LanePolicy
foreach ($rule in @($withoutDotnetDocs.rules)) { $rule.patterns = @($rule.patterns | Where-Object { $_ -ne 'docs/kb-isolation-contract.md' }) }
Assert-Policy (@(@(Test-LaneMap $withoutDotnetDocs) -like 'docs/kb-isolation-contract.md is read by dotnet*').Count -eq 1) 'a document the Gateway tests read, mapped to toolchain only, was not detected.'
$withoutToolDefinitions = Copy-LanePolicy
foreach ($rule in @($withoutToolDefinitions.rules)) { $rule.patterns = @($rule.patterns | Where-Object { $_ -ne 'src/GxMcp.Gateway/tool_definitions.json' }) }
Assert-Policy (@(@(Test-LaneMap $withoutToolDefinitions) -like 'src/GxMcp.Gateway/tool_definitions.json is read by toolchain*').Count -eq 1) 'the tool schema the CLI tests read, mapped to dotnet only, was not detected.'
$gxwireOnlyRust = Copy-LanePolicy
($gxwireOnlyRust.rules | Where-Object { $_.patterns -contains 'src/gxwire/*' }).lanes = @('rust')
Assert-Policy (@(@(Test-LaneMap $gxwireOnlyRust) -like 'src/gxwire/* is read by dotnet*').Count -gt 0) 'gxwire mapped away from the dotnet lane that builds it was not detected.'
$withoutEmbeddedSkills = Copy-LanePolicy
foreach ($rule in @($withoutEmbeddedSkills.rules)) { $rule.patterns = @($rule.patterns | Where-Object { $_ -ne '.gemini/skills/nexa/*' }) }
Assert-Policy (@(@(Test-LaneMap $withoutEmbeddedSkills) -like '.gemini/skills/nexa/references/common-collections.md is read by dotnet*').Count -eq 1) 'a skill the Gateway embeds through a csproj wildcard, mapped to no lane, was not detected.'
$engineWithoutCrateLane = Copy-LanePolicy
($engineWithoutCrateLane.rules | Where-Object { $_.patterns -contains 'src/gx-engine/*' }).lanes = @('dotnet')
Assert-Policy (@(@(Test-LaneMap $engineWithoutCrateLane) -like 'src/gx-engine/* is read by rust-engine*').Count -gt 0) 'gx-engine sources mapped away from their crate lane were not detected.'
$sdkScript = Get-Content -LiteralPath (Join-Path $ciScripts 'sdk-validation.ps1') -Raw
Assert-Policy ($sdkScript -match 'check-build-warning-baseline\.ps1' -and $sdkScript -notmatch '-ValidateOnly') 'the SDK lane must compare real Release warning locations, not only validate the manifest.'

foreach ($workflowFile in Get-ChildItem -LiteralPath (Join-Path $root '.github/workflows') -Filter '*.yml') {
    $text = Get-Content -LiteralPath $workflowFile.FullName -Raw
    Assert-Policy ($text -notmatch '(?m)^\s*pull_request_target\s*:') "$($workflowFile.Name) must not use pull_request_target (fork code with a privileged token)."
    Assert-Policy ($text -notmatch '(?m)^\s*continue-on-error\s*:') "$($workflowFile.Name) must not mask failures with continue-on-error."
}

# A pwsh step exits with the code of the LAST native command, so a failing native command followed
# by a passing one leaves the step green (the gx-engine lane once ran 8 clippy errors and a failed
# test green). Every native command of a multi-command step must be followed by an explicit
# $LASTEXITCODE check; a step with a single native command needs none.
function Get-WorkflowSteps([string]$Workflow) {
    @([regex]::Split($Workflow, '(?m)^      - (?=name:|uses:)') | Select-Object -Skip 1)
}

function Get-StepRunLines([string]$Step) {
    $lines = @($Step -split '\r?\n')
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^        run:\s*(?<inline>.*)$') { continue }
        $inline = $Matches['inline'].Trim()
        $body = @()
        if ($inline -and $inline -notmatch '^[|>][-+]?$') { $body += $inline }
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim() -and $lines[$j] -notmatch '^          ') { break }
            $body += $lines[$j].Trim()
        }
        return @($body | Where-Object { $_ -and -not $_.StartsWith('#') })
    }
    return @()
}

function Test-NativeCommandExitCodes([string]$Workflow) {
    $errors = @()
    foreach ($step in (Get-WorkflowSteps $Workflow)) {
        $name = [regex]::Match($step, '(?m)^name:\s*(?<n>.+?)\s*$').Groups['n'].Value
        $lines = @(Get-StepRunLines $step)
        $native = @(for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^(cargo|python|dotnet|npm|node)(\.exe)?\s') { $i }
        })
        foreach ($index in ($native | Select-Object -SkipLast 1)) {
            $next = if ($index + 1 -lt $lines.Count) { $lines[$index + 1] } else { '' }
            if ($next -notmatch '\$LASTEXITCODE') {
                $errors += "step '$name' runs '$($lines[$index])' without checking `$LASTEXITCODE before the next command"
            }
        }
    }
    return $errors
}

Assert-Policy (@(Get-WorkflowSteps $ci).Count -gt 20) 'the step parser must find the workflow steps.'
Assert-Policy (@(Test-NativeCommandExitCodes $ci).Count -eq 0) ("native command exit codes: " + (@(Test-NativeCommandExitCodes $ci) -join '; '))
Assert-Policy ($ci -match "(?m)^        run: cargo test --manifest-path src/gxwire/Cargo.toml( --locked)?\s*$") 'the gxwire lane must run cargo test as a step of its own.'
# gx-engine runs its tests through nextest (parallel across test binaries, per-test time
# limit) and its doctests, which nextest skips, through cargo test --doc.
Assert-Policy ($ci -match '(?m)^        run: cargo nextest run --manifest-path src/gx-engine/Cargo.toml --locked --profile ci\s*$') 'the gx-engine lane must run cargo nextest with the ci profile as a step of its own.'
Assert-Policy ($ci -match '(?m)^        run: cargo test --doc --manifest-path src/gx-engine/Cargo.toml --locked\s*$') 'the gx-engine lane must run its doctests as a step of its own.'

# ---- CI time budget ---------------------------------------------------------
# CI wall time is a budget (CODING_STANDARDS.md, CI time budget). Every job declares a
# ceiling, and each test lane enforces a per-test limit, so a slow or hung test fails
# where it was added instead of silently growing every later run.
function Test-CiTimeBudget([string]$Workflow, [string]$NextestConfig) {
    $errors = @()
    foreach ($job in (Get-JobNames $Workflow)) {
        $block = Get-JobBlock $Workflow $job
        $timeout = [regex]::Match($block, '(?m)^    timeout-minutes:\s*(?<m>\d+)\s*$')
        if (-not $timeout.Success) { $errors += "job $job declares no timeout-minutes"; continue }
        $hosted = $block -notmatch '(?m)^    runs-on:\s*\[self-hosted'
        if ($hosted -and [int]$timeout.Groups['m'].Value -gt 30) { $errors += "hosted job $job allows $($timeout.Groups['m'].Value) minutes; the ceiling is 30" }
    }
    $dotnet = Get-JobBlock $Workflow 'dotnet'
    if ($null -eq $dotnet -or $dotnet -notmatch 'assert-test-durations\.ps1') { $errors += 'the dotnet lane must assert the per-test time budget (assert-test-durations.ps1)' }
    if ($NextestConfig -notmatch '(?ms)^\[profile\.ci\].*?^slow-timeout\s*=\s*\{[^}]*terminate-after\s*=\s*\d+') { $errors += 'the nextest ci profile must kill a test that exceeds its slow-timeout (terminate-after)' }
    return $errors
}

$nextestConfig = Get-Content -LiteralPath (Join-Path $root 'src/gx-engine/.config/nextest.toml') -Raw
Assert-Policy (@(Test-CiTimeBudget $ci $nextestConfig).Count -eq 0) ('CI time budget: ' + (@(Test-CiTimeBudget $ci $nextestConfig) -join '; '))
$timeBudget = Get-Content -LiteralPath (Join-Path $ciScripts 'test-time-budget.json') -Raw | ConvertFrom-Json
Assert-Policy ($timeBudget.dotnetTestSeconds -gt 0 -and $timeBudget.dotnetTestSeconds -le 30 -and $timeBudget.releaseScriptSeconds -gt 0 -and $timeBudget.releaseScriptSeconds -le 180) 'test-time-budget.json budgets must stay positive and tight (dotnet <= 30s per test, release scripts <= 180s each).'
foreach ($group in @('dotnet', 'releaseScripts')) {
    foreach ($entry in @($timeBudget.exceptions.$group.PSObject.Properties)) {
        Assert-Policy (([string]$entry.Value).Trim().Length -ge 20) "time budget exception '$($entry.Name)' must explain why it cannot be faster."
    }
}

# Mutations: the time-budget checks above must be able to fail.
$noTimeout = $ci -replace '(?m)^    timeout-minutes: 5\r?\n', ''
Assert-Policy ($noTimeout -ne $ci) 'mutation setup: a job timeout was not removed.'
Assert-Policy (@(@(Test-CiTimeBudget $noTimeout $nextestConfig) -like 'job changes declares no timeout-minutes').Count -eq 1) 'a job without timeout-minutes was not detected.'
$longTimeout = $ci -replace '(?m)^    timeout-minutes: 20$', '    timeout-minutes: 90'
Assert-Policy (@(@(Test-CiTimeBudget $longTimeout $nextestConfig) -like 'hosted job dotnet allows 90*').Count -eq 1) 'a hosted job above the 30-minute ceiling was not detected.'
$noDurationGate = $ci -replace 'assert-test-durations\.ps1', 'removed.ps1'
Assert-Policy (@(@(Test-CiTimeBudget $noDurationGate $nextestConfig) -like '*assert-test-durations*').Count -eq 1) 'a dotnet lane without the per-test budget was not detected.'
$noTerminate = $nextestConfig -replace 'terminate-after\s*=\s*\d+', ''
Assert-Policy (@(@(Test-CiTimeBudget $ci $noTerminate) -like '*terminate-after*').Count -eq 1) 'a nextest profile that never kills a slow test was not detected.'
Assert-Policy ($ci -match '(?m)^        run: cargo clippy --manifest-path src/gx-engine/Cargo.toml --all-targets( --locked)? -- -D warnings\s*$') 'the gx-engine lint must be a step of its own (-D warnings).'
$mergedCargo = [regex]::Replace($ci, '(?m)^        run: (cargo clippy [^\r\n]+)\r?\n', [System.Text.RegularExpressions.MatchEvaluator] {
    param($match)
    "        run: |`n          $($match.Groups[1].Value)`n          cargo fmt --manifest-path src/gx-engine/Cargo.toml --check`n"
})
Assert-Policy ($mergedCargo -ne $ci) 'mutation setup: clippy and fmt were not merged.'
Assert-Policy (@(Test-NativeCommandExitCodes $mergedCargo).Count -gt 0) 'two cargo commands in one step without an exit-code check were not detected.'
$guardedCargo = $mergedCargo -replace '(?m)^(          cargo clippy [^\r\n]+)\r?\n', "`$1`n          if (`$LASTEXITCODE -ne 0) { throw 'clippy failed' }`n"
Assert-Policy ($guardedCargo -ne $mergedCargo) 'mutation setup: the exit-code check was not added.'
Assert-Policy (@(Test-NativeCommandExitCodes $guardedCargo).Count -eq 0) 'an explicit $LASTEXITCODE check after the first cargo command must satisfy the rule.'

# Publication workflow: gate precedes every trust decision, and nothing can bypass it.
$gateIndex = $release.IndexOf('name: Verify release commit identity and required checks')
Assert-Policy ($gateIndex -ge 0) 'release.yml must verify the release commit identity and required checks.'
foreach ($later in @('name: Upgrade npm for Trusted Publishing', 'name: Download release artifacts', 'name: Publish to npm with provenance')) {
    Assert-Policy ($release.IndexOf($later) -gt $gateIndex) "the gate must precede '$later'."
}
Assert-Policy ($release.IndexOf('name: Resolve release commit') -lt $gateIndex) 'the gate must run on the resolved release commit.'
Assert-Policy ($release -match 'verify-release-commit\.ps1') 'release.yml must call verify-release-commit.ps1.'
Assert-Policy ($release -match '(?m)^  checks: read\s*$') 'release.yml needs checks: read to read the commit check runs.'
Assert-Policy ($release -match '(?ms)fetch-depth:\s*0') 'the release checkout needs full history for the main-ancestry proof.'
$dispatchInputs = [regex]::Match($release, '(?ms)workflow_dispatch:\s*\n\s*inputs:\s*\n(?<body>.*?)(?=^permissions:)').Groups['body'].Value
$inputNames = @([regex]::Matches($dispatchInputs, '(?m)^      ([A-Za-z0-9_-]+):\s*$') | ForEach-Object { $_.Groups[1].Value })
Assert-Policy ($inputNames.Count -gt 0 -and (@($inputNames | Where-Object { $_ -ne 'tag' }).Count -eq 0)) "workflow_dispatch may only accept the tag (found: $($inputNames -join ', ')); no skip/force/bypass input."
$gateBlock = $release.Substring($gateIndex, $release.IndexOf('name: Upgrade npm for Trusted Publishing') - $gateIndex)
Assert-Policy ($gateBlock -notmatch '(?m)^\s*if:') 'the release gate step must be unconditional.'
$verifier = Get-Content -LiteralPath (Join-Path $ciScripts 'verify-release-commit.ps1') -Raw
Assert-Policy ($verifier -notmatch '(?i)\b(skip|bypass|force|override)\b\s*[\]=)]') 'the release verifier must not expose a bypass switch.'

# ---- Behavior ---------------------------------------------------------------

# Runs a CI script in this process, with the exit code a `pwsh -File` child would report
# (its `exit N`, or 1 for an uncaught error). About 55 cases used to start a pwsh each,
# which made this the slowest release script (40-95s under the parallel suite's load).
# Strict mode is switched off for the callee, as in a fresh pwsh.
function Invoke-ScriptInProcess([string]$Path, [hashtable]$Parameters) {
    $global:LASTEXITCODE = 0
    try {
        $output = & { param($ScriptPath, $Splat) Set-StrictMode -Off; & $ScriptPath @Splat } $Path $Parameters *>&1 | Out-String
        $exitCode = $global:LASTEXITCODE
    } catch {
        $output = $_ | Out-String
        $exitCode = 1
    }
    [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-CiScript([string]$Script, [hashtable]$Arguments) {
    $parameters = @{}
    foreach ($key in $Arguments.Keys) { $parameters[$key] = [string]$Arguments[$key] }
    Invoke-ScriptInProcess (Join-Path $ciScripts $Script) $parameters
}

# Splits each comma-separated argument into an array, as `-Name 'a','b'` would pass it.
function ConvertTo-ArrayParameters([hashtable]$Arguments, [string]$OutputFile) {
    $parameters = @{ GitHubOutput = $OutputFile }
    foreach ($key in $Arguments.Keys) {
        # A single value stays a string: an advanced script's [string] parameter refuses an array.
        $values = [string[]]@([string]$Arguments[$key] -split ',')
        $parameters[$key] = if ($values.Count -eq 1) { $values[0] } else { $values }
    }
    $parameters
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-ci-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # classify-sdk-evidence.ps1
    function Assert-Classification([string]$Name, [hashtable]$Arguments, [string]$Expected) {
        $outputFile = Join-Path $work "$($Name -replace '\W', '_').out"
        $result = Invoke-ScriptInProcess (Join-Path $ciScripts 'classify-sdk-evidence.ps1') (ConvertTo-ArrayParameters $Arguments $outputFile)
        Assert-Policy ($result.ExitCode -eq 0) "classify '$Name' exited $($result.ExitCode): $($result.Output)"
        $written = Get-Content -LiteralPath $outputFile -Raw
        Assert-Policy ($written -match "(?m)^sdk_evidence_required=$Expected\s*$") "classify '$Name' expected $Expected, got: $written"
    }
    Assert-Classification 'default-flip' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/EngineBackendSettings.cs,docs/x.md' } 'true'
    Assert-Classification 'registry' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/EngineCapabilityRegistry.cs' } 'true'
    Assert-Classification 'client' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/GxEngineClient.cs' } 'true'
    Assert-Classification 'configuration' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/Configuration.cs' } 'true'
    Assert-Classification 'backslash-path' @{ EventName = 'pull_request'; ChangedFile = 'src\GxMcp.Gateway\EngineRoutedOperations.cs' } 'true'
    Assert-Classification 'unrelated' @{ EventName = 'pull_request'; ChangedFile = 'README.md,src/GxMcp.Worker/Services/PropertyService.cs,src/GxMcp.Gateway.Tests/EngineBackendTests.cs' } 'false'
    Assert-Classification 'no-event' @{ EventName = 'workflow_dispatch'; BaseSha = 'a'; HeadSha = 'b' } 'true'
    Assert-Classification 'missing-shas' @{ EventName = 'pull_request' } 'true'
    Assert-Classification 'diff-failure' @{ EventName = 'pull_request'; BaseSha = ('0' * 40); HeadSha = ('1' * 40) } 'true'

    # classify-lanes.ps1: expected lanes are the ones that must run; every other lane is skipped.
    function Assert-LaneClassification([string]$Name, [hashtable]$Arguments, [string[]]$Expected, [string]$ExpectedReason = '') {
        $outputFile = Join-Path $work "lanes_$($Name -replace '\W', '_').out"
        $result = Invoke-ScriptInProcess (Join-Path $ciScripts 'classify-lanes.ps1') (ConvertTo-ArrayParameters $Arguments $outputFile)
        Assert-Policy ($result.ExitCode -eq 0) "lanes '$Name' exited $($result.ExitCode): $($result.Output)"
        $written = Get-Content -LiteralPath $outputFile -Raw
        $runAll = $Expected -contains 'all'
        foreach ($lane in $laneOutputs.Keys) {
            $want = ($runAll -or $Expected -contains $lane).ToString().ToLowerInvariant()
            Assert-Policy ($written -match "(?m)^$($laneOutputs[$lane])=$want\s*$") "lanes '$Name' expected $($laneOutputs[$lane])=$want, got: $written"
        }
        if ($ExpectedReason) { Assert-Policy ($written.Contains($ExpectedReason)) "lanes '$Name' reason lacks '$ExpectedReason': $written" }
    }
    $pr = @{ EventName = 'pull_request' }
    function With-Files([string]$Files) { @{ EventName = 'pull_request'; ChangedFile = $Files } }
    Assert-LaneClassification 'docs-only' (With-Files 'docs/agent_playbook.md,docs/superpowers/specs/x.md') @('toolchain')
    Assert-LaneClassification 'docs-read-by-gateway-tests' (With-Files 'README.md,CHANGELOG.md,docs/kb-isolation-contract.md') @('dotnet', 'toolchain')
    Assert-LaneClassification 'plans-only' (With-Files 'plans/001-index-flush-count-regression-test.md') @()
    Assert-LaneClassification 'cli-only' (With-Files 'cli/run.js,cli/run.test.js') @('dotnet', 'toolchain')
    Assert-LaneClassification 'nexus-ide-only' (With-Files 'src/nexus-ide/src/extension.ts') @('toolchain')
    Assert-LaneClassification 'gxwire-only' (With-Files 'src/gxwire/src/main.rs,src/gxwire/Cargo.lock') @('rust', 'dotnet')
    Assert-LaneClassification 'gx-engine-only' (With-Files 'src/gx-engine/src/lib.rs') @('rust-engine', 'dotnet')
    Assert-LaneClassification 'gx-engine-readme' (With-Files 'src/gx-engine/README.md') @('rust-engine', 'dotnet', 'toolchain')
    Assert-LaneClassification 'csharp-only' (With-Files 'src/GxMcp.Worker/Services/PropertyService.cs,src/GxMcp.Gateway.Tests/EngineBackendTests.cs') @('dotnet')
    Assert-LaneClassification 'tool-definitions' (With-Files 'src/GxMcp.Gateway/tool_definitions.json') @('dotnet', 'toolchain')
    Assert-LaneClassification 'backslash-path' (With-Files 'src\gxwire\src\main.rs') @('rust', 'dotnet')
    Assert-LaneClassification 'workflow-change' (With-Files 'docs/agent_playbook.md,.github/workflows/ci.yml') @('all') 'changes how every lane runs'
    Assert-LaneClassification 'ci-script-change' (With-Files 'scripts/ci/assert-ci-gate.ps1') @('all') 'changes how every lane runs'
    Assert-LaneClassification 'lane-map-change' (With-Files 'scripts/ci/lane-paths.json') @('all')
    Assert-LaneClassification 'unmapped-path' (With-Files 'plans/001-index-flush-count-regression-test.md,brand-new/file.txt') @('all') "no lane rule maps 'brand-new/file.txt'"
    Assert-LaneClassification 'empty-diff' (With-Files '') @('all') 'lists no changed files'
    Assert-LaneClassification 'lanes-no-event' @{ EventName = 'workflow_dispatch'; BaseSha = 'a'; HeadSha = 'b' } @('all') 'no pull-request diff'
    Assert-LaneClassification 'lanes-missing-shas' $pr @('all') 'SHA is unavailable'
    Assert-LaneClassification 'lanes-diff-failure' @{ EventName = 'pull_request'; BaseSha = ('0' * 40); HeadSha = ('1' * 40) } @('all') 'failed'

    # A move must classify both sides: rename detection would list only the new, unrelated path.
    $renameRepo = Join-Path $work 'rename-repo'
    New-Item -ItemType Directory -Path (Join-Path $renameRepo 'src/GxMcp.Gateway') -Force | Out-Null
    function Invoke-RenameGit([string[]]$Arguments) {
        $output = & git -C $renameRepo -c user.name=policy -c user.email=policy@example.invalid @Arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $output" }
        return $output.Trim()
    }
    Invoke-RenameGit @('init', '-q', '-b', 'main') | Out-Null
    (1..40 | ForEach-Object { "internal static class EngineRouting$_ { }" }) | Set-Content -LiteralPath (Join-Path $renameRepo 'src/GxMcp.Gateway/EngineRouting.cs')
    Invoke-RenameGit @('add', '.') | Out-Null
    Invoke-RenameGit @('commit', '-q', '-m', 'base') | Out-Null
    $renameBase = Invoke-RenameGit @('rev-parse', 'HEAD')
    New-Item -ItemType Directory -Path (Join-Path $renameRepo 'plans') -Force | Out-Null
    Invoke-RenameGit @('mv', 'src/GxMcp.Gateway/EngineRouting.cs', 'plans/engine-routing.md') | Out-Null
    Invoke-RenameGit @('commit', '-q', '-m', 'move') | Out-Null
    $renameHead = Invoke-RenameGit @('rev-parse', 'HEAD')
    Assert-Policy ((Invoke-RenameGit @('diff', '--name-only', "$renameBase...$renameHead")) -eq 'plans/engine-routing.md') 'mutation setup: git must detect the move as a rename.'
    $renameArguments = @{ EventName = 'pull_request'; BaseSha = $renameBase; HeadSha = $renameHead; Repository = $renameRepo }
    Assert-LaneClassification 'rename-out-of-dotnet' $renameArguments @('dotnet')
    Assert-Classification 'rename-out-of-engine-routing' $renameArguments 'true'

    # assert-ci-gate.ps1
    $commit = 'a' * 40
    function New-NeedsJson([hashtable]$Overrides = @{}, [string]$SdkResult = 'skipped', [string]$SdkState = '', [string]$SdkCommit = '', [hashtable]$Decisions = @{}) {
        $needs = [ordered]@{}
        foreach ($lane in @('changes', 'dotnet', 'toolchain', 'rust', 'rust-engine', 'contracts')) {
            $needs[$lane] = @{ result = $(if ($Overrides.ContainsKey($lane)) { $Overrides[$lane] } else { 'success' }); outputs = @{} }
        }
        foreach ($lane in $laneOutputs.Keys) {
            $output = $laneOutputs[$lane]
            $needs['changes'].outputs[$output] = 'true'
        }
        foreach ($output in $Decisions.Keys) { $needs['changes'].outputs[$output] = $Decisions[$output] }
        $needs['changes'].outputs['lanes_reason'] = 'test classification'
        $needs['sdk-validation'] = @{ result = $SdkResult; outputs = @{ state = $SdkState; commit = $SdkCommit } }
        $needs | ConvertTo-Json -Depth 5 -Compress
    }
    function Assert-Gate([string]$Name, [string]$NeedsJson, [hashtable]$Arguments, [int]$ExpectedExit, [string]$ExpectedText = '') {
        $summary = Join-Path $work "$($Name -replace '\W', '_').summary"
        $all = @{ NeedsJson = $NeedsJson; EventName = 'pull_request'; Repository = 'owner/repo'; HeadRepository = 'owner/repo'; ExpectedCommit = $commit; SummaryPath = $summary }
        foreach ($key in $Arguments.Keys) { $all[$key] = $Arguments[$key] }
        $result = Invoke-CiScript 'assert-ci-gate.ps1' $all
        Assert-Policy ($result.ExitCode -eq $ExpectedExit) "gate '$Name' expected exit $ExpectedExit, got $($result.ExitCode): $($result.Output)"
        if ($ExpectedText) { Assert-Policy ($result.Output.Contains($ExpectedText)) "gate '$Name' output lacks '$ExpectedText': $($result.Output)" }
    }
    $passLane = New-NeedsJson -SdkResult 'success' -SdkState 'pass' -SdkCommit $commit
    Assert-Gate 'unrelated-optional-lane-skipped' (New-NeedsJson) @{ SdkEvidenceRequired = 'false'; SdkEnabled = '' } 0 'not required'
    Assert-Gate 'unrelated-optional-lane-failed' (New-NeedsJson -SdkResult 'failure' -SdkState 'fail') @{ SdkEvidenceRequired = 'false'; SdkEnabled = 'true' } 0
    Assert-Gate 'rust-engine-failed' (New-NeedsJson @{ 'rust-engine' = 'failure' }) @{ SdkEvidenceRequired = 'false' } 1 'rust-engine=failure'
    Assert-Gate 'lane-skipped-is-not-success' (New-NeedsJson @{ contracts = 'skipped' }) @{ SdkEvidenceRequired = 'false' } 1 'contracts=skipped'
    Assert-Gate 'contracts-skipped-even-if-classified' (New-NeedsJson @{ contracts = 'skipped' } -Decisions @{ run_contracts = 'false' }) @{ SdkEvidenceRequired = 'false' } 1 'contracts=skipped'
    Assert-Gate 'lane-skipped-by-classifier' (New-NeedsJson @{ 'rust-engine' = 'skipped' } -Decisions @{ run_rust_engine = 'false' }) @{ SdkEvidenceRequired = 'false' } 0 'Lanes skipped because this diff cannot affect them: rust-engine'
    $docsOnly = @{ run_dotnet = 'false'; run_rust = 'false'; run_rust_engine = 'false' }
    Assert-Gate 'docs-only-heavy-lanes-skipped' (New-NeedsJson @{ dotnet = 'skipped'; rust = 'skipped'; 'rust-engine' = 'skipped' } -Decisions $docsOnly) @{ SdkEvidenceRequired = 'false' } 0 'dotnet, rust, rust-engine'
    Assert-Gate 'lane-skipped-but-required' (New-NeedsJson @{ dotnet = 'skipped' } -Decisions @{ run_dotnet = 'true' }) @{ SdkEvidenceRequired = 'false' } 1 "dotnet=skipped although run_dotnet='true'"
    Assert-Gate 'lane-skipped-classification-missing' (New-NeedsJson @{ toolchain = 'skipped' } -Decisions @{ run_toolchain = '' }) @{ SdkEvidenceRequired = 'false' } 1 "toolchain=skipped although run_toolchain=''"
    Assert-Gate 'lane-skipped-classification-garbled' (New-NeedsJson @{ rust = 'skipped' } -Decisions @{ run_rust = 'False ' }) @{ SdkEvidenceRequired = 'false' } 1 'rust=skipped although'
    Assert-Gate 'classified-skip-but-failed' (New-NeedsJson @{ dotnet = 'failure' } -Decisions @{ run_dotnet = 'false' }) @{ SdkEvidenceRequired = 'false' } 1 'dotnet=failure'
    Assert-Gate 'classified-skip-but-cancelled' (New-NeedsJson @{ rust = 'cancelled' } -Decisions @{ run_rust = 'false' }) @{ SdkEvidenceRequired = 'false' } 1 'rust=cancelled'
    Assert-Gate 'classifier-failed' (New-NeedsJson @{ changes = 'failure'; dotnet = 'skipped' } -Decisions @{ run_dotnet = 'false' }) @{ SdkEvidenceRequired = 'false' } 1 'dotnet=skipped'
    Assert-Gate 'classification-missing' (New-NeedsJson) @{ SdkEvidenceRequired = '' } 1 'sdk_evidence_required'
    Assert-Gate 'backend-change-no-runner' (New-NeedsJson) @{ SdkEvidenceRequired = 'true'; SdkEnabled = '' } 1 'GXMCP_SDK_CI_ENABLED'
    Assert-Gate 'backend-change-lane-skipped' (New-NeedsJson) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 "concluded 'skipped'"
    Assert-Gate 'backend-change-lane-failed' (New-NeedsJson -SdkResult 'failure' -SdkState 'fail') @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 "concluded 'failure'"
    Assert-Gate 'backend-change-skip-state' (New-NeedsJson -SdkResult 'success' -SdkState 'skip' -SdkCommit $commit) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 "state 'skip'"
    Assert-Gate 'backend-change-wrong-commit' (New-NeedsJson -SdkResult 'success' -SdkState 'pass' -SdkCommit ('b' * 40)) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 'bound to commit'
    Assert-Gate 'backend-change-unknown-commit' (New-NeedsJson -SdkResult 'success' -SdkState 'pass' -SdkCommit 'unknown') @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 'bound to commit'
    Assert-Gate 'backend-change-fork' $passLane @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true'; HeadRepository = 'fork/repo' } 1 'fork pull request'
    Assert-Gate 'backend-change-fork-unknown' $passLane @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true'; HeadRepository = '' } 1 'fork pull request'
    Assert-Gate 'backend-change-proven' $passLane @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 0 'pass'
    Assert-Gate 'backend-change-other-lane-failed' (New-NeedsJson @{ dotnet = 'failure' } 'success' 'pass' $commit) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 'dotnet=failure'

    # verify-release-commit.ps1 against a throwaway repository
    $repo = Join-Path $work 'repo'
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    function Invoke-TestGit([string[]]$Arguments) {
        $output = & git -C $repo -c user.name=policy -c user.email=policy@example.invalid @Arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $output" }
        return $output.Trim()
    }
    Invoke-TestGit @('init', '-q', '-b', 'main') | Out-Null
    'one' | Set-Content -LiteralPath (Join-Path $repo 'a.txt')
    Invoke-TestGit @('add', '.') | Out-Null
    Invoke-TestGit @('commit', '-q', '-m', 'release') | Out-Null
    $releaseCommit = Invoke-TestGit @('rev-parse', 'HEAD')
    Invoke-TestGit @('tag', 'v1.0.0') | Out-Null
    Invoke-TestGit @('update-ref', 'refs/remotes/origin/main', $releaseCommit) | Out-Null
    Invoke-TestGit @('checkout', '-q', '-b', 'candidate') | Out-Null
    'two' | Set-Content -LiteralPath (Join-Path $repo 'b.txt')
    Invoke-TestGit @('add', '.') | Out-Null
    Invoke-TestGit @('commit', '-q', '-m', 'unmerged') | Out-Null
    $unmergedCommit = Invoke-TestGit @('rev-parse', 'HEAD')
    Invoke-TestGit @('tag', 'v1.0.1') | Out-Null

    function New-CheckRuns([string]$Sha, [object[]]$Runs) {
        $path = Join-Path $work ([guid]::NewGuid().ToString('N') + '.ndjson')
        ($Runs | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 }) | Set-Content -LiteralPath $path -Encoding utf8
        return $path
    }
    function New-Run([string]$Sha, [string]$Name = 'test', [int]$AppId = 15368, [string]$Status = 'completed', [string]$Conclusion = 'success', [string]$Started = '2026-10-21T10:00:00Z') {
        @{ name = $Name; app = @{ id = $AppId }; status = $Status; conclusion = $Conclusion; started_at = $Started; head_sha = $Sha }
    }
    function Assert-Release([string]$Name, [string]$Sha, [string]$Tag, [object[]]$Runs, [int]$ExpectedExit, [string]$ExpectedText = '') {
        $result = Invoke-CiScript 'verify-release-commit.ps1' @{ Commit = $Sha; Tag = $Tag; CheckRunsPath = (New-CheckRuns $Sha $Runs); Repository = $repo }
        Assert-Policy ($result.ExitCode -eq $ExpectedExit) "release '$Name' expected exit $ExpectedExit, got $($result.ExitCode): $($result.Output)"
        if ($ExpectedText) { Assert-Policy ($result.Output.Contains($ExpectedText)) "release '$Name' output lacks '$ExpectedText': $($result.Output)" }
    }
    Assert-Release 'verified' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit) 0 'verified'
    Assert-Release 'no-test-check' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -Name 'dotnet') 1 "no 'test' check run"
    Assert-Release 'no-checks' $releaseCommit 'v1.0.0' @((New-Run $releaseCommit -Name 'other')) 1 "no 'test' check run"
    Assert-Release 'forged-app' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -AppId 1) 1 "no 'test' check run"
    Assert-Release 'check-failed' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -Conclusion 'failure') 1 'completed/success'
    Assert-Release 'check-running' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -Status 'in_progress' -Conclusion '') 1 'completed/success'
    Assert-Release 'rerun-failed-after-success' $releaseCommit 'v1.0.0' @((New-Run $releaseCommit), (New-Run $releaseCommit -Conclusion 'failure' -Started '2026-10-21T11:00:00Z')) 1 'completed/success'
    Assert-Release 'rerun-passed-after-failure' $releaseCommit 'v1.0.0' @((New-Run $releaseCommit -Conclusion 'failure'), (New-Run $releaseCommit -Started '2026-10-21T11:00:00Z')) 0
    Assert-Release 'check-for-other-sha' $releaseCommit 'v1.0.0' @(New-Run $unmergedCommit) 1 'not'
    Assert-Release 'tag-points-elsewhere' $releaseCommit 'v1.0.1' @(New-Run $releaseCommit) 1 'not the checked-out'
    Assert-Release 'not-on-main' $unmergedCommit 'v1.0.1' @(New-Run $unmergedCommit) 1 'not reachable'
    Assert-Release 'unknown-tag' $releaseCommit 'v9.9.9' @(New-Run $releaseCommit) 1 'does not resolve'
    Assert-Release 'abbreviated-commit' $releaseCommit.Substring(0, 12) 'v1.0.0' @(New-Run $releaseCommit) 1 'full SHA'

    # check-line-endings.ps1
    function New-LineEndingRepo([string]$Name, [string]$Attributes) {
        $dir = Join-Path $work $Name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        & git -C $dir init -q -b main
        & git -C $dir config core.autocrlf false
        [IO.File]::WriteAllBytes((Join-Path $dir 'crlf.txt'), [Text.Encoding]::ASCII.GetBytes("a`r`nb`r`n"))
        [IO.File]::WriteAllBytes((Join-Path $dir 'lf.txt'), [Text.Encoding]::ASCII.GetBytes("a`nb`n"))
        & git -C $dir add crlf.txt lf.txt
        if ($Attributes) {
            [IO.File]::WriteAllText((Join-Path $dir '.gitattributes'), $Attributes)
            & git -C $dir add .gitattributes
        }
        return $dir
    }
    $violating = New-LineEndingRepo 'eol-violation' ''
    & git -C $violating -c user.name=policy -c user.email=policy@example.invalid commit -q -m 'crlf in index'
    [IO.File]::WriteAllText((Join-Path $violating '.gitattributes'), "* text=auto eol=lf`n")
    & git -C $violating add .gitattributes
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $violating }
    Assert-Policy ($result.ExitCode -eq 1 -and $result.Output.Contains('crlf.txt (i/crlf)')) "CRLF text blob was not rejected: $($result.Output)"
    $exempt = New-LineEndingRepo 'eol-exempt' "* text=auto eol=lf`ncrlf.txt -text`n"
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $exempt }
    Assert-Policy ($result.ExitCode -eq 0) "A -text CRLF file must be exempt: $($result.Output)"
    $clean = New-LineEndingRepo 'eol-clean' "* text=auto eol=lf`n"
    & git -C $clean rm -q -f crlf.txt
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $clean }
    Assert-Policy ($result.ExitCode -eq 0) "LF-only repository must pass: $($result.Output)"
    # Working tree: a modified or untracked eol=lf file with CRLF bytes passes the index check
    # because Git normalizes on add, so the check must read the working-tree EOL column.
    function New-CommittedLfRepo([string]$Name, [string]$Attributes) {
        $dir = New-LineEndingRepo $Name $Attributes
        & git -C $dir rm -q -f crlf.txt
        & git -C $dir -c user.name=policy -c user.email=policy@example.invalid commit -q -m 'lf only'
        return $dir
    }
    $crlfBytes = [Text.Encoding]::ASCII.GetBytes("a`r`nb`r`n")
    $patched = New-CommittedLfRepo 'eol-worktree-modified' "* text=auto eol=lf`n"
    [IO.File]::WriteAllBytes((Join-Path $patched 'lf.txt'), $crlfBytes)
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $patched }
    Assert-Policy ($result.ExitCode -eq 1 -and $result.Output.Contains('lf.txt (w/crlf)')) "A modified eol=lf file with CRLF bytes was not rejected: $($result.Output)"
    $untracked = New-CommittedLfRepo 'eol-worktree-untracked' "* text=auto eol=lf`n"
    [IO.File]::WriteAllBytes((Join-Path $untracked 'new.txt'), $crlfBytes)
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $untracked }
    Assert-Policy ($result.ExitCode -eq 1 -and $result.Output.Contains('new.txt (w/crlf)')) "An untracked eol=lf file with CRLF bytes was not rejected: $($result.Output)"
    $mixed = New-CommittedLfRepo 'eol-worktree-mixed' "* text=auto eol=lf`n"
    [IO.File]::WriteAllBytes((Join-Path $mixed 'lf.txt'), [Text.Encoding]::ASCII.GetBytes("a`r`nb`n"))
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $mixed }
    Assert-Policy ($result.ExitCode -eq 1 -and $result.Output.Contains('lf.txt (w/mixed)')) "A modified eol=lf file with mixed endings was not rejected: $($result.Output)"
    $worktreeExempt = New-CommittedLfRepo 'eol-worktree-exempt' "* text=auto eol=lf`nlf.txt -text`nnew.txt -text`n"
    [IO.File]::WriteAllBytes((Join-Path $worktreeExempt 'lf.txt'), $crlfBytes)
    [IO.File]::WriteAllBytes((Join-Path $worktreeExempt 'new.txt'), $crlfBytes)
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $worktreeExempt }
    Assert-Policy ($result.ExitCode -eq 0) "-text CRLF files in the working tree must be exempt: $($result.Output)"
    $worktreeLf = New-CommittedLfRepo 'eol-worktree-lf' "* text=auto eol=lf`n"
    [IO.File]::WriteAllBytes((Join-Path $worktreeLf 'lf.txt'), [Text.Encoding]::ASCII.GetBytes("c`nd`n"))
    [IO.File]::WriteAllBytes((Join-Path $worktreeLf 'new.txt'), [Text.Encoding]::ASCII.GetBytes("e`n"))
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $worktreeLf }
    Assert-Policy ($result.ExitCode -eq 0) "LF-only modified and untracked files must pass: $($result.Output)"
    $result = Invoke-CiScript 'check-line-endings.ps1' @{}
    Assert-Policy ($result.ExitCode -eq 0) "This repository must have no CRLF text blobs or CRLF changed files: $($result.Output)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'ci-workflow-policy: gate dependencies, backend-change evidence, fork/skip refusal, release commit verification and line endings passed' -ForegroundColor Green
