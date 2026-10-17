# Numeric engine validation and comparison

## Reproduce

From the repository root (Python standard library, Cargo and rustc only):

```powershell
python -m unittest discover -s src/gx-engine/scripts -p test_validate.py
python src/gx-engine/scripts/validate.py --out scratchpad/gx-engine-validation.json
cargo test --offline --locked --manifest-path src/gx-engine/Cargo.toml
```

The script builds the release compiler, uses temporary directories, and removes native outputs afterward. It never opens a KB, invokes GeneXus/MSBuild, or writes a database. Reports contain host/compiler metadata and raw measurements, not KB content. Change `--seed` for another reproducible corpus; cases are capped at 500 and repetitions at 20. Process deadlines are safety limits, not a sandbox.

## Observed results

Local Windows x86_64/MSVC host, rustc 1.93.1, seed 129, default 200 generated depth-three expressions plus three boundary cases:

- **Verified:** 203 expression results and 10 branch/loop outputs matched a separate Python integer oracle. Signed division truncates toward zero; comparisons produce 0/1. This tests our documented i64 subset, not GeneXus Numeric semantics.
- **Verified:** eight malformed/unsupported/uninitialized-source cases failed with controlled CLI diagnostics.
- **Verified:** five generated programs stopped with controlled runtime errors: addition overflow, division by zero, MIN/-1, negating MIN, and an empty infinite loop.
- **Verified:** each timed native build produced an executable whose full stdout matched the oracle corpus. Direct rustc compilation of the same emitted source also matched.
- **Verified:** 50 existing Rust tests passed (one informational benchmark ignored), plus four new Python oracle tests. Existing tests also cover recursion, helper side effects/order, failed-build preservation, timeouts, and incremental rollback.

Warm-host fresh-build wall times, five samples per path, alternating order, warm-up excluded:

| Path | Median | Range |
| --- | ---: | ---: |
| Release gx-engine CLI → validation/emission → compiler probe → rustc/link → publication | 493.28 ms | 489.41–499.66 ms |
| Direct rustc/link on that same emitted source, same flags | 380.27 ms | 374.16–389.16 ms |

The approximately 113 ms difference is an aggregate pipeline overhead observation, not an attribution to any particular phase. No cold OS-cache claim, statistical significance claim, optimization, or GeneXus speed comparison is made. Compilation uses opt-level=1 and overflow-checks=yes in both paths. Process-launch/output wall times were 5.49 ms and 4.16 ms respectively; these tiny, sequential samples of equivalently generated programs are **not** evidence of runtime superiority. The corpus is constant-heavy and does not represent database or production workloads.

Raw samples: ignored `scratchpad/gx-engine-validation.json`. New runs replace that local report; these numbers record the documented run only.

Additional stress run (seed 130, 500 generated expressions plus three boundaries): **513 observable values matched**, eight compile rejections and five runtime-error programs passed. Three alternating samples gave engine build median 915.74 ms versus direct rustc 722.70 ms. This is a different workload, not a before/after optimization measurement; both timed paths' executables were checked. Raw samples: ignored `scratchpad/gx-engine-validation-seed130.json`.

## Fair comparison with the official GeneXus engine

### Authorized live KBTeste investigation

The user explicitly authorized creating objects and running builds in `C:\KBs\KBTeste`. This supersedes the earlier authorization/setup blocker for that named KB, not for other KBs or production.

**Verified:** opened/selected KBTeste through the published Gateway and official GeneXus 18 SDK (`18.0.10.184260`). Created synthetic Procedure `RustConformance92026188` with `Numeric(10,0)` variable `Value`, source `&Value = (21 * 2)` followed by `msg(&Value.ToString().Trim(), status)`, and `IsMain=true`. An authoritative refreshed read confirmed the source. The task-created procedure was subsequently deleted through the SDK; deletion reported `persisted=true`, `rereadConfirmed=true`, and a separate refreshed read returned `ObjectNotFound`.

**Verified SDK syntax checks (not executable conformance):** ten in-memory checks against that procedure: arithmetic, signed division, minimum-i64 literal, overflow expression, division-by-zero expression, numeric loop condition, undeclared-variable assignment, and If/Else passed the syntax layer; malformed arithmetic and an unresolved procedure call were rejected. In particular, syntax acceptance of overflow, division by zero, and an undeclared variable proves neither semantic validity nor runtime behavior. Our engine's earlier rejection of some cases happens at a different phase; do not score these as parity failures or passes.

**Earlier high-level build attempt (superseded for the local arithmetic fixture below):** the existing `.NET` environment's real targeted build terminated **Failed** before specification/compilation: `Input parameters are not complete: User` in the GeneXus Cloud request. A direct supported MSBuild SDK inspection confirmed `DEPLOY_CLOUD=Yes`. Attempting a temporary supported setter to `No` was rejected as invalid; inspection of the property descriptor's contextual standard values returned **only `Yes`**. No unsupported override or licensing bypass was attempted. The original `Yes` setting was restored and read back. A separate SpecifyOneOnly attempt failed because this new object had never been built into the target environment. That attempt produced no executable; failed builds are not benchmark samples.

Raw request/response evidence and authored probe scripts remain in ignored `scratchpad/rust-native-1792026489830048600/`, `scratchpad/rust-native-probe.py`, `scratchpad/rust-local-build.proj`, and `scratchpad/rust-local-build.log`. Intermediate failed probes are retained separately. No SDK decompilation or KB artifacts were added to tracked source. The high-level Cloud/deployment build remains unavailable, but the custom local SDK pipeline below no longer depends on it.

Follow `docs/live-kb-test-harness.md`, using an explicitly authorized synthetic fixture and the supported SDK. Do not copy a KB folder and assume its SQL identities changed. Do not import these JSON bodies verbatim as GeneXus procedures: parameter rules, Numeric types, output channels and expression semantics need explicit mapping first.

