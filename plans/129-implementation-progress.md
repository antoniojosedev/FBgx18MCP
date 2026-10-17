# Rust alternative engine — implementation ledger

## Authorization and boundaries

The user authorized agents to begin implementing the independent Rust engine. This supersedes the investigation-only status of plan 129 for implementation work, not its correctness/security boundaries. No engine commit, push, release, direct SQL writes, license bypass, or proprietary implementation copying is authorized. Subsequent explicit user authorization permits fixture creation/modification, builds, and execution in `C:\KBs\KBTeste` through supported SDK paths; it does not authorize other KBs or production.

Baseline: `b9de0a5ae4cdf0e3932b486b2f01702a60e7e3cd`. Preserve existing plan changes. Implementation is isolated under `src/gx-engine/`; existing MCP/Worker behavior is unchanged.

## Confirmed destination and logical-condition increment

The user reaffirmed that **complete SDK/toolchain replacement is the objective**.
Numeric-subset warnings describe current evidence, not the intended endpoint.
README and plan 129 now make that distinction explicit. SDK adapters are temporary
migration/oracle paths, not permanent feature boundaries.

Implemented `And`/`Or`/`Not` with bounded parsing, precedence and short-circuit
native emission. Fresh differential execution in KBTeste matched **91 cases /
92 values** on GeneXus and gx-engine, including truth tables, grouping, Not,
logical loop guards and skipped zero-division operands. The known native CS0220
constant product remains an explicitly excluded native failure. Exact source
readback and logs: `scratchpad/RustEdge7476154929200-retry-1792028006004645000/`.

Engine-only: 92 cases/93 outputs, 28 source rejections, 8 invalid documents,
7 controlled runtime errors. Rust: 52 passed, 1 ignored benchmark; Python: 8
passed; Clippy passed. Both new logic regressions failed under an And-to-Or
emission mutation and passed after restoration. Ripwire edit-check passed;
quality-delta/test-gate still exit 2/4; no gate suppression or baseline reset.

## Typed compiler integration — current increment

Implemented portable format 2 with explicit Boolean/Numeric variable declarations,
strict type checking, decimal literals/arithmetic and standalone native Rust
emission. Format 1 keeps its original i64 contract. The new executable matches
19 observations against the source-matched native run
`scratchpad/typed-native-1792028364801578100/`; fresh Rust evidence:
`scratchpad/typed-rust-differential.json`. Native wrapping is excluded explicitly
and checked overflow is tested as a safety divergence, not hidden as parity.

Verified: declared-type round-trip, unknown fields and duplicate declarations
(including exact JSON duplicates), mixed-type/unassigned-read rejection,
short-circuiting, runtime budgets/errors, retained decimal storage precision,
display rounding/overflow markers and integer truncation. Cargo: 62 passing tests,
1 ignored informational benchmark; Python: 16 passing tests. Rounding and type-check
mutations made the corresponding guards fail, then were restored. Legacy offline
lane still passes 92 cases/93 outputs, 28 source rejections, 8 invalid documents
and 7 runtime errors. Clippy/fmt passed. See VALIDATION.md for gate limitations.

### Typed calls, storage widths and incremental explain

Implemented typed by-value parameters and Boolean/Numeric return declarations,
checked conversions on arguments/results, all-path return validation, and shared
step/depth guards. Typed incremental explain now reuses parsed syntax while
invalidating declaration/signature changes and transitive callers; failed
revisions retain the previous accepted state. CLI explain is exercised end to end.

A fresh SDK/MSBuild/native-vs-Rust differential run verified all authored Source,
Rules and variable declarations for four procedures and matched 12 output lines:
`scratchpad/typed-calls-native-1792030284812864000/`. This caught and corrected a
real native semantic gap: GeneXus hoists helper calls before Boolean evaluation,
even for a logically skipped operand. Typed lowering now matches that behavior;
pure expressions still short-circuit. Loop guards rerun hoisted calls on each
condition evaluation. The portable fixture is `fixtures/typed-calls.json`.

Fresh native width probes verified every precision 1..18 with complete Source and
Variables readback: storage wraps at 15/31/63 doublings for precisions 1..4/5..9/
10..18 respectively. Evidence: `scratchpad/width-native-1792029927991083100/`.
Rust now enforces the corresponding signed Int16/Int32/Int64 ranges, retaining
explicit overflow diagnostics rather than native wrapping. An initial authoring
attempt supplied incorrect variable field names and yielded VARCHAR declarations;
those results were rejected, and the successful run explicitly checked types.
A transient gateway read error was retried without claiming mutation success.

Not yet admitted: output/inout parameters, void helpers, repeating decimal
division/general precision rounding, or full SDK compatibility. Next executable
step: strings/dates/SDTs and broader parameter modes, then navigation/data-model
workstreams. These are implementation
work, not requests for renewed authorization. Keep executing validated increments
toward full replacement; milestone reports are not a stopping condition.
No engine commit/push authorization.

### Text implementation and next Date gate

Implemented Character/Varchar declarations, literals, raw standalone-procedure
storage, concatenation, trailing-space-insensitive equality, UTF-16 Length(),
and typed text helpers with checked 1 MiB per-value growth. Date/SDT support is
not yet implemented. Text evidence: native
`scratchpad/string-native-1792031065612440700/`; Rust comparison
`scratchpad/text-rust-differential.json`. Of 18 observations, 17 agree after four
length-label normalizations. The other is explicitly divergent: the native
CP1252 C# generator changed the emoji to `??`; Rust preserves the authored Unicode.
Do not reproduce silent encoding loss or describe that observation as parity.

Fresh validation: 79 engine Rust tests passed (1 ignored), gxwire 65 passed,
Python 18 passed, Clippy/fmt passed. Offline numeric lane still passes 92 cases/
93 outputs, 28 rejections, 8 invalid documents, 7 runtime errors; 19 typed Numeric/
Boolean observations still match. Named parser/emitter/incremental edit-checks
pass. Quality-delta is exit 2 with 7 gating clone findings against HEAD (no pinned
baseline); test-gate exit 4. One added clone report structurally equates the
independent Rust type-name mapping with gxwire's WriteMode mapping; no unrelated
cross-domain abstraction or baseline suppression was introduced. Broader .NET/
Nexus/npm/release lanes were not run for these isolated Rust increments.

Date discovery advanced through supported SDK paths in KBTeste: source/Date
variables verified, fresh native build/run, 22 observations at
`scratchpad/date-native-1792031491025211900/`. Leap-day arithmetic, date difference,
invalid-date-to-empty, empty Year()=0 and century leap behavior were observed.
Two-digit years are context-sensitive: 1 -> 2001, 49 -> 1949, 50 -> 1950,
99 -> 1999, 100 -> 100 in this environment. A further boundary probe verified
0 -> 2000, 29 -> 2029, 30 -> 2030, 39 -> 2039, 40 -> 1940: measured pivot 40.
This must be represented as environment context, not assumed universal.
Next: characterize the environment setting and date
boundaries, then implement/test the admitted Date IR/runtime/compiler path.
Initial native syntax rejected method invocation on a parenthesized difference;
assigning the difference to a declared Numeric variable fixed the authored probe.
`RustTypedProbe` now holds the Date fixture; historical text/native snapshots are
preserved. No SQL access, Cloud publication, engine commit/push or production work.

## Native typed-semantics discovery (before Rust type implementation)

Executed 20 authored Boolean/Numeric observations through the official SDK,
custom MSBuild pipeline and fresh .NET executable in authorized `KBTeste`.
Exact Source and Variables readback verified `Numeric(5,2)`, `Numeric(3,0)`
and Boolean declarations. All 20 output observations matched, exit 0.
Evidence: `scratchpad/typed-native-1792028364801578100/` (ignored).
Portable authored inputs and strict output comparator:
`src/gx-engine/scripts/typed_semantics_cases.py`.

Observed GeneXus 18.0.10 .NET behavior, not universal language guarantees:

- `Numeric(5,2)` retains `1.235` and `1.236` internally, confirmed by equality
  branches. Its `ToString()` renders `1.235` as `1.24`, `-1.235` as `-1.24`,
  and `1.234` as `1.23`. Assignment is not fixed-scale quantization.
- Integer assignments `1.5`, `-1.5`, and decimal-variable `1.55` become
  `1`, `-1`, and `1`, respectively; do not infer decimal rounding rules
  apply to integer assignments.
- `0.1 + 0.2` renders `0.30`; `1/8` and `-1/8` render `0.13` and `-0.13`.
- Declared display width does not enforce stored range: `1000` in Numeric(3)
  and `1000.12` in Numeric(5,2) remain available to comparisons, while
  `ToString()` renders `***` and `*****`. These are formatting overflow
  observations, not storage rejection.
- Numeric(3) generated storage wraps negative after `32760 + 10`, confirmed
  by a branch. This differs from the existing Rust checked-i64 policy.
  Preserve this as an explicit compatibility/safety divergence; no silent
  arithmetic-policy change was made.
- Boolean literal, Not and comparison/logical assignment worked.

A second authoring attempt exposed MCP `AtomicCreateStepFailed` (Variables
part missing, compensation incomplete). The runner inspected the whole existing
fixture before an explicit update, then re-read declarations/source and built
successfully. This is recorded as a separate authoring reliability finding,
not blamed on the language compiler. Fixture `RustTypedProbe` remains in KBTeste.

Validation: 12 Python tests passed, including strict missing/extra/duplicate/
changed-output rejection. Native observations repeated successfully. Rust
At this discovery checkpoint, Boolean declarations/decimal literals/typed storage
were not implemented; the current typed-compiler increment above supersedes that
status. These observations prevented encoding an incorrect fixed-scale contract.
Ripwire quality/test gates still exit 2/4 (six gating findings); not release-ready.

## First integration contract

One standalone Cargo package, `gx-engine`, edition 2024. Portable authored JSON projects (format version 1) describe an entry procedure and named procedures with numeric input parameters and source bodies. This is our explicit portable format, not a native KB decoder or full GeneXus language.

- `model.rs`: bounded project loading, version/identity checks, deterministic portable serialization.
- `frontend.rs`: source lexer/parser and public AST. Numeric i64 expressions, assignments, calls, Print, Return, If/Else/EndIf, Do While/EndDo. Unsupported constructs fail explicitly. No string/date/decimal compatibility claim.
- `backend.rs`: validation of procedures/variables/calls/control flow and deterministic native Rust emission; checked arithmetic, no source injection.
- `incremental.rs`: transactional per-procedure parse reuse, old/new reverse-call-graph impact propagation, full candidate semantic validation, and rollback on invalid revisions. This does not cache executables or skip backend validation.
- Coordinator: Cargo package, CLI (`check`, `emit`, `explain`, `build`, `run`), authored fixtures, build/run path using rustc directly, integration tests, docs and verification.

Shared AST: `Procedure { name: String, params: Vec<String>, body: Vec<Stmt> }`; `Stmt::{Assign(String, Expr), Print(Expr), Return(Expr), If(Expr, Vec<Stmt>, Vec<Stmt>), While(Expr, Vec<Stmt>)}`; `Expr::{Integer(i64), Variable(String), Call(String, Vec<Expr>), Binary(Box<Expr>, BinOp, Box<Expr>), Negate(Box<Expr>)}`; `BinOp::{And, Or, Add, Subtract, Multiply, Divide, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual}`. Comparisons produce numeric 0/1; conditions use nonzero truth. Frontend `parse_body(&str) -> Result<Vec<Stmt>, String>`; backend `emit(&[Procedure], entry: &str) -> Result<String, String>`.

## First integration acceptance evidence

- [x] **Verified:** portable project load/round-trip and malformed/unsupported input rejection.
- [x] **Verified:** lexer/parser/semantic tests, unsupported syntax, identifier-injection rejection and bounded deep/flat expressions.
- [x] **Verified on this host:** fixture compiles directly through rustc and prints `42` twice; native outputs also cover precedence, comparison, i64 boundaries, helper effects/evaluation order and controlled runtime failures. No SDK/MSBuild calls exist in this compiler path. A clean SDK-absent-host/process-tree audit remains not verified.
- [x] **Verified:** changed source builds separate executable generations, preserves previous outputs, and matches expected output. Incremental emitted source equals clean compilation across edits; invalid revisions roll back. No executable cache is implemented.
- [x] **Verified:** Cargo test/clippy/fmt and final diff checks. Ripwire attempts recorded separately as unavailable, not green.
- [x] **Verified:** README documents the exact subset, prototype execution caps and limitations. SDK behavioral parity, SDTs, For each, native KB interoperability, complete platform replacement and speedup remain not verified.

## Follow-up validation and native-build measurements

User requested error/edge-case validation and comparison against ordinary/native builds. Added `src/gx-engine/scripts/validate.py`, four oracle unit tests, and `src/gx-engine/VALIDATION.md`. Final run: 203 expressions + 10 branch/loop outputs exactly matched independent Python expectations; eight compile rejections and five native runtime-error cases passed. All ten timed builds were executed and their output checked. Five alternating warm-host samples: release engine fresh build median 493.28 ms vs direct rustc on identical emitted source/flags 380.27 ms. This measures aggregate pipeline overhead, not GeneXus performance. Python tests 4/4 and existing Rust tests 50/50 passed (one informational benchmark ignored). Temporary executables cleaned automatically; raw JSON under ignored `scratchpad/gx-engine-validation.json`. Qualified Ripwire edit-check resolved the new validation function with no incompatible callers; quality-delta remains exit 2 and test-gate exit 4. Diff check passed with the existing plans/README.md line-ending warning.

Initial official GeneXus comparison was blocked by fixture authorization/setup; that blocker was superseded by explicit user authorization for KBTeste. See the live follow-up below. Full parity, production workload performance and unsupported types remain unverified. No engine commit/push.

### Authorized KBTeste live follow-up

Selected KBTeste with official SDK 18.0.10.184260; created a synthetic Procedure/variable/source, confirmed refreshed persistence, exercised ten real SDK syntax cases, and removed the task-created procedure with deletion plus independent absence confirmation. Syntax accepted eight inputs and rejected malformed arithmetic and an unresolved call; this is not semantic/runtime parity (undeclared variables, overflow, and zero division pass the syntax layer).

A real targeted build failed before compilation: GeneXus Cloud requires `User`. Supported direct MSBuild inspection found `DEPLOY_CLOUD=Yes`; the contextual allowed-values list contains only `Yes`, and the supported setter rejected `No`. Original setting `Yes` restored/read back; no licensing/configuration bypass attempted. SpecifyOneOnly also failed because no first-build target copy exists. This high-level build path remains blocked; the later custom local pipeline below supersedes the executable/timing blocker for a trivial arithmetic fixture. Evidence: ignored `scratchpad/rust-native-1792026489830048600/` and authored local probe files; details in VALIDATION.md.

