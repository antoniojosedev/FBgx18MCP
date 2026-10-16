# 128 — Evidence-led SDK acceleration with REA and Rust

## Status and mandate

- **Status:** SUPERSEDED IN DIRECTION by [129 — independent Rust SDK and toolchain](./129-independent-rust-toolchain.md), following the user's rejection of the SDK-centered ceiling. Historical proposal retained for evidence and integrity gates; its SDK-first research ordering is no longer the selected direction. No implementation or installation is authorized by either document.
- **Priority:** P1 for SDK-backed snapshots and native analysis; P2 for internal SDK adapters; experimental for direct UDM reads.
- **Planned at:** `b9de0a5ae4cdf0e3932b486b2f01702a60e7e3cd`, 2026-10-14.
- **Repository:** `C:/projetos/Genexus18MCP`.
- **Research checkout:** `C:/projetos/rea`, `c9764084b851f9e41773cc470c2ce167deb21946`, package `rea-agents` 6.5.0.
- **Requested outcome:** make the existing MCP substantially less dependent on expensive SDK work, without breaking KB integrity, compatibility, session isolation, or truthful results.
- **Related integrated plans:** 077 (SDK executor), 078 (revision-aware caches), 080 (semantic graph/build). Extend these mechanisms; do not recreate them. Their historical line references are not current-code evidence.
- No commit, push, release, MCP registration, global install, production-KB access, or SQL configuration change is included in this planning task.

## Decision

**Approve the direction, not the original execution order or the promise of “100% Rust.”**

The first production architecture should be:

```text
MCP request -> Gateway: resolve KB/session and authorize
                |                              |
                | pure, covered snapshot query | authoritative/live request or mutation
                v                              v
         gxwire: immutable snapshot       existing Worker / SDK executor
         parse / graph / impact            official SDK remains authority
                ^                              |
                | versioned SDK export + incremental changes
                +------------------------------+

REA = offline investigation tool, not a runtime dependency of MCP/gxwire.
Direct SQL -> experimental, isolated, read-only candidate provider only after parity.
```

This removes repeated analysis from STA after extraction. It does **not** remove initial SDK opening, authoritative synchronization, specifying, generating, or saving. Memory improves only if duplicated retained state is measured and reduced; adding a Rust process alone does not shrink the Worker.

## Verified repository facts and corrections to the submitted proposal

These are source observations, not a new live-KB or performance validation.

| Evidence | Finding / consequence | Confidence |
|---|---|---|
| `src/GxMcp.Gateway/tool_definitions.json` | 60 tool definitions currently exist. Preserve their schemas unless an intentional contract change is tested. | High |
| `src/GxMcp.Gateway/Program.ToolDispatch.cs:1163–1184` | Native KB directories are deliberately rejected with `GXWIRE_REQUIRES_TEXT_SOURCE_TREE`, not silently treated as an empty valid graph. Keep this protection. | High |
| `src/GxMcp.Worker/Services/SdkTextTreeService.cs:14–65` and `TextMirrorService.cs:12–58` | SDK-backed native text-tree export, a manifest, and incremental mirror already exist. First integrate these with gxwire rather than starting with proprietary blobs. | High |
| `src/GxMcp.Gateway/tool_definitions.json`, `genexus_io` | Existing actions include `export_kb_to_text`, `text_mirror_start`, `text_mirror_status`, and `text_mirror_catchup`. Native export supports parts, references, module metadata and table projections. No new public tool is initially necessary. | High |
| `src/GxMcp.Worker/Services/InProcessBuildRunner.cs:25–63`, `BuildService.cs:4049` | In-process Specify/Build tasks and a reflection-based compile path already exist. They need measurement and coverage, not a replacement implementation. Comments describing speedups are not current benchmark results. | High |
| `src/GxMcp.Worker/Services/WriteService.DataSelectorConditions.cs:76–111` | Nested-condition refusal protects a flat, whole-list replacement from destroying groups. This is a request-model limitation, not proof that the SDK cannot represent groups. | High |
| `src/GxMcp.Worker/Services/DataSelectorReadService.cs:95–124` | The public projection explicitly distinguishes persisted conditions from unavailable projection/resolved joins. Internal members may also lack resolved navigation until Specify. | High |
| `src/GxMcp.Worker/Services/MtaCommandExecutor.cs:7–39`, `Program.cs:547–595` | Bounded non-SDK MTA dispatch and owning-STA execution already coexist. “Everything runs on one STA” is too broad; move pure work, not live SDK objects. | High |
| `src/gxwire/src/cache/mod.rs:14–66` | Per-file parse caching and external cache-path support already exist. Retain the read-only source-tree boundary. | High |
| REA `docs/managed-code-analysis.md`, `src/cli/managedCommands.ts:170–193` | Seven managed tools exist. `import-managed-reconstruction` imports supplied reconstruction JSON against static evidence; it is not an automatic token-to-C# decompiler command. | High |
| REA `docs/managed-code-analysis.md`, “Member comparison” / “Tool and packaging boundary” | Normalized IL digest is not semantic equivalence; native declarations do not recover native bodies. Static inspection does not execute target assemblies or prove runtime behavior. | High |

