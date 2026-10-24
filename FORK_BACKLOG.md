# Fork Backlog

Non-blocking gaps in the parts of this checkout that the fork owns. This file is
**fork-exclusive**: upstream never touches it, and it must never track a defect
that points at upstream-authored code. Those are filed as issues in
`lennix1337/Genexus18MCP` and are deliberately absent from this list.

Scope: the fork-owned pre-push routine — `scripts/Invoke-PrePushMechanicalChecks.ps1`,
`scripts/tests/test-pre-push-mechanical-checks.ps1`,
`scripts/tests/test-fork-local-backend-helpers.ps1`, `FORK_CHANGELOG.md`, and the
fork-owned blocks in `AGENTS.md` — plus the fork-local backend helpers
`scripts/Invoke-GxMcpLocalBuild.ps1` and `scripts/Set-GxMcpLocalEngineBackend.ps1`.

Each item records why it did not block, the fix sketch, and how to validate it.
Re-evaluate an item in a later session before implementing it; line numbers and
function names drift. Resolved items are removed from the open list; their
validation is recorded in `FORK_CHANGELOG.md` and the commit history.

## Open

### FB-009 — Invoke-GitWhitespacePhase duplicates the native capture/log body

**Where:** `scripts/Invoke-PrePushMechanicalChecks.ps1`: `Invoke-GitWhitespacePhase`
repeats the phase-construction, captured-field extraction, 60-line raw tail,
`Set-RawPhaseTail`, redacted `outputTail` and `Write-PhaseLog` block from
`Invoke-NativePhase`.

**Trigger:** any change to capture handling, tail length, redaction or log
collision behavior must be made twice; a divergence would make the native and
whitespace phases report different logs or tails for the same failure.

**Why it did not block:** the duplication is localized, current behavior is
covered by the focused suite, and the advisory change deliberately left
`Invoke-NativePhase` untouched.

**Fix sketch:** extract the shared block into a helper (for example
`New-CapturedPhase -Name -Command -Result -LogRoot`) that returns a phase with
`exitCode`, `durationSeconds`, `outputTail`, `outputPath` and the raw tail
already set; both callers keep their own status/classification logic. If the
parity test needs to drive both phase functions, add a `-CapturedResult` seam to
`Invoke-NativePhase` or assert that both callers use the shared helper. Preserve
the log collision suffix, redaction and the 60-line tail.

**Validation:** add a parity test that feeds the same synthetic captured result
(multi-line output, a secret, an error and a non-zero exit) through both paths
and asserts the same log shape and `outputTail` (including redaction and the
60-line cap); keep the existing whitespace classification tests green. Mutation:
change the tail length or redaction in one path only and confirm the parity test
goes red.

## Accepted limits (not backlog)

These are documented design limits of the guard, recorded in `FORK_CHANGELOG.md`,
not pending work: `using` refusal is detection rather than prevention; function
bodies are not inspected; `#requires` is not parsed; `filter` is accepted as a
function definition; the guard has no content hash; and `release-preflight.ps1` /
`release-doctor.ps1` remain unguarded because they are upstream.