Additional independent stress run, seed 130: 503 expressions and 10 branch/loop values passed; eight controlled compile rejections and five runtime-error programs passed again. Three alternating fresh-build samples: engine median 915.74 ms vs identical-source direct rustc 722.70 ms. Different population from seed 129; not pooled or compared to GeneXus. Python tests 4/4 passed; diff check retains only prior line-ending warning. Raw JSON: ignored `scratchpad/gx-engine-validation-seed130.json`.

### Custom local MSBuild comparison — executable evidence

User requested direct MCP/custom MSBuild timings. Created `RustTimingProbe` (`IsMain=true`, internal `CALL_PROTOCOL=CLINE`, Numeric(10,0) Value), then used official CopyModelCommand → working-model SpecifyObjects → Generate/WaitFinalize → Run.Compile through an authored inline MSBuild task. No Cloud deployment or property-validation/license override. Missing design-to-working copy explained earlier generation returning success without a procedure artifact; only actual generated files and executable output counted as success. GeneXus .NET 8 artifact and equivalent gx-engine native artifact both exited 0 and printed exactly `42`, checked nine times each.

Three alternating builds: median GeneXus stage total **10,930.95 ms** (range 10,797.50–11,027.39); MSBuild process including KB startup/close **16,366.35 ms**; release gx-engine CLI including rustc/publication **223.19 ms** (210.38–241.93). Approximate 49:1 stage-total/build ratio applies only to this trivial fixture, not full-engine parity/performance. GeneXus spec was forced and .NET output reuse allowed; generation can process common/pending KB artifacts. Runtime-launch medians: .NET **123.06 ms**, native **5.75 ms**, primarily startup rather than arithmetic throughput. Existing CSS permission and unrelated STYLES diagnostics remain, not hidden.

First successful pre-repeat compilation took 15,316.96 ms alone; not pooled with warm repeats or called a clean-KB benchmark. Raw timings: ignored `scratchpad/rust-native-comparison.json`; authored reproduction scripts `scratchpad/rust-timing-create.py`, `scratchpad/rust-timing.proj`, `scratchpad/compare-native-timing.py`. VALIDATION.md records exact APIs, stage boundaries, limitations, and evidence paths. Fixture and generated artifacts intentionally retained in authorized KBTeste for reproduction; DEPLOY_CLOUD remains Yes. No proprietary artifacts tracked, no engine commit/push.

## Initial numeric checkpoint (historical evidence)

**First independent numeric vertical slice implemented and verified; full engine remains in progress.** Final parent run: `cargo test --offline --locked` passed 34 unit + 7 CLI + 9 native-pipeline tests (50 total), with one informational benchmark intentionally ignored in the normal suite. `cargo clippy --offline --locked --all-targets -- -D warnings`, `cargo fmt --check`, and `git diff --check` passed. The ignored release benchmark was then executed successfully separately.

Mutation evidence: frontend expression-node bound, model format version, backend definite-assignment, incremental premature-commit rollback, compiler deadline, failed-build staging cleanup, and successful-build log cleanup each failed their focused guard under mutation; all mutations were restored and final checks are green. Independent review found blocking cleanup via taskkill and publication of temporary logs; cleanup waits now have deadlines, logs are deleted before publication, and regression checks pass. A reviewer PID-reuse concern was withdrawn after Microsoft/Rust handle-lifetime evidence.

**Measured, no speedup claim:** release microbenchmark, 200 procedures / 40 arithmetic nodes per helper / 8 repetitions. Unchanged session 49.2841 ms vs clean compile 49.2284 ms; one-body edit session 50.0294 ms vs clean 50.3904 ms. These samples show no meaningful advantage and are not SDK or native-build benchmarks. The session reuses parse results but still canonicalizes, clones, and validates/emits the entire candidate.

**Not verified / remaining:** full GeneXus semantic/oracle parity, SDTs/richer types, specifier/navigation/data engine, native-KB providers, persistent per-query scheduling, production MCP integration, SDK-free clean-host proof, broader repository .NET/npm/Nexus/release lanes, cross-platform descendant cleanup, compiler memory/disk quotas, and end-to-end SDK performance. Nothing was committed, pushed, released, deployed, or installed globally; the initial portable-engine milestone did not touch native KBs/databases; the later authorized SDK-only KBTeste follow-up is recorded above. Disposable demo build generations were removed; Cargo's ignored build cache remains.

Ripwire exemplar/orientation and initial edit/quality/test checks exited 3 without output. A later final run produced evidence: edit-check for unqualified `build` exit 1; quality-delta exit 2 (7 gating rows); test-gate exit 4 (14 test files, 9 statically untested symbols). Quality rows: untouched gxwire `EdgeKind::as_str` reported dead; four-variant parser terminator-name match compared as a clone of unrelated gxwire enum string mappings; standard temporary-directory test setup compared to helpers in a separate crate. These are not cleared by refactoring unrelated code or sharing independent crate test internals. New-symbol complexity/verbosity findings also remain visible; lexer/AST match dispatches were kept direct. No baseline/ack suppression was introduced. Static gate is **not green**.

All 14 test-gate files were discharged with the engine suite plus a fresh `cargo test --offline --locked --manifest-path src/gxwire/Cargo.toml`: gxwire passed 65 tests (including existing reindex/scale checks). Static graph coverage remains not verified (ambiguous cross-crate names and unindexed files reported by tool). No gxwire source was changed. Engine files are intent-to-add only so diff review sees new source; no commit/push.

## Expanded differential validation (in progress)

- Acceptance: authored shared-subset edge corpus must compile and execute on both engines; compare every output to an independent expected value. Retain divergences rather than masking native rounding/typing differences.
- Exercise precedence, signed division, 18-digit native numeric boundaries, all comparisons in branches, zero/nested loops, variable case and seeded arithmetic. Separately retain full-i64 and controlled rejection/error checks in the standalone engine lane.
- Native work remains confined to uniquely named synthetic procedures in authorized KBTeste; no cloud deployment, production changes or commits/pushes. Full SDK coverage is not claimed.

### Expanded differential results � 2026-10-15

- Verified native SDK build/execution against engine and independent expected
  outputs: 75 cases / 76 values. Complete source readback and fresh native artifact
  required before execution. Evidence and gap matrix are in `src/gx-engine/VALIDATION.md`.
- Found native .NET generator CS0220 on constant `123456789 * 123456789` despite
  Numeric(18,0) destination; variable equivalent succeeds. Engine gives the correct
  15241578750190521. Added mutation-verified native-pipeline regression.
- Portable safety lane: 76 cases / 77 values, 22 source rejections, 8 invalid JSON/
  document cases, 7 controlled runtime errors. Empty procedure accepted per contract.
- New seed 20261015: 513 oracle values, 8 rejections, 5 runtime errors passed.
  No native broad-parity or representative performance claim.
- Cargo 50 pass/1 ignored; Python 8 pass; Clippy/fmt pass. Ripwire edit-check 0,
  quality-delta 2 and test-gate 4; broader quality/release still not verified.
- Kept owned KBTeste fixture RustEdge7476154929200 for reproduction. One later
  creation failed with AtomicCreateFailed (inner cause absent); successful update
  and build of the existing fixture used instead. No production/cloud work,
  proprietary SDK sources in Git, commit or push.


### Date implementation and measured year-window policy

Continued beyond the text milestone into Date implementation. Native 37-output
probe `scratchpad/date-native-1792031609833525800/` matched the fresh Rust
`typed-dates.json` fixture (report `scratchpad/date-rust-differential.json`).
Supports typed Date helper values, YMDToD, components/IsEmpty/SetEmpty, comparisons,
integer-day arithmetic/differences. Two-digit-year maximum is an explicit optional
project setting, measured here as 2039; absence rejects years 0..99 rather than
guessing. Policy changes invalidate all semantic results and reuse parsed bodies.
Native negative-year exception is separately recorded, not counted as parity.

Fresh checks: engine 87 passed/1 ignored, Python 21, Clippy passed, Date edit-check
passed; quality-delta exit 2 (8 clone gates vs HEAD), test-gate exit 4. See
VALIDATION.md for repro, limitations, mutation evidence and pending broader lanes.
No commit/push/release or production changes. Full SDK replacement remains open.
Continue into further calendar/type capabilities and specifier/navigation/provider
work; this is a progress checkpoint, not a reason to await new authorization.


### Calendar-method follow-through

Implemented AddMonths/AddYears after native characterization rather than stopping
at the Date checkpoint. Day clamping, negative shifts, leap years and empty/minimum
date shifts match all 18 native outputs at
`scratchpad/calendar-native-1792032205289008600/`; fresh Rust differential report
`scratchpad/calendar-rust-differential.json`. Reproduce with the same
`validate_dates.py` command using `--calendar` and those evidence/report paths.
Integral offsets and checked range are admitted; fractions fail explicitly.
`typed-calendar.json` and native-matched Rust regressions cover the authored case.
Fresh engine suite: 90 passed, 1 ignored; Clippy and 21 Python tests passed.
The 37-output base Date lane remains separate. Full SDK parity/release lanes
remain open. Next capability work continues toward DateTime/structured types
and specification/navigation, with existing restrictions unchanged.


### Integral-second DateTime implementation

Continued into DateTime rather than pausing at Date/calendar. Native evidence
`scratchpad/datetime-native-1792032567109039000/` verifies 42 outputs with complete
SDK source/declaration readback and fresh native build/run. `typed-datetime.json`
and `validate_dates.py --datetime` match all 42 in fresh Rust output (report:
`scratchpad/datetime-rust-differential.json`). Covers constructor/components,
midnight rollover, Difference, empty values, invalid date/time distinction and
the explicitly configured 2039 year window. Invalid date plus valid time yields
minimum date with that time; invalid time resets all time components to zero.

Rust DateTime values, helper parameters/returns, comparisons, SetEmpty/IsEmpty,
integral AddSeconds/Difference and controlled range/fraction errors are implemented.
Timezone conversion, milliseconds, display and broader DateTime libraries remain
unsupported. This is not full DateTime/SDK parity. Last full run: 93 engine tests
passed/1 ignored, Clippy passed; Python 22 passed after extending evidence tests.
Additional backwards-rollover/conversion regressions were then added and require
the next full check. DateTime edit-check passed; quality-delta still exit 2 with
8 HEAD-comparison clone gates, test-gate exit 4. No commit/push/release.

Next stage: structured types. Static inspection found a direct-SQL SDT propagation
helper used by WriteService; do not invoke that editing path under the no-SQL rule.
ObjectService.CreateObject and SDTService.UpdateSDTStructure instead use native
SDK creation/Save/transactions. Investigate the supported SDK model-copy route
for prototype propagation, rather than relaxing the restriction or copying native
storage internals. No SDT mutation/probe has been executed at this checkpoint.


### Agent-parallel checkpoint (catalog, structured values, .NET Decimal)

User asked for agents to accelerate full SDK conversion and a final review agent
after completion. Agents delivered: portable KB catalog (`catalog.rs`, 8 tests),
structured Record/Collection runtime (`structured.rs`, 9 tests, owned deep-clone
only; native assignment/Clone semantics NOT characterized), and .NET Decimal
arithmetic (`types.rs`; 288 cases match a local System.Decimal oracle, labeled
.NET evidence, not GeneXus parity). Parent integrated `catalog`, `kb`
(catalog -> compiler bridge) and `structured` into lib.rs, added `kb-check/emit/
build/run` CLI and `fixtures/catalog-demo.json` (runs, prints 42). Repeating
division now yields the 28-digit .NET result; the old typed-pipeline expectation
was replaced. The oracle test is ignored unless `scripts/validate_decimal.py`
supplies fresh results.

Fresh: cargo test 120 passed/0 failed/2 ignored; Clippy -D warnings passed;
validate_decimal.py --full-suite passed; Python 22 passed.

Native SDT lane (official SDK only, no SQL): created SDT RustRecordProbe1015 in
KBTeste (`scratchpad/sdt-native-1792033080780298200`), CopyModel succeeded,
procedure source with SDT variables authored and read back; Specify/Generate
succeed but Compile fails because the referenced SDT class is never generated
(only the procedure key is specified). Next: specify/generate the SDT key too via
official APIs and capture compiler diagnostics. Alias/Clone native output pending.
Capability matrix (`plans/129-sdk-capability-matrix.md`) was not written: agents
hit the usage limit. Full SDK replacement remains far from complete; the final
review agent has not run. No commit/push.

## SDT native build checkpoint (fresh SDK-created SDT, no Cloud)

- Fixed `InProcessBuildRunner.ExecuteBuildBatch` BuildOptions regression from 449b67c4:
  `0x01|0x04|0x08` (BuildCalled|CompileMains|OnlyThemes) -> `BatchBuildOptions = 0xE0|0x3800|0x1` (14561).
  `BuildService.BuildProcess` ORs `0x3800` anyway, so the old value still built but dropped ContinueOnError and added OnlyThemes.
  Regression test `Batch_build_options_request_full_build_of_called_objects`; Worker tests 5099 passed, 5 skipped; `build.ps1` ok.
- Disposable `KBTeste`: SDK-only `RustFreshSdt091817` (ID 175, `Amount NUMERIC(4,0)`) and `RustFreshProbe091817` (ID 723, vars bound via `DataType.ParseInto`, saved and validated).
  Evidence: `scratchpad/sdt-fresh/`.
- A fresh SDK SDT also reports `GenerateObject=False`, `IsGeneratable=False`: this is normal for SDTs, not a creation defect.
  SDT classes are generated only from the build's object list (`ObjectListCalc` -> Specify -> Generate). Every route that produces that list first calls `AskRequiredProperties`, which requires a GeneXus Cloud account while `DEPLOY_CLOUD=Yes`.
- Setting `DEPLOY_CLOUD=No` in memory is rejected (value stays `Yes`). `DeployToCloudAfterSetHandler`/`DeployToCloudValidResolver` enforce "Trial/Learning version supports only Deploy To Cloud environment".
  Skipping that gate would be a license bypass, so it was not attempted.
- MCP finding: `genexus_create object_atomic` with SDT-typed variables failed validation (`src0216 'Amount' invalid property`). In `mode:update` it did not retype existing variables. The SDK `DataType.ParseInto` route works.
- Native SDT defaults, aliasing and `Clone()` remain unverified. They need a GeneXus account logged in (Cloud) or a non-trial license.

### SDT native generation: no-Cloud manual pipeline (2026-10-15)