### Verified custom MSBuild / SDK pipeline

A subsequent operator-authorized local run created `RustTimingProbe` in KBTeste, with `Numeric(10,0)` variable `Value`, `IsMain=true`, and the correct internal property **`CALL_PROTOCOL=CLINE`**. Its body computes `21 * 2` and prints `42`; the Rust project computes the same expression and prints the same value. The earlier display-name property `CallProtocol` was not exposed, but the actual internal property is supported through MCP.

The custom MSBuild task invokes official APIs: `CopyModelCommand` (design to working model), `ISpecifierService.SpecifyObjects` on the **working model**, `IGeneratorsService.Generate` followed by `WaitFinalize`, and `IRunService.Compile` for the object's key. The generator ID is 41. No cloud deployment, license patch, property-validation override, database reorganization or application execution against a datastore was requested. `DEPLOY_CLOUD` remains `Yes`.

Important corrections discovered experimentally: specifying the design model alone can produce a navigation XML without generating the procedure; `Generate(..., true)` needs a build-object-list context; `GenerateGroup` in this installed implementation simply returns false. Neither a true generation return value nor MSBuild exit alone was accepted as proof. The completed path produced `NetModel/Web/rusttimingprobe.cs`, `arusttimingprobe.cs`, and `Web/bin/arusttimingprobe.exe`/`.dll`; executing the GeneXus command-line artifact returned exit 0 and exactly `42`. The Rust artifact also returned exit 0 and exactly `42`.

First successful run (not a clean-KB/cold-cache benchmark): copy 609.89 ms; specification 9,230.43 ms; generation 4,328.10 ms; compilation 15,316.96 ms. Three subsequent alternating GeneXus/Rust build samples on the same machine:

| Measurement | Median | Range |
| --- | ---: | ---: |
| GeneXus SDK stages, including initialization/copy/specify/generate/compile | 10,930.95 ms | 10,797.50–11,027.39 ms |
| GeneXus MSBuild process wall time, also including startup and KB open/close | 16,366.35 ms | 14,869.40–16,660.95 ms |
| Release gx-engine CLI build, including rustc and publication | 223.19 ms | 210.38–241.93 ms |
| GeneXus executable launch + output (9 executions) | 123.06 ms | 117.52–127.13 ms |
| Rust executable launch + output (9 executions) | 5.75 ms | 5.35–9.07 ms |

Every execution was checked for exit 0 and output `42`. The measured stage-total/build ratio is approximately **49:1 for this single trivial fixture only**, not a claim of general GeneXus speedup or equivalent feature coverage. GeneXus specification is forced, its .NET build can reuse outputs, generation can include pending/common KB resources, and gx-engine always invokes rustc; these are explicitly different pipelines, not a full-build parity benchmark. Runtime figures mostly measure native/.NET startup, not arithmetic throughput. SDK warnings about denied `CssProperties.json` writes and unrelated empty STYLES sources remain in logs, so the environment is not warning-free.

Local reproduction: `python scratchpad/rust-timing-create.py`, then `python scratchpad/compare-native-timing.py`. The latter builds the Rust CLI, takes three alternating samples, checks both executables, and writes ignored `scratchpad/rust-native-comparison.json` with raw stage/launch durations. Authored `scratchpad/rust-timing.proj` contains the custom MSBuild task; `rust-timing-copy.log` records the first successful run and `rust-timing-repeat-{0,1,2}.log` the measured repeats. `RustTimingProbe` and generated test artifacts are intentionally retained in authorized KBTeste for reproduction. No SDK source or generated GeneXus artifacts are tracked in Git.

Acceptance criteria for the broader comparison (not yet satisfied):

1. **Semantic oracle:** same authored inputs and recorded types in both engines; compare ordered observable results and classify intentional i64/Decimal/null differences. Pair negative cases by expected error category, not identical message text. A shared mistake in generated source must not become the only oracle.
2. **Baseline identity:** record SDK/generator versions, fixture revision, target/compiler flags, licenses, datastore identity attestations and runtime dependencies without credentials or proprietary artifacts in Git.
3. **Separate phases:** record specification/navigation, generation, target compile/link, total build and executable runtime independently where available. Compare total pipelines separately from individual phases; never compare a full GeneXus Build All to a numeric expression parse.
4. **Build matrix:** full/rebuild, unchanged build, one-body edit and dependency/signature edit; verify changed output and preserved unaffected behavior after each. Record terminal build completion, diagnostics and generated artifact identity, not only a job ID/zero exit.
5. **Measurements:** warm-up plus alternating repeated samples, same machine/concurrency/input/output, raw durations and median/range. Cold and warm runs are different populations. Include launch and I/O explicitly for executable tests, or use a checksum workload with enough runtime to amortize them.
6. **Scope:** strings/decimal/date/null, SDTs, Rules, For each/navigation and datastore behavior remain unsupported by this prototype and cannot be marked passing from numeric results.

Static gates remain **not green**: Ripwire quality-delta exits 2 on cross-crate duplication findings and test-gate exits 4 on unresolved graph coverage. No baseline suppression or unrelated refactoring was used. The broader .NET/npm/Nexus/release matrix was not run for this isolated validation addition.


## Expanded edge validation � 2026-10-15

**Verified:** a freshly read-back, non-truncated synthetic Procedure in authorized
`KBTeste` was copied, specified, generated, compiled and executed using the official
GeneXus 18.0.10.184260 SDK/.NET generator. The same authored cases were independently
built and executed with the release gx-engine. All **75 shared cases / 76 output
values** matched each other and the independent integer oracle.

