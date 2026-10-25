$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Issue #590 follow-up (native proof on a GeneXus 18 U16 test KB): the optional action lane
# of workwith_persistence_smoke.ps1 sent add_action without `parameters`, so any Procedure whose
# parm() rule takes arguments failed with ArityMismatch before the durability reread. These
# guards parse the script (no Knowledge Base, no Gateway) and pin the pass-through.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\workwith_persistence_smoke.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "workwith_persistence_smoke.ps1 does not parse: $($errors[0].Message)" }

$parameters = @($ast.ParamBlock.Parameters)
$actionParameters = @($parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'ActionParameters' })
if ($actionParameters.Count -ne 1) { throw 'The smoke must declare -ActionParameters so a Procedure with a parm() rule can be used.' }
if ($actionParameters[0].StaticType -ne [string[]]) { throw '-ActionParameters must be a string[].' }
if (@($parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Procedure' }).Count -ne 1) { throw 'The optional action lane parameter -Procedure is missing.' }

# The add_action arguments must be a variable that can carry `parameters`, and `parameters`
# must be assigned from -ActionParameters (a literal hashtable without the key was the defect).
$assignments = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [System.Management.Automation.Language.IndexExpressionAst] -and
    $node.Left.Target.Extent.Text -eq '$addActionArgs' -and
    $node.Left.Index.Extent.Text -match "^['""]parameters['""]$"
}, $true))
if ($assignments.Count -ne 1) { throw 'add_action must receive `parameters` through $addActionArgs[''parameters''].' }
if ($assignments[0].Right.Extent.Text -notmatch '\$ActionParameters') { throw '`parameters` must be taken from $ActionParameters.' }

$invocations = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Invoke-Tool' -and
    $node.Extent.Text -match "add_action|addActionArgs"
}, $true))
$addActionCall = @($invocations | Where-Object { $_.Extent.Text -match '\$addActionArgs' })
if ($addActionCall.Count -ne 1) { throw 'The add_action Invoke-Tool call must pass $addActionArgs.' }

# The documented example must not advertise a Procedure lane that cannot work with parameters.
$help = $ast.GetHelpContent()
if ($null -eq $help -or $help.Notes -notmatch 'ArityMismatch') { throw 'The smoke help must explain ArityMismatch and -ActionParameters.' }

Write-Host 'workwith-persistence-smoke: -ActionParameters is declared, forwarded as add_action parameters and documented'

# Issue #645 (follow-up of #586/#590): the smoke must remove the filter attribute and the
# condition it created, on the success and on the failure path, behind an ownership guard,
# through one finalizer that never exits inside a try. These guards parse the script only;
# the live mutation check (removing the cleanup call) is recorded in the pull request.
$scriptText = Get-Content -LiteralPath $scriptPath -Raw
function Find-Ast([scriptblock]$Predicate) { return @($ast.FindAll($Predicate, $true)) }
function Get-Function([string]$Name) {
    $fn = @(Find-Ast { param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name })
    if ($fn.Count -ne 1) { throw "The smoke must define exactly one function $Name (found $($fn.Count))." }
    return $fn[0]
}

# 1. The removal exists and takes only the condition text this run sent.
$removeLiterals = @(Find-Ast { param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value -eq 'remove_filter_attribute' })
if ($removeLiterals.Count -lt 1) { throw 'The smoke never calls genexus_workwith remove_filter_attribute (#645: it leaves the filter attribute behind).' }
if ($scriptText -notmatch 'removeConditions\W*=\s*@\(\$Condition\)') { throw 'removeConditions must be exactly the condition text this run sent (@($Condition)), never every observed condition that mentions the variable.' }

# 2. Ownership guard: the name is normalized once, an existing name stops the run before any write,
# and $filterWritten is set before the add call (the attempt may persist with an unreadable answer).
if ($scriptText -notmatch '\$Attribute\.Trim\(\)\.TrimStart\(''&''\)') { throw 'The attribute name must be normalized once (Trim + leading &) before the baseline check.' }
$addCall = @(Find-Ast {
    param($n)
    $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-Tool' -and $n.Extent.Text -match 'add_filter_attribute'
})
if ($addCall.Count -ne 1) { throw "Expected exactly one add_filter_attribute Invoke-Tool call (found $($addCall.Count))." }
$writtenFlag = @(Find-Ast {
    param($n)
    $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $n.Left.Extent.Text -eq '$filterWritten' -and $n.Right.Extent.Text -eq '$true'
})
if ($writtenFlag.Count -ne 1) { throw 'Expected exactly one `$filterWritten = $true` assignment.' }
if ($writtenFlag[0].Extent.StartOffset -ge $addCall[0].Extent.StartOffset) { throw '$filterWritten must be set to $true BEFORE add_filter_attribute is called.' }
$existsGuard = [regex]::Match($scriptText, 'already exists before the write')
if (-not $existsGuard.Success -or $existsGuard.Index -ge $writtenFlag[0].Extent.StartOffset) { throw 'The baseline "already exists" refusal must come before $filterWritten is set.' }
if ($scriptText -notmatch '\$cleanupDone') { throw 'The finalizer must guard with $cleanupDone so the cleanup runs once.' }

