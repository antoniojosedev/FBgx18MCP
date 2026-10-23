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
function names drift.

## Open

### FB-006 — Global harness config does not know the `-IncludeRustLane` invocation

**Where:** the operator's global `CLAUDE.md` / command allowlist, outside this
repository.

**Trigger:** running the checker with `-IncludeRustLane`.

**Observed:** the canonical invocation forms documented there are literal and
cover only `-AsJson`; the new flag can trigger a permission prompt.

**Why it did not block:** the default routine does not use the flag.

**Fix sketch:** add a third literal form (or widen the existing entry) in the
global config. This is user configuration, not a repository change.

**Validation:** invoke the flag from a fresh session and confirm no prompt.

### FB-007 — Auto-enable the Rust lane when the change touches the Rust crates

**Where:** the lane wiring in `scripts/Invoke-PrePushMechanicalChecks.ps1`.

**Trigger:** a change under `src/gx-engine/` or `src/gxwire/` without
`-IncludeRustLane`.

**Observed:** the default routine skips both lanes, so an edit to the Rust crates
gets `readyLocal` with `rustLane.status='not-requested'`. The only signal is the
`notCovered` entry (`readyLocal` is not a claim about the Rust crates).

**Why it did not block:** the exclusion is declared and the lane is advisory by
policy; nothing Rust-related gates `pushReadiness`.

**Fix sketch:** when `-IncludeRustLane` is absent and `summary.changedPaths`
intersects `src/gx-engine/` or `src/gxwire/`, run the lane (still advisory) and
record `note='auto-enabled-by-changed-paths'`. Keep the explicit switch as an
override. Decide whether `gxwire` needs its own command set (separate crate) or
whether the engine lane covers only `gx-engine`; if separate, keep it a distinct
lane.

**Validation:** synthetic `changedPaths` test; mutation: remove the auto-enable
and confirm the lane stays `not-requested` for an engine-only change.

## Accepted limits (not backlog)

These are documented design limits of the guard, recorded in `FORK_CHANGELOG.md`,
not pending work: `using` refusal is detection rather than prevention; function
bodies are not inspected; `#requires` is not parsed; `filter` is accepted as a
function definition; the guard has no content hash; and `release-preflight.ps1` /
`release-doctor.ps1` remain unguarded because they are upstream.