Coverage includes arithmetic precedence/associativity, signed division (including
halves), large exact arithmetic within Numeric(18,0), variable multiplication,
all six comparisons with unequal and equal operands, nested branches, zero and
nested loops, case-insensitive variables, copy/overwrite behavior, output ordering,
and 40 deterministic arithmetic expressions (seed 890129).

### Confirmed native generator failure

The additional case `123456789 * 123456789` assigned to `Numeric(18,0)` passed
specification and generation, but **failed the generated C# build with CS0220**.
The generator casts the result of the multiplication to `long` rather than widening
an operand before the multiplication, leaving an overflowing `int` constant
expression. The correct result, `15241578750190521`, fits both the declared native
numeric field and signed i64. The same multiplication through a variable passed
on both engines. No generated files were patched to make the native case pass.

The engine independently compiled/executed this constant case correctly. It is
retained in the portable corpus and the Rust native-pipeline regression, whose
oracle was deliberately mutated to zero: the test failed, then passed after
restoration. The failing constant case was **explicitly excluded from the 75-case
native executable**, not counted as a native pass.

### Engine-only safety lane

`validate_edges.py` passed **76 result cases / 77 outputs**, **22 malformed or
unsupported-source rejections**, **8 invalid-document rejections**, and **7
controlled runtime failures** (addition/subtraction/multiplication overflow,
zero division, MIN/-1, negating MIN, and empty infinite-loop exhaustion).
An empty procedure was separately verified as accepted, returning zero with no
printed output, according to the existing engine contract.

The fresh seed-20261015 standalone oracle run passed 503 expressions / 513 values,
8 rejections and 5 controlled runtime errors. This is **engine versus Python and
same-code rustc**, not additional native GeneXus differential coverage. Its build
medians were 934.28 ms (pipeline) versus 727.06 ms (direct rustc); no new speedup
claim is made.

### Evidence and reproduction

Portable authored corpus: `scripts/edge_corpus.py`; safety runner:
`scripts/validate_edges.py`; oracle/translator/comparison tests:
`scripts/test_edge_corpus.py`. No SDK/decompiled/generated proprietary sources
were added to versioned files.

```powershell
cargo build --release --offline --locked --manifest-path src/gx-engine/Cargo.toml
python -m unittest discover -s src/gx-engine/scripts -p 'test*.py'
python src/gx-engine/scripts/validate_edges.py --engine src/gx-engine/target/release/gx-engine.exe --out scratchpad/edge-offline-report.json
```

Ignored native evidence: `scratchpad/RustEdge7476154929200/native-build.log` and
`dotnet-diagnostic.log` (failing constant product); successful full-source readback,
SDK build log, executable stdout/stderr and `report.json` under
`scratchpad/RustEdge7476154929200-retry-1792027763270659800/`.
Fixture `RustEdge7476154929200` remains in KBTeste with the passing 75-case source.
The original failed-build log preserves the separate failing source evidence.
An intervening attempt to create `RustEdge7531887929800` returned
`AtomicCreateFailed` without an inner cause in the worker log; no success is
claimed for that creation. Updating our existing owned fixture succeeded.

### Logical-operator implementation and differential follow-up

The objective remains **complete SDK/toolchain replacement**; the coverage below
records progress toward it, not a ceiling on the project.

Implemented case-insensitive `And`, `Or`, and `Not`, including parentheses,
comparison-before-Not precedence, And-before-Or precedence, and left-to-right
short-circuit emission. Not lowers to numeric equality with zero in the current
IR; this does not yet introduce a GeneXus Boolean type or typed operator checks.
Expression-node/depth limits and validation of both operands remain enforced.

**Verified:** all **91 shared cases / 92 ordered values** matched the independent
oracle on both freshly built executables in KBTeste. This adds truth tables,
precedence/grouping, repeated Not, mixed keyword case, loop conditions, and
short-circuit guards around division by zero. The native constant-product failure
remains separately excluded, not silently treated as passing.

Engine-only validation now passes **92 cases / 93 values**, **28 source
rejections**, 8 invalid documents and 7 controlled runtime errors. Malformed,
excessively deep and excessively wide logical expressions are included.
Two new native pipeline guards cover logical control flow and helper effects:
skipped operands do not print; needed calls execute once. Changing And emission
to Or made both guards fail; restoration made both pass. Helper-effect parity
with a native GeneXus helper is not established by these engine-only guards.

Evidence: ignored `scratchpad/RustEdge7476154929200-retry-1792028006004645000/`
contains exact fresh source readback, SDK build diagnostics, both executable
outputs and `report.json`; `scratchpad/logical-edge-offline-report.json` records
the offline lane. Our owned fixture now contains the passing 91-case source.
Rust suite: 52 passed, 1 informational benchmark ignored; Python: 8 passed;
Clippy with warnings denied passed. Static quality/test-gate remain non-green.

### Remaining work toward complete SDK replacement

| Area | Current evidence / gap |
| --- | --- |
| Shared integer/logical subset | 91 native differential cases verified; one additional native emitter failure reproduced. |
| Full signed-i64 domain | Engine checks/oracle verified; native Numeric(18,0) is narrower. Not native numeric type parity. |
| Error behavior | Engine fail-closed checks verified; native syntax probes previously recorded. Native runtime-error differential matrix remains not verified. |
| Calls/recursion/incremental rebuild | Existing engine tests pass; equivalent expanded native call/signature/revision corpus not verified. |
| Boolean/Numeric types | Format-2 native emission implemented; 19 native observations match. Integer wrapping is an explicit safety divergence. Typed calls/general decimal behavior remain gaps. |
| Strings, dates | Not implemented yet; part of the full-replacement objective. |
| SDTs, collections, modules, domains, rules, navigation | Not implemented/parity not verified. |
| DB transactions, SQL, generators/deployment | No equivalent engine implementation; test KB build only, no production/database application workload or cloud deployment. |
| Other SDK majors/generators/real KB applications | Not validated by this run. |