### Changes required before adopting the submitted plan

1. **Remove `NOLOCK`.** Dirty reads, missed/duplicate rows and cross-table inconsistency are unacceptable input to dependency/safe-delete decisions. `ApplicationIntent=ReadOnly` is routing intent, not SQL permission enforcement. A future SQL reader requires a restricted principal and a coherent isolation strategy validated on an isolated restored KB; no database-wide setting changes by default.
2. **Do not assume SQL schema, compression, connection-file layout or XPZ format.** Table names, active-version selection, blobs and serialization must be observed on each supported format. ZIP/XML alone does not establish a valid importable XPZ.
3. **Do not equate AST analysis with the GeneXus compiler.** Base-table selection, extended tables, implicit navigation, subtype/domain semantics, generator options and DDL require independent reference outputs. Mark estimates as estimates; never apply a generated reorganization.
4. **Do not call decompilation alone clean-room.** Keeping dumps out of Git is necessary, not sufficient. Review license/interoperability constraints and use an independently written behavioral specification and appropriate separation/review where required. Do not copy proprietary implementation into Rust or tests.
5. **Do not globally install or run `setup` first.** Pin REA and any ILSpy version, inspect setup's proposed client/skill changes, and validate standalone static inspection first. Managed metadata inspection needs neither ILSpy nor Ghidra. REA has Windows-specific setup guidance, but host/backend support must be demonstrated locally rather than inferred from PE target support.
6. **Do not use exact assembly fingerprints as runtime compatibility gates.** Record SHA-256/MVID/tokens as investigation identity; preserve the repository's supported-major contract and probe required capabilities within a major.
7. **No automatic fallback to direct SQL when an export is missing.** Explicitly report snapshot state and start/require supported SDK synchronization. SQL is a different trust and consistency boundary.

## Priorities and dependency order

Effort is a relative estimate, not a delivery commitment. Risk means implementation risk.

| Work package | Priority | Effort | Risk | Depends on | Deliverable |
|---|---|---|---|---|---|
| A. Baseline and REA qualification | P1 | M | Low/Medium | — | Actual bottleneck report and reproducible static-analysis environment |
| B. Coherent SDK snapshot -> gxwire | P1 | L | Medium | A | Useful native-KB analysis without DB reverse engineering |
| C. Parser/graph fidelity | P1 | L | Medium | B | Identity-aware, provenance-aware native analysis |
| D. DataSelector hierarchical editing | P1 | M/L | High | A; B useful for analysis | Group-preserving edit contract and persistence evidence |
| E. Existing in-process build acceleration | P1 | M | High | A | Fewer unnecessary external fallbacks without false success |
| F. Capability compatibility catalog | P2 | M | Medium | A; apply to D/E | Tested capabilities across installed GX16/17/18 |
| G. Direct UDM SQL feasibility spike | P2/research | L/unknown | High | A/B baseline; separate approval | Narrow go/no-go, not a production reader commitment |
| H. Native XPZ inspection / estimated schema analysis | P3/research | L/unknown | High | C; independent fixtures | Narrow optional capabilities, no compiler replacement |

