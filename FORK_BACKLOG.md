# Fork Backlog

Non-blocking gaps in the parts of this checkout that the fork owns. This file is
**fork-exclusive**: upstream never touches it, and it must never track a defect
that points at upstream-authored code. Those are filed as issues in
`lennix1337/Genexus18MCP` and are deliberately absent from this list.

Scope: the fork-owned pre-push routine — `scripts/Invoke-PrePushMechanicalChecks.ps1`,
`scripts/tests/test-pre-push-mechanical-checks.ps1`, `FORK_CHANGELOG.md`, and the
fork-owned blocks in `AGENTS.md` — plus the fork-local backend helpers
`scripts/Invoke-GxMcpLocalBuild.ps1` and `scripts/Set-GxMcpLocalEngineBackend.ps1`.

Each item records why it did not block, the fix sketch, and how to validate it.
Re-evaluate an item in a later session before implementing it; line numbers and
function names drift.

## Open

### FB-001 — An invalid `-CargoExecutable` reports three failed checks instead of `not-run`

**Where:** `Resolve-CargoExecutable` / `New-RustLaneSummary` in
`scripts/Invoke-PrePushMechanicalChecks.ps1`.

**Trigger:** `-IncludeRustLane -CargoExecutable <path that does not exist>`.

**Observed:** `rustLane.ran=true`, `rustLane.status='failed'`, and each of the
three checks fails with the launch error. The intended state is `not-run` with
`note='cargo-not-found'`, the same as an absent `cargo`.

**Why it did not block:** the lane is advisory; `pushReadiness` is unchanged and
the operator sees the launch error in the check records.

**Fix sketch:** in `New-RustLaneSummary`, treat a non-empty `CargoPath` that does
not resolve to a leaf as absent. Keep `Resolve-CargoExecutable` as the resolver,
but validate the explicit path there or at the lane entry.

**Validation:** extend the fake-cargo block with a nonexistent explicit path and
assert `not-run`/`cargo-not-found`; mutation: remove the new existence check and
confirm red.

### FB-002 — A missing `src/gx-engine` reports launch failures instead of `engine-missing`

**Where:** `New-RustLaneSummary` in `scripts/Invoke-PrePushMechanicalChecks.ps1`.

**Trigger:** `-IncludeRustLane` in a checkout without the `src/gx-engine` crate.

**Observed:** the three `Invoke-NativeCapture` calls fail at `Push-Location`, so
the lane reports three failures. The intended state is `not-run` with a distinct
note (`engine-missing`), which is not the same as "cargo ran and the code is
red".

**Why it did not block:** advisory lane; the error text names the missing
directory.

**Fix sketch:** check `Test-Path -LiteralPath $EnginePath -PathType Container`
before the first check and return `not-run`/`engine-missing`.

**Validation:** fake engine path in the lane test; mutation: remove the check.

### FB-003 — `notCovered` omits the Rust lane when the run aborts before the lane

**Where:** the `rustLane` block in the checker's main flow.

**Trigger:** an exception before the lane (for example, a failed preflight that
throws out of the try) with `-IncludeRustLane`.

**Observed:** `notCovered` keeps `gxwire Rust lane` but does not add
`gx-engine Rust lane`, because the entry is appended only after the lane runs.

**Why it did not block:** the run already failed; `notCovered` is diagnostic.

**Fix sketch:** seed `notCovered` with the opt-in lane entry at summary
initialization and remove it when the lane actually runs.

**Validation:** synthetic summary/flow test; mutation: skip the removal and
confirm the entry stays.

### FB-004 — New `FORK_CHANGELOG.md` entries are all under `### Fixed`

**Where:** `FORK_CHANGELOG.md` → `## Unreleased`.

**Trigger:** the five entries from the peer-reviewed pre-push work.

**Observed:** `-IncludeRustLane` is a feature and the `New-Phase` shape change is
a change, but both sit under `### Fixed`. The file's own header asks for the
matching subsection.

**Why it did not block:** prose only; no behavior.

**Fix sketch:** move the Rust-lane entry to `### Added` and the phase-shape entry
to `### Changed`; keep the redaction, guard and aggregate entries under
`### Fixed`.

**Validation:** none beyond re-reading the section order.

### FB-005 — Two of the four redacted capture points have no behavioral leak test

**Where:** `scripts/tests/test-pre-push-mechanical-checks.ps1`.

**Trigger:** the C6 redaction work.

**Observed:** the native and whitespace captures have behavioral tests; the
PowerShell-parse and Rust-lane captures are covered only by a source pin that
counts four redacted assignments. The pin catches removal, not a regression in
the redaction content.

**Why it did not block:** the source pin fails on removal, and both paths use the
same `Redact-PrePushLogLine`.

**Fix sketch:** a parse-phase fixture whose error line carries a recognized
secret, and a fake-cargo check whose output carries one; assert the serialized
phase/check object does not contain the secret.

**Validation:** mutation: drop the redaction from each path and confirm the new
test goes red.

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

### FB-008 — The fork-local backend helpers have no behavioral test

**Where:** `scripts/Invoke-GxMcpLocalBuild.ps1` and
`scripts/Set-GxMcpLocalEngineBackend.ps1`; nothing under `scripts/tests/` names
either script.

**Trigger:** any edit to either helper, including the exit-code fix recorded in
`FORK_CHANGELOG.md`.

**Observed:** the only automated check is the checker's PowerShell AST parse over
tracked `.ps1` files - syntax, not behavior. The wrapper's exit-code contract
already broke once (every failed entry point reported `1`) and was caught by
manual reproduction, not by a test; the setter's merge, no-BOM, uniform-EOL and
read-back guarantees are unverified the same way.

**Why it did not block:** the helpers run interactively and their failures are
visible immediately, and the upstream-owned `scripts/tests/run-release-script-tests.ps1`
enumerates its own test list, so adding a fork test there would patch an upstream
script - which the fork policy forbids.

**Fix sketch:** add a fork-local test that drives both scripts against a
temporary root with fake entry points: a non-zero `build.ps1`/`install.ps1` must
yield that same code and leave `config.json` untouched; a zero exit must apply
the backend; the setter must preserve every other key, write no BOM, end CRLF and
be idempotent. Pick a fork-owned entry point to run it from (the checker is the
only one today) instead of editing the upstream test list.

**Validation:** mutation - drop `-ErrorAction Continue` (the exit code collapses
to `1`) and force a lone-LF write; each mutation must turn the test red.

## Accepted limits (not backlog)

These are documented design limits of the guard, recorded in `FORK_CHANGELOG.md`,
not pending work: `using` refusal is detection rather than prevention; function
bodies are not inspected; `#requires` is not parsed; `filter` is accepted as a
function definition; the guard has no content hash; and `release-preflight.ps1` /
`release-doctor.ps1` remain unguarded because they are upstream.