Release quality is **not fully verified**: Cargo 50 tests (one informational
benchmark ignored), Python 8 tests, Clippy and formatting pass; Ripwire edit-check
passes, but repository-wide quality-delta exits 2 and test-gate exits 4. These
retain the earlier unresolved prototype-wide findings; no gate was weakened.


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
At discovery time, Boolean declarations/decimal literals/typed storage were not
implemented. The implementation checkpoint below supersedes that status;
these observations prevented encoding an incorrect fixed-scale contract.
Ripwire quality/test gates still exit 2/4 (six gating findings); not release-ready.

## Typed compiler implementation checkpoint

Portable format 2 now authors explicit Boolean/Numeric declarations and compiles
through typed checking, Rust emission, rustc, and a standalone executable. It uses
no SDK/MSBuild in the compiler path. Format 1 retains the original checked-i64
semantics. `types.rs` implements exact checked decimal storage independently;
`typed_backend.rs` validates types and emits native programs. See README for the
format and precise admission limits.

**Verified:** 19 typed executable outputs match the earlier verified, complete
native source/declarations/run above. A newly built release Rust executable was
used; the native build was not rerun for this comparison. Source-matched evidence
is required, and missing/extra/changed output or either failed execution rejects
the comparison. Evidence: `scratchpad/typed-rust-differential.json`.

The twentieth native case wraps Int16 storage. Rust deliberately diagnoses that
overflow (exit 70); it is an explicit safety divergence, not a matching case.
Native overflow-marker formatting is distinct from that storage error. Only
Numeric(3,0)'s storage boundary has been characterized; other integer declarations
currently use checked i64. Repeating division and unsupported intermediate
precision produce controlled errors, not silent approximations. Typed helpers,
parameter/return contracts and typed incremental explain remain to implement.

Fresh checks: **62 Rust tests passed**, one informational benchmark ignored;
**16 Python tests passed**; Clippy with warnings denied and rustfmt passed.
New native-pipeline coverage exercises typed arithmetic/conditions, strict
Boolean/Numeric rejection, declarations/round-trip/duplicates/unknown fields,
short-circuiting, loop budgets, overflow and zero division. Mutating half-rounding
to truncate and bypassing type checks each made the relevant test fail; fixes
were restored. The legacy offline lane still passes 92 cases/93 values, 28 source
rejections, 8 malformed documents and 7 runtime errors. Ripwire's named gxwire
lane was rerun successfully; no gxwire source was changed.

Ripwire typed emit edit-check exits 0. Repository-wide quality-delta still exits
2 with six preexisting-worse major rows against Git HEAD, with no pinned baseline;
test-gate exits 4 (9 named test files, 3 statically untested symbols, including
typed emit despite executed integration coverage; graph resolution is incomplete).
No gate suppression/baseline reset. Broader .NET/npm/Nexus/release lanes were not
run for this isolated engine increment; full replacement/release readiness is not
verified. No engine commit/push or production/Cloud changes.

Reproduce (from repository root, with the ignored native evidence present):

```powershell
cargo test --offline --locked --manifest-path src/gx-engine/Cargo.toml
cargo clippy --offline --locked --manifest-path src/gx-engine/Cargo.toml --all-targets -- -D warnings
cargo build --release --offline --locked --manifest-path src/gx-engine/Cargo.toml
python src/gx-engine/scripts/validate_typed.py --engine src/gx-engine/target/release/gx-engine.exe --native-evidence scratchpad/typed-native-1792028364801578100 --report scratchpad/typed-rust-differential.json
python -m unittest discover -s src/gx-engine/scripts -p 'test*.py'
```

Full SDK replacement remains the execution objective. The next capability gates
are typed procedure calls/signatures and wider native storage characterization,
then strings/dates/SDTs and navigation/data-model integration.

## Typed calls / integer storage / incremental checkpoint

Supersedes the earlier one-procedure admission limit above. Portable format 2
supports declared by-value parameters and Boolean/Numeric return types. Arguments
and return values undergo checked storage conversion. Helpers must return on all
paths; void/output/inout contracts are not implemented. Shared depth (128) and
execution-step (100,000) guards apply across all calls.

**Verified:** fresh four-procedure native SDK/MSBuild generation, compilation and
execution in KBTeste, compared with the current independent release executable.
All Source/Rules/declarations were read back and checked before building. Both
executables exited 0 and matched **12 ordered output lines**, including repeated
helper side effects, multiargument order, nested calls, retained decimal storage,
Boolean returns, and helper calls on each loop-condition evaluation. Evidence:
`scratchpad/typed-calls-native-1792030284812864000/report.json`.
Portable authored fixture: `fixtures/typed-calls.json` (no generated SDK code).

The first native run revealed a mismatch: GeneXus hoists calls before evaluating
the enclosing Boolean expression, so a helper inside the right side of a true
Or still executes. Typed lowering was corrected and regression-tested. Pure
Boolean operands retain short-circuit evaluation; format 1 retains its existing
legacy behavior. This is a measured native-emitter rule, not an assumption that
all GeneXus generators behave identically.

**Verified:** integer widths 1..18, fresh native execution with complete Source
and Numeric declarations checked. First negative value occurred after 15, 31 or
63 doublings for precisions 1..4, 5..9 or 10..18 respectively. Evidence:
`scratchpad/width-native-1792029927991083100/`. Rust applies checked signed
Int16/Int32/Int64 storage ranges instead of reproducing native wrap. Boundary
regressions exercise both valid extrema and out-of-range neighbors.

**Verified:** typed incremental updates reuse unchanged syntax and revalidate
semantics; declaration/return-signature changes invalidate callers, invalid
revisions roll back, and emitted output equals clean compilation. CLI explain
executes this path without a native build. Disabling metadata invalidation made
the regression fail; restoring it passed. No executable-cache/speedup claim.

