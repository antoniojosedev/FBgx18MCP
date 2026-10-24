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

### FB-008 — Aborted run reports a requested Rust lane as `not-requested`

**Where:** the Rust lane seeding in `scripts/Invoke-PrePushMechanicalChecks.ps1`
(the two `Set-RustLaneCoverage` calls before `try`, and the `rustLane` /
`gxwireRustLane` initial objects in `$summary`).

**Trigger:** running with `-IncludeRustLane` when the script throws inside `try`
before the lanes run (refused release contract, an exception while reading the
base, a failure mid-run).

**Observed:** `notCovered` is seeded as `gx-engine Rust lane` / `gxwire Rust lane`
(requested, not run), but the lane objects are only replaced at the end of `try`,
so the emitted JSON still carries `rustLane.status='not-requested'` and
`requested=false`. The two fields contradict each other. The pattern predates
FB-007 (it existed for `gx-engine` in `c85c0389`); FB-007 extended it to `gxwire`.

**Why it did not block:** it only happens on a run that already ended
`status=failed`; neither lane feeds `pushReadiness`, and `notCovered` still
declares the lane as not run.

**Fix sketch:** seed the lane objects consistently with the seeded coverage —
when `-IncludeRustLane` is present, initialize both as `requested=true`,
`ran=false`, `status='not-run'` with a note such as `aborted-before-lane`, which
the normal path then overwrites. Keep the default (no flag) unchanged; the
auto-enable decision is not known until `changedPaths` is read.

**Validation:** a test that forces an abort before the lanes with
`-IncludeRustLane` and asserts that each lane object agrees with its `notCovered`
entry; mutation: restore the `not-requested` seed and confirm the test goes red.

## Accepted limits (not backlog)

These are documented design limits of the guard, recorded in `FORK_CHANGELOG.md`,
not pending work: `using` refusal is detection rather than prevention; function
bodies are not inspected; `#requires` is not parsed; `filter` is accepted as a
function definition; the guard has no content hash; and `release-preflight.ps1` /
`release-doctor.ps1` remain unguarded because they are upstream.
