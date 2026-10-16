# Plan 129 — independent engine completion coordination

## Objective and constraints

User requests parallel implementation of remaining gx-engine capabilities, current documentation, and README updates as each agent finishes. Full SDK replacement remains the objective, not an achieved compatibility claim. Preserve existing work. No commit/push/merge/deploy/Cloud, SQL writes, license bypass, or proprietary source copying. Native tests only in disposable `C:/KBs/KBTeste*`. Run `build.ps1` last after applicable repository checks. Do not reload or terminate unrelated processes.

## Active ownership

| Worker | Owned files / lane | Acceptance evidence |
| --- | --- | --- |
| 3255fcce | exchange.rs, tests/exchange.rs, 129-exchange-progress.md | Strict nested/collection XML projection, negative schema/security tests, real CLI path |
| b57cada1 | kb.rs, tests/kb_pipeline.rs, 129-catalog-projection-progress.md | Nested/collection catalog projection, bounded dependencies and executable output |
| 6073c8b4 | tests/sdk_replacement_acceptance.rs, 129-independent-review.md | Independent regression and complete check/emit/build/run evidence; concrete findings |
| 7d12c731 | gx-engine README, capability matrix, implementation progress | Accurate implemented/native-verified/unsupported distinctions; update per integrated milestone |
| 539105a2 | diagnostics.rs, tests/diagnostics.rs, 129-diagnostics-progress.md | Useful stable categories based on actual existing errors, no invented native-code compatibility |
| Parent | Coordination, integration, CLI/build and unowned language gaps | Focused red/green tests, final Rust suite/fmt/Clippy/release, repository gates and final build |

Agents must ask before cross-file edits; documentation is single-writer. Parent relays results to documentation agent promptly. Native probes are serialized by parent. No duplicate worktrees or heavy separate build caches.

## Current evidence

Historical full Rust suite: 195 passed, 2 ignored before later clone-budget regression fix; do not label that a current run. Nested/collection runtime and codegen were implemented after stale README descriptions. Build/check/run work for the admitted portable subset, not native KB Build/Rebuild. Most SDK families remain unsupported.

## Execution queue

- [x] Reconcile current README/matrix with implemented capabilities and native observations (continuous owner updates).
- [x] Extend portable catalog and authored XML projection for implemented nested/collection runtime (bounded direct-level XML; real exported-file differential still in progress).
- [x] Independently validate actual portable end-to-end check/emit/build/artifact execution/run; fix typed build manifest metadata after red regression.
- [x] Classify diagnostics without claiming native Specify parity; public module and optional JSON stderr CLI integrated. Additional phase E2E tests in progress.
- [ ] Advance next unblocked language/build/integration gaps after each increment.
- [ ] Reconcile all findings, tests, limitations, and remaining SDK categories.
- [ ] Run final applicable checks and `build.ps1` last; record failures as failures, not completion.

## Latest integrated checkpoint

- Full current Rust suite: **224 passed, 0 failed, 2 ignored**, across 25 harnesses, log `scratchpad/engine-fanout-full-tests.log`. No full SDK parity claim.
- `For` added in format 2: Numeric iterator, start evaluated once, To reevaluated each condition, nonzero integer-literal Step, correct inclusive ascending iterator result, nested loops, shared execution budget, dependency tracking and fail-closed unsupported For Each. `tests/for_loop.rs` has six passing tests. Ascending and dynamic-start/limit semantics have fresh native observations; further Step/direction/type probes remain active.
- Explicit `Str(value,width[,scale])` supports literal width 1..18 and scale 0..width; three native-recorded explicit-format observations matched, broader formatting parity uncharacterized. `tests/str_format.rs` 2/2 passed.
- Public diagnostics API 5/5; CLI JSON diagnostics 3/3; default human errors preserved. Operation-level Build includes code generation and rustc. Never infer SDK codes or CLI source coordinates from arbitrary message strings.
- Catalog nested kb-run printed 7/9 unchanged input; direct Uses/schema/cycle/128-schema bounds tested, two mutations checked. Owner testing 1,024-Procedure cap next.
- XML focused suite 11/11; actual existing native Child/Rows/custom-item exports found, actual XML executable differential active.
- Independent acceptance 2/2 passed after backend metadata fix (format 1 i64 versus format 2 typed) and correcting test expectation to canonical MAIN.
- Parent-owned files rustfmt completed. **Clippy currently failed**: exchange::parse_sdt has eight arguments; owner 3255fcce asked to refactor cohesive context, not suppress lint. Release/repository final gates and build.ps1 still pending.
- Native oracle worker 45c3f8de active; after For, characterize/export deeper SDT hierarchy for safe recursive XML projection. No other worker may run concurrent SDK probes.
- Diagnostics worker 539105a2 now also owns tests/cli_diagnostics.rs for real compilation/build/execution phase error tests; parent must avoid concurrent edits there.

## Environment reliability note

Shared OptChat operation journaling temporarily blocks independent workers while another Bash operation is running or has a recorded error. Expected-red checks must print their ACTUAL_TEST_EXIT (or relevant actual command exit) and use a successful shell wrapper; this avoids mistaking expected regression evidence for an uncertain external effect. Wrapper success is never test success. Independently observe before reconciling uncertain outcomes; do not repeat external effects. Agent tools cannot directly send to other workers here, so parent relays all documentation milestones.

This ledger is a live execution record. A green increment does not mean SDK replacement is complete. Remaining native KB/storage, broader language/object types, Specify/navigation, database/runtime/generation and MCP integration work remains listed in the capability matrix; it is not erased by this checkpoint.