**Not verified:** full native calling convention, output/inout/void procedures,
all return/control-flow behavior, Decimal rounding parity, full SDK replacement,
native KB interoperability, and release readiness. Earlier non-green static
quality/test-graph gates remain independent from these executable results.

## Character/Varchar compiler checkpoint

Implemented portable Character/Varchar declarations, double-quoted Unicode
literals, assignment, concatenation, equality/inequality and UTF-16 `.Length()`.
Typed text helper arguments/results preserve ownership and stored content.
Stored values have a checked 1 MiB UTF-8 resource ceiling; metadata lengths are
bounded to 1..65535, not claimed as native limits. Unsupported escapes, methods,
ordering and mixed-type operations fail closed. Legacy format 1 rejects text.

**Verified native oracle:** `scratchpad/string-native-1792031065612440700/`,
complete Source/Variables readback, fresh SDK/MSBuild native executable, exit 0.
`Character(5)` and `VarChar(5)` store short, empty and over-width strings without
padding/truncation in this standalone .NET procedure. Concatenation preserves
spaces; equality ignores trailing spaces and distinguishes case. Unicode BMP
text is preserved. These are not database column/storage or all-generator rules.

**Verified Rust comparison:** `scratchpad/text-rust-differential.json`, against
that source-matched native run. Of 18 observations, 17 agree (four length labels
are normalized to values). One explicit safety divergence: the native generator
wrote its C# source in CP1252, replacing the authored emoji surrogate pair with
`??`; the executable printed `a??b`, while Rust preserves `a😀b`. Source readback
still contained the emoji. Native `.Length()` returned 4 for the resulting `a??b`;
Rust's length of the original non-BMP text is also 4 UTF-16 units. Do not describe
this as native emoji round-trip parity. No lossy behavior was copied into Rust.

The string regression failed before implementation. Replacing trailing-space
comparison with raw string equality made it fail again; restoration passed.
Library/pipeline tests also exercise ownership across calls, invalid declaration
lengths, malformed literals, non-text Length receivers and runtime growth limits.
Python tests reject stale evidence, wrong declarations, failed native runs and
missing/extra/reordered output. No proprietary generated sources were added to Git.

Reproduce: `python src/gx-engine/scripts/validate_text.py --engine
src/gx-engine/target/release/gx-engine.exe --native-evidence
scratchpad/string-native-1792031065612440700 --report
scratchpad/text-rust-differential.json` (single shell command).

Remaining: richer text libraries/collations/encodings, full Decimal behavior,
output/inout/void calls, dates/SDTs, navigation, data model/KB providers and broader
SDK/runtime parity. Full replacement is still the objective, not achieved.


## Date compiler checkpoint

The format-v2 Date path is implemented independently in Rust: explicit Date
variables/parameters/returns; YMDToD; Year/Month/Day/IsEmpty; SetEmpty assignment;
comparisons; integer-day offsets and Date differences. Ordinal zero is the .NET
minimum-date/empty sentinel, with empty components reported as zero. The runtime
rejects fractional offsets, out-of-range years/arithmetic and unconfigured
0..99 years. Date display, DateTime and general calendar operations remain open.

Native evidence: `scratchpad/date-native-1792031609833525800/` (authorized KBTeste,
complete authored source/declarations, fresh Specify/Generate/Compile/run, exit 0).
All **37 outputs match** a freshly compiled `fixtures/typed-dates.json`, after
normalizing diagnostic labels/Print formatting. The explicit project setting
`date_two_digit_year_max: 2039` reproduces the measured environment's 39->2039,
40->1940 window. There is no universal or clock-dependent default. The setting is
validated and included in incremental invalidation; parsed bodies remain reusable.

A separate negative-year probe at `scratchpad/date-native-1792031579886450900/`
exited 1 with a native ArgumentOutOfRangeException after the first 22 observations.
It is error evidence, **not a successful differential**. Rust reports controlled
errors instead of copying native exception text. Date helper boundaries,
comparisons, policy invalidation/rollback and error guards have Rust regressions;
not every one has its own native equivalent yet.

Validation: 87 engine tests passed (1 ignored), Clippy warnings denied, 21 Python
tests passed. Gregorian component round-trip/day-continuity tests cover every
valid day in years 100..9999. Mutating the window cutoff `>` to `>=` failed the
2039 regression; restored code passed. Date edit-check passed. Ripwire
quality-delta remains exit 2 (8 HEAD-comparison clone gates; added Date type mapping
is structurally matched with unrelated gxwire mode mappings); test-gate exit 4.
No baseline suppression or cross-domain abstraction was introduced. Broader .NET,
Nexus/release lanes remain unverified for this Rust checkpoint; build.ps1 must
remain the final build step when executing the full repository verification set.

Reproduce the offline Date differential (no KB/SDK writes):

```powershell
cargo build --release --offline --locked --manifest-path src/gx-engine/Cargo.toml
python src/gx-engine/scripts/validate_dates.py --engine src/gx-engine/target/release/gx-engine.exe --native-evidence scratchpad/date-native-1792031609833525800 --report scratchpad/date-rust-differential.json
```

Full SDK replacement remains the goal, not an achieved compatibility claim.
Next capability work: calendar methods/DateTime, structured types, parameter modes,
then read-only specification/navigation and independent KB storage/provider work.


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

## Parameter modes checkpoint (2026-10-15)

- Native evidence (disposable `C:\KBs\KBTeste`, official .NET build path):
  `scratchpad/parm-modes-native-1792068810475057600/report.json` and
  `scratchpad/parm-default-native-1792069248962676800/report.json`.