- `scratchpad/sdt-fresh/manual-pipeline.proj` builds the object list itself
  (`ObjectListCalcCommand` via its internal ctor, `checkReorg=null`) and runs
  `SpecifyCommand` -> `GenerateCommand` -> `GenerateMetadataCommand` ->
  `Run.Compile`. This skips `BuildCommandFactory.AskRequiredProperties`, so
  the GeneXus Cloud check (error 3) no longer fires. Disposable `KBTeste` only.
- Object list includes the SDT (`toSpecify=Procedure,SDT`), but the native
  specifier prints only `Specifying RustFreshProbe091817 (1 of 1)`; with the
  SDT alone it specifies nothing. No `type_SdtRustFreshSdt091817.cs`,
  no `genexus.programs.sdt.bld`, no SDT spec in `GXSPC002`. Compile still
  fails with CS0246 `SdtRustFreshSdt091817`. KBTeste has never produced any
  `type_Sdt*` file, including after the earlier SpecifyAll.
- Strings in the native `Artech.Generator.DotNetCore.dll` mention
  `Read saved SDTs.`, `optimize_sdt_generation`, `force_gx_sdts`: SDT class
  emission is decided inside the native generator; trigger still unknown.
- Status: native SDT assignment/`Clone()` semantics remain unverified.

### MCP fixes found while authoring SDT probes (2026-10-15)

- `genexus_create object_atomic` bound SDT variables by name, so a Procedure created in
  the same call that read `&Var.Member` failed src0216 and was rolled back.
  `VariableInjector.BindVariableToSdt` now uses `DataType.ParseInto` first (resolved
  `StructureTypeReference`). Live check `scripts/tests/live-sdt-member-access.py`
  passes on KBTeste and fails when the fix is disabled (mutation run).
- Terse error envelopes dropped `fieldErrors`; now kept (gateway test added).
- Worker 5099 passed / 5 skipped; Gateway 2511 passed / 35 skipped.

### Procedure parameter modes (2026-10-15)

- Native probes in disposable KBTeste:
  `scratchpad/parm-modes-native-1792068810475057600` and
  `scratchpad/parm-default-native-1792069248962676800`. Observed: `in` copy-in only;
  `out` is empty (0.00) on entry and copied back, also when never assigned; `inout`
  copy-in/copy-back; `Proc(...)` and `Proc.Call(...)` identical; one variable passed as
  in/out/inout ends with the last copy-back (4.00); unprefixed `Parm(&A)` is inout for
  a variable argument and by value for a literal or expression.
- `gx-engine` typed path implements these: `in:`/`out:`/`inout:` parameter prefixes
  (unprefixed = inout), void helpers, call statements in both forms, copy-back in
  parameter order, out targets count as assigned. Format 1 rejects modes and call
  statements. `tests/parm_modes.rs` (6) encodes the native values; mutations of the
  copy-back order and of the out entry value both fail it.
- Boolean/VarChar/Date out defaults probed too (`scratchpad/parm-outtypes-native-1792069401258459300`):
  false / "" / empty (Year 0), matched by `non_numeric_out_parameters_start_and_return_empty`.
- Not covered: explicit `inout:` with a literal; GeneXus' last-out-parameter-as-result form.
- Follow-through: `tests/parm_modes.rs` now has 9 tests. The compiler marks output
  arguments assigned only after evaluating inputs and emitting the call; a regression
  rejected `Helper(&Y, &Y)` with `in:A, out:B` when `Y` was unassigned (failed before
  the fix). Parameter-mode changes invalidate statement callers; an invalid revision
  preserves the previous session. Removing statement-call edges failed the regression.
- SDK compatibility builds against GeneXus16 and GeneXus17Trial passed; focused Worker
  tests passed 57 / skipped 1 for each. These are build/unit checks, not native KB parity
  for those majors. Published SDK18 worker still needs rebuilding after mutation tests.
- Ripwire: unchanged contracts for `BindVariableToSdt`/`TrimErrorEnvelope`, new-symbol
  `parse_parameter`; quality-delta exit 2 (12 duplication/clone findings, including new
  test/setup and trivial enum/default shapes), test-gate exit 4 (large ambiguous graph,
  tests not fully inferred). These gates are not green; no suppressions added.
- Intermediate review agent `03c1508e` is checking the typed-call implementation.
  Native agent `638188ba` is characterizing mutable nested arguments and explicit
  inout literals before any ordering change. Full SDK replacement remains incomplete.

### Portable Parm Rules admission and autonomous continuation

- Shared instructions now explicitly require execution to resume after compaction,
  worker reports, and checkpoints, not another handoff. Canonical source and all
  seven links verified; dotfiles mirror pushed as `a3b9c3f`, unrelated edits preserved.
- Portable catalog accepts only a bounded standalone `Parm(...)` Rules subset;
  modes, argument order and original catalog text are preserved. Unsupported Rules
  and mixed Signature/Rules fail closed. This is not native KB decoding.
- Rules unit tests 2/2 and portable KB pipeline 4/4 pass. Mutation forcing all modes
  to inout fails both Rules tests; restored. CLI loads the saved Parm catalog,
  compiles and executes the final-out-return procedure as `42`, then verifies the
  stored catalog is unchanged. Full offline Cargo suite, strict Clippy, and Python
  tests (22/22) pass; evidence `scratchpad/engine-rules-*`.
- Last-out expression regression and void-call depth/copyback-overflow tests pass;
  parm_modes 11/11. Argument snapshot ordering remains an open native-comparison
  question: independent review reproduced Rust `22` for Pair(X,Bump(X)). Do not
  silently change semantics or claim mismatch before native evidence.
- SDT design and target models both contain Amount NUMERIC(4,0); model-copy loss
  is ruled out for the inspected object. Native SDT class-generation blocker remains.
- Active work: native-order agent 638188ba, Do Case parser agent caee0a90. Next:
  integrate their evidence/tests, resolve ordering against GeneXus, validate Do Case
  natively, then proceed through the remaining capability matrix. No engine commit,
  push, merge or deployment authorized. Final repository check remains build.ps1.

### Native order resolved (not a Rust semantic bug)

- Native agent report `scratchpad/parm-order-native-19b0581ce13e4d3da76765b69d5cf0fe/findings.md`
  verifies nine authored/read-back Procedures and five successful direct SDK builds
  and executions. Pair(X,Bump(X)) returns 22, not 12; X+Bump(X) from X=10 returns
  22 with X=11, as does Bump(X)+X. Rust's existing hoisting matches these cases.
  The review candidate is resolved; inaccurate source-order comments corrected.
- New native-backed tests cover nested call effects/direct operand captures,
  explicit Numeric inout rvalues, and interleaved in/out/inout aliases (final 41).
  Parm suite now 14/14. No general full-language evaluation-order claim.
- Native Do Case probes and Rust parser agent remain active; integrate next.

Validation refresh after native ordering regressions: complete offline engine suite
144 passed, 0 failed, 2 ignored; strict Clippy and offline release build exit 0
(`scratchpad/engine-order-*`). Moving direct operand captures into the nested-call
prelude (a left-to-right snapshot mutation) fails the native-backed ordering test;
restored implementation passes. Gateway ModuleSafetyTests 22/22 and SDK18 Worker
focused compatibility/build/SDT tests 57 passed, 1 skipped. No full SDK parity claim.

Live regression caveat: one earlier harness start returned JSON-RPC -32603 for
operation 33e1eefd0d1847d1aad4ee28dbfa17b3 before mutation assertions; the immediate
rerun succeeded, with evidence live-sdt-member-access-1792070009465857300. No cause
established for that transient failure; do not describe repeated live startup as
universally reliable. Invalid source fieldErrors and rollback were verified on the
successful rerun. Removing the fieldErrors allowlist entry makes its Gateway unit
regression fail; the allowlist was restored and ModuleSafetyTests22/22 pass.

Gate refresh: ripwire edit-check lower_call and project_from_catalog exit0 (new
symbols, no incompatible callers). Quality-delta remains exit2, now 11 clone gates;
includes cross-crate enum-match shapes and test-directory helpers. Scoped KB
 test-gate exit4 despite mapped kb_pipeline.rs: CLI entry execute/main lack graph
coverage and runner is marked unknown. Actual CLI subprocess test did execute and
pass, but these static gates are not green. No gate suppression or baseline reset.
Git diff --check is clean, Python live-harness syntax compiles, and untracked engine
sources contain no exposed exe/dll/pdb/lib/obj/pyc or target/.gx-engine artifacts.

Existing native-differential lanes rerun against the fresh release engine after
parameter changes (no native KB accessed): Date37/37, calendar18/18, DateTime42/42,
typed Numeric/Boolean19 matched with documented checked-storage exclusion, text
17/18 plus documented intentional astral-encoding safety divergence. Validators
checked source/declaration/native-run evidence integrity, not only expected strings.
Fresh reports/logs: scratchpad/order-refresh-{date,calendar,datetime,typed,text}.*.

Next concrete void grammar gap reproduced: release `check` rejects the bare Return
in scratchpad/void-return-gap.json at HELPER line2 column7 (expects expression).
Planned smallest extension after Do Case frontend owner finishes: distinct ReturnVoid
AST statement (preserves existing Return(expr) constructors), typed early exit with
current out/inout tuple copyback, fail-closed in format1 and in value-returning
helpers; add runtime/budget/control-flow/incremental tests and native comparison.
Do not edit frontend concurrently with caee0a90. Native early-return characterization
queued with 638188ba after current requested probes.

In-flight coordinated bare-Return work: parent has added ReturnVoid handling in
backend.rs/typed_backend.rs/incremental.rs and tests/void_return.rs. Frontend owner
caee0a90 was asked to add the distinct AST variant and newline/EOF parser case.
Until that variant lands, builds are intentionally incomplete; the prior144-pass
checkpoint is not current integrated validation. Parent must test/integrate after
owner report. Typed early exit uses current tuple copyback; format1 rejects it.

Additional early-return dataflow bug reproduced with the last built release:
check scratchpad/early-return-assignment-gap.json rejects LOCAL as unassigned for
If False / Return0 / Else Local=7 / EndIf / Value=Local. Terminating branches should
not participate in assignment intersection. Parent patched typed If merge to use
only surviving branches; returns() recognizes ReturnVoid too. Regression includes
both returning-branch positions and a no-return control that must still reject an
unassigned local. Not yet validated: integrated library awaits frontend ReturnVoid.

### Integrated Do Case and bare Return work

Frontend owner added ReturnVoid; builds resume. Parent void-return5/5 and parm-modes
15/15 pass (Character/DateTime new default test remains Rust-only pending native).
Restoring old typed branch-intersection logic makes the fallthrough regression fail;
restored code passes. Native-return comparison is still pending.

Native Do Case report `scratchpad/do-case-native-20261015104124607/findings.md`
verifies5 exact source/rules/variable readbacks and4 fresh SDK builds/executions.
First match wins; no-match/noOtherwise falls through; nested cases select expected
branches; later helper condition is skipped after an earlier match (Hits0.00).
Fixture typed-do-case.json projects these authored control structures (Msg literals
become Print, labeled Hits output becomes its value) without changing decisions or
helper modes. CLI and typed_pipeline native-projection test match all8 output lines.
This is explicitly a presentation-normalized subset comparison, not a claim that
native Msg/ToString libraries or all Do Case behavior are implemented.

Parent awaits frontend owner's final parser/mutation report; still continue with
native bareReturn/default probes and SDT retry. Current source is integrated,
but a fresh whole-engine check is required after owner finishes.

Open integration findings sent to frontend owner:
- Reviewer03c1508e found bare Return before semicolon rejected despite separator
  contract. Runtime entry regression now reproduces failure (line1 column17),
  scratchpad/return-semicolon-before.txt. Not fixed/validated yet.
- Parent found flat Do Case chains bypass effective AST depth: check accepts150
  cases although MAX_NESTING128 (scratchpad/do-case-flat-depth-before.txt). Lowering
  creates nested If ASTs, so guard must count case-chain depth plus body nesting.
  Frontend owner tasked with guard + flat/nested regression; do not run crash-sized
  inputs to demonstrate it. Prior whole-engine passing checks predate these tests.

Follow-on source-compatibility opportunity (not implemented): bounded CLI
`Msg(expr,status)` can lower to existing Print using native evidence in all Do Case
probes; reject other message modes rather than implying interactive UI parity.
After current frontend owner finishes ReturnVoid/Case bounds, this could remove
literal Msg-to-Print projection from fixture comparisons. Numeric ToString/Trim
remain separate unimplemented library semantics; do not silently normalize them in
claimed exact-source compatibility. Keep focus on native validated increments.

Public CLI depth regression added in tests/cli.rs: check/build must reject150
lowered Case levels with a nesting error before creating .gx-engine artifacts.
Current check fails this regression exactly as expected (case-depth-cli-before log),
so the depth bug now has an executable user-facing regression as well as repro.

Read-only SDT review83d97520: generated failing Procedure references expected SDT
GUID447527b5-9210-4523-898b-5dccb17be60a/id175 and Amount member, but its class file
and Nemo JSON spec are absent. This does not prove why generation skipped it.
SDK inspection exposes a Nemo gate (FindingNemo + service availability + active
object generator), but applicability to the legacy path is unproven. Native agent
queued to retry with the newly mutation-verified LiveSdtMember60744600 objects,
then inspect current flags/services read-only if needed. Do not enable hidden
features, change licensing, alter Cloud config, or treat empty Nemo Objects list
as evidence of unsupported SDTs. Exact read-only probe API requested from reviewer.

### Oracle prerequisite: stale gateway PID ownership

Native Character/DateTime probe was blocked before authoring by stale ownership:
record GatewayPID34556 now identifies a Brave renderer; WorkerPID31580 is absent.
Agent preserving exact-key diagnostic/backup before scoped cleanup; no user KB data
or unrelated process may be deleted/killed. This is not semantic probe evidence.

Parent confirmed Acquire/Reconcile used PID-only liveness for gateway ownership,
although Worker identity already used start ticks. Added gateway start timestamp
to new records and identity comparison in both paths; legacy missing timestamps
remain conservatively reserved while PID is live, and identity access uncertainty
also preserves ownership. Two synthetic stale-reservation cases failed before
fix (Acquire conflict; Reconcile kept record), then all6 ownership tests passed,
including live/legacy reservation guards and worker PID-reuse protection. No real
KB opened and no process killed by these tests. Published Gateway is not updated
yet; real native workflow with new binary remains unverified. New Gateway full
suite/final build required; native agent uses backed-up scoped cleanup for now.

### Current integrated checkpoint (still not full SDK parity)

- Frontend owner fixed semicolon Return and effective lowered Case depth; parser,
  public CLI150-case rejection, void-return5/5 and typed native-case projection pass.