Recommended sequence: **A -> B -> C**, with D/E after A, F alongside them; decide G only after measuring B. H is not a prerequisite for useful acceleration.

## Work package A — Establish evidence before changing engines

### Scope

Inspect existing `src/GxMcp.Benchmarks`, `scripts/test-live-matrix.ps1`, `docs/live-kb-test-harness.md`, Worker queue/build diagnostics and Rust benchmarks. Reuse them before adding a new harness. Research outputs go under `C:/projetos/scratchpad/rea-evidence/`, outside Git, with redacted metadata only in committed reports.

### Steps

1. Recheck Git state and planned-at commit. Record selected installed SDK/version, process architecture, test KB identity, dataset sizes, and SDK license availability. Do not use business KBs.
2. Measure cold/warm read, list/query, source search, wire/impact, DataSelector read, compile_check and targeted build separately. Distinguish queue wait, SDK work, serialization, IPC, Rust startup/index/query and subprocess startup. Record process private bytes and total working set, not only Rust memory.
3. Run 1/2/4/8-client scenarios using the existing harness and bounded admission. At least 30 warm samples per scenario; report p50/p95 and failures, plus cold samples separately. Mark unavailable lanes blocked.
4. Qualify pinned REA as a standalone CLI on this Windows host. Inspect one operator-owned managed fixture first, then licensed local Artech metadata only after confirming scope. Record exact invocation, parser coverage/limits, exit status, SHA-256/MVID and tool version. Use selected types/summary paging instead of dumping whole assemblies.
5. Add ILSpy only if C# reconstruction is needed and explicitly approved; prefer a scoped, pinned installation. Do not register MCP or modify all account configs just to run a CLI experiment.

### Acceptance / stop gate

- A reproducible baseline distinguishes where time and memory are actually spent. No performance figures from the submitted plan become accepted facts.
- Static inspection records whether coverage is complete/partial/unavailable. Empty partial results never establish that a member is absent.
- STOP the affected experiment if REA does not run on this host; evaluate a separately approved host/tool alternative. Other work packages need not stop.

## Work package B — Coherent SDK snapshot -> Rust

### In-scope entry points and exemplars

- `src/GxMcp.Worker/Services/SdkTextTreeService*.cs`: native export and manifest.
- `src/GxMcp.Worker/Services/TextMirrorService*.cs`: catchup, changes and deletions.
- `src/GxMcp.Worker/Services/SdkExecutor.cs`: SDK affinity, not a new thread-per-request mechanism.
- `src/GxMcp.Gateway/Program.ToolDispatch.cs`, existing KB-resolution/cache services: route analysis only after resolving the requesting session.
- `src/gxwire/src/cache/mod.rs`, `model.rs`, `parser/`, and matching test projects.
- Existing test pattern: `TextMirrorServiceTests.cs` uses disposable directories and tests delete/manifest reconciliation; retain this style.

### Implementation increments

1. Characterize the existing export/mirror and native-KB refusal end to end. Export a disposable KB with the current `genexus_io` native format, then point `genexus_wire` explicitly at that export. Record missing object/part/reference coverage before extending the format.
2. Extend the existing manifest only where needed: schema version, physical KB/model/environment identity, stable object/type/module identity, selected parts, source version token, content digest, deletion state, coverage and publication generation. Reuse repository revision identities; hashes are integrity data, not authorization.
3. Publish a complete generation atomically. Per-file replace alone does not prove a multi-object snapshot is consistent. Capture SDK data on its owning executor, then encode/write immutable data off STA. Use supported revision fences or repeat/check/retry to detect external IDE edits; if no cross-object consistency guarantee is available, declare that limitation instead of claiming an atomic SDK snapshot.
4. Rust ingests only an admitted manifest generation, not arbitrary generated output. Validate paths, sizes, schema and identities; cap payload and cache allocations. Store cache outside the KB. Scope caches by KB/model/environment/generation/parser version. Reject truncated/corrupt cache files and rebuild only from trusted source input.
5. Route eligible graph/source-analysis requests to the snapshot. Initial scope is `genexus_wire` and proven pure-analysis consumers, not a wholesale replacement for `genexus_read/query/inspect` semantics. Preserve authorization, session KB selection, compact fields and response envelopes.
6. Define internal states `building`, `ready`, `stale`, `incomplete`, `unavailable` (names are proposals, not public API yet). Expose freshness/coverage in existing diagnostics. Stale data must not authorize safe delete, rename, or incremental compilation. Missing data uses the supported SDK path or explicit structured degradation.
7. Invalidate on edit/create/delete/rename/import, external changes, environment switch, restore and Worker recycle; full reconcile after missed events. Prove no old in-flight extraction can publish over a newer generation.