- `tests/parm_modes.rs` runs `fixtures/typed-parm-modes.json` and expects the 18 native
  values in order, the unprefixed-default case (5 values), and empty Boolean/VarChar/Date
  out values (6 values, `scratchpad/parm-outtypes-native-1792069401258459300`). Mutations (reverse
  copy-back order; out entry value 1 instead of 0) each fail the native-value test.
- Full offline suite, `cargo clippy --all-targets -D warnings`, `cargo fmt` and the 22
  Python script tests pass. Scope: Numeric, Boolean, VarChar and Date parameters; not general SDK parity.

### Native call ordering and portable Rules follow-through

Native evidence `scratchpad/parm-order-native-19b0581ce13e4d3da76765b69d5cf0fe`
contains nine exact source/rules/variables readbacks and five successful SDK native
builds/executions. Nested call side effects precede direct operand captures in the
observed cases: Pair(X,Bump(X)) returns 22; X+Bump(X) from 10 returns 22 with X=11.
Rust matches; the earlier review candidate was not a bug. Explicit Numeric inout
rvalues mutate only their temporary. Interleaved alias writes end at the last formal
copyback (41). Tests cover these observations and final-out expression returns,
with 14 parameter tests passing. This is not general evaluation-order parity.

Portable catalogs admit standalone Parm Rules, preserving modes/order and retaining
the original catalog parts. Rules units 2/2 and KB pipeline 4/4 pass, including a
saved-catalog CLI build/run printing 42 without the SDK and unchanged-catalog check.
Forcing all parsed modes to inout fails both Rules tests; restored. General Rules,
native KB decoding and full SDK replacement remain unsupported.

## SDK-vs-engine benchmark (str_format)

`python src/gx-engine/scripts/benchmark.py` (repository root) measures one
shared capability on both sides over the same recorded corpus and re-checks live
parity in the same run. The native side calls the real
`GeneXus.Utils.StringUtil.Str` from the official `genexus.classes.core` package
through `scripts/sdk-oracle` (the runtime generated applications link against,
not an independent reimplementation); the engine side runs
`benches/str_format.rs` (`gx_engine::types::format_str`). Both sides execute one
warm-up pass and N measured passes, verify every live result against the
corpus' recorded expected text, and report per-pass milliseconds plus an
FNV-1a/UTF-16 digest over all output characters; the runner fails on any
row-count, character-count, digest or expected-text mismatch. Reports go to the
ignored `scratchpad/engine-benchmark.json`; they are measurements, not committed
evidence.

Prerequisites: .NET 8 SDK (first restore needs nuget.org) and the Rust
toolchain. Default corpus: `tests/fixtures/str_matrix.tsv` (6,152 rows);
`--iterations`, `--corpus` and `--out` override.

Measured runs (Windows 11 x64, rustc 1.93.1, .NET SDK 10.0.204, package
1.30.0, 2026-10-16; medians across runs of 100 passes unless noted):

- **Verified live parity on every run:** identical row count, character count
  (80,235) and digest (`0xb9e1b48fb72067b0`); 0 expected-text mismatches on
  both sides.
- Native `StringUtil.Str`: 274-286 ns/row.
- Engine `format_str`: 121-130 ns/row (~2.25x native throughput).

The engine's first recorded run (50 passes) measured 530 ns/row. Two hot-path
fixes followed: rendering now assembles the picture into a stack buffer with a
single `String` allocation (no `fmt` machinery), and `Wide::div_rem` gained a
`u128` fast path that skips the 256-step bitwise loop for every operand shape
the picture pipeline can produce. Both are output-identical under the recorded
fixtures.

This is a constant-heavy micro-benchmark of one formatting helper - not MCP
overhead, KB work, or a whole-build comparison. It measures the exact helper a
product operation would replace, per `docs/engine_backend.md`. Short runs vary
by a few percent on this host; compare medians of full runs.

## Numeric implicit rendering parity (`NumericType::render`)

`python scratchpad/run-numeric-render-native.py` (ignored probe driver) authored
one Procedure in disposable KBTeste that, for every case, records both
`&var.ToString()` and `Str(&var, precision, scale)` on the same stored value.
The scan covered 120 cases / 244 observations:

- precision 1..18 with the picture bound, the first value over it and one past
  that;
- scale corners for 13 `(precision, scale)` pairs: bound, first over, an exact
  half that rounds into the next integer - the rounded text may leave the
  picture, e.g. `9.95` at `(3,1)` renders `10.0` - and the negative bound;
- the width 15..18 regime where the bound is narrowed through `(decimal)`:
  `10^15` renders as digits at width 15, and `10^16`..`10^18`, positive and
  negative, render one digit past the declared width;
- zero, negative zero, dot accounting (`(5,2)` `999.99` is `*****`; the largest
  renderable `(5,2)` value is `99.99`) and half-away-from-zero rounding.

Result: `implicit == Str(...).lstrip()` for all 120 cases, and the four raw
captures equal the padded `Str` output byte for byte - native
`&var.ToString()` **is** `StringUtil.Str(value, precision, scale)`. The engine's
`NumericType::render` therefore delegates to the already-verified `format_str`
pipeline; the recorded rows are checked in at
`tests/fixtures/numeric_render_native.tsv` (format
`value|precision|scale=[Str]`, padding included) and run by
`tests/numeric_render_native.rs`. Probe-lane fixtures whose native sources
recorded `.ToString().Trim()` (date/datetime, For, parm modes, void return,
typed calls/do-case/helpers) compare printed lines at value level, with the
provenance documented in each test file.

Mutation checks: reverting `render` to unpadded output fails the fixture; using
a picture one digit narrower fails it; both restored green. Portable gate:
247 tests passed / 0 failed / 2 ignored; Clippy and rustfmt clean.

## Text function parity (`Substr`/`Upper`/`Lower`/`StrReplace` and methods)

