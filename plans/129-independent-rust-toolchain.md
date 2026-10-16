# 129 — Independent Rust SDK and incremental GeneXus toolchain

## Mandate, status and corrected ambition

**Implementation authorized by the user; independent engine development started.** Original design baseline: `b9de0a5ae4cdf0e3932b486b2f01702a60e7e3cd`. See [implementation ledger](129-implementation-progress.md) for current scope and executed verification. The static findings below are historical investigation evidence, not proof that the entire toolchain is implemented.

The user explicitly rejected a read-cache-only ambition: investigate recreating the SDK in Rust, including the build system. This document supersedes plan 128's *architectural ceiling and SDK-first research ordering*, not its integrity safeguards or already integrated work. The static findings in `128-sdk-rust-investigation.md` remain evidence.

**Target: an independently implemented Rust knowledge-base engine and compiler toolchain, capable of loading a characterized KB, analyzing and editing its portable model, specifying a supported language subset, generating executable applications, and building them without the GeneXus SDK or MSBuild on admitted paths.** **Complete SDK/toolchain replacement is the user-confirmed objective, not an optional follow-up or a numeric-subset ceiling.** Full GeneXus feature compatibility is the implementation target, not a claim about the current prototype or a promised first release. No feature is declared permanently SDK-only merely because the current implementation delegates to it.

The .NET adapter is a migration/compatibility provider and behavioral oracle, not the mandatory center of the final architecture. Replace capabilities, not proprietary class names. A Rust implementation cannot be dropped into CLR DLL call sites as if it were the original managed assembly; binary consumers need an adapter or intentional API migration.

## New inspection: build and generation boundaries

Static inspection used existing ILSpy 11.0.0.9375 against the locally installed GeneXus 18. Target methods were not executed. Proprietary implementation is not reproduced here.

| Evidence | Observed boundary | Consequence |
|---|---|---|
| Installed `Genexus.Tasks.targets` | 104 active `UsingTask` declarations; inspected registrations specify x86 | The task surface extends well beyond compile: import/export, versions, properties, modules, reorganization, deployment and daemons. Count is from parsed XML, excluding comments. |
| `Genexus.MsBuild.Tasks.BuildOne.Execute` | Delegates a working set and entity keys to `GenexusBLServices.Build.Build` | Recreating task XML or subprocess scheduling does not recreate specification/generation. |
| `Genexus.MsBuild.Tasks.Compile.Execute` | Delegates to `GenexusBLServices.Run.Compile` | MSBuild is not itself the GeneXus semantic compiler, nor the C#/Java compiler. |
| `Genexus.MsBuild.Tasks.GenerateOnly.Execute` | Delegates to `GenexusBLServices.Generators.Generate` | Generation is independently identifiable as a capability. |
| `Artech.Genexus.Common.Commands.BuildOptions` | Distinct Specify, Generate, Compile, Execute and impact/reorganization options; BuildProcess combines Specify/Generate/Compile | Preserve phase semantics and separate artifact readiness from a Boolean build result. |
| `IBuildServiceBL`, `ISpecifierService`, `IGeneratorsService`, `IRunService` in `Artech.Genexus.Common.dll` | Expose phase-level methods, cancellation and build/model context | These provide a useful replacement matrix, not proof that service injection or native FFI is supported. |
| `Artech.Generator.BusinessExpert` in `Artech.Generator.DotNetCore.dll` | Managed interface wraps native `CGxGenerator`, process current-directory changes and native initialization | This generator is already partly C++; changing the implementation language alone establishes no speed advantage. Global state cautions against concurrent in-process use. |
| `IGenerator` in `Artech.Genexus.Common.dll` | Initialize, Generate, StartDaemon, Cancel, Finalize and ClosingKB lifecycle | A transitional generator adapter is a candidate seam; registration, inputs and output contracts remain untested. |
| `Artech.Generators.GeneratorEngine` in `Artech.Generators.dll` | Strategies, metadata, before/after custom generators and cancellation | Not all generation is one native black box; metadata/template stages form a separate replacement surface. It is not established that all core generators use this engine. |
| `SpecifierDaemon`, `GeneratorDaemon` tasks | Segment/mutex/process-identity parameters and service delegation | Persistent compilation is not a novel absent feature. Measure what existing daemons reuse before claiming a Rust daemon alone is the innovation. |
| Repo `InProcessBuildRunner.cs:25–63`, `:129–222`; `BuildService.cs:3810–3881` | Existing hot-KB, direct-task and compile-only paths; external XML remains a fallback | Compare against the best existing path, not only a cold external-MSBuild baseline. Existing source comments contain historical timing estimates, not fresh measurements. |

Additional inspected binary identities (SHA-256):