### Acceptance

- Native KB -> official SDK export -> admitted generation -> gxwire request -> correct visible answer is demonstrated through the Gateway, not only by Rust unit tests.
- Cold extraction is measured; after ready, covered warm graph queries make **zero SDK calls** (verified by instrumentation).
- Same-name objects in different modules/types/KBs remain distinct. Changes, deletion, failed/cancelled export, external modification and restart never produce a mixed ready generation.
- Corpus parity is complete for the declared supported parts. Text compares exact content and explicitly defined encoding/newline policy; structured parts compare a documented canonical representation. Preserve original bytes when claiming byte parity. Wrapper formatting is not an SDK semantic oracle.
- Re-run A before/after under the same data and machine conditions. Ship a performance claim only for measured improvements without correctness loss.

## Work package C — Parser and graph fidelity before more semantics

1. Reuse `src/gxwire/src/parser/lexer.rs` and existing `call_forms_tests.rs`, `formula_reference_tests.rs`, `ingest_edge_cases.rs` rather than replacing everything at once.
2. Define a grammar-supported subset: tokens excluding strings/comments; qualified identifiers; nested expressions; source/rules/events; DataSelector grouping and source spans. Preserve unknown constructs and report diagnostics instead of silently guessing.
3. Resolve references by stable identity and module scope. Keep SDK reference evidence separate from heuristic source edges. Record unresolved dynamic calls, coverage and provenance; “no callers found” is not “safe to delete.”
4. Differential-test source graphs against official SDK/reference outputs on authored fixtures. Include dynamic calls, events, overloaded names, SDTs, formulas, subtypes and incomplete exports. Treat source dataflow/impact as conservative analyses, not compiler proofs.
5. Only then extend pure-Rust filtering/analysis to additional proven snapshot fields. Live or unsupported properties remain SDK-owned.

Done means the supported subset and uncertainties are machine-tested and visible. A larger but less truthful graph is a regression.

## Work package D — Group-preserving DataSelector editing

The current refusal must remain for the existing ambiguous flat replacement operation.

1. Read `DataSelectorReadService.cs`, `DataSelectorConditionsEditor.cs`, `WriteService.DataSelectorConditions.cs` and their focused tests. Capture an SDK-authored nested AND/OR example in a disposable KB.
2. Define a structured hierarchical request (group operator, ordered children and expression leaves), preferably an explicit opt-in operation/format. Preserve existing flat clients. Define maximum depth/count, validation and stale-version preconditions.
3. Test public `DataSelectorLevel` / child APIs first. Use REA-selected member evidence only for a demonstrated gap; private reflection is a narrow compatibility adapter, not a blanket bypass.
4. Implement dry-run and transactional stage/readback/rollback. Preserve parameters, orders, defined-by, unrelated parts and child ordering. Do not claim projected columns or resolved joins merely because a private collection exists.
5. Save, close/reopen, read again and compare group semantics; compile/specify only as an explicit separate validation action. Inject failure and prove no partially persisted condition tree.

Acceptance: old flat requests still refuse destructive nested replacement; new structured edits preserve the hierarchy after reopen on tested majors. A unit test that merely observes a private field is insufficient. Add a mutation check showing the regression test fails when group preservation is removed.

