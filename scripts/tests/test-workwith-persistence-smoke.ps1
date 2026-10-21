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