- Added distinct MsgStatus AST for format2 CLI text Msg(expr,status); other modes,
  non-text implicit conversions and format1 are rejected. Literal/concat output,
  returned text, incremental dependencies and rejections pass. Removing Text guard
  makes rejection test fail; restored. Static independent review found no defect.
  Native case fixture now admits literal Msg source directly; numeric trace display
  remains explicitly projected. Interactive GUI/message semantics are unsupported.
- Full offline Rust159 passed/2ignored; Python22/22; strict Clippy and fmt checks pass.
  Fresh release run case fixture emits all8 expected lines, bareReturn repro now
  emits1 then2, and excessive Case depth is rejected (line256, limit128).
- Gateway full suite2517 passed/35skipped after PID-reuse fix. Read-only review found
  its new start-time and fail-closed policy sound. Pre-existing conditional Reconcile
  TOCTOU remains: it lacks Acquire's cross-process mutex; reviewer found no normal
  caller path holding a stale snapshot. Not a demonstrated new regression; record
  as pending lifecycle hardening, not a silently fixed guarantee.
- Native out defaults: parm-out-defaults-native-20261015111326252 native-report.json
  shows2 fresh successful builds/executions and exact authoring readbacks. Character
  resets OLD to empty on entry, Q-space survives copyback; DateTime resets2001 to
  empty/year0, then nonempty2024 survives copyback. Rust primitive interface test
  now matches these entry/writeback observations. Native Len(Q-space)=1 while the
  blank survives; do not infer .Length() parity from this. Dedicated comparison queued.
- Native agent rebuilt at safe gap: build-lease-fix-20261015111903646 stdout/stderr
  show successful Gateway/Worker/Rust builds and artifacts, but detached launcher
  did not retain exit code. Live new ownership timestamp capture and final parent
  build with observed exit code remain pending. Unrelated runtime Gateway32152
  and reused dotnet18168 left untouched. SDT retry + bareReturn native probe remain.

A2 next-path reconnaissance: gxwire's XML-related entity-kind detection is only
bounded header/folder heuristics (src/gxwire/src/cache/mod.rs extract_type_guid_from_xml,
kind_from_type_guid, detect_entity_kind), not a full Source/Rules/Variables export
reader. No quick_xml dependency/import exists. Do not mistake it for native KB or
lossless XPZ/XML import support. A native-export bridge would first need an official
export of only authored disposable fixtures, format/evidence review and a bounded
real parser; no dependency or parser added speculatively. Direct native KB decode
and generic Rules/Events remain open P0/P1 scope.

### Native SDT blocker resolved for the numeric-field oracle

After agent638188ba hit account usage limit, parent resumed sequential SDK work.
New main SDM87563700 was authored/read back with IsMain=True/CALL_PROTOCOL=CLINE
and the SDK-created RustFreshSdt091817 Numeric(4,0) SDT. The manual oracle had omitted
AfterSpecifyGenerate Nemo commands; added only the SDK public NemoSpecify then
NemoGenerate local commands (no flags/license/Cloud changes). Initial14561 attempt
still omitted the old SDT from ObjectsToSpecify. Rebuild14563 (14561|Rebuild2)
explicitly specified both, emitted type_SdtRustFreshSdt091817.cs, freshly generated
and compiled main, and executable exited0 with default=0, alias_first=9,
alias_second=9, clone_first=9, clone_second=11. Evidence:
scratchpad/sdt-fresh/typed-sdt-nemo-rebuild.log and
scratchpad/sdt-main-native-1792075205187446300 exact Source/Rules/Variables readbacks.
Thus scalar SDT assignment aliases in this case; Clone separates this numeric field.
No collections/nested clone behavior established. SDK copies remain outside Git.

The alternate live-regression SDT LiveSdtMember60744600 has a Varchar Amount (the
firstItemType Numeric(4.0) authoring input did not make it numeric). Its fresh main
FSM10924400 specification rejected numeric assignments; this was not an SDT compile
success. That failed baseline is preserved. The oracle project now throws on false
Specify/Generate/Compile instead of reporting an MSBuild0 for a failed SDK phase.
Existing production BL.Build path uses factory orchestration; the missing Nemo
phase belonged to our manual oracle, not an established production SDK defect.

Additional native fixtures resume-native-1792075013410821800:4 exact author/readbacks,
2 fresh SDK build/execution passes. Bare Return exits nested While/If and preserves
out7; main emits value7/after only. Text Character/VarChar Length and Len both omit
trailing ASCII spaces: Q-space1, space-Q-space2, spaces0; stored text stays intact.
Windows literal C:\new\test is preserved, length11. Fixed Rust .Length() accordingly
and admitted literal backslashes (not escape interpretation); both tests red before
fix, pass after (old backslash-rejection expectation removed as superseded contract).
Captured new published live ownership timestamp: Gateway27256/start639276718134349534,
Worker7352/start639276718142028929; exact checkout+KB key verified in live-ownership.json.

Required lease-change read-only test-live.ps1 gate passed1/1 exit0 with explicit KB:
scratchpad/live-753951a53af142e58bb1376f84248fec/live-summary.json, Gateway34916,
Worker29716, selection valid/kbteste, errorCount0, cleanup passed/processExitedtrue.
Unrelated runtime Gateway32152 untouched. Full SDK replacement still unfinished.
Next: integrate bounded numeric SDT compiler syntax/runtime with observed alias vs
Clone behavior (NOT Rust owned-record default), then broaden native semantics/tests.

### Numeric-record compiler/catalog/parameter integration

Implemented independently in model/frontend/typed_backend/incremental: bounded
record_types, numeric member reads/writes, new(), alias assignment, scalar Clone.
Generated executables use only std + existing inlined runtime, not structured.rs
Serde linkage. Schemas reject duplicates, bad refs/types and >1024 fields; schema
changes invalidate incremental sessions. Existing literal-backslash/trailing-space
length fixes are included in the full test suite.

Portable catalog now follows explicit Uses -> StructuredDataType dependencies,
requiring exactly one Fields JSON map and no SDT references. Calls must target
Procedures. Invalid/raw-injected/duplicate Fields fail closed via bounded Project
validation; omitted dependencies do not infer schemas. kb-run record case builds,
executes and prints11, saved catalog remains unchanged.

Fresh native record parameter oracle: scratchpad/record-params-native-1792076189364263600.
Four unique objects with Source/Rules/Variables readbacks, public local Nemo rebuild,
fresh executable build/run exit0. Member-mutation results (entry/First/old alias):
in=7/9/9, out=0/9/7, inout=7/9/9. Second fresh oracle
scratchpad/record-rebind-native-1792076401968894100 replaces each formal with new():
in=7/7/7, out=0/9/7, inout=7/9/7. Rust tests match all18 values. Void record
helpers admit only same-schema variable arguments; returns/expression outputs and
record parameters on value-returning helpers are rejected for now. Out before
early Return initializes caller to zero-filled record. No Cloud/config/license/SQL
changes, no production KB touched.

Validation: full cargo offline locked 173 passed /2 ignored; fmt check, Clippy -D
warnings, release build and Python22 passed. Alias mutation (copy instead of alias)
failed native-backed regression; restored full suite passed. Catalog regression was
red before bridge (Calls-only), green after (6 tests, including native executable CLI).
Ripwire edit-check project_from_catalog passes (new symbol). Quality-delta exits2
(10 gating duplicate/test verbosity findings across existing whole diff); test-gate
exits4 with broad ambiguous/unresolved C# impact. These gates are NOT green.
Independent record review caee0a90 was requested twice but blocked by team usage
limit (~195min); no independent review claimed. Existing review reports refer to
earlier code states, not the new record compiler. No Genexus commit/push.

Global autonomy instruction updated to reconcile delayed reports and continue
independent work when a reviewer is unavailable. Canonical source and seven links
verified with pathlib; dotfiles-only commit7be9ec4 pushed. Unrelated Pi config edits
in dotfiles preserved.

NEXT: broaden native SDT scalar field observations (text/Boolean/calendar) before
changing runtime representation; numeric-only records remain a bounded subset,
not full SDK replacement. Run root build.ps1 last after current validation and
record its observed exit code; independent record review remains pending.

### Native SDK XML projection and broader scalar records

Text record oracle record-text-native-1792076741653949900: SDK RST54161700,
RTM27934700;9 observations for empty text fields, preserved spaces, alias/Clone,
Length5. Calendar oracle record-calendar-native-1792076977979030500: SDK
RSC79223900/RTM06759200;12 outputs for Boolean/Date/DateTime defaults, aliases
and Clone. Added dependency-free generated tagged scalar fields; both Rust
fixtures match. All scalar kinds now admitted in flat record schemas.

Added roxmltree0.21.1 (std only, no positions) for bounded readonly SDK ExportFile
compilation projection, not native DB decoding/lossless SDK import/XPZ extraction.
Byte cap8MiB, node cap50000, depth128, no entity resolution/DTD content. Duplicate
IDs/names/properties/parts, unknown kinds/parts/declaration content, module names,
missing Calls/Uses, nested SDTs fail closed. Shared AST dependency traversal moved
from incremental.rs to dependencies.rs; reused for XML closure. XML fixture tests
are hand-authored, not copied SDK exports. xml-check/emit/build/run are SDK-free.

Real XML E2E: record-exchange-native-1792077703292230800 creates RTM95411000
using existing scalar RSC79223900. SDK build/run exit0. SDK XPZ exports only those
two authored objects: record-exchange-export-1792077741100664600. Bounded external
extraction of the sole authored.xml entry stays scratchpad; release xml-run consumes
UNCHANGED XML and matches native alias/alias/alias/clone. SHA unchanged; report
scratchpad/record-exchange-differential.json. Second real E2E includes5 exported
objects (RTPM69933200 + RTPHIN/OUT/INOUT69933200 + RSC79223900):
record-text-params-native-1792078140969840100 and
record-params-export-1792078186981298200/differential.json; all9 native/Rust stdout
lines match, exits0, XML unchanged. Full XML+records tests182 passed2 ignored,
Clippy/release/Python22 passed before subsequent Str/Trim changes.

Native SDK text-tree export action returned planned1/exported1 SDT for targets
including a Procedure; root cause not investigated. Do not claim that path exported
both. Official XPZ contained both (or all5) selected objects and is the proven path.
No raw SDK XML, ZIP, provenance metadata or proprietary artifact added to Git.

Independent reviewer8c768d39 completed full Rust suite and found P2: XML allowed
CALL_PROTOCOL values while dropping semantics. Added CLINE positive + HTTP/SOAP/
invalid/empty negative test: red admittedHTTP before fix; now specified protocol
must be CLINE case-insensitively. Exchange7/7 pass. Reviewer confirmed fix closes
finding; no further concrete defects in identity/bounds/closure/record semantics.
Review is scoped, NOT full SDK replacement approval. Root build after earlier
records stage observed exit0 at scratchpad/records-root-build.txt; rerun LAST
after current changes (XML/Str/Trim/next runtime) before final handoff.

### Native Str/Trim and next nested/collection phase

Native probe str-trim-native-1792078418337983400 RTM41852400: Str defaultwidth10,
right padding on left, rounding1.25->1,1.5->2,-1.5->-2, overflow10stars; Trim removes
ONLY ASCII spaces, preserving TAB and NBSP; variable/member .Trim works. Literal
string .Trim failed SDK Save src0051 (original str-trim-native-1792078376419240400).
At this checkpoint, only one-arg default Str and Trim function + variable/member .Trim()
were implemented; explicit width/scale Str remained rejected (native observed3 examples
but not modeled). The later implementation/test milestone is recorded below.
Default runtime reuses NumericType render10,0 with left padding; Text::trim_spaces
uses trim_matches(' '), not Unicode whitespace trim. Typed builtin names centralized
in frontend for codegen/XML closure. Native-backed targeted test passed11 values;
boundary/arity/type/receiver/incremental tests pass. New original numeric SDT E2E
through unchanged SDK XML now matches all5 lines default0/aliases9/9/clone9/11:
scratchpad/numeric-record-export-1792078700319740300/differential.json. Native exe
and Rust both exit0; XML unchanged. Full post-Str suite still pending.

NEXT phase (in flight): parent to extend model/frontend/typed_backend for nested
Record and Collection schema refs, shared runtime, defaults with bounded constructor
budget, generalized member/Item references, Count/Add/shallow Clone. Agent3d6aec9e
owns ONLY record_runtime.rs + tests/record_runtime.rs + lib.rs module line. Parent
must not edit those until report. Exact API in spawn task: Field scalar+Record+
Collection, Record/Collection Clone aliases Rc storage; shallow_clone copies outer
map/list preserving nested refs; checked get/set/schema/add/1-based item. Std only,
source-inlineable, no serde. Existing structured.rs deep_clone remains separate.

Critical fresh native evidence:
- Readonly public API dump scratchpad/sdt-collection-public-api.txt: Root SDTLevel
  AddLevel(name,bool), IsCollection, CollectionItemName; no flags changed.
- SDK-created RSN03944700 with Child noncollection and Rows collection (Numeric
  Amount4,0). record-nested-native-1792078896703774100 RTM88868600: defaultChild0,
  assignment aliases9/9; root.Clone then Child mutation =>11/11. Clone is SHALLOW
  across nested child references, not a deep clone.
- record-collection-native-1792079116541738600 RTM45184200: defaultCount0, add Row
  gives1/item7. Root.Clone shares LIST+items: mutate to9/add11 => FirstCount2,
  SecondCount2. Rows.Clone separates LIST ONLY: item1 mutation13 visible through
  BOTH, adding third row changes only second => FirstCount2,SecondCount3.10 outputs.
  All readbacks/fresh builds/runs pass. Correct row variable type is
  RSN03944700.RowsItem (SDK hint). RSN.Rows bound root and specify failed spc0023,
  preserved at record-collection-native-1792079079187652000. Inline Add(new())
  failed SDK Save src0051 line3 char20 at ...1792079019228452500. Use Row=new(),
  Rows.Add(&Row), Rows.Item(1).Amount assignment. Collection/nested Rust pipeline
  parity NOT YET implemented/claimed. Native failures and harness syntax failures
  remain scratchpad; no SDK settings, native SQL, Cloud, other KB, Git changes.

### Documentation reconciliation: nested/collection status and current gates

The preceding `NEXT phase (in flight)` paragraph is a historical checkpoint and is
superseded by the current compiler source: `model.rs` admits nested Record/Collection
references with cycle checks; `frontend.rs` contains member/clone/item and assignment/
Add nodes; `typed_backend.rs` emits nested defaults, member access/updates, Count,
Add, Item and shallow Clone through `record_runtime.rs`. Portable fixtures now exist
in `fixtures/typed-record-nested.json` and `fixtures/typed-record-collection.json`,
with compiler regressions in `tests/typed_pipeline.rs` and runtime coverage in
`tests/record_runtime.rs`.