`python scratchpad/probe-function-names.py` (ignored probe driver) first pinned
which spellings the GeneXus 18 specifier accepts at save time, reusing one
Procedure in disposable KBTeste. Accepted: `Substr(&V,1,2)`, `Upper(&V)`,
`Lower(&V)`, `StrReplace(&V,"a","b")`, `&V.Substring(1,2)`, `&V.ToUpper()`,
`&V.ToLower()`, `&V.Replace("a","b")`. Rejected (`Função desconhecida`):
`Substring(...)` as a function, `Replace(...)` as a function, `&V.Substr(...)`,
`&V.Upper()`, `&V.Lower()`, `StrReplaceAll`, and a literal receiver
(`"banana".Replace(...)`). The engine admits both accepted forms and rejects the
others as unresolved calls.

`python scratchpad/run-text-functions-native.py`,
`run-text-functions-semantics.py` and `run-text-functions-charspace.py` then
measured runtime semantics across three Procedures (safe, risk and
character/space): 63 recorded observations, raw captures in
`scratchpad/text-functions-*-1792*`.

- `Substr` operates on the raw stored value, not the padded field: an explicit
  trailing space survives (`Substr(" ab ",2,3)` is `ab `) and a declared
  `Character(4)` holding `ab` returns `ab ` for `Substr(&c,1,3)` with no
  padding. Positions are 1-based; `start < 1` or `start` beyond the raw length
  yields empty text; a negative length takes the remainder of the string and an
  over-long length clamps. `.Length()` still counts without trailing spaces.
- `Upper`/`Lower` map per character with a single-character result; when the
  mapping expands, the original character is kept (`ß` stays `ß`), `µ` becomes
  `Μ`, `ÿ` becomes `Ÿ` and `Ÿ` becomes `ÿ`. Accented round-trips
  (`áéíóúçñü` <-> `ÁÉÍÓÚÇÑÜ`) match.
- `StrReplace` is case-sensitive, replaces every non-overlapping occurrence
  left to right (`("aaa","aa","b")` is `ba`), an empty find is a no-op and an
  empty replacement removes the match. The method form shares every rule.

Recorded rows: `tests/fixtures/text_functions_native.tsv` (63 rows, format
`function|args=[result]`, generated from the recorded labels by
`scratchpad/make-text-functions-fixture.py` - no hand-typed expected values) and
`tests/text_functions.rs` (fixture differential; build/run e2e over both the
function and method spellings against a 15-line expected stdout; rejection of
wrong arity, non-text receivers and malformed method forms).

Mutation checks: removing the substring length clamp panics on the fixture;
using the first mapped character for expanding case mappings fails `ß`;
removing the empty-find guard fails `("banana","","X")`; removing the
`Substring` method branch fails the e2e with the unsupported-method error; all
restored green. The method grammar was moved out of `parse_primary` into
`parse_method` in the same change because the nesting-limit test encodes the
parser's stack budget: it now passes on a 1.5 MiB thread stack where the
pre-change parser needed more than 1.93 MiB. Portable gate: 250 tests passed /
0 failed / 2 ignored; Clippy and rustfmt clean. Outside this claim: non-BMP
(astral) positions, database-column semantics and the native KB store; no
product operation consumes the capability yet.

## Display-path discovery and Date/DateTime display parity

Three probe Procedures in disposable KBTeste
(`scratchpad/run-display-native.py`, `run-display-native2.py`,
`run-display-native3.py`; raw captures in `scratchpad/display-native-1792*`)
measured what the GeneXus 18 .NET runtime actually emits when values convert
to text. Two probe loops failed first and are kept as evidence: the first
source stopped at specify (`scratchpad/display-native-1792152734293958900`)
and the second failed at compile because the generator emitted invalid C#
(`...-1792153372444693300/arustdisplayprobe.generated.cs`). The itemized
`spc*` error list comes from `genexus_lifecycle action=specify` **after** the
MSBuild build refreshes the environment copy - the gateway specify on its own
reads the previous build's source, so it is a post-build diagnostic only
(`scratchpad/specify-errors.py`).

Native conversion facts recorded during the discovery (each one shaped an
engine decision):

- Bare `msg(&N5)`, `msg(&D)` and `msg(&DT)` are type mismatches (`spc0009`),
  and `&T40 = &D` (Text = Date) is a type mismatch (`spc0010`). Only a
  constant numeric expression is accepted bare: `msg(21 * 2, status)` builds
  and renders through `StringUtil.LTrimStr` (`1234567890 + 5` is
  `1234567895`). A variable inside the expression (`msg(&N5 + 0, status)`) is
  rejected again.
- Concatenation with text is raw C# concatenation, not a formatted render:
  `"x=[" + &N5 + "]"` prints `42` unpadded, `"x=[" + &P52 + "]"` prints `7`
  (the `(decimal)` literal carries no scale), and after
  `&P52 = &P52 / 3` the same concatenation prints all 28 decimal digits with
  the machine's comma separator - the .NET decimal keeps full precision and
  the declared scale is only applied by formatting calls.
- A parenthesized arithmetic expression inside a `msg` text concatenation
  generates **invalid C#** in 18.0.10 (`"ne_prod=[" + (21 * 2) + "]"` becomes
  `"ne_prod=["+21+*+2+"]"`; `+`-only forms compile but concatenate instead of
  adding: `(&N5 + 0)` prints `420`). The compile fails, so the shape is
  unusable in native code.
- Date/DateTime concatenation prints the raw .NET `DateTime.ToString()` -
  culture-dependent (`07/03/2024 00:00:00` on this machine) - while
  `&D.ToString()` uses the GeneXus fixed picture. The engine therefore keeps
  rejecting text concatenation with Date/DateTime and numeric operands
  (`binary operand type mismatch`): no culture-dependent bytes are claimed,
  and callers that need text use `Str(...)`/the render path.