- `Genexus.MsBuild.Tasks.dll`: `75AA64E35A062358CB7E97CEB777419E93143EA1AF321FCFC0C64DD21E1B4F09`
- `Artech.Generator.DotNetCore.dll`: `0400703643AEBC985051C8BD0444D79AAC46192D2321E84A8974D74EBCAF8931`
- `Artech.Generators.dll`: `0824256ADE59657053FA5E8025D7FE6DA6D3E771D10E6C03788AD484AF6123F6`

These identify evidence, not compatibility requirements. REA was not run; it does not recover the native generator bodies through managed inspection.

## Architecture: own the semantic model, not just the cache

The names below are conceptual responsibilities, not a request to create eight crates or a new process for each box.

```text
Native KB read provider / portable authored project / live SDK overlay
                              |
                  typed versioned KB model
        identities, modules, properties, parts, model inheritance
                              |
                persistent incremental query engine
            syntax -> binding -> types -> rules -> data model
                              |
                semantic intermediate representation
       control flow + data access + effects + dependency provenance
                         /             \
          navigation / SQL plans      diagnostics / refactors
                         |
                  backend lowering
           C# compatibility | native executable | later targets
                         |
                  Rust build scheduler
       immutable artifacts / exact inputs / compile / package / run

Existing SDK host: explicit provider for not-yet-admitted capabilities.
```

The performance bet is **less repeated work and less mutable global state**, not Rust syntax: decode once, resolve once, incrementally recompute affected queries, share immutable state, generate only changed outputs and avoid copying unchanged deployment trees. Rust provides ownership and efficient compact data structures; architecture determines whether those properties matter end to end.

## Replacement matrix: the whole platform surface

| Workstream | Native responsibility | First meaningful capability | What full compatibility adds | Effort / risk |
|---|---|---|---|---|
| 1. KB/storage SDK | Model/version catalog, typed properties, parts, composition, references, portable persistence | Coherent native-KB read -> portable model -> graph answers without KBObject hydration | Native format/version compatibility, live overlays, history, merges, extensions and eventually an authorized save provider | L / High |
| 2. Language front end | Lossless syntax, token decoding, name/module resolution, types/domains, diagnostics | Procedures and SDTs with identity-aware references and deterministic diagnostics | Rules, events, dynamic calls, object families, dialect/version behavior and all expression semantics | XL / High |
| 3. Data-model engine | Attributes/domains, transaction levels, keys, relationships, extended tables, subtype groups | Explain base-table and join choices for a bounded authored fixture set | Normalization, inferred tables, formula semantics, referential actions, DBMS behavior and historical schema evolution | XL / Very high |
| 4. Specifier | Bound program -> executable navigation/data-access IR | A constrained For each with filters/order and explicit diagnostics for unsupported cases | Nested navigation, implicit joins, aggregate/formula placement, updates, locking, optimization and generator-specific semantics | XL / Very high |
| 5. Generators | Lower IR into supported targets; source maps and deterministic outputs | Pure Procedure/SDT -> runnable C# or native executable | Web UI/events, BCs, APIs, offline/mobile, themes/resources, extensions and complete target/runtime contracts | XL / Very high |
| 6. Build system | Inputs/actions graph, scheduling, artifacts, package manifests, cancel/recovery | Execute an explicit GeneXus build graph without external MSBuild on one admitted path | Custom tasks/hooks, generated project imports, resource/config transforms, signing, target toolchains and deployment variants | L initially; XL broad / High |
| 7. Runtime | Decimal/string/date/null behavior, collections, calls/errors, I/O, data access and transactions | Small explicit runtime for admitted pure programs | GeneXus standard library, providers, serialization, security/session/GAM integration and runtime ABI compatibility | XL / Very high |
| 8. Authoring SDK | Typed edits, validation, deterministic serialization, revisions, conflict detection, import/export | Edit and round-trip our portable project format without an SDK installation | Native persistence fidelity, undo/redo, extension events, XPZ interchange and concurrent IDE/session semantics | L initially; XL broad / Very high |

The full target includes write semantics, but **this investigation does not authorize direct SQL writes or native-KB mutation**. First own persistence in a portable format; native saves stay behind the existing SDK adapter until separately characterized and authorized. This is a migration boundary, not a permanent surrender of SDK independence.

## Replacing MSBuild: three distinct claims

### A. Remove GeneXus's MSBuild envelope

Rust owns an explicit action graph and submits coarse-grained compatibility commands to a persistent, owning-STA SDK host while unsupported phases still exist. No reflection-per-token or SDK object graph crosses FFI/IPC. The host returns typed diagnostics, artifact manifests and phase outcomes. Existing in-process execution is the baseline; this is not advertised as a newly discovered bypass.