The official GeneXus observations above remain the evidence boundary: nested
`Record.Clone()` separates the outer record while preserving child aliases;
`Collection.Clone()` separates list membership while preserving aliases to item
records. That native behavior has been measured in the disposable KBTeste evidence
paths already listed above. Source/tests show the Rust implementation intent, but a
fresh integrated Cargo run and Rust-vs-native comparison of these current nested and
collection fixtures have not been run in this documentation pass. Do not describe
parity as verified. At this prior checkpoint the SDK XML projection remained
flat-scalar-only; it is not a native KB decoder or a general SDT importer.

### Tested milestone checkpoint: nested XML, Str formatting, CLI diagnostics

Subsequent owner reports supersede the in-progress notes above:

- The exchange compile error E0382 was fixed by continuing the needed traversal;
  `cargo test` on the exchange tests passed 10/10. The XML projection now handles
  bounded nested Record/Collection Structure data. Its direct tests use synthetic
  schema IDs/references and verify the `RowsItem` alias. This is not a claim of a
  native nested/collection ExportFile roundtrip.
- Explicit `Str(value, width[, scale])` is implemented for integer literal width
  1..18 and optional integer literal scale 0..width; dynamic width/scale are rejected.
  Three outputs match recorded native oracle cases 7..9. The focused command
  `cargo test --test str_format --test cli_diagnostics --test diagnostics` passed
  2+3+5 tests. Broader formatting parity is still uncharacterized.
- Leading `--diagnostics=json` is integrated. JSON goes to stderr without changing
  stdout; the default remains human-readable. It reports explicit operation-level
  phases; Build intentionally includes code generation plus rustc as one phase.
  Locations are available only through the direct parser diagnostic API, not the CLI
  envelope. These categories are gx-engine semantics, not inferred SDK-code matches.
  The same focused command above passed the CLI diagnostics and diagnostics tests.
- At this checkpoint the `For` semantics lane was still owned by worker `45c3f8de`
  and pending; the later bounded-For milestone below supersedes that status. The
  docs-only pass itself did not run Cargo, probe GeneXus, or change source/tests.

### Tested checkpoint: bounded Numeric For/EndFor

Owner-reported implementation and native observations:

- Fresh GeneXus evidence at
  `scratchpad/for-native-probe-1792100818161492200/ascending-report.json` confirms an
  ascending inclusive loop producing 1, 2, 3 and a post-loop iterator value of 4.
- Format 2 implements Numeric `For`/`EndFor`; `Step` is restricted to a nonzero
  integer literal. At this checkpoint focused `for_loop` tests passed 2/2; the combined
  gate was reported 54/54 (47 typed, 2 For, 2 Str, 3 CLI diagnostics). This is a prior
  gate snapshot, not the current final suite result.
- This does not establish broad `For` parity. Dynamic-bound and descending semantics
  remain in flight; no behavior beyond the exact recorded native observation should be
  inferred. These test results are owner-reported and were not rerun by this
  documentation-only update.

### Owner-reported update checkpoint: native XML shapes, catalog pipeline, and For evaluation

- The exchange suite now passes 11/11. New native hierarchy exports are available at
  `scratchpad/nested-record-export-1792080479819133800/authored.xml` (`RSN03944700`,
  nested `Child` and `Rows` collection) and
  `scratchpad/custom-item-export-1792080825587526300/authored.xml`
  (`idCollectionItemName=Entry`). These establish native export shapes, not Rust XML
  projection parity: running the exports through XML build/run and comparing exact
  output is the next gate. It remains unverified here.
- Catalog `kb_pipeline` tests passed 10/10. Nested `kb-run` printed `7` and `9`, leaving
  the catalog unchanged. Reachability traverses explicit `Uses`; direct `Uses` are
  required for Procedure Record/Collection variable and return types and nested fields.
  Unknown/undeclared references and schema cycles are rejected. Limits are 1,024
  Procedures, 128 schemas and 1,152 reachable objects. Mutation checks confirmed that
  removing the `Uses` guard fails three tests and replacing the scalar fixture field
  fails the recursion test; fixes were restored and the reported checks were green.
  This validates the portable catalog/compiler path, not native SDK parity.
- For native evidence now also observes dynamic-start evaluation once and `To`
  reevaluation at each condition; the matching helper test passed. The `for_loop` suite
  is being expanded to six tests; after initializing `J=0` in its definite-assignment
  fixture, the owner reports 6/6 passing. Broader dynamic-bound and descending behavior
  remains in flight.
- At this checkpoint, a manifest-red regression was proven and fixed and the acceptance
  owner had corrected test casing; the full suite rerun was still pending. The subsequent
  full-suite result is recorded below. The earlier 54/54 combined gate is a prior
  checkpoint, not evidence for the current full suite. No tests or native probes were
  run by this documentation-only update.

### Owner-reported validation checkpoint before LEN/catalog followups

- At this checkpoint the full Rust test suite exited 0; owner log:
  `scratchpad/engine-fanout-full-tests.log`. The parent later counted the suite; totals
  and applicability to later increments are recorded below. This is not a complete
  validation result.
- All six `For` tests passed, including start-once and `To`-each-condition helper side
  effects, plus incremental dependency/budget/limit coverage. The fixture initializes
  `J=0` to satisfy definite assignment. Broader native dynamic-bound and descending
  behavior remains uncharacterized.
- The manifest metadata regression was fixed after reproducing the red state; format
  metadata is now `v1i64`/`v2typed`. An independent CLI check→emit→build→artifact
  execution→run acceptance check passed 2/2.
- Diagnostics public API tests passed 5/5 and CLI tests 3/3. Real phase end-to-end tests
  are being extended; this does not establish SDK diagnostic equivalence.
- Clippy's last reported result failed in exchange `parse_sdt` due to eight arguments;
  no newer Clippy result was supplied. Do not report overall validation complete until
  Clippy and the real phase E2E lane have been resolved. None of these checks were
  rerun by this documentation-only update.

### Latest incremental owner reports: LEN and catalog closure

- The last counted full Rust suite passed 224, failed 0, ignored 2 across 25 harnesses
  (`scratchpad/engine-fanout-full-tests.log`). That run predates the following LEN and
  catalog increments; no later full-suite total was supplied.
- `Len(text)` is implemented with the same UTF-16-unit and trailing-ASCII-space
  semantics as `.Length()`. `len_builtin` passed 2/2, comparing empty text (0), a
  backslash path (11), padded Character (2), and an astral character (4) against
  `.Length()`. This is not a separate full native `LEN` differential.
- `fixtures/typed-for.json` is now a runnable ascending-loop example. Native `Step`
  characterization and deeper nested-export work remain active; the native XML exports
  still need Rust XML build/run and exact-output comparison.
- Owner `b57cada1` separately reports the second catalog increment `kb_pipeline` 12/12:
  transitive `Probe`→`Child`→`Grandchild` plus `Row` emitted `7`, `8`, `9`, with the
  catalog unchanged. A missing `Child`→`Grandchild` `Uses` edge rejects; direct `Uses`
  for typed Record returns is covered; 1,024 Procedures are accepted and 1,025 rejected.
  Mutating each of the transitive-Uses, return-Uses and procedure-threshold guards made
  the test red; restored code passed. The owner ledger update is still pending, so this
  entry records the report separately.
- The next catalog check is whether a missing direct `Calls` edge is incorrectly
  admitted when its target is reachable transitively. No result yet. The earlier Clippy
  failure has no updated status; overall validation remains incomplete. This
  documentation-only pass did not rerun tests or native probes.

### Deep SDT hierarchy projection and the depth-2 collection item alias

- SDK XML exchange now projects SDT `<Level>` trees recursively: one synthetic schema
  per Level (depth-first nesting, `Uses` edges to direct child Levels only), scalar
  Items only, nested collections admitted from `AttCollection=True`, and fail-closed
  on missing scalars, duplicate Level names/GUIDs, or synthetic identity collisions.
  Recursion stays inside the existing XML depth bound.
- Alias registration is evidence-gated and was extended in this increment: direct
  children of the root register `Root.Name` (composite) or `Root.Item` (collection
  item), and depth-2 collections additionally register the full-path item alias
  `Root.Child.Item`; deeper collections and non-root composite names stay
  unregistered and fail closed when referenced.
- Native evidence captured 2026-10-15 against disposable KBTeste (raw exports stay
  outside Git):
  - Round 1 (`scratchpad/probe-deep-aliases.py`, evidence under
    `C:\Users\swluc\AppData\Local\Temp\opencode\deep-alias-probe-20261015195027`):
    created `NativeDeepSdt85299100` (`RootValue` VarChar(30); `Child` { `ChildValue`
    VarChar(40); `Grandchild` { `GrandchildValue` Numeric(8) }; `NestedCollection` {
    `NestedValue` VarChar(24) } }) plus a Procedure whose item-alias variable wrote
    `&V.NestedValue = "i"`; build/specify/generate/compile and run exited 0.
  - Round 2 (`scratchpad/probe-deep-binding-registry.py`, evidence
    `...\deep-binding-registry-20261015195329`) used the Variables readback text as
    the discriminator: the SDK registry resolves only
    `NativeDeepSdt85299100.Child.NestedCollectionItem`; `...Child`, `...Child.
    Grandchild`, `...Grandchild`, `...Child.NestedCollection`, `...NestedCollection`
    and flat `...NestedCollectionItem` all collapse to the root binding. This proves
    the round-1 `src0216` failures were worker-binder artifacts (dotted typeName
    collapse in the fallback binding), not native rejections.
- Implementation and gates on the final tree: exchange suite 16/16 -- two new tests
  cover catalog resolution plus `Uses` wiring of `sdt:Deep.Child.NestedCollectionItem`
  and an `xml-run` runtime case (`&Nested = new()`, `&Nested.NestedValue = "i"`,
  message `deep_item=i`); the rejection test now also pins
  `sdt:Deep.Child.NestedCollection` (collection level name) and `sdt:Deep.Grandchild`
  (depth-2 composite) as unregistered. Mutation check: gating the item alias at
  `depth < 1` made both new tests fail with `unresolved XML SDT declaration`; the fix
  was restored and the suite rerun green.
- Real-export e2e: `scratchpad/run-deep-projection-check.py` injects a companion
  Procedure into the captured export
  `scratchpad/deep-sdt-native-export-1792102090401/xpz-contents/exportfile-sanitized.xml`
  and runs `xml-run`: exit 0, stdout exactly
  `deep_default=0 / deep_write=42 / deep_count=0 / deep_text=child / deep_item=i`
  (line-separated), captured input unchanged. The item variable's type is the exact
  round-2-preserved name `sdt:NativeDeepSdt85299100.Child.NestedCollectionItem`.
- Full suite on the final state: 241 passed, 0 failed, 2 ignored across 27 binaries
  (`scratchpad/engine-gate-6.log`); `cargo clippy --all-targets -- -D warnings` and
  `cargo fmt --check` both exit 0.
- Test-infrastructure fix in the same increment: `tests/collection_indices.rs` and
  `tests/for_loop.rs` temp-dir roots now include a per-process `AtomicU64` sequence.
  This machine's `SystemTime::now()` has a ~100 ns tick (measured on the order of a
  million consecutive duplicate nanos), so pid+nanos-only roots could collide across
  parallel tests and the first `Folder` drop deleted a sibling's in-flight build tree
  (`cannot read compiler log: ... os error 2`). Same fix class as the earlier
  `exchange.rs` helper.
- This is a portable-subset gate; no SDK replacement or native-KB parity is claimed.
  Open work: dynamic collection-index native differential, more `Str` cases, alias
  evidence for depth >= 3 collections, native KB reading, Specify/navigation/SQL,
  Rules/Events beyond `Parm`, and Gateway/Worker wiring.

### Dynamic collection-index native differential (truncation toward zero)

- Native probe `scratchpad/probe-dynamic-indices.py` ran two rounds against
  disposable KBTeste (evidence
  `C:\Users\swluc\AppData\Local\Temp\opencode\dynamic-index-probe-20261015205601`
  and `...\dynamic-index-probe-round2-20261015205946`), using SDT `RSN03944700`
  (`Rows` collection, item `RowsItem.Amount`). Each case was a fresh Procedure
  that built (exit 0) and executed natively:
  - In-range variable index (`&I = 1`/`2`), expression index (`&I + 1`, `2 - &I`),
    and For-driven traversal all matched the engine exactly: `dyn_var=7`/`dyn_var2=9`,
    `dyn_expr=9`/`dyn_expr2=7`, `for_item=7`/`for_item=9`.
  - Invalid indices (0, -1, 3 with Count=2) build successfully but terminate at
    runtime with `System.NullReferenceException` (exit 1, stack trace on stdout).
  - Fractional indices are accepted natively and truncate toward zero: 1.5 -> item 1
    (7), 1.9 -> item 1, 2.7 -> item 2 (9), 0.9 -> 0 (runtime failure), -0.5 -> 0
    (runtime failure). Round 2's 1.9 and 2.7 cases discriminate truncation from
    rounding.
- Engine alignment: `Decimal::truncate_to_i64` (checked, truncates toward zero)
  now converts collection `Item` indices in `typed_backend.rs`. Nonpositive and
  out-of-range results keep the controlled diagnostic (exit 70) where native crashes
  with `NullReferenceException`; that mechanism difference is a deliberate safety
  divergence recorded in the README. Tests: a new `collection_indices` truncation
  test (1.9/2.7/1.5 -> 7/9/7 on a populated collection; 0.9 and -0.5 -> controlled
  error) replaces the old "fractional is non-integral" expectation, and
  `typed_values` adds unit coverage for `truncate_to_i64`. Mutation check: reverting
  the call site to `to_i64` failed the new test with `expected integral Numeric
  value`; restored.
- Gates on the final state: 243 passed, 0 failed, 2 ignored across 27 binaries
  (`scratchpad/engine-gate-7.log`); Clippy `-D warnings` and `cargo fmt --check`
  exit 0. README, capability matrix, and CHANGELOG updated.

### Str picture rule native differential (overflow, rounding, rendering)

- The black-box `StringUtil.Str` harness (`scratchpad/str-util-probe`; needs its
  AppDomain assembly resolver on this machine) reproduced every recorded KBTeste
  probe round before it was trusted, so the formatter could then be interrogated
  with `KBTeste` at rest.