## Work package E — Improve the existing in-process build path

1. Characterize `InProcessBuildOutcome` (`Succeeded`, `FailedWithDiagnostics`, `CouldNotRun`) in `InProcessBuildRunnerTests.cs`. Keep diagnostic failure distinct from inability to start; do not run an external duplicate build after a real compilation failure.
2. Measure actual fallback causes, assembly/service initialization, queue wait and per-phase time. Select one high-frequency cause before probing Packages DLLs.
3. Use REA to trace that specific service/method and its native boundaries. Static call edges are leads, not evidence of thread safety, license independence, cancellation support or reentrancy.
4. Adapt the existing runner only after validating lifecycle/affinity and behavior. Preserve compile-only freshness checks, dirty-set rules, target/caller closure, generation and diagnostic propagation. Keep an explicit external-process path for operations without safe in-process support.
5. Test successful compile, invalid source, missing service, stale generated code, cancellation before/after start, Worker crash and repeated jobs. Never abort a thread inside the SDK; report non-interruptible work honestly.

Acceptance: the same requested operation and artifacts/diagnostics match the reference path, actual subprocess counts fall for the chosen scenario, and A's measurements show the claimed benefit. No success based solely on “service call returned.”

## Work package F — Compatibility by capability

- Keep `config/gx-versions.json` as the supported-major catalog. Research diffs belong in a diagnostic evidence manifest, not automatically generated support claims.
- Match members by declaring type/signature and behavior probes, not by token across builds. Tokens are local to assembly SHA/MVID; normalized IL digest does not prove semantic equality or inequality across builds.
- Probe required API capabilities and choose reviewed adapters. Unknown patch within a supported major must not fail merely because a hash changed.
- Run `scripts/test-live-matrix.ps1` against installed GX16, GX17Trial and GX18 with explicit path mapping and the documented harness. Missing installation/fixture/license is `unavailable`, not `passed`.
- Private APIs require focused persistence/build smoke tests per affected installed major before enabling that adapter. No automatic speculative edits to the version catalog.

## Work package G — Direct UDM SQL feasibility, not default architecture

Only pursue if A/B demonstrate a remaining material extraction bottleneck and the maintainer authorizes this research scope.

1. Use an isolated restored test database. Do not infer connection strings from arbitrary files or log secrets. Do not attach raw MDF files or change live database isolation/security settings.
2. Give the test identity minimal SELECT permissions. Verify INSERT/UPDATE/DELETE/DDL/EXEC denial independently of application options. Use parameterized fixed read queries, TLS/authentication, timeouts, cancellation and bounded result sizes.
3. Discover actual schema, model/version selection, references, transaction boundaries and part encoding. Distinguish stored/derived/lazy-loaded data. Record hypotheses and evidence separately.
4. Derive an independently written format specification from approved observations. A local reconstruction import is not a redistributable implementation. Unknown compression/serialization/type must fail closed; never use unsafe object deserialization or execute blobs.
5. Start with **one known textual part and one supported GX18 installation**. Differential-test against SDK across multiple objects, commits, rollback, concurrency and reopen. Include Unicode, empty/large parts, corruption and unknown format.
6. Evaluate whether pure Rust SQL authentication supports the real LocalDB deployment and credentials without changing machine configuration. Select `tiberius`/async runtime/compression crates only after requirements and formats are proven, not in advance.
7. Produce a go/no-go report with schema/format coverage, consistency guarantees, extraction timings, operational risk and maintenance cost. If no coherent snapshot strategy is possible, reject production use.

A successful spike does not replace SDK reads wholesale, enable SQL writes, or prove GX16/17 compatibility. If eventually shipped, the reader is explicit opt-in, independently disableable, provenance-marked and SDK-fallback-capable.

## Work package H — Later optional research