This can remove the *outer MSBuild.exe invocation*. It cannot honestly be called MSBuild-free if an invoked SDK/compiler task internally starts MSBuild. Audit the entire process tree and file activity.

### B. Own the generated application's build

For an admitted generator/toolchain profile, enumerate all sources, references, resources, compiler switches and postprocessing explicitly. Rust schedules and caches those actions. Existing C#/Java compilers may still be invoked; a build driver is not a replacement for Roslyn, javac, NuGet or the platform SDK.

A C# backend can begin with normal `dotnet build` for semantic testing, but that is **not** an MSBuild-free milestone. A later direct-compiler profile must explicitly reproduce generated resources, references, analyzers/source generators, configuration and packaging inputs for that supported profile. Do not silently treat a general `.csproj` as a simple source list.

### C. Fully independent compile path

Rust front end + specifier + generator + runtime + build driver produce an executable from the portable model with no GeneXus or MSBuild processes. A native backend can initially generate a small Rust project and use Cargo/rustc, subject to deterministic inputs and an explicit runtime. It need not start with a custom LLVM integration or an in-house machine-code compiler.

This is an actual SDK/compiler replacement **for the admitted language subset**. It is not full GeneXus compatibility. A general drop-in MSBuild evaluator with arbitrary XML imports, properties/items, conditions, batching, targets and .NET task hosting is a different product; build only the required compatibility surface unless evaluation proves a broad clone is justified.

## The central invention: semantic incremental compilation

Use one revision-consistent model and memoized queries such as:

```text
object_part(identity, revision, part_kind)
bind(module_scope, syntax, visibility)
resolve_type(symbol, model_options)
infer_navigation(bound_program, transaction_model, dialect)
lower(semantic_ir, backend_profile)
compile(artifact_manifest, toolchain_identity)
```

Track dependencies on actual semantic inputs, including failed lookups. A newly created object can invalidate an earlier unresolved name; an unrelated edit should not recompile every Procedure. Source references alone are not a correct build graph: callers also depend on signatures, domains, inferred table structure, model properties, generated runtime versions and target options.

Candidate fingerprints separate public interface, implementation, storage/navigation and deployment resources. They are optimizations only after dependency tracking proves them safe. Handle cycles as strongly connected components and define fixed-point/error behavior. Never parallelize SDK calls under the assumption that Rust's scheduler makes native global state safe.

Every action needs a complete input digest, toolchain/generator identity, declared outputs and atomic publication. A cache hit must validate the artifact manifest. Cancellation/crash cannot publish partial output; actions with side effects are not cacheable pure queries. Local cache first; remote/shared cache only after reproducibility and isolation are proven.

The interface should explain: **why rebuilt, why not rebuilt, which semantic input changed, and which capability forced an SDK fallback**. This makes acceleration auditable rather than a stale-cache gamble.

## Three demonstration milestones that earn the ambition

### M1 — Independent executable, not another index

A checked-in, independently authored portable fixture contains a module, SDT, two Procedures, typed parameters, arithmetic, conditionals, loops and calls. Specify the exact supported grammar and standard-library operations before implementation.

- Parse -> bind -> type-check -> semantic IR -> backend -> compile -> execute.
- Match SDK-generated behavior on the same authored inputs: outputs, numeric rounding/overflow, string comparison, null/empty and error cases relevant to the admitted types.
- Produce the executable from a portable fixture in an environment without the GeneXus SDK. Record full process tree and toolchain versions; no MSBuild for the native backend.
- Change one implementation and one public signature separately; demonstrate correct incremental invalidation and equivalent clean-build results.
- Benchmark cold and warm compile plus actual execution. Native runtime speed is a separate metric from build speed.

This is the first compelling proof of compiler independence. Parser/graph work and baseline instrumentation can run alongside it; they must not become an indefinite prerequisite queue.

### M2 — Recreate a piece of the specifier

An authored transaction/attribute fixture and constrained read-only For each compile to an inspectable navigation/SQL plan. Initially compare results against in-memory fixture relations and an SDK reference; any live SQL reads need an explicit test connection and restricted principal.

- Include base table, extension join, filters, ordering, domain/subtype cases and an intentionally unsupported construct.
- Match observable rows, ordering where defined, types, null/empty behavior and error behavior. SQL text equality alone is insufficient; equivalent SQL may be formatted differently, and identical SQL can still use different parameters/session settings.
- Explain and test navigation decisions. Diagnose unsupported semantics; never silently approximate them.
- No generated DDL execution, reorganization or SQL writes.

### M3 — Build a bounded real application through both engines

Use an owned, disposable KB and a fixed generator profile. Compare existing hot-SDK execution, Rust scheduling with compatibility phases, and independent native phases where admitted.