- Two dense native scans were captured: `scan-1.txt` (8,398 cases; widths 1..5,
  scales 0..3, magnitudes 0..12) and `scan-2.txt` (21,760 cases; widths 1..8,
  scales 0..4, magnitudes up to 999,999), plus ~220 labeled discriminator rows
  (`cases-all.txt`). A trailing-zero round (`1.50` vs `1.5`, `10.0` vs `10`)
  showed the formatter depends on the value only, so the engine's normalized
  decimal storage is equivalent.
- Fitted rule (0 mismatches over all 30,158 scan rows; iterated in
  `fit2.py`): places = width - scale - (scale > 0); `Str` prints asterisks iff
  `|v| + 0.5*10^-scale > 10^places` for non-negative values and
  `|v| + 0.5*10^-scale > 10^places - 1` for negative ones (the sign reserves one
  picture position); otherwise the value rounds half away from zero at the
  display scale, the integral zero renders only when a place exists, a value
  rounding to zero loses its sign, and rendering left-pads without truncating
  (a carry may exceed the declared width by one character).
- Engine alignment: `types::format_str` was rewritten over the 256-bit `Wide`
  intermediate (exact power-of-ten comparison, no floats); `Decimal::rounded`
  and `NumericType::render` stay untouched for Numeric-variable display.
  A temporary differential test ran all 30,158 scan rows against the engine with
  zero mismatches and was deleted; the committed
  `tests/fixtures/str_matrix.tsv` carries 524 discriminating native rows checked
  by `str_matrix_matches_recorded_native_runtime`.
- Mutation check: removing the negative-sign allowance made the matrix test red
  on `-9.5`/`-0.05` boundary rows; restored green. Gates on the final state: 244
  passed, 0 failed, 2 ignored across 27 binaries
  (`scratchpad/engine-gate-8.log`); Clippy `-D warnings` and
  `cargo fmt --check` exit 0. README, capability matrix, and CHANGELOG updated.

## MCP Gateway engine backend switch (integration seam, no behavioral swap)

- User request: a flag to swap normal SDK usage for gx-engine, restricted to
  100%-supported parts, freely switchable for experimentation, with the whole
  integration base ready for the ongoing conversion. Delivered as a Gateway-side
  seam plus a capability gate; no product operation is served by the engine yet,
  which is the constraint, not a shortcut: `str_format` is the only capability
  with full-parity evidence and it has no product consumer.
- New Gateway pieces: `EngineBackendSettings` (config `Engine.*`, non-strict
  `GXMCP_ENGINE_*` overrides; strict documents reject all four variables because
  the backend is a behavioral/execution switch), `EngineCapabilityRegistry` +
  `EngineRouter` (status = claim with evidence; only `Parity` routes;
  `Fallback=error` refuses with `EngineCapabilityNotParity`), `GxEngineLocator`
  (explicit path authoritative → Gateway dir → `publish/` → checkout cargo
  release/debug → cwd → `PATH`), `GxEngineProcessRunner` (concurrent pipe drain
  plus process-tree kill on budget expiry, restating the two issue-#498
  lessons) and `GxEngineClient` (`--version` handshake, 10s probe budget).
  `gx-engine` itself gained `--version`/`-V` (`gx-engine <version>`).
- `genexus_doctor` carries an `engine` block (backend, fallback, capability
  counts, `routedOperations`; with `Backend=gxengine`: executable / available /
  version or a structured probe error) and warns when the switch is enabled but
  the binary is unusable. `docs/engine_backend.md` documents the switch, the
  capability table and the rules for adding the first routed capability.
- Evidence: 44 focused Gateway tests (registry invariants with evidence-path
  existence, routing matrix, settings/env precedence, strict-config
  accept/reject, locator authority, version parsing, stub probes, doctor
  reporting, real-binary `--version` when the checkout has a cargo build) and
  the full `GxMcp.Gateway.Tests` suite at 2560 passed / 0 failed / 35 skipped;
  Rust `cargo test` green with `cargo fmt --check` and Clippy `-D warnings`
  clean. Mutation checks (guard reverted → red, then restored): parity gate
  (2 tests red), process-tree kill (1 red), strict `Engine.Backend` validation
  (1 red), doctor warning (1 red) and naive sequential pipe drain (red on the
  budget timeout). `routedOperations` is empty, so corpus coverage is
  unchanged.

## Str large-width parity: the native double→(decimal) narrowing

- Extended the `StringUtil.Str` differential past the previously fitted domain:
  `scan-3.txt` (5,180 rows; widths 9..18, scales 0..6), `boundary.txt` (427;
  exact powers at the picture bound, strict carries, exact halves, 2^53
  neighborhoods) and `powers.txt` (294; exact powers at widths 16..18, both
  signs). The exact-rational rule diverged 15/5,180 and 68/427 (36 in-domain;
  the other 32 are widths 19..24, outside the admitted subset, where the engine
  keeps failing closed by design).
- Root cause from decompiled `GxClasses`/`System.Decimal` (behavioral reference
  only; dumps stay in the git-ignored scratchpad): native computes
  `10^places - sign` as a `double`, subtracts `5.0/10^(scale+1)`, narrows the
  result through `(decimal)` (`DecCalc.VarDecFromR8`, which keeps 15 significant
  digits) and compares it strictly against `|v|` as decimals. Past 15
  significant digits the narrowed bound is not the exact rational one, which
  explains the non-monotone star pattern at widths 16..18.
- `verify` mode in the probe re-derives the star decision from that pipeline over
  every recorded scan: 36,278 rows, 0 mismatches, so the reading was confirmed
  before any Rust change.
- Fix: `types::format_str` now reproduces the native pipeline — `f64` bound with
  `NumberUtil.DecPow` semantics, a faithful `(decimal)double` emulation (biased
  exponent, `14 - (e*19728 >> 16)` scale, `DoublePowers10` scaling, the `< 1e14`
  bump, ties-to-even rounding, negative-scale multiply-back) and an exact decimal
  comparison over the 256-bit `Wide`. Admitted width 1..18 and scale ≤ width are
  unchanged.
- Evidence: 36,246 in-domain native rows across the six scans, 0 mismatches
  (boundary's 32 remaining rows are width ≥ 19, rejected in a controlled way,
  not divergence). `thresholds` mode dumps the native double bits and narrowed
  decimal components for all 378 admitted (width, scale, sign) combinations into
  `src/gx-engine/fixtures/str-bounds-native.tsv`, pinned bit for bit by the
  `types::tests` unit test; `tests/fixtures/str_matrix.tsv` was regenerated from
  the six scans (6,152 rows, admitted subset only) by the scratchpad generator.
  The temporary `zz_scratch_scan3.rs` differential was deleted after use; the
  scan files stay in the git-ignored scratchpad as raw evidence.
- Mutation check: restoring the old exact-rational overflow block makes
  `str_matrix_matches_recorded_native_runtime` red on the recorded divergent
  rows; restored green. Clippy/rustfmt clean; portable suite 246 passed / 0
  failed / 2 ignored.
- Follow-up (not in scope): `NumericType::render` (implicit Numeric printing)
  keeps its own truncation-based rule; whether native implicit conversion
  matches it at precision ≥ 16 is unmeasured.

## SDK-vs-engine benchmark and swap-candidate recon (2026-10-16)

- Benchmark harness (uncommitted, like the rest of the crate):
  `scripts/sdk-oracle/` (C#, net8.0, official `genexus.classes.core` 1.30.0
  package - the real `GeneXus.Utils.StringUtil` runtime, not an independent
  oracle), `benches/str_format.rs` (one JSON report line per run) and
  `scripts/benchmark.py` (runs both sides, fails on any parity mismatch).
  First recorded run: 50 passes over `tests/fixtures/str_matrix.tsv` with
  identical row count, character count (80,235) and FNV-1a/UTF-16 digest
  (`0xb9e1b48fb72067b0`), 0 expected-text mismatches; native median
  1.75 ms/pass (284 ns/row) vs engine 3.26 ms/pass (530 ns/row) on Windows 11
  x64, rustc 1.93.1, .NET SDK 10.0.204. Two hot-path fixes then moved the
  engine to 121-130 ns/row (medians of three 100-pass runs) against a native
  274-286 ns/row: `format_str` renders into a stack buffer with one allocation
  and no `fmt` machinery, and `Wide::div_rem` gained a `u128` fast path that
  skips the 256-step bitwise loop for picture-sized operands (both
  output-identical). Report: ignored `scratchpad/engine-benchmark.json`.
  Constant-heavy micro-benchmark only; not an end-to-end SDK comparison.
- First-swap candidate recon (all four stay unswapped):
  - `str_format` consumer: re-verified zero consumers (`EngineRouter.Decide`
    has only test callers; no Gateway/Worker path renders a GX numeric with
    picture semantics). Nearest future funnels: `RenderPropertyValue`
    (worker property rendering), `SampleDataService.FakeValueSql`, report
    layout geometry; routing any of them changes user-visible behavior and
    needs its own differential evidence.
  - XML exchange: the only product XML carrier is `genexus_transfer export`
    (`.xpz`); its `ExportFile` documents are ZIP-internal, consumed by import
    fidelity (`TransferService.ReadExportWebForms`). Standalone ExportFile XML
    is never produced or returned, namespaced real exports are rejected by the
    engine parser, and `xml_projection` remains `Partial` with no consumer
    key; routing would need XPZ extraction, a consumer operation and
    client-side invocations.
  - `genexus_analyze` corpus: produced a native SDK text tree with
    `genexus_io export_kb_to_text format=native part=all mode=all` on
    disposable KBTeste into ignored `scratchpad/kbtext-corpus` (684 files:
    650 `.gx`, 33 `module.toml`, manifest; 499 under `src/`, 184 under
    `ref/`; 649/675 planned objects, 26 stale index entries returned
    `ObjectNotFound`). gxwire's parser accepts `.gx` directly; the
    parse-coverage comparison study is the next step, not done here.
  - Implicit numeric rendering (`NumericType::render`): still unmeasured;
    would need fresh native `Print(&numeric)` probes, which this benchmark does
    not cover.
- No product routing changed; `routedOperations` remains empty.

## Numeric implicit rendering parity (2026-10-16)

- Native scan: `scratchpad/run-numeric-render-native.py` authored
  `RustNumericRenderProbe` in disposable KBTeste and recorded, per case, both
  `&var.ToString()` and `Str(&var, precision, scale)` on the same stored value:
  120 cases / 244 observations. `implicit == Str(...).lstrip()` held for all
  120, and the raw captures equal the padded `Str` output byte for byte, so
  native `&var.ToString()` **is** `StringUtil.Str(value, precision, scale)`.
  Grid: precision 1..18 with bound / first-over / over+1; scale corners for 13
  `(p, s)` pairs (bound, first over, exact half rounding past the integer
  bound, negative bound); the width 15..18 narrowed-bound regime (`10^15`
  renders at width 15; `10^16`..`10^18` and their negatives render one digit
  past the picture); zero/negative zero; dot accounting (`(5,2)` `999.99` is
  `*****`, max is `99.99`). The earlier follow-up note
  ("`NumericType::render` keeps its own truncation-based rule; unmeasured at
  precision ≥ 16") is resolved.
- Engine: `NumericType::render` now delegates to the verified `format_str`
  picture pipeline; the old truncation-based rule diverged in the separator
  place for scale > 0, the double->decimal narrowed bound at width ≥ 15, the
  sign reserve in the narrowed regime, and padding. Dead `Decimal::rounded`
  removed. Committed rows: `tests/fixtures/numeric_render_native.tsv` (120
  rows, `value|precision|scale=[Str]`, padding included) + the new
  `tests/numeric_render_native.rs` differential.
- Fixture alignment: the affected probe lanes recorded `.ToString().Trim()`
  values, so date/datetime, For, parm-mode, void-return and typed-call/do-case
  comparisons now trim each printed line through per-file `value_level_lines` /
  `printed_values` helpers, with the provenance noted in place; raw picture
  bytes are asserted by `numeric_render_native.tsv` and `typed_values.rs`.
- Mutation checks: reverting `render` to unpadded output fails the new fixture;
  narrowing the picture by one digit fails it; restored green. Clippy/rustfmt
  clean; portable suite 247 passed / 0 failed / 2 ignored.
- Not in this claim: `Print` of expressions, record members, date components
  and call results still uses the untyped display path (unpadded); their native
  raw bytes are unmeasured, so they carry no parity claim yet.
- Registry: new `numeric_render` capability at `Parity` (no consumers;
  `routedOperations` stays empty), with the doctor table and
  `docs/engine_backend.md` updated.

## Text function parity (2026-10-16)

- Spelling scan: `scratchpad/probe-function-names.py` reused one Procedure in
  disposable KBTeste and asked the GeneXus 18 specifier which spellings save
  (evidence `scratchpad/function-names-native-1792124171301645000`). Accepted:
  `Substr(&V,1,2)`, `Upper(&V)`, `Lower(&V)`, `StrReplace(&V,"a","b")`,
  `&V.Substring(1,2)`, `&V.ToUpper()`, `&V.ToLower()`, `&V.Replace("a","b")`.
  Rejected (`Função desconhecida`): `Substring(...)` and `Replace(...)` as
  functions, `&V.Substr(...)`, `&V.Upper()`, `&V.Lower()`, `StrReplaceAll`, and
  a literal receiver (`"banana".Replace(...)`). The engine admits exactly the
  accepted set; the rejected ones fall through to the unresolved-call error.
- Semantics probes: `scratchpad/run-text-functions-native.py` (safe + risk
  Procedures), `run-text-functions-semantics.py` and
  `run-text-functions-charspace.py` ran clean in the same KB (evidence
  `scratchpad/text-functions-*-1792*`) and recorded 63 observations: `Substr`
  reads the raw stored value (a declared `Character(4)` holding `ab` returns
  `ab ` for `Substr(&c,1,3)`; explicit trailing spaces survive), 1-based
  positions, `start < 1` or past the raw length -> empty, negative length ->
  remainder, over-long length clamps; `.Length()` still counts without trailing
  spaces. `Upper`/`Lower` map per character with a single-char result and keep
  the original when the mapping expands (`ß` -> `ß`, `µ` -> `Μ`, `ÿ` <-> `Ÿ`);
  accented round-trips (`áéíóúçñü` <-> `ÁÉÍÓÚÇÑÜ`) match. `StrReplace` is
  case-sensitive, non-overlapping, left to right (`("aaa","aa","b")` -> `ba`),
  empty find is a no-op, empty replacement removes; the method form shares
  every rule.
- Engine: `Text::substring/to_upper/to_lower/str_replace`, the frontend method
  grammar (`Substring`/`ToUpper`/`ToLower`/`Replace` folded into the accepted
  function builtins `SUBSTR`/`UPPER`/`LOWER`/`STRREPLACE`) and the typed-backend
  arms. Committed rows: `tests/fixtures/text_functions_native.tsv` (63 rows,
  `function|args=[result]`, machine-generated from the recorded labels by
  `scratchpad/make-text-functions-fixture.py`) exercised by
  `tests/text_functions.rs` (fixture differential, build/run e2e over both
  spellings with a 15-line expected stdout, wrong-arity/non-text rejection).
- Parser stack budget: the first inline implementation grew `parse_primary` -
  the frame of the deepest expression recursion - enough to overflow the
  nesting-limit test's thread stack (the test encodes a real budget: 130 paren
  levels must produce the controlled "expression nesting exceeds" error). The
  whole postfix method grammar moved into `parse_method`, which is only called
  on method tokens; the test now passes on a 1.5 MiB thread stack where the
  pre-change parser needed more than 1.93 MiB (default test threads: 2 MiB).