# 3. One finalizer: exit is never inside a try or a function, Write-Summary runs once,
# and Exit-Fail / Exit-Unavailable throw instead of exiting.
$exits = @(Find-Ast { param($n) $n -is [System.Management.Automation.Language.ExitStatementAst] })
foreach ($exit in $exits) {
    $parent = $exit.Parent
    while ($null -ne $parent) {
        if ($parent -is [System.Management.Automation.Language.TryStatementAst]) { throw "exit at line $($exit.Extent.StartLineNumber) is inside a try; the finalizer must choose the exit code last." }
        if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { throw "exit at line $($exit.Extent.StartLineNumber) is inside function $($parent.Name); helpers must throw." }
        $parent = $parent.Parent
    }
}
$summaryCalls = @(Find-Ast { param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Summary' })
if ($summaryCalls.Count -ne 1) { throw "Write-Summary must be called exactly once, after the cleanup (found $($summaryCalls.Count))." }
foreach ($name in 'Exit-Fail', 'Exit-Unavailable') {
    $fn = Get-Function $name
    if ($fn.Extent.Text -notmatch '\bthrow\b') { throw "$name must throw." }
    if ($fn.Extent.Text -match 'Write-Summary') { throw "$name must not write the summary; the finalizer does." }
}
$cleanup = Get-Function 'Invoke-Cleanup'
if ($cleanup.Extent.Text -match 'Exit-Fail|Exit-Unavailable') { throw 'Invoke-Cleanup must not call Exit-Fail/Exit-Unavailable; each step records its own result.' }
if ($summaryCalls[0].Extent.StartOffset -lt $cleanup.Extent.EndOffset) { throw 'Write-Summary must come after the cleanup function.' }
$cleanupCalls = @(Find-Ast { param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-Cleanup' })
if ($cleanupCalls.Count -ne 2) { throw "Invoke-Cleanup must be called twice: once on the success path and once in the finalizer (found $($cleanupCalls.Count))." }

# 4. In the main flow the absence assertion comes before the full baseline comparison, so removing
# the cleanup call makes the smoke fail on the absence of the attribute and condition.
$absence = [regex]::Match($scriptText, 'still present after the cleanup')
$comparison = [regex]::Match($scriptText, 'Get-ProjectionDifferences -Expected \$baselineProjection')
if (-not $absence.Success -or -not $comparison.Success -or $absence.Index -ge $comparison.Index) { throw 'The absence assertion must precede the comparison with the baseline.' }

# 5. Obsolete statements about the pre-#586 state are gone and the new statements are present.
foreach ($stale in 'stay in the KB', 'remains in the KB', 'has no remove_filter_attribute', 'leaves a filter attribute behind') {
    if ($scriptText -match [regex]::Escape($stale)) { throw "Obsolete text still present: '$stale'." }
}
foreach ($required in 'cleanup', 'durability', 'unverified', 'FilterAttributeReferencedByConditions') {
    if ($help.Description -notmatch $required -and $scriptText -notmatch $required) { throw "The smoke must mention '$required'." }
}
if ($help.Description -notmatch 'EXIT CODES|exit') { throw 'The help must document the exit codes.' }
$summaryInit = @(Find-Ast { param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$script:Summary' })
if ($summaryInit.Count -ne 1) { throw 'Expected one $script:Summary initialization.' }
$summaryText = $summaryInit[0].Extent.Text
foreach ($field in 'durability', 'cleanup', 'outcome') { if ($summaryText -notmatch "(?m)^\s*$field\s*=") { throw "The summary must carry '$field'." } }
if ($summaryText -notmatch "demonstrates\s*=[^\r\n]*(removed|cleanup)") { throw '`demonstrates` must describe the cleanup.' }
if ($summaryText -notmatch "doesNotCover\s*=[\s\S]*action[\s\S]*pristine|doesNotCover\s*=[\s\S]*pristine[\s\S]*action") { throw '`doesNotCover` must state that the cleanup is not pristine restoration and does not cover the action lane.' }

Write-Host 'workwith-persistence-smoke: #645 cleanup guards (removal call, ownership guard, single finalizer, absence assertion, current texts)'