`&D.ToString()` / `&DT.ToString()` measured rules (18 recorded rows):

- `Date`: `MM/dd/yy` with zero padding and the year modulo 100 (year 100 and
  1900 render `00`, 9999 renders `99`); empty renders `"  /  /  "`.
- `DateTime`: the same date part, then a 12-hour `hh:mm AM/PM` time with
  seconds dropped (`14:05:09` is `02:05 PM`, `00:30` is `12:30 AM`, noon is
  `12:00 PM`); empty renders `"  /  /   12:00 AM"`.
- Date-empty-with-time keeps the existing component override: `YMDHMSToT(0,
  0, 0, 14, 5, 9)` renders `01/01/01 02:05 PM`, matching `year()`/`month()`/
  `day()` returning `1/1/1` for that shape.
- The two-digit-year window used by these rows is the measured environment
  policy (2039, already encoded in `fixtures/typed-dates.json`).

`scratchpad/make-date-display-fixture.py` derives the checked-in fixture from
the three probe reports (constructor arguments parsed from the case files,
expected bytes from the recordings - nothing hand-typed):
`tests/fixtures/date_display_native.tsv` (18 rows, format
`kind|year|month|day|hour|minute|second|expected`) and
`tests/date_display_native.rs` (fixture differential over
`Date::ymd_with_window`/`DateTime::from_parts` + `to_text`, plus a build/run
e2e that prints through `Print` and compares raw stdout bytes, including an
arithmetic date value).

Mutation checks: dropping the zero padding fails both tests; changing the
`0 -> 12 AM` rule fails the midnight row; changing the empty DateTime
sentinel fails the empty rows; reverting the `Print` arm to the previous
rejection fails the e2e. All restored green. `Print` of a Date/DateTime value
(variable or expression) is now the engine counterpart of native
`&var.ToString()`, and the capability is registered as `date_display` at
parity. Portable gate: 252 tests passed / 0 failed / 2 ignored; Clippy and
rustfmt clean. Outside this claim: date/time text concatenation, timezones,
fractional seconds and `Str`-style width/scale time pictures; no product
operation consumes the capability yet.

## Date/character conversions (`CToD`, `DToC`, `TToC`)

`DateTimeUtil.CToD2`/`DToC2`/`TToC2` from the same `genexus.classes.core`
package are the runtime helpers that generated code calls for the language
`CToD`, `DToC` and `TToC` conversions. `scripts/sdk-oracle` mode
`generate-date-conversions` prints the checked-in corpus
(`tests/fixtures/date_conversions_native.tsv`, 50 rows; spaces in inputs are
encoded as `\s` so trimming rows survive whitespace-insensitive storage) and
`tests/date_conversions.rs` consumes it through `Date::parse_cto_d`,
`Date::to_iso_text` and `DateTime::to_iso_text`.

Measured `CToD` grammar (39 rows):

- Three numeric components separated by any mix of `-`, `/` or `.`
  (`2024-02/29` and `02.29.2024` are accepted). A four-digit first component is
  read `yyyy`-first; otherwise the order is `M/d/[yy]yy`, so `29/02/2024` is
  empty while `02-29-2024` is 2024-02-29.
- Input is trimmed; empty or unparsable input yields the empty date.
- A trailing part is accepted only after a space or `T`, and only when it is a
  valid `H[:mm[:ss]][.fraction]` time with an optional case-insensitive
  `AM`/`PM`: `2024-02-29 14:30`, `... 14:30:45.123` and
  `2024-02-29T14:30:45` keep the date, while `2024-02-29 zzz`,
  `... 25:00`, `...T` and `2024-02-29.5` are empty.
- Out-of-range components are empty (`2024-02-30`, `2024-13-01`, `24-02-29`),
  and compact or single-token forms are rejected (`20240229`, `00010101`, `1`,
  `12345`).
- Two-digit years resolve through the project's measured environment window
  (`"date_two_digit_year_max":2039`): `02/29/24` is 2024-02-29 and `12/31/39`
  is 2039-12-31.

`DToC` renders the ISO exchange picture `yyyy-MM-dd` with the empty sentinel
`"0000-00-00"` - deliberately not the `MM/dd/yy` display picture of
`&date.ToString()`. `TToC` renders `yyyy-MM-ddTHH:mm:ss` and the empty value
`"0000-00-00T00:00:00"`; it accepts a DateTime only, so `TToC(&date)` is
rejected like every other typed mismatch.

Outside this claim, with the reason recorded:

- `CToT` - the context-less native helper attaches the *current* date to the
  parsed time (`CToT2("14:30:45")` recorded 2026-10-17T14:30:45), so its date
  part cannot be characterized without a live Knowledge Base environment.
- The millisecond `TToC3` variant (`"....T14:30:45.000"`) and fractional
  seconds.
- Timezone-offset forms (`2024-02-29-01` records 2024-02-28 in the helper, i.e.
  offset math) and the two-digit-year boundary `01/01/40`, where the
  context-less helper resolves by century (2040) instead of the measured
  environment window (1940).

The e2e test builds and runs a project that prints `DToC`/`TToC` results
through `Print` and compares raw stdout bytes.

Mutation checks: removing the trailing-time validation fails the
`...zzz`/`...25:00` rows; forcing the first component to be read as `yyyy` fails
the `02/29/24`, `2/9/24`, `01/01/00` and `12/31/39` rows plus the e2e; relaxing
`TToC` to accept a Date fails the rejection set (the reject rows assign every
variable first, so a wrong reason cannot mask a wrong admission). All restored
green.

Arm gate for this change: 58 test binaries, **476 tests passed / 0 failed /
2 ignored**; `cargo clippy --all-targets` and `cargo fmt --check` clean. The
`Portable gate` counts recorded in the earlier sections are the suite sizes at
the time each section was written, so they are lower by construction.