- Mutation checks: removing the substring length clamp panics the fixture;
  keeping the first mapped character for expanding case mappings fails `ß`;
  removing the empty-find guard fails `("banana","","X")` -> `XbXaXnXaXnXaX`;
  removing the `Substring` method branch fails the e2e with the
  unsupported-method error; all restored green. Clippy/rustfmt clean; portable
  suite 250 passed / 0 failed / 2 ignored.
- Registry: new `text_functions` capability at `Parity` (no consumers;
  `routedOperations` stays empty); `docs/engine_backend.md` status list updated.
- Not in this claim: non-BMP (astral) positions (Rust counts chars; CP1252 is
  1 char/unit), database-column semantics, the native KB store; the exchange
  lane keeps namespaced XML.

## Extended text functions parity (2026-10-16)

- Native Oracle & Decompilation: Extracted `GeneXus.Utils.StringUtil` semantics
  from `genexus.classes.core` 1.30.0 runtime via REA / ILSpy:
  - `LTrim` and `RTrim` trim ASCII spaces (`' '`) only, preserving tabs and newlines.
  - `PadL` trims both ends of input (`text.Trim()`) before left-padding with replicated
    `fill`; `PadR` trims trailing spaces (`RTrim(text)`) before right-padding. Both
    truncate to `size` if input length exceeds `size`, and return empty if `size <= 0`.
  - `Space(count)` returns `new string(' ', count)` or `""` if `count <= 0`.
  - `Concat(init, last[, sep])` right-trims `init` only, appending optional `sep` and
    leaving `last` untrimmed.
  - `StartsWith`, `EndsWith`, `Contains` use ordinal case-sensitive comparison.
  - `StrSearch(s1, s2, start)` / `IndexOf` uses 1-based indexing, clamping `start <= 0`
    to 1, returning 0 if `start > s1.Length` or not found.
- Fixture & Evidence: Generated `tests/fixtures/text_extended_native.tsv` (95 rows)
  directly from `GeneXus.Utils.StringUtil` via `generate-text-extended` in
  `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/text.rs`: Implemented `trim_start_spaces`, `trim_end_spaces`,
    `pad_left`, `pad_right`, `space`, `concat_with_sep`, `starts_with`, `ends_with`,
    `contains`, `str_search` with checked 1 MiB resource bounds.
  - `src/gx-engine/src/frontend.rs`: Added AST mappings for `LTRIM`, `RTRIM`, `PADL`,
    `PADR`, `SPACE`, `CONCAT`, `STARTSWITH`, `ENDSWITH`, `CONTAINS`, `STRSEARCH` and
    method forms `.TrimStart()`, `.TrimEnd()`, `.PadLeft()`, `.PadRight()`, `.Concat()`,
    `.StartsWith()`, `.EndsWith()`, `.Contains()`, `.IndexOf()`.
  - `src/gx-engine/src/typed_backend.rs`: Emitted lowerings for all built-ins and method forms.
  - `src/gx-engine/tests/text_extended.rs`: Added fixture oracle verification (95 rows),
    executable e2e Procedure validation, and arity/type rejection tests.
- Verification: 253 passed / 0 failed in `gx-engine`, Clippy and rustfmt clean.
- Registry: new `text_extended_functions` capability at `Parity` (no consumers).

## Mathematical & Number Conversion Core Parity (2026-10-16)

- Native Oracle & Decompilation: Extracted `GeneXus.Utils.NumberUtil` and `GeneXus.Utils.StringUtil` semantics from `genexus.classes.core` 1.30.0 runtime via REA / ILSpy:
  - `NumberUtil.Int`: `Math.Floor(value)`.
  - `NumberUtil.Round`: `MidpointRounding.AwayFromZero`; supports negative digits for rounding to powers of 10 (`Round(1250, -2) == 1300`, `Round(-1250, -2) == -1300`).
  - `NumberUtil.Trunc`: Truncates towards zero; supports negative digits (`Trunc(-3.14, 0) == -3`, `Trunc(1250, -2) == 1200`).
  - `NumberUtil.Val` / `ValParse`: Trims whitespace, parses characters up to first non-digit/illegal char (allowing leading '-' and single decimal separator), returning `0` on empty/invalid.
  - `StringUtil.Left`: Returns first `size` characters, clamping `size < 0` to 0.
  - `StringUtil.Right`: Returns last `size` characters; throws `ArgumentOutOfRangeException` when `size < 0`.
  - `StringUtil.Asc`: Returns ASCII code point of first char; returns 32 (space) on empty string.
  - `StringUtil.Chr`: Returns single-character string from ASCII code point.
  - `StringUtil.NotNumeric`: Returns true if any char in space-trimmed text is not a digit; empty string returns false.
  - `StringUtil.LTrimStr`: Returns `LTrim(Str(val, len, dec))`.
- Fixture & Evidence: Generated `tests/fixtures/numeric_math_native.tsv` (107 rows) directly from official runtime via `generate-numeric-math` in `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/types.rs`: Added `floor_to_i64`, `round`, `trunc`, `val` with exact scaling and rounding logic, and `From<i64> for Decimal`.
  - `src/gx-engine/src/text.rs`: Added `left`, `right`, `asc`, `chr`, `not_numeric`.
  - `src/gx-engine/src/frontend.rs`: Added AST mappings for `INT`, `ROUND`, `TRUNC`, `VAL`, `LEFT`, `RIGHT`, `ASC`, `CHR`, `NOTNUMERIC`, `LTRIMSTR` and methods `.Int()`, `.Round()`, `.Trunc()`, `.ToNumeric()`, `.Left()`, `.Right()`, `.Asc()`, `.NotNumeric()`.
  - `src/gx-engine/src/typed_backend.rs`: Emitted code lowerings for all built-ins and method forms.
  - `src/gx-engine/tests/numeric_math.rs`: Added fixture oracle verification (107 rows), executable e2e Procedure validation, and arity/type rejection tests.
- Verification: 256 passed / 0 failed in `gx-engine`, Clippy and rustfmt clean.
- Registry: new `numeric_math_functions` capability at `Parity` (no consumers).

## Collection Operations & For In Iteration Core Parity (2026-10-16)

- Native Oracle & Decompilation: Extracted `GeneXus.Utils.GxSimpleCollection<T>`, `GeneXus.Utils.GXBaseCollection<T>`, and `GeneXus.Utils.IGxCollection` semantics from `genexus.classes.core` 1.30.0 runtime via REA / ILSpy:
  - `Add(item, 0)`: Appends to end of collection (same as omitting the index).
  - `Add(item, idx)`: When `idx > 0`, inserts at 1-based index `idx - 1`.
  - `Remove(idx)` / `RemoveElement(idx)`: When `idx > 0 && idx <= Count`, removes item at `idx - 1`. When `idx <= 0` or `idx > Count`, it is a silent safe no-op (no exception).
  - `Clear()` / `ClearCollection()`: Removes all items from the collection.
  - `IndexOf(item)`: Compares items using reference/object equality (`Rc::ptr_eq`), returning 1-based index (`1..=Count`), or `0` if not present.
  - `For &item in &collection ... EndFor`: Iterates elements, rebinding `&item` as an alias to the element inside the collection so mutations inside the loop directly update the collection elements.
  - `Exit`: Immediately breaks execution of the enclosing loop (`break;`).
- Fixture & Evidence: Generated `tests/fixtures/collection_ops_native.tsv` (24 rows) directly from official runtime via `generate-collection-ops` in `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/record_runtime.rs`: Added `insert`, `remove`, `clear`, and `index_of` to `Collection`.
  - `src/gx-engine/src/frontend.rs`: Updated `Stmt::CollectionAdd` to accept optional index; added `Stmt::CollectionRemove`, `Stmt::CollectionClear`, `Stmt::ForIn`, and `Stmt::Exit`. Updated parser for `.Add(item [, index])`, `.Remove(index)`, `.Clear()`, `Exit`, and `For &item in &col`.
  - `src/gx-engine/src/dependencies.rs`: Updated AST traversal for new statement variants.
  - `src/gx-engine/src/typed_backend.rs`: Emitted lowerings for `INDEXOF` (supporting both collections and strings), `CollectionAdd`, `CollectionRemove`, `CollectionClear`, `ForIn` (with alias binding and per-iteration step cost budget), and `Exit`. Allowed `new()` for `DataType::Collection` in `container_assignment`.
  - `src/gx-engine/tests/collection_ops.rs`: Added fixture oracle verification (24 rows), executable e2e Procedure validation, and schema mismatch rejection tests.
- Verification: 259 passed / 0 failed in `gx-engine`, Clippy and rustfmt clean.
- Registry: new `collection_operations` capability at `Parity` (no consumers).

## Calendar & DateTime Operations Core Parity (2026-10-16)

- Native Oracle & Decompilation: Extracted `GeneXus.Utils.DateTimeUtil` semantics from `genexus.classes.core` 1.30.0 runtime via REA / ILSpy:
  - `Day`, `Month`, `Year`, `Hour`, `Minute`, `Second`: Return numeric component; return `0` on empty/nullDate (`DateTime.MinValue`).
  - `Dow`: Returns `0` on empty/nullDate; otherwise returns `(byte)(dt.DayOfWeek + 1)` (1=Sunday, 2=Monday, ..., 7=Saturday).
  - `AddYr(dt, n)` / `AddMth(dt, n)`: Returns `nullDate` on empty/invalid; clamps day to target month end (e.g. Feb 29 + 1 year = Feb 28; Jan 31 + 1 month in leap = Feb 29). Preserves time on DateTime.
  - `DAdd(dt, days)` / `TAdd(dt, seconds)`: Adds days or seconds with correct Euclidean rollover and cross-day calculation.
  - `DDiff(dt1, dt2)`: Truncated difference in whole days (`Convert.ToInt32((dt1 - dt2).TotalDays)`).
  - `TDiff(dt1, dt2)`: Difference in seconds (`Convert.ToInt64((dt1 - dt2).TotalSeconds)`).
  - `Age(birth, current)`: Completed years calculation (`current.Year - birth.Year`, decremented if current date/time is before the birthday in that year).
  - `EndOfMonth(dt)`: Sets day to `DateTime.DaysInMonth(year, month)` while preserving time.
  - `ResetTime(dt)`: Zeroes time component to `00:00:00` while preserving date.
  - Added `.ToString()` support across all primitive types (Numeric, Date, DateTime, Boolean, Text) for uniform string conversions.
- Fixture & Evidence: Generated `tests/fixtures/calendar_ops_native.tsv` (83 rows) directly from official runtime via `generate-calendar-ops` in `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/date.rs`: Added `day_of_week`, `end_of_month`, `reset_time`, `age`, and exposed `month_days`.
  - `src/gx-engine/src/datetime.rs`: Added `day_of_week`, `end_of_month`, `reset_time`, `age`, `add_days`, `add_months`, `add_years`, `add_minutes`, `add_hours`.
  - `src/gx-engine/src/frontend.rs`: Added AST mappings for `DAY`, `MONTH`, `YEAR`, `HOUR`, `MINUTE`, `SEC`, `DOW`, `ADDYR`, `ADDMTH`, `ADDDAYS`, `DADD`, `TADD`, `DDIFF`, `TDIFF`, `AGE`, `ENDOFMONTH`, `RESETTIME`, `TOSTRING`, and method forms `.DayOfWeek()`, `.Dow()`, `.EndOfMonth()`, `.ResetTime()`, `.AddDays()`, `.AddMonths()`, `.AddYears()`, `.AddHours()`, `.AddMinutes()`, `.AddSeconds()`, `.Age()`, `.ToString()`.
  - `src/gx-engine/src/typed_backend.rs`: Emitted lowerings for all built-ins, date shift/diff operations, and `.ToString()`.
  - `src/gx-engine/tests/calendar_ops.rs`: Added fixture oracle verification (83 rows), executable e2e Procedure validation, and invalid type/argument rejection tests.
- Verification: 262 passed / 0 failed in `gx-engine` (3 new tests, 83 oracle assertions), Clippy and rustfmt clean.
- Registry: new `calendar_operations` capability at `Parity` (no consumers).

## Local Subroutine Control Flow Core Parity (2026-10-16)

- Native Decompilation & Codegen Analysis: Inspected generated C# in `C:\KBs\KBTeste\NetModel\web\general\ui\listprograms.cs` and REA (`C:\Projetos\rea`):
  - GeneXus extracts each `Sub 'Name' ... EndSub` block into a dedicated method (`protected void S111()`), never executing subroutines on sequential fallthrough from the main body.
  - All procedure variables are shared across the main procedure body and all subroutines.
  - When `Return` is executed inside a subroutine, GeneXus sets `returnInSub = true; return;`, and every caller checks `if (returnInSub) return;` immediately after invoking the subroutine, exiting the entire enclosing procedure.
  - Subroutine names accept single quotes (`'Name'`), double quotes (`"Name"`), and bare identifiers (`Name`), resolved case-insensitively.
- Engine Implementation:
  - `src/gx-engine/src/frontend.rs`: Extended `Lexer` to lex single-quoted and double-quoted strings; added `BlockEnd::EndSub`, `Stmt::DoSub(String)`, and `Stmt::Sub(String, Vec<Stmt>)`; implemented `parse_sub` (enforcing top-level `nesting == 0`) and extended `parse_do` to dispatch `Do 'SubName'`.
  - `src/gx-engine/src/dependencies.rs` & `src/gx-engine/src/backend.rs`: Traversed `Stmt::Sub` bodies for dependency analysis; rejected subroutines in legacy format version 1 (`subroutines require format_version 2`).
  - `src/gx-engine/src/typed_backend.rs`: Implemented compile-time validation of subroutine calls (`validate_subroutine_calls`) and duplicate definitions; emitted zero-allocation `struct P{index}Sub<'a>` holding mutable references (`&'a mut`) to all procedure-scoped variables, shared execution `steps` budget, `depth`, `sub_depth` (bounded at 128), `return_in_sub` flag, and optional `return_value` for value-returning helper procedures.
  - `src/gx-engine/tests/subroutines.rs`: Added 13 end-to-end tests covering variable mutation, case-insensitive resolution, quote styles, cooperative chaining (`SubA` -> `SubB` -> `SubC`), early `Return` propagation (including nested subroutines and value-returning helpers), fallthrough isolation, recursion depth limit, loops/collections/procedure calls inside subroutines, and compile-time error checks.