- Record phase timings, critical path, SDK/SQL call counts, bytes, file writes/copies, peak total memory and spawned processes.
- Exercise no change, Procedure implementation change, signature change, domain change, object rename/move/delete and target option change.
- Check full application behavior, not just exit code: reachable endpoint/CLI, output, resources and restart from a clean environment.
- Inject cancellation and worker crash; no mixed-generation artifacts, stale success or orphan process.
- Report supported capability coverage and explicit fallbacks. Do not publish one global compatibility percentage without a defined denominator.

No speedup number is accepted before these comparisons. Amdahl's law applies: removing scheduling overhead cannot remove time spent in an unchanged native specifier, disk, database or target compiler.

## Compatibility is a matrix, not a Boolean

Track each capability by GX major/update, KB format, object family, language construct, property set, backend, DBMS, runtime feature and operation (read/analyze/edit/specify/generate/build/run).

Four states suffice initially: native-verified, compatibility-provider-verified, unsupported, uncharacterized. A result carries its engine, revision, coverage and provenance. Unsupported paths may explicitly delegate; an SDK-free command must refuse any fallback and expose the missing capability. A full-rebuild/reference comparison periodically detects incremental invalidation defects.

Correctness fixtures must include comments/Unicode, token identity after renames, module homonyms, deletion, inherited properties, model changes, domain/subtype behavior and malformed input. Protect XML decoders with size/depth/entity limits. Preserve credentials outside fixtures, logs and cache keys.

## What REA contributes, and what it does not

Use REA/ILSpy for API and assembly dependency inventory, managed control-flow evidence, native-boundary identification and update-to-update capability drift. Inspect native entry-point/export/call evidence with appropriate native tools only in a separately scoped pass. Managed signatures are not recovered C++ algorithms.

The independent implementation is derived from an owned behavioral corpus and reviewed specifications; decompiled code is not a Rust code-generation input. Keep proprietary bodies/binaries outside Git. Interoperability/licensing review is required before distributing a replacement; no DRM, license checks or protected initialization is bypassed. Static decompilation alone does not make a project clean-room.

## SDK improvements to propose upstream

These are requested contracts, not existing capabilities:

1. Coherent bulk snapshot API for catalog, parts, typed references and change/deletion cursors; stable identities and documented revision boundaries.
2. Versioned semantic/token interchange with type/module identities and source maps; avoid forcing reconstruction and reparsing of text.
3. Explicit specifier input/output contract: typed diagnostics, navigation IR, dependencies and generated-artifact manifests, independent of IDE global state.
4. Phase-pure build APIs: Specify, Generate, Compile, Package and Deploy with exact declared inputs and no undocumented phase escalation.
5. Supported headless persistent compiler host, cancellation, per-phase telemetry and documented concurrency/isolation; existing daemons are a starting point, not proof this contract is already available.
6. Documented generator/runtime extension ABI and deterministic fixtures, enabling a native generator without reverse-engineering the entire IDE.
7. Capability negotiation by SDK version and target; public conformance corpus for model semantics and interchange.

These contracts help both a faster official SDK and a progressively independent Rust implementation. The project need not wait for upstream acceptance to build its portable vertical slice.

## Next investigative work, with bounded deliverables

1. Enumerate public service/part/property contracts and generator/runtime dependencies into a capability inventory; inspect native PE boundaries separately. Cover object families instead of merely adding more assembly counts.
2. Define M1's exact source semantics and authored oracle fixtures. Decide the initial backend based on demonstrable dependency footprint; the native path must be part of the comparison, not omitted as 'future work'.
3. Characterize one real source-part framing/token schema and module identity round-trip, in parallel with M1. Extend the catalog/graph experiment from report 128 rather than blocking the compiler on complete KB decoding.
4. Trace one complete build with the existing harness: queue -> specifier -> generator -> compiler -> package/deploy -> runnable result. The installed daemon protocols and generated project/runtime dependencies remain uncharacterized.
5. Use measured work/compatibility coverage to select the first production replacement and revise cost. No reliable whole-SDK schedule can be inferred from static metadata.

## Verification ledger for this document

- **Verified statically:** task count, selected service/task boundaries, native generator wrapper, managed generator strategy surface, repository in-process/external paths and listed assembly hashes.
- **Not verified:** runtime hooks, daemon protocol compatibility, all task implementations, native body algorithms, complete generated project/runtime dependencies, live-KB schema/parts parity, independent compilation, native persistence, feature completeness and performance.
- **At investigation time:** no MCP/SDK/Rust implementation, KB access, SQL execution, target process execution, install, commit, push or release occurred. Subsequent authorized Rust implementation and verification are tracked separately in the implementation ledger; native-KB and SQL-write restrictions remain in force.