- **XPZ:** first inspect SDK-created test packages and their version/dependency semantics. Native archive inspection may be useful; retain official export/import until generated packages survive SDK import/reopen and preserve every supported part/reference. Guard against archive traversal, zip bombs and XML external entities.
- **Schema/navigation estimates:** start from SDK-exported table metadata before attempting relational inference. Test against Specify/reorg outputs across generators, nullable keys, subtypes, formulas, multilevel transactions and environment differences. Call results estimated; unsupported cases remain unknown.
- **WorkWithPlus:** only with available licensed package versions and fixtures. Preserve unknown XML, namespaces and ordering. Do not infer portability from a single `childrenOrderedList` observation.
- No claim of a Rust GeneXus compiler, complete 3NF engine, authoritative SQL planner or safe-delete proof is included.

## Verification commands and evidence policy

Run from repository root. These are execution gates for future changes, **not tests run during this planning task**. Use the installed SDK mapping and follow AGENTS.md's complete lane list for an implementation/release; focused tests alone do not qualify a release.

```powershell
# Baseline/context and Rust
 git status --short
 cargo test --manifest-path src/gxwire/Cargo.toml
 cargo build --release --manifest-path src/gxwire/Cargo.toml

# Worker: select one SDK in this shell; repeat per affected installed major
 $env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
 dotnet build src/GxMcp.Worker/GxMcp.Worker.csproj
 dotnet test src/GxMcp.Worker.Tests --filter 'FullyQualifiedName~TextMirrorServiceTests|FullyQualifiedName~DataSelectorConditionsEditorTests|FullyQualifiedName~DataSelectorReadServiceTests|FullyQualifiedName~InProcessBuildRunnerTests'
 dotnet test src/GxMcp.Gateway.Tests

# Intentional published-contract changes
 python scripts/validate-tool-contracts.py
 python scripts/generate-operation-contract-inventory.py --check
 git diff --check
```

Live matrix parameters and fixture provisioning must be taken from `docs/live-kb-test-harness.md` and the current script, not guessed. Do not run build/release/install entry points during research merely to see what happens. For builds, follow the repository's required final `build.ps1` ordering.

For each work package record: requirement -> action/request -> execution -> response/state -> visible result -> persistence/reopen if relevant. Label each criterion `verified`, `not verified` or `blocked`, with commands, versions, fixture identity, timings and redacted logs. New nontrivial logic needs focused tests; regression guards need the repository-required mutation check.

Before each implementation: read current code/consumers and `ripwire --for` / `--exemplar`. After changes run `--edit-check`, `--quality-delta` and `--test-gate`; report tool failure as inconclusive, not clean. Update schemas/router/dispatcher/help/goldens and capabilities inventories together for deliberate contract changes; add CHANGELOG entries only for implemented verified behavior.

## Safety and stop conditions

- SDK is the only write authority. Rust never writes SQL, KB internals, reference tables or caches owned by GeneXus.
- Never pass live SDK objects to Rust/MTA; only immutable extracted values. No new Worker per analysis request.
- Preserve KB/session authorization, cancellation, progress, cache isolation and existing major compatibility.
- Proprietary DLLs, CIL, decompiled C#, actual KB exports, SQL dumps, connection strings and customer data remain outside Git. Commit only approved authored specifications/code and synthetic fixtures.
- Stop the affected path on unknown format, stale/mixed generation, ambiguous identity, missing authorization, uncertain write outcome, licensing constraints or unsupported runtime behavior. Other independent work can continue.
- Commit/push/merge/release remain separate user authorizations. This plan does not authorize installing/registering REA or ILSpy.

## Acceptance ledger for this planning task

- **Verified:** REA cloned locally; commit and package version recorded; seven managed tools and reconstruction boundary checked in repository docs/code.
- **Verified:** current Genexus18MCP architecture inspected at the stamped commit; pre-existing snapshot, in-process build and DataSelector guard accounted for.
- **Verified:** this roadmap records scope, priorities, dependencies, test commands, stop conditions and deferred research.
- **Not verified:** REA execution/Windows backend qualification, Artech DLL findings, SQL/UDM formats, actual performance/memory improvements, SDK/KB parity and all proposed capabilities.
- **Not performed:** source edits, tool installations, builds/tests, KB reads/writes, SQL queries, MCP registration or publication.
