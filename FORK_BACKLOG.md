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

None.

## Accepted limits (not backlog)

These are documented design limits of the guard, recorded in `FORK_CHANGELOG.md`,
not pending work: `using` refusal is detection rather than prevention; function
bodies are not inspected; `#requires` is not parsed; `filter` is accepted as a
function definition; the guard has no content hash; and `release-preflight.ps1` /
`release-doctor.ps1` remain unguarded because they are upstream.