- Verification: 275 passed / 0 failed in `gx-engine` (13 new subroutine tests), Gateway `EngineBackendTests` 40/40 passed, Clippy and rustfmt clean.
- Registry: new `subroutine_control_flow` capability at `Parity` (no consumers).

## Conditional, Empty/Null State & String Formatting Core Parity (2026-10-16)

- Native Decompilation & Codegen Analysis: Extracted `GeneXus.Utils.StringUtil` semantics from `genexus.classes.core` 1.30.0 runtime via ILSpy and inspected generated C# in `C:\KBs\KBTeste\NetModel\web\gx0010.cs` and `Artech.Genexus.Common.AST.ASTNodesHelpers`:
  - `Iif(condition, trueExpr, falseExpr)`: Short-circuit conditional expression (`(cond ? a : b)`), evaluating only the taken branch.
  - Universal `.IsEmpty()` and `IsEmpty(expr)`: `Numeric` compares equal to `0`; `Text` checks `String.IsNullOrEmpty(StringUtil.RTrim(s))` (`trim_end_spaces().utf16_length() == 0`); `Boolean` checks `!b`; `Date`, `DateTime`, and `Collection` check `.is_empty()`.
  - Universal `.SetEmpty()`: Resets `Numeric` to `0`, `Text` to `""`, `Boolean` to `false`, `Date`/`DateTime` to empty sentinels, `Collection` to empty list, `Record` to fresh default record, and supports record member targets (`&rec.Field.SetEmpty()`).
  - `Nullvalue(&var)`: Returns the typed empty sentinel for `&var` or `&rec.Field` without requiring `&var` to be previously assigned.
  - `Proc.UDP(...)`: Invokes value-returning (or out-parameter) procedures identically to `Proc(...)`.
  - `StringUtil.Format(pattern, v1..v9)`: Replaces `%1`..`%9` (supporting `\%` escape and preserving non-`%` backslashes) or `{0}`..`{8}` when no `%N` replacement occurs.
  - `StringUtil.Replicate(str, size)`: Repeats `str` `size` times (`size <= 0` returns `""`).
  - `StringUtil.CharAt(str, idx)`: 1-based character extraction (`idx <= 0` or `idx > Length` returns `""`).
  - `StringUtil.StringSearchRev(s1, s2, start)`: 1-based reverse search (`s1.LastIndexOf(s2, start - 1) + 1`, searching within `s1[..start]`).
  - `StringUtil.BoolToStr(b)` (`"true"` / `"false"`), `StringUtil.StrToBool(s)` (true iff `"true"` case-insensitive or `"1"`), and `StringUtil.NewLine()` (`"\r\n"`).
- Fixture & Evidence: Generated `tests/fixtures/conditional_state_native.tsv` (57 rows) directly from official runtime via `generate-conditional-state` in `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/text.rs`: Added `format_template`, `replicate`, `char_at`, `str_search_rev`, `bool_to_str`, `to_boolean`, and `newline`.
  - `src/gx-engine/src/frontend.rs`: Added builtins `IIF`, `ISEMPTY`, `NULLVALUE`, `FORMAT`, `NEWLINE`, `REPLICATE`, `CHARAT`, `STRSEARCHREV`, `BOOLTOSTR`, `TOBOOLEAN`, `STRTOBOOL`; generalized `.SetEmpty()` to variables and record fields; added `.CharAt()`, `.LastIndexOf()` / `.StrSearchRev()`, `.ToBoolean()`, and `.UDP(...)` call support.
  - `src/gx-engine/src/typed_backend.rs`: Emitted short-circuit lowering for `IIF`, universal `ISEMPTY` and `SetEmpty` across all types and container assignments, `NULLVALUE`, and string formatting/search/boolean builtins.
  - `src/gx-engine/tests/conditional_state.rs`: Added 57-row native oracle test and end-to-end compiled Procedure tests.
- Verification: 280 passed / 0 failed in `gx-engine`, Clippy and rustfmt clean.
- Registry: new `conditional_and_state_operations` capability at `Parity` (no consumers). **Control Flow & Procedure Pipeline** reaches **100.0% parity (6/6 areas)**.

## Structured Data & Collection JSON Serialization Parity (2026-10-16)

- Native Decompilation & Runtime Oracle: Analyzed `GxUserType.ToJSonString(includeState)` / `FromJSonString(s)` and `GXBaseCollection<T>.ToJSonString(includeState)` / `FromJSonString(s)` from `genexus.classes.core` and generated C# SDTs (`type_Sdt*.cs`):
  - `&sdt.ToJson()` / `&col.ToJson()`: Compact JSON string format, escaped strings, boolean `true`/`false`, empty sentinels (`"0000-00-00"` for Date, `"0000-00-00T00:00:00"` for DateTime, 0 for Numeric, `""` for Text).
  - `&sdt.FromJson(json)` / `&col.FromJson(json)`: Case-insensitive property mapping, partial updates (preserving unmentioned fields), array item repopulation, and fail-closed false return on invalid/non-container JSON.
- Fixture & Evidence: Generated `tests/fixtures/sdt_json_native.tsv` (32 rows) directly from official runtime via `generate-sdt-json` in `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/record_runtime.rs`: Added pure-Rust `SimpleJson` parser and `Record::to_json`, `Record::from_json`, `Collection::to_json`, `Collection::from_json`.
  - `src/gx-engine/src/frontend.rs`: Added AST support for `TOJSON` and `FROMJSON` expressions and statement forms (`&sdt.FromJson(...)`).
  - `src/gx-engine/src/typed_backend.rs`: Emitted lowerings for `TOJSON`, `FROMJSON`, and `Stmt::FromJson`.
  - `src/gx-engine/tests/sdt_json.rs`: Added 32-row native oracle verification and end-to-end compiled executable tests.
- Verification: 283 passed / 0 failed in `gx-engine`.
- Registry: new `sdt_and_collection_json` capability at `Parity` (no consumers).

## Structured Data & Collection XML Serialization Parity (2026-10-16)

- Native Decompilation & Runtime Oracle: Analyzed `GxUserType.ToXml(sdt, rootName)` / `FromXml(sdt, xml, rootName)` and `GXBaseCollection<T>.ToXml(col, rootName)` / `FromXml(col, xml, rootName)` from `genexus.classes.core` 1.30.0 and generated C# SDTs (`type_Sdt*.cs`):
  - `&sdt.ToXml([root])` / `&col.ToXml([root])`: Indented multi-line XML with root tag (defaults to schema name or `"Collection"` for collections), self-closing tags for empty strings (`<Tag />`), XML entity escaping (`&lt;`, `&gt;`, `&amp;`, `&quot;`, `&apos;`), ISO date/datetime format (`yyyy-MM-ddT00:00:00`), booleans as `true`/`false`.
  - `&sdt.FromXml(xml, [root])` / `&col.FromXml(xml, [root])`: Case-insensitive element name matching, nested child SDT and collection deserialization, resetting missing fields to empty defaults (matching GeneXus SDK behavior), and returning `false` on malformed XML or mismatched root.
- Fixture & Evidence: Generated `tests/fixtures/sdt_xml_native.tsv` (40 rows) directly from official runtime via `generate-sdt-xml` in `src/gx-engine/scripts/sdk-oracle/Program.cs`.
- Engine Implementation:
  - `src/gx-engine/src/record_runtime.rs`: Added pure-Rust `SimpleXmlElement` parser and `Record::to_xml`, `Record::from_xml`, `Collection::to_xml`, `Collection::from_xml`.
  - `src/gx-engine/src/frontend.rs`: Added AST support for `TOXML` and `FROMXML` expressions, `Stmt::FromXml(Box<FromXmlStmt>)`, and stack-frame-optimized statement parsing (`parse_variable_statement`, `parse_call_statement`).
  - `src/gx-engine/src/typed_backend.rs`: Emitted lowerings for `TOXML`, `FROMXML`, and `Stmt::FromXml`.
  - `src/gx-engine/tests/sdt_xml.rs`: Added 40-row native oracle verification and end-to-end compiled executable tests.
- Verification: 285+ passed / 0 failed in `gx-engine`, Clippy and rustfmt clean.
- Registry: new `sdt_and_collection_xml` capability at `Parity` (no consumers).
- Milestone: **Structured Data & Collections** reaches **100.0% parity (5/5 areas)**. All 3 core language subsystems (**Language & Runtime Core 8/8**, **Control Flow & Procedure Pipeline 6/6**, **Structured Data & Collections 5/5**) now at **100.0% Native Parity (19/19 areas)**. Overall SDK replacement reaches **37.0%**.

## Domain and Attribute Projection & Resolution in Portable Catalog and SDK XML (2026-10-16)

- Native Contract & Object Class Analysis:
  - Official GeneXus GUIDs from `Artech.Genexus.Common.ObjClass`:
    - Domain: `00972a17-9975-449e-aab1-d26165d51393`
    - Attribute: `adbb33c9-0906-4971-833c-998de27e0676`
  - Semantic Rules:
    - Domains can only be based on other Domains (never on Attributes).
    - Attributes can be based on Domains.
    - Procedures and SDTs reference Domains and Attributes via `ReferenceKind::BasedOn`.
    - Variable declarations, parameter signatures, and return types resolve to concrete `DataType`s before compilation.
    - Bounded resolution: max depth 16, strict cycle detection, visited set tracking.
- Engine Implementation:
  - `src/gx-engine/src/kb.rs`:
    - Added `RawCatalogType` (`Concrete(DataType)`, `BasedOnShort { target }`, `BasedOnTagged { kind, target }`).
    - Added `resolve_domain_or_attribute_data_type` and `deserialize_raw_variables` rejecting duplicate declarations.
    - Updated `project_from_catalog`: admits `ObjectKind::Domain` and `ObjectKind::Attribute` with `TYPE` source parts, resolves `BasedOn` references on Procedures and SDTs before lowering.
  - `src/gx-engine/src/exchange.rs`:
    - Added `DOMAIN` and `ATTRIBUTE` GUID constants.
    - Added `ParsedTypeDefinition` (`Concrete(DataType)`, `BasedOn(String)`) and `parse_type_definition`.
    - Updated `project_level` and `parse_sdt` to support `BasedOn` domain/attribute references on SDT items.
    - Updated `catalog_from_xml` to parse Domain and Attribute objects, extract definitions from properties/parts, and wire explicit `BasedOn` references.
  - `src/gx-engine/tests/domain_attribute.rs`: Added 10 end-to-end integration tests covering procedure variables based on Domain/Attribute, chained Domains, SDT items based on Domain, cyclic dependency detection, domain-cannot-be-based-on-attribute enforcement, and end-to-end `xml-run`.
- Verification: 306 passed / 0 failed in `gx-engine`, Clippy and rustfmt clean. 40/40 `EngineBackendTests` passed in .NET.
- Registry: new `domain_attribute_projection` capability at `Parity` in `EngineCapabilityRegistry` (no consumer).
- Milestone: **Portable KB Catalog & XML Projection** advances from **33.3% to 66.7% (4/6 areas: Procedures, SDTs, Domains, Attributes)**. Overall weighted SDK replacement advances from **37.0% to 40.7% (31/56 areas touched: 55.4%)**.

## Transaction Structure, Keys & Business Component Record Lowering in Portable Catalog & XML (2026-10-16)

- Native Contract & Object Class Analysis:
  - Official GeneXus GUIDs from `Artech.Genexus.Common.ObjClass`:
    - Transaction: `1db606f2-af09-4cf9-a3b5-b481519d28f6`
    - Transaction Structure: `1608677c-a7a2-4a00-8809-6d2466085a5a`
  - Semantic Rules:
    - Root level and nested child levels require at least one primary key attribute (`IsKey = true`).
    - Level names must be valid identifiers and unique within the transaction hierarchy.
    - Attribute names must be unique within their level.
    - Transaction levels lower into Business Component `RecordType` schemas for procedure manipulation.
    - Attribute types are resolved against catalog `Attribute` and `Domain` objects when not specified inline.
    - Bounded recursion: max depth 16, strict cycle detection, visited set tracking.
- Engine Implementation:
  - `src/gx-engine/src/transaction.rs`:
    - Defined `TransactionAttributeRef`, `TransactionLevel`, and `TransactionStructure`.
    - Implemented `validate` checking primary keys, bounded recursion depth (16), unique names, and valid identifiers.
    - Implemented `root_primary_key()` and `all_attributes()`.
  - `src/gx-engine/src/kb.rs`:
    - Added `project_transaction_level_schemas` lowering Transaction levels to `RecordType` schemas.
    - Updated `project_from_catalog` to admit `ObjectKind::Transaction` with `"STRUCTURE"` source part and wire references to Attributes and Domains.
    - Updated `Procedure` dependency checking to permit `ReferenceKind::Uses` targeting `ObjectKind::Transaction`.
  - `src/gx-engine/src/exchange.rs`:
    - Added `TRANSACTION` and `TRANSACTION_STRUCTURE` GUID constants.
    - Added empty metadata part admission for Transaction Layout, Events, and Help.
    - Implemented `parse_transaction_structure` and `parse_transaction_level` extracting `<Level>`, `<Attribute>`, `IsKey`, `Formula`, `Nullable`, `IsForeignKey`.
    - Updated `catalog_from_xml` to parse Transaction objects and wire references to referenced Attributes/Domains.
    - Updated XML variable declaration check to admit `ObjectKind::Transaction` for `Record` schemas.
  - `src/gx-engine/tests/transaction_structure.rs`:
    - Added 10/10 end-to-end integration tests covering nested levels, primary key validation, attribute referencing, cycle/depth limits, XML parsing, procedure interoperability.
- Verification: 316 passed / 0 failed across all 30 test binaries in `gx-engine`, Clippy and rustfmt clean. 40/40 `EngineBackendTests` passed in .NET.
- Registry: new `transaction_structure_projection` capability at `Parity` in `EngineCapabilityRegistry` (no consumer).
- Milestone: **Portable KB Catalog & XML Projection** advances from **66.7% to 83.3% (5/6 areas: Procedures, SDTs, Domains, Attributes, Transactions)**. Overall weighted SDK replacement advances from **40.7% to 42.5% (33/56 areas touched: 58.9%)**.

