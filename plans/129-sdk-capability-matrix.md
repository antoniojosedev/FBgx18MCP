# 129 - SDK capability matrix: full replacement versus gx-engine

Baseline snapshot: 2026-10-15, repository HEAD `b9de0a5a` with uncommitted `src/gx-engine`,
`plans/` and scratchpad work. This operation-level matrix has since been reconciled
against the current nested/collection compiler source and the newer native evidence
in the implementation ledger; static counts and verification notes below remain
historical where labeled. Companion to [plan 129](129-independent-rust-toolchain.md):
it uses the same four states (native-verified, compatibility-provider-verified,
unsupported, uncharacterized) plus the `none` and `portable subset` states needed for
`src/gx-engine`. Test-side companion: [`129-overhaul-coverage-report.md`](129-overhaul-coverage-report.md)
maps the repository's SDK-dependent test corpus onto these rows.

Scope: this document only. It opened no KB, ran no build, test, SDK probe or MCP call,
and changed no code, KB or database. Every status below comes from a file read, a
literal or ripwire search, or a recorded evidence file named in the row. Short paths such
as `tests/x.rs` and `fixtures/x.json` are under `src/gx-engine/`.

## 1. Vocabulary

| State | Meaning |
| --- | --- |
| `none` | No gx-engine implementation, and no route from any MCP tool, Worker service, CLI or build script. |
| `portable subset` | Implemented in gx-engine for its own admitted input format. It carries no claim of GeneXus parity for that row. |
| `native-verified subset` | Admitted behaviour was compared with output from the official GeneXus SDK or its generated/compiled native output, in a disposable KB. The claim is limited to the named observations. |
| `uncharacterized` | GeneXus behaviour has not been measured. Any engine behaviour there is an assumption. |
| `divergence` | GeneXus and gx-engine differ on purpose, or the difference is documented as a safety choice. |

"Native" in this document means GeneXus-native behaviour. Rust executables that tests
call "native" (for example `tests/native_pipeline.rs`) are gx-engine outputs, not
GeneXus evidence.

Priority:

| Priority | Meaning |
| --- | --- |
| P0 | Blocks reading or running real KB content through an independent path. Nothing else is useful without it. |
| P1 | Needed to replace a core area. Required before any parity claim for that area. |
| P2 | Broad coverage. Follows the core. |
| P3 | Operational, UI or peripheral. Deferred unless the user reprioritizes. |

## 2. Headline

- gx-engine is a standalone experimental compiler for its own JSON project format.
  Nothing calls it: a case-insensitive search for `gx-engine` / `gx_engine` in
  `src/GxMcp.Gateway`, `src/GxMcp.Worker` and `cli` returned nothing. It is not in
  `Genexus18MCP.sln`, `build.ps1` or `.github`.
- Its admitted compile path is still Procedure-only, but is no longer a five-statement,
  scalar-only slice. Format 2 includes typed Boolean/Numeric/Character/Varchar/Date/
  DateTime values; bounded Record/Collection schemas, nested fields, member access and
  assignment, alias/Clone behavior, collection Count/Add/Item; typed calls and the
  bounded control-flow/message subset described in the README. These are portable
  compiler capabilities, not blanket GeneXus parity. The portable catalog can feed
  explicit SDT schemas to it. The SDK ExportFile XML projection now maps recursively
  nested SDT hierarchies (per-level synthetic schemas with direct-child `Uses` edges,
  nested collections) and resolves the evidence-gated `RowsItem` and `Root.Child.Item`
  aliases; deeper collections and non-root composite aliases stay fail-closed. A
  captured three-level native export runs through `xml-run` with a companion Procedure
  with exact output (see B3). It does not decode
  native KB storage.
  `structured.rs`'s broader owned-value API is not evidence for SDK behavior.
- Native-verified islands are limited to the exact recorded observations. Existing
  reports cover numeric/logical, typed scalar values/calls, text, Date/DateTime and
  scalar SDTs. Later GeneXus 18 probes also measured nested-record Clone and collection
  Clone semantics in `scratchpad/record-nested-native-1792078896703774100/` and
  `scratchpad/record-collection-native-1792079116541738600/`. Native measurement does
  not by itself establish full compiler parity. The native nested-record ExportFile has
  passed Rust check/emit/build/run against native stdout after CRLF-only normalization,
  and the captured three-level deep-SDT export runs through `xml-run` with a companion
  Procedure; broader nested/collection parity and the dynamic-index differential remain
  pending.
- Major SDK areas remain `none` or outside the compiler path: native KB storage/read,
  Attribute, Domain, Transaction, Table, WebPanel, DataProvider, Specify/navigation,
  GeneXus generators, KB-level build, database access, UI and deployment. The bounded
  SDK XML compilation projection is not native KB decoding or full import/export.
- Scale of the target: `docs/sdk-probe/INDEX.md` (generated 2026-07-20) lists 109
  assemblies with 14,745 public types of 29,239 total. The MCP surface is 60 tools in
  `src/GxMcp.Gateway/tool_definitions.json`. The Worker has 435 `.cs` files, 266 of them
  under `Services/`.

## 3. Capability matrix

Columns: ID; SDK operation or area with Worker evidence; gx-engine status with evidence;
native evidence still needed; priority.

### A. KB storage, open and model

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| A1 | Open, close and select a KB; environments; startup configuration. `KnowledgeBase.OpenOptions` and `KnowledgeBase.Open(options)` under `SdkGate` (`src/GxMcp.Worker/Services/KbService.cs:467-468`). Tools: `genexus_kb`, `genexus_doctor`, `genexus_whoami`. | none. The engine has no KB handle; its project is a JSON file. | None for the engine itself. Depends on the read-route decision (decision 4). | P2 |
| A2 | Read native KB objects and parts (Source, Variables, Rules, Events, Structure, WebForm) without hydrating KBObjects. Worker: `KBObjectDescriptor` (33 occurrences in the Worker), `ObjectReader`, `ObjectInspectionModule`. Tools: `genexus_read`, `genexus_inspect`, `genexus_list_objects`, `genexus_query`. | none. The catalog is our format and is explicitly "not a decoder or writer for native GeneXus KB data" (`README.md`, "Independent portable KB catalog"). | Read-only SDK export of a complete disposable KB (synthetic objects in KBTeste) diffed against a portable import: object inventory, identities, module and name, and every part. Route decision: decision 4. | P0 |
| A3 | Portable catalog: stable identity, typed references, revisioned edits, atomic save and load. | portable subset: `src/gx-engine/src/catalog.rs` (ObjectKind: Attribute, DataProvider, Domain, Module, Procedure, StructuredDataType, Transaction, WebPanel); `tests/catalog.rs` (8 tests). Latest owner-reported `kb_pipeline` increment passed 12/12: transitive `Probe`→`Child`→`Grandchild` plus `Row` emitted `7\n8\n9`, leaving the catalog unchanged; a missing `Child`→`Grandchild` `Uses` edge rejects. Direct `Uses` for typed Record returns and 1,024/1,025 Procedure boundaries are covered. Mutations of transitive-Uses, return-Uses and procedure-threshold guards each went red; restored checks passed. Explicit `Uses` closure is bounded to 1,024 Procedures, 128 schemas and 1,152 reachable objects. `portable_catalog_requires_direct_calls_and_preserves_recursive_edges` rejects a missing direct `Calls` edge even when its target is transitively reachable, while preserving valid recursive graphs; it is included in the reported 12/12 `kb_pipeline` run. Cross-process CAS and durability are not claimed (README). | Native identity mapping (GUID, module, qualified name). README: naming is "a conservative globally unique namespace, not full native module/type naming compatibility". | P1 |
| A4 | Modules, qualified names, homonyms and module-scoped resolution (`Module`, `QualifiedName`, `module` arguments). | none. | Native resolution probes for homonyms across modules. | P1 |
| A5 | Concurrency and locking: `MultiAgentLockService`, `GxServerSyncService`, KB sessions. | none. Catalog revisions are in-process only. | Only needed once the write path is decided (decision 5). | P2 |

### B. Object kinds

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| B1 | Procedure: Source, Variables, Rules/Parm, Signature. `ObjectService.CreateObjectInstance` (`ObjectService.cs:611`) creates Procedures with a placeholder Source. | Portable subset & Parity: `frontend.rs`, `backend.rs`, `typed_backend.rs`, `incremental.rs`, `rules.rs`; `kb.rs` admits Procedures with Source, Variables, and full Rules/Parm grammar. Native islands are listed under C1, C3, and G1-G10. | Broad source grammar (C2). | P0 |
| B2 | Transaction: keys, levels, attributes, relations. `InitializeTransactionWithDefaultKey` (`ObjectService.cs:1491`), `TransactionDslParser`, `TransactionRecordsService`. Tool: `genexus_structure` (13 actions). | Portable subset: `TransactionStructure` with root/nested `TransactionLevel`, `TransactionAttributeRef`, primary key enforcement, unique level/attribute validation, bounded recursion (depth 16), catalog projection to Business Component `RecordType` schemas, and `ExportFile` XML `<TransactionStructure>` parsing (`src/gx-engine/tests/transaction_structure.rs`, 10/10 tests). Native parity for structural projection; DDL and SQL persistence remain separate (H1/D3). | Full DDL generation (H1) and database reorganization/SQL writes (D3). | P1 |
| B3 | Structured Data Type (SDT): items, levels, collections, defaults. `StructureService`, `SDTService`, `SdtDslParser`, `genexus_structure`. | Portable subset: format-2 compiler/model admit nested Record/Collection schemas, member reads/writes, aliasing, shallow Clone, Count/Add/Item, with fixtures in `typed_pipeline.rs` and runtime tests in `record_runtime.rs`; catalog `Uses` feeds explicit schemas. Latest owner-reported `kb_pipeline` increment passed 12/12: transitive `Probe`→`Child`→`Grandchild` plus `Row` emitted `7\n8\n9` without catalog mutation, and missing nested `Uses` rejects. SDK XML projection exchange tests passed 16/16, including recursive deep-Level projection, the natively evidenced depth-2 item alias (`NativeDeepSdt85299100.Child.NestedCollectionItem`), and fail-closed rejection of deeper or non-root composite aliases. Owner-reported nested Record ExportFile `check`→`emit`→`build`→`run` exits 0 and matches native stdout after CRLF-only line-ending normalization, with the XML input unchanged (`scratchpad/nested-record-export-1792080479819133800/authored.xml`). The captured three-level deep-SDT export (`scratchpad/deep-sdt-native-export-1792102090401/xpz-contents/exportfile-sanitized.xml`) runs through `xml-run` with an injected companion Procedure with exact expected output and unchanged input (`scratchpad/run-deep-projection-check.py`). The custom-item export contains `idCollectionItemName=Entry` (`scratchpad/custom-item-export-1792080825587526300/authored.xml`) but no Procedure, so the compiler refuses its missing entry and no runtime-equivalence result exists for that file. | Native scalar oracle: `scratchpad/sdt-fresh/typed-sdt-nemo-rebuild.log`, text record 9 observations and calendar record 12 observations; nested Clone: `scratchpad/record-nested-native-1792078896703774100/` (Clone separates outer root, child mutation remains shared); collection Clone: `scratchpad/record-collection-native-1792079116541738600/` (list membership separates, item records remain aliases). Still needed: broader fields/operations and a native differential for dynamic collection indices; the custom-item XML cannot provide runtime equivalence without a Procedure entry. Record expression results, full authoring and direct native KB decoding remain out of scope. Do not copy direct SQL propagation from Worker. | P0 |
| B4 | WebPanel, forms, layouts, events. `WebFormEditService`, `K2bWebPanelDesignerService`, `LayoutService`. Tools: `genexus_layout`, `genexus_edit_form`, `genexus_k2b_designer`. | none. | Out of core scope; needs UI and runtime contracts. | P3 |
| B5 | DataProvider. The Worker creates a placeholder Source (`ObjectService.cs`, DataProvider branch). | none. Kind is listed in the catalog but not admitted. | Native DataProvider source semantics. | P2 |
| B6 | Domain: types through `basedOn`, enum domains. `InitializeDomain` (`ObjectService.cs:2294`), `DomainPropertyApplier`. | Portable subset & Parity: `ObjectKind::Domain` admitted with `TYPE` source parts, `BasedOn` resolution in portable catalog and `ExportFile` XML, chained domain resolution, cycle detection, depth 16 limit (`src/gx-engine/tests/domain_attribute.rs`, 10/10 tests). | Enum domains with value collections. | P1 |
| B7 | Attribute: typed attributes, `genexus_variable basedOnAttribute`, `AttributeTypeApplier`. | Portable subset & Parity: `ObjectKind::Attribute` admitted with `TYPE` source parts, `BasedOn` resolution to Domains/Attributes in portable catalog and `ExportFile` XML, resolution at Procedure and SDT item boundaries (`src/gx-engine/tests/domain_attribute.rs`, 10/10 tests). Formula attributes in Transactions specified with automatic context correlation and SQL query planning (`src/gx-engine/tests/formula_navigation.rs`, 11/11 tests). | Native DB column mappings. | P1 |
| B8 | Table: physical tables and relations. `TableDslParser`, `TableRelationsService`, `DataModelInspector`. | Portable subset & Parity: `src/gx-engine/src/table.rs` and `tests/table_ddl.rs` (9/9 tests). Normalized relational physical `Table` schema with columns, primary keys, foreign keys, and indexes derived from Transaction levels, inheriting parent keys and resolving attribute data types across catalog dictionaries. | Table readback and dynamic table mutations. | P1 |
| B9 | Other families: DataView (`genexus_data_view`), DataSelector (`DataSelectorReadService`), WorkWithPlus (`genexus_wwp`, `genexus_workwith`, `WwpActionService`), REST API (`genexus_api`), Theme, Business Component, Dashboard, Module, pattern instances (`genexus_apply_pattern`). | none. | Readback and semantics per family. DataView also belongs to H4. | P2 |

### C. Parsing and diagnostics

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- | --- |
| C1 | Parse and check admitted Procedure source (Specify diagnostics, `genexus_analyze`, `genexus_edit`). | Portable subset & Parity: format-1 numeric and format-2 typed compiler; `frontend.rs` now includes field assignment, collection operations, control flow, calls, output, and full `For Each` loops with wheres, orders (ASC/DESC/NONE), defined_by, body, and When None blocks ending in EndFor (`tests/frontend.rs`, 13/13 tests; `tests/navigation_specifier.rs`, 10/10 tests). Typed checks reject mismatches before rustc (`tests/typed_pipeline.rs`). Native islands: typed scalar/call and nested/collection observations listed under G8/B3. | Map native diagnostic categories for the admitted subset (VALIDATION acceptance criterion 1: pair negative cases by category, not by text); fresh integrated tests for new nested/collection paths. | P1 |
| C2 | Full Source grammar used by real KBs: For each, Do Case, subroutines, Call/Parm, Msg, Commit and Rollback, SQL-backed loops, string, math and date functions, events. The Worker only extracts calls (`Services/SourceParser.cs`). | Bounded Do Case, bare void Return, call statements, CLI text Msg(...,status), format-2 Numeric For/EndFor, subroutines (`Sub`/`Do`), database transaction control statements (`Commit` and `Rollback` with optional parens, `tests/transaction_control.rs`, 7/7 tests), and complete `For each` grammar with wheres, orders, defined_by, and when_none (`tests/frontend.rs`, 13/13 tests, `tests/navigation_specifier.rs`, 10/10 tests). For Step is limited to a nonzero integer literal; `for_loop` passed 8/8 and `fixtures/typed-for.json` is a runnable ascending example. Native observations in [`plans/129-native-for-progress.md`](129-native-for-progress.md) match the bounded engine behavior. Data access queries and query plans are specified via `navigation.rs`. | Broader source grammar and full language/runtime parity. No general compiler equivalence is claimed. | P0 |
| C3 | Rules and Parm (in, out, inout), the Variables part, signatures. `GeneXusSignatureParser`; `genexus_read` returns "rules with parm". | Native parity for Procedure and Transaction Rules grammar: `src/gx-engine/src/rules.rs` and `tests/rules_parity.rs` (10/10 tests). Full grammar parser and AST covering `Parm`, `Error`, `Msg`, `Default`, `NoAccept`, `Accept`, `Order`, `Prompt`, `Serial`, `Hidden`. Strips `//` and `/* */` comments cleanly, preserves string literals containing semicolons and escaped quotes, rejects `Unique` with `src0295` and index guidance, rejects duplicate Parm, enforces procedure scope (variables only, no transaction-only rules in procedures), and integrates into KB catalog and `ExportFile` XML. | Native KB direct read. | P0 |
| C4 | Diagnostics model: stable codes, locations, categories, fail-closed behaviour. `DiagnosticEngine`, `ErrorDiagnoser`, `SpecificationDiagnostics`. | `--diagnostics=json` is integrated and emits phase-level operation categories on stderr while preserving stdout and the default human-readable errors. Build intentionally groups codegen and rustc as one phase; source locations are only exposed by the direct parser diagnostic API. Focused `cli_diagnostics` and `diagnostics` tests passed (3+5); real diagnostic-phase end-to-end tests are now being added. This is not SDK code equivalence. | Native code inventory (for example `src0051`, `src0216`, CS0220) and a category mapping table; do not infer SDK code mappings from parser implementation. | P1 |
| C5 | Formatting and linting (`genexus_format`, `LinterService`). | none. | Native formatter rules. | P3 |

### D. Specification and navigation

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| D1 | Specify per object and in batches. `ISpecifierService`, `SpecifyOneOnly`, `SaveSpecifyOrchestrator`. | Portable parity: `specify_procedure` and `specify_catalog` (`src/gx-engine/src/navigation.rs`, `tests/navigation_specifier.rs`, 10/10 tests). Relational base table inference with fail-closed diagnostics (`spc0004` for unrelated attributes, `spc0005` for ambiguous base tables), extended table computation up to depth 16, foreign key join resolution, index selection matching primary keys and secondary indexes, automatic correlation between nested For Each loops, and structured `NavigationReport` generation for procedures and catalog batches. | Native Specify outcomes for the admitted subset, including the navigation XML side effect (VALIDATION: specify without generation produced navigation XML). Recorded specify times were 6.3-9.2 s on KBTeste. These are not benchmarks. | P1 |
| D2 | Navigation report and For each / data-access IR. `NavigationService` reads SDK-produced `.nvg.xml` (`LoadNavigationDocument`, line 340); `NavigationSqlService`. Tool: `genexus_navigation`. | Portable parity: Navigation report, For each data-access IR, and Formula specification (`src/gx-engine/src/navigation.rs`, `tests/navigation_specifier.rs`, 10/10 tests; `tests/formula_navigation.rs`, 11/11 tests). Typed IR with `NavigationReport`, `NavigationLevel`, `NavigationFormula`, `NavigationJoin`, `NavigationFilter`, JSON serialization, aggregate calculation navigation (Sum, Count, Max, Min, Find, Average), horizontal formulas, context correlation, index selection, and read-only multi-dialect SQL query generation (ANSI, SQL Server, PostgreSQL, Oracle) with zero external dependencies and zero SQL writes. | Characterize `.nvg.xml` for the admitted constructs. Define the IR the engine would own. | P1 |
| D3 | Reorganization and DB impact. `ISpecifierService.ImpactDatabase`, `ReorgImpactService`, `DbDriftService`. | Read-only schema normalization & DDL generation (`table.rs`, `tests/table_ddl.rs`, 9/9 tests). Zero SQL writes. | Native impact output on a disposable KB with a data-model change. No SQL writes. | P2 |
| D4 | Dependency graph and impact. `genexus_impact`, `genexus_object_context`, `genexus_dfd`, `CallerGraphService`, `ImpactAnalyzer`. | Portable subset: reverse call graph and reparse/affected sets in `incremental.rs`, shown by `explain`. Covers Procedures in one project only. `dependencies.rs` visits `For Each` loops, wheres, orders, when_none, and attribute references alongside procedure calls. | Native call-graph equality for admitted Procedures. Uncharacterized. | P2 |

### E. Generation

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| E1 | Native code generation: `IGeneratorsService.Generate`, `GenerateGroup`, `GenerateResources`; generator ID 41; output in `NetModel/Web/*.cs`; MSBuild `CompileMains` (`BuildService.cs:3828`). | none for GeneXus targets. The engine emits Rust (`backend.rs`, `typed_backend.rs`). | Native generated-code contract for admitted Procedures (read-only inspection of `arusttimingprobe.cs`). Behaviour of `GenerateGroup`: it returned false in the observed run (VALIDATION). Target decision: decision 1. | P1 |
| E2 | Generator references and extension generators: `genexus_generator_reference`, `ICurlGeneratorService` (`CurlProcService`). | none. | Native add and remove semantics. Not needed for the admitted subset. | P3 |
| E3 | Generated resources and messages (`GenerateResources`; `messages.eng.txt` in the observed run). | none. | Native resource layout. | P3 |

### F. Compile, build, run and test

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| F1 | Compile one object: `IRunService.Compile`, reflected through `GenexusBLServices.Run.Compile` (`InProcessBuildRunner.cs:54-60`). | none in-process. The engine invokes rustc (`build.rs:180`). | Nothing for the engine. Native compile of the admitted subset is already measured (`rust-timing` logs). | P2 |
| F2 | KB-level Build, BuildAll, Rebuild and incremental rules. `genexus_lifecycle` (17 actions); BuildAll task XML (`BuildService.cs:3814-3870`); SpecifyOneOnly and IdeWebBuildAndDeploy (`InProcessBuildRunner.cs`). | none at KB level. The engine does a fresh rustc build per project and reuses parsed bodies only (`incremental.rs`). It has no KB-wide graph. | A build matrix on a multi-object disposable KB: full, unchanged, one-body edit, signature edit, each with terminal completion evidence (VALIDATION criterion 4). The only timing is one trivial fixture (`rust-native-comparison.json`). | P1 |
| F3 | Run artifacts and runtime URLs. `genexus_run_object`, `RunObjectService`, `genexus_browser`. | portable subset: `gx-engine run` executes authored programs with a 10-second deadline (README). The independent CLI check→emit→build→artifact-execution→run acceptance flow passed 2/2. No web runtime. | Native executable output for admitted programs is already matched. Web runtime waits on B4. | P2 |
| F4 | Native tests: `genexus_test` (GXtest). | none. | Native GXtest semantics. | P3 |
| F5 | Cloud and deploy prerequisites: `DEPLOY_CLOUD`, `genexus_deploy`, `IDeploymentService.Deploy`. | none. VALIDATION records that the SDK rejected `DEPLOY_CLOUD=No`; the KB must stay `Yes`. | Out of scope unless authorized. Record as an external dependency. | P3 |

### G. Runtime data types and semantics

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| G1 | Boolean: literals, Not/And/Or, comparisons, typed declarations. | portable subset (`typed_backend.rs`, `typed_pipeline.rs`). native-verified subset: 19 typed observations (`typed-native-1792028364801578100`); 91-case logical differential. | Boolean attribute semantics remain open; scalar SDT Boolean members now have native-backed compiler coverage. | P2 |
| G2 | Numeric integer storage and overflow (precision 1-18). | portable subset: checked Int16/Int32/Int64 storage for precision 1-4, 5-9 and 10-18 (README). native-verified boundaries: `width-native-1792029927991083100`. **divergence**: native wraps (Numeric(3,0) goes negative after 32760+10, VALIDATION); the engine returns a controlled error (exit 70). | Decision 2. Native behaviour of other integer kinds (Int, Long) is uncharacterized. | P1 |
| G3 | Decimal arithmetic, rounding, division and display (`Numeric(p,s)`, `ToString`, overflow markers `***`). | Portable subset: `types.rs` (96-bit coefficient, scale at most 28; display rounds halves away from zero). Native-verified subset: 19 typed observations (1.235 renders 1.24; 0.1+0.2 renders 0.30; 1/8 renders 0.13; `***` overflow). A `.NET System.Decimal` oracle covers 288 cases (`scripts/validate_decimal.py`); that is .NET, not GeneXus. The README/test disagreement recorded in the original snapshot is no longer current: the README now describes the 28-digit .NET result for `1 / 3`. | Native results for repeating division (`1/3`), coefficient overflow in multiplication, and intermediate scale. | P1 |
| G4 | Character and Varchar: literals, assignment, concatenation, equality, length, storage. | Portable subset: `text.rs`, typed string fixture and `tests/text_values.rs`; native-verified subset: 17 of 18 (`string-native-1792031065612440700`, `text-rust-differential.json`). **Divergence**: the CP1252 generator turns the emoji into `??`; the engine keeps it. Default one-argument `Str(Numeric)`, ASCII-space `Trim(text)`/variable `.Trim()`, `Len(text)` and `Str(Numeric, width[, scale])` with literal width 1..18 and optional literal scale 0..width are implemented. `len_builtin` passed 2/2, matching `.Length()` for empty=0, backslash path=11, padded Character=2 and astral=4; this is not a separate full native `LEN` differential. Explicit `Str` now matches the native picture rule across the whole admitted width/scale space (30,158-case black-box `StringUtil.Str` differential against the GeneXus 18 runtime, zero mismatches; the 524 discriminating rows are checked in at `tests/fixtures/str_matrix.tsv`); dynamic width/scale are rejected. | Dynamic `Str` width/scale and broader formatting parity (Substring, Upper/Lower, Replace, Val, Format, ordering/collation, padding and truncation on storage) remain uncharacterized or unsupported. | P1 |
| G5 | Date: YMDToD, components, empties, arithmetic, two-digit year window, AddMonths and AddYears. | portable subset: `date.rs`; `tests/date_pipeline.rs` (9 tests). native-verified subset: 37 observations (`date-native-1792031609833525800`) and 18 calendar observations (`calendar-native-1792032205289008600`). The year pivot is an explicit setting, 2039 measured here (decision 3). Native raised ArgumentOutOfRange for year -1. | Date formatting and conversion; database date mapping; years outside 1-9999. | P1 |
| G6 | DateTime: components, rollover, differences, empties; integral seconds only. | portable subset: `datetime.rs`; `tests/datetime_values.rs`. native-verified subset: 42 observations (`datetime-native-1792032567109039000`). | Milliseconds, time zones, formatting. A Time type has no evidence and is not admitted. | P2 |
| G7 | Null, unassigned and empty semantics. | portable subset: an unassigned read is an error; the Date empty sentinel is the .NET minimum (README). | Native behaviour for unassigned variables and the default value of each type. Uncharacterized outside Date and DateTime. | P1 |
| G8 | Collections, nested records and defaults at runtime (SDT runtime). | Portable subset is implemented in `model.rs`, `typed_backend.rs`, `record_runtime.rs`, with nested/collection fixtures in `tests/typed_pipeline.rs` and runtime tests in `tests/record_runtime.rs`. `Item` accepts numeric expressions, variables and helper results; the evaluated value truncates fractional digits toward zero (native KBTeste: 1.9 -> item 1, 2.7 -> item 2, 1.5 -> item 1), and nonpositive or out-of-range results fail with controlled runtime errors while native terminates with an unhandled `NullReferenceException` (observed exit 1; controlled diagnostic kept as a deliberate safety divergence). `collection_indices` passed 3/3; For-driven traversal/member updates printed `7`, `9`, `11` in the portable pipeline. The dynamic-index native differential ran against disposable KBTeste: in-range variable/expression/For indices matched exactly and fractional truncation matched 1.9/2.7/0.9/-0.5. `kb_pipeline` passed 12/12, including direct Calls-edge enforcement and transitive nested Uses. The SDK XML exchange suite passed 16/16 (recursive deep-Level projection plus the depth-2 item alias); the native nested Record ExportFile and the captured deep-SDT export both pass the Rust XML flow with matching expected output (CRLF-only normalization for the former, an injected companion Procedure for the latter). The custom-item ExportFile has no Procedure entry; it is rejected for execution and has no runtime-equivalence result. `structured.rs` is a separate owned-value API. | Native evidence now characterizes nested Record.Clone and Collection.Clone: child records remain aliases; Collection.Clone copies membership but retains item-record aliases. The native hierarchy exports are listed under B3. Dynamic collection-index behavior is now characterized (truncate toward zero, native `NullReferenceException` on invalid indices); broader operations and custom-item runtime remain open (custom-item lacks an entry Procedure). | P0 (with B3) |
| G9 | Other primitive types (Time, Guid, Long and Int named types, Blob, Image, Video, Currency, Bit, geolocation). | none. | Native behaviour per type. | P2 |
| G10 | Calls and helper semantics: argument order, side effects, call hoisting in And/Or, recursion limits. | portable subset: typed helpers (`typed_backend.rs`, `fixtures/typed-calls.json`), in/out/inout and void calls (`fixtures/typed-parm-modes.json`). Native evidence includes 12 ordered helper lines and parameter mode/aliasing observations (implementation ledger). | Validate nested calls with mutable arguments, native Rules mapping, and calls to arbitrary KB Procedures. | P1 |
| G11 | Output channels: `Print`, `msg(..., status)`, return value to caller, console versus web. | portable subset: `Print` writes stdout; `Return` yields a numeric value, not an OS exit code (README). Native fixtures use `msg`. | Native output channels per context and the semantics of `status`. | P2 |

### H. Database and data access

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| H1 | Physical schema and DDL, reorg preview, drift. `genexus_db` (`sql_ddl`, `reorg_preview`, `drift_check`); `DbDriftService`, `ReorgSqlPreview`. | Portable subset & Parity: multi-dialect SQL DDL generation (`generate_database_ddl`, `to_full_ddl` in `table.rs`) for ANSI, SQL Server, PostgreSQL, and Oracle dialects. Maps Numeric, Character, Varchar, Boolean, Date, DateTime; emits primary keys, foreign keys, and unique/secondary indexes without SQL writes. Verified by 9/9 integration tests in `tests/table_ddl.rs`. | Reorganization preview and live DB drift. | P1 |
| H2 | Records and queries on transaction rows: `genexus_db` (`records_query`, `records_insert`, `records_update`), `TransactionRecordsService`. | Read-only SQL query plan generation for transaction records, relational navigation, and aggregate formulas across 4 SQL dialects (`navigation.rs`, `tests/navigation_specifier.rs`, `tests/formula_navigation.rs`, 21 tests total). Zero SQL writes. | Read-only query execution against a disposable database. The write path stays SDK-only. | P2 |
| H3 | DataStores and providers (Oracle, PostgreSQL, MySQL, SQL Server, Informix). `DatabaseProviderResolver`, `DatabaseInfoService`. | none. | Native behaviour per DBMS. Waits on H1 and H2. | P2 |
| H4 | Data views and transaction-to-table mapping. `genexus_data_view` (5 actions), `DataViewService`. | none. | Native DataView readback and its mapping to a physical table. | P2 |
| H5 | Optimizer and sample data: `genexus_db` (`optimize_*`, `sample_data`), `DbOptimizeService`, `SampleDataService`. | none. | Not needed now. | P3 |

### I. Authoring, round-trip and text exchange

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| I1 | Portable authoring edits with deterministic serialization and revisions (plan 129, workstream 8). | portable subset: catalog `Edit` operations (`catalog.rs`). The portable project format is our own. | Needed only if the engine owns writes (decision 5). | P1 |
| I2 | Native persistence through the SDK: create, save, delete, atomic create, version tokens. `AtomicCreateService`, `ObjectService`, `WriteService`. Tools: `genexus_create`, `genexus_edit`, `genexus_delete_object`, `genexus_authoring`. | none in the engine. The SDK adapter is the only write path. | Writes need explicit authorization for each target. | P2 |
| I3 | Text export and import, text mirror: `genexus_io` (`export_kb_to_text`, `import_text_to_kb`, `text_mirror_*`), `SdkTextTreeService`. | none. `src/gxwire` is a separate Rust index of text trees and does not read KBs. | Native text-tree format for admitted object kinds. | P2 |
| I4 | Versioning, history and undo: `genexus_versioning`, `HistoryService`, `TimeTravelService`. | none. | Native version semantics. Not needed for the admitted core. | P3 |

### J. Platform, UI and operations

| ID | SDK operation / area (Worker evidence) | gx-engine status | Native evidence still needed | Priority |
| --- | --- | --- | --- | --- |
| J1 | UI and WorkWith: `genexus_wwp`, `genexus_workwith`, `genexus_apply_pattern`, `genexus_layout`, `genexus_edit_form`, `genexus_api`, `genexus_k2b_designer` (the last depends on the IDE bridge, `src/GxMcp.K2bIdeBridge`). | none. | Out of core scope. | P3 |
| J2 | GAM and security: `genexus_gam` (`IIntegratedSecurityService`), `genexus_security` (`SecurityScanService`, `GamService`). | none. | Out of core scope. Authorization-sensitive. | P3 |
| J3 | Team development, transfer, modules, versions, merge and compare: `genexus_gxserver` (`IGXserverService`, `ITeamDevClientService`), `genexus_transfer` (XPZ, `IKnowledgeManagerService`), `genexus_module` (`IModuleManagerService`), `genexus_kb_version` (`KBVersionHelper`), `genexus_merge`, `genexus_compare`, `genexus_kb_diff`, `genexus_kb_import`. | none. `genexus_kb_diff` compares the filesystem only (`docs/sdk_coverage_gap_matrix.md`). | Out of core scope. | P3 |
| J4 | Deployment: `genexus_deploy` (`IDeploymentService.Deploy`). | none. Destructive; requires confirm. | Out of core scope. | P3 |
| J5 | Platform internals: `genexus_worker_reload`, `genexus_worker_pool`, `genexus_connection_recover`, `genexus_telemetry`, `genexus_memory`, `genexus_recipe`, `genexus_sandbox`, `genexus_whoami`, `genexus_doctor`, `genexus_sdk_probe`, `genexus_tutorial`. | none. These are MCP runtime concerns, not KB semantics. | None. | P3 |

## 4. Top 10 gaps, in priority order

1. **A2 - Native KB read model (P0).** There is no reader. The catalog is our own
   format. The Worker opens native KBs through `KnowledgeBase.Open` (`KbService.cs:467-468`).
   Evidence needed: read-only SDK export of a disposable KB, diffed against a portable
   import, covering identities and every part.
2. **C3 with G10 - Procedure interface (P0).** Update 2026-10-15: in/out/inout modes,
   void Procedures and call statements are implemented in the typed path and match
   native KBTeste probes for Numeric and empty Boolean/VarChar/Date out values (see plan 129
   progress). Final-out expression results, explicit Numeric inout rvalues and nested-call
   side effects now match native probes; portable standalone Parm Rules are parsed.
   Still open: native KB signature import, general Rules, Character/DateTime out defaults
   and broader type/signature boundaries. Historical baseline: parameters were by value
   only and void Procedures were absent.
3. **C2 - Source grammar for real Procedures (P0).** Bounded Do Case, void Return,
   call statements, standalone Parm, CLI Msg(text,status), and a limited format-2
   Numeric For/EndFor with literal nonzero integer Step are implemented. Native For
   evidence in [`plans/129-native-for-progress.md`](129-native-for-progress.md) now covers inclusive ascending/descending, positive/negative literal Step, direction mismatch with zero iterations, start evaluated once, and `To` reevaluation on every condition including the final false check; dynamic-bound
   and descending semantics remain in flight, so broad parity is not claimed. Still
   missing: For each, subroutines, general Rules/message modes, Commit/Rollback, broader
   function libraries and events.
   Evidence needed: a corpus with SDK accept and reject results, and a lossless
   grammar.
4. **B3 with G8 - SDT (P0).** Native evidence now covers scalar records plus a
   nested record Clone and collection Clone: nested child references stay shared;
   collection Clone separates list membership but preserves item-record aliases.
   Current model/backend/runtime and fixtures implement those bounded cases. The
   fresh Rust compiler-vs-native differential is still a required gate; source/tests
   alone are not evidence that it passed. Exchange tests now pass 16/16; the nested
   Record ExportFile and the captured deep-SDT export run through the Rust XML flow
   with exact output (see B3), while the custom-item export still lacks a Procedure
   entry for a runtime differential. Full SDK SDT authoring and native KB decoding
   remain open.
   Separate `structured.rs` owned-value semantics are not native evidence. No SQL
   propagation.
5. **B6-B8 - Data-model objects: Domain, Attribute, Transaction, Table (P1).** None is
   admitted; typed declarations accept primitives only (`model.rs:7-13`). Evidence
   needed: readback of each object kind and its effect on types and keys.
6. **D1-D2 - Specify and navigation (P1).** The engine has no Specifier. The Worker
   consumes SDK-produced `.nvg.xml`. Evidence needed: `.nvg.xml` characterization for
   the admitted constructs.
7. **E1 - Generation contract (P1, decision 1).** The engine emits Rust; the GeneXus
   pipeline emits .NET C#. Evidence needed: a read-only snapshot of generated C# for
   the admitted subset, and the target decision.
8. **F2 - KB-level build graph (P1).** The engine builds one project per rustc call and
   has no KB dependency graph or BuildAll equivalent. Evidence needed: a multi-object
   build matrix (full, unchanged, body edit, signature edit) with terminal completion
   evidence.
9. **G2-G7 - Runtime semantics (P1).** Numeric division and overflow policy (decisions
   2 and 3), text functions beyond `.Length()`, default `Str`, `Trim` and the
   literal-width/scale `Str` subset, date formatting, and unassigned/default semantics
   remain incomplete. Broader formatting and focused native probes are still needed,
   as is the integer-policy decision.
10. **H1 - DDL and reorg preview (P1).** Required before any DB-backed Procedure can
    be claimed. Evidence needed: read-only native DDL preview on a disposable KB. No SQL
    writes.

Beyond the top 10: B4 (WebPanel and UI), I1 and I2 (authoring and writes), H2 and H3
(records and providers), and the J area. All are P2 or P3 in the table above.

## 5. Decisions for the user (not blocking the matrix)

1. **Generation target.** Should admitted Procedures compile to Rust executables only,
   or must the output also be GeneXus .NET output that existing deployments can run?
   This changes E1, F2 and the runtime rows.
2. **Integer policy.** Native integer storage wraps (Numeric(3,0), VALIDATION). The
   engine errors instead. Keep the error as a documented safety divergence, or match
   the native wrap? Native also raises CS0220 for a constant product in Numeric(18,0),
   which the engine does not reproduce.
3. **Two-digit year pivot.** Today it is an explicit per-project setting; 2039 is what
   was measured here. Should there ever be a default, or should it stay explicit?
4. **Read route for native KBs.** Options: (a) read through the SDK (needs no
   proprietary decoding); (b) decode the native store directly. (b) is proprietary
   format work and needs explicit authorization. Proprietary artifacts stay out of Git
   either way.
5. **Writes and SQL.** Keep native writes SDK-only, and never write SQL, until each
   write path is explicitly authorized? Current constraint, not yet confirmed as
   long-term policy.

## 6. Verification status and caveats

This section records the original static inventory pass; later owner-reported
milestones are reflected in the updated B3/C2/G4/C4/G8 rows and detailed in
`plans/129-implementation-progress.md`. The docs-only reconciliation here did not
run tests, builds, SDK probes, or KB operations.

Checked in the original inventory pass:

- `src/GxMcp.Gateway/tool_definitions.json`: parsed; 60 tools; enum values read for
  `action`, else `mode`, else `operation`.
- Worker literal scan of `"<tool name>"` across 435 `.cs` files. Literal hits are
  locators, not proof of an SDK call: help-catalog files also contain tool names, so
  `HelpService.cs` and `DynamicSdkBridge.cs` were excluded from the table.
- gx-engine: `src/*.rs` (6,642 lines); 122 `#[test]` attributes counted statically
  (86 in `tests/*.rs`, including 1 ignored; 36 in `src` unit tests).
- Differential JSON reports (5 files) and the existence of 12 cited native evidence
  directories.
- `docs/sdk-probe/INDEX.md`, `docs/sdk_coverage_gap_matrix.md`, `plans/128-*`,
  `plans/129-*`, `src/gx-engine/VALIDATION.md`, `src/gx-engine/README.md`.
- ripwire: `--grep`, `--callers`, `--callees=CreateObjectInstance` (the object-kind
  construction branches: `ResolveObjectTypeGuid`, `InitializeTransactionWithDefaultKey`,
  `InitializeSDTWithDefaultItem`, `InitializeDomain`), and a grep over `src/gx-engine`.

Not done by this documentation pass: no `cargo` test, clippy or build; no `dotnet` or
MSBuild run; no KB open or read; no SDK probe; no MCP call. Latest integrated Rust gate
(parent run, after the LEN/catalog/For/diagnostics increments): 236 passed, 0 failed,
2 ignored across 27 harnesses (`scratchpad/engine-gate-4.log`); `cargo clippy
--all-targets -- -D warnings` and `cargo fmt --check` pass. Includes `kb_pipeline` 13/13,
exchange 11/11, CLI acceptance 4/4 and CLI diagnostics 5/5 (all five phases). This is
portable-subset validation only. See the implementation ledger. Counts and
statuses describe the original inventory snapshot.

Caveats:

- **Historical conflict, resolved in this documentation pass.** An earlier README
  snapshot said repeating division fails explicitly, while
  `tests/typed_pipeline.rs:109` expects `Print(1 / 3)` to print the 28-digit .NET
  result. The README now states the same portable behavior as the test. Neither is
  native-verified (G3).
- **Concurrent work (historical note).** Other agents were editing `src/gx-engine`
  (structured values, decimal, catalog, SDT probe) and `scratchpad/` during the original
  inventory pass. Subsequent exchange, Str and diagnostics milestones are summarized
  in the implementation progress ledger and reflected in the relevant rows above.
- **Evidence location.** Native evidence lives under `scratchpad/`, which is git-ignored
  (`.gitignore:56`). `VALIDATION.md` cites those paths, which exist only locally.
- **Existing SQL write path.** `src/GxMcp.Worker/Helpers/SdtModelPropagation.cs` writes
  SDT model rows with direct SQL, called from `WriteService.cs:1738`. It is flagged here,
  not reproduced, and it is not changed by this document.
- **Not compiled from KBs.** `src/gxwire` indexes text trees on disk. It is not a KB
  reader and not part of gx-engine.

## 7. Tool surface map

Columns: tool, area (A platform, B read and analysis, C authoring, D specify, build and
run, H database, J UI and platform), enum values of the first of `action`, `mode` or
`operation` (`-` if none), Worker literal hits (all files), main Worker files
(excluding `HelpService.cs` and `DynamicSdkBridge.cs`), and matrix rows.

| Tool | Area | Enum values | Worker hits | Main Worker files | Rows |
| --- | --- | ---: | ---: | --- | --- |
| `genexus_kb` | A | 13 | 4 | KbOpenNextStep.cs, InjectionService.cs, RefactorService.cs | A1 |
| `genexus_whoami` | A | - | 4 | KbStartupService.cs, LearningReportService.cs, TutorialService.cs | A1 |
| `genexus_doctor` | A | - | 1 | CommandDispatcher.cs | A1 |
| `genexus_connection_recover` | A | 3 | 0 | none found | J5 |
| `genexus_worker_reload` | A | 2 | 0 | none found | J5 |
| `genexus_worker_pool` | A | 1 | 0 | none found | J5 |
| `genexus_sandbox` | A | 2 | 0 | none found | J5 |
| `genexus_telemetry` | A | 9 | 0 | none found | J5 |
| `genexus_memory` | A | 6 | 0 | none found | J5 |
| `genexus_recipe` | A | 4 | 0 | none found | J5 |
| `genexus_sdk_probe` | A | 2 | 0 | none found | A2 (evidence), J5 |
| `genexus_read` | B | - | 28 | HealingService.cs, BatchService.cs, CommandDispatcher.cs | A2, B1-B8 |
| `genexus_inspect` | B | - | 16 | AnalyzeService.cs, BatchService.cs, ConversionService.cs | A2, B1-B8 |
| `genexus_list_objects` | B | - | 28 | HealingService.cs, AnalyzeService.cs, AtomicCreateService.cs | A2 |
| `genexus_query` | B | - | 11 | CompareService.cs, GraphToolsService.cs, LinterService.cs | A2, D4 |
| `genexus_search_source` | B | - | 2 | none found | A2 |
| `genexus_navigation` | B | 1 | 1 | none found | D2 |
| `genexus_object_context` | B | - | 0 | none found | D4 |
| `genexus_impact` | B | - | 0 | none found | D4 |
| `genexus_dfd` | B | - | 0 | none found | D4 |
| `genexus_analyze` | B | 19 | 8 | K2bWebPanelDesignerService.cs, OrientService.cs, RefactorService.cs | C1, C4, D4 |
| `genexus_wire` | B | 10 | 0 | none found (separate gxwire crate) | I3 |
| `genexus_compare` | J | 2 | 0 | none found | J3 |
| `genexus_kb_diff` | J | 2 | 0 | none found | J3 |
| `genexus_doc` | B | 3 | 1 | none found | J5 |
| `genexus_create` | C | 12 | 4 | AtomicCreateService.cs, ErrorDiagnoser.cs | B1-B9, I2 |
| `genexus_edit` | C | 3 | 14 | BatchService.cs, CommandDispatcher.cs, DryRunPlanBuilder.cs | C1, C2, I2 |
| `genexus_edit_and_build` | C | 2 | 0 | none found | C1, F2 |
| `genexus_delete_object` | C | - | 5 | ObjectService.cs, PatternApplyService.cs, SaveAsService.cs | I2 |
| `genexus_properties` | C | 3 | 4 | PropertyService.cs, ErrorDiagnoser.cs, WriteService.Variables.cs | B1-B8, I2 |
| `genexus_structure` | C | 13 | 4 | StructureService.cs, GroupStructureService.cs, ErrorDiagnoser.cs | B2, B3, B8, G8 |
| `genexus_variable` | C | 3 | 4 | WriteService.Variables.cs, ErrorDiagnoser.cs | B6, B7, C3 |
| `genexus_authoring` | C | 4 | 0 | none found | I2 |
| `genexus_format` | C | - | 0 | none found | C5 |
| `genexus_refactor` | C | 6 | 4 | PropertyService.cs, RefactorService.cs, VoiceIntentService.cs | D4, I1 |
| `genexus_lifecycle` | D | 17 | 18 | McpResponse.cs, AnalyzeService.cs, BuildPlanService.cs | D1, E1, F2, F5 |
| `genexus_run_object` | D | - | 2 | RunObjectService.cs, VoiceIntentService.cs | F3 |
| `genexus_test` | D | - | 0 | none found | F4 |
| `genexus_browser` | D | 6 | 0 | none found | F3, J1 |
| `genexus_generator_reference` | D | 5 | 2 | CommandDispatcher.cs | E2 |
| `genexus_data_view` | H | 5 | 1 | CommandDispatcher.cs | H4, B9 |
| `genexus_db` | H | 17 | 0 | none found (Worker has DbDriftService, DbOptimizeService, TransactionRecordsService, ReorgImpactService by name; dispatch path not traced) | H1-H5, D3 |
| `genexus_layout` | J | 15 | 3 | LayoutService.cs, LayoutService.SourcePersistence.cs, LayoutService.VisualContext.cs | B4, J1 |
| `genexus_edit_form` | J | 5 | 1 | VoiceIntentService.cs | B4, J1 |
| `genexus_wwp` | J | 20 | 4 | WorkWithService.cs, WwpActionService.cs, CommandDispatcher.cs | B9, J1 |
| `genexus_workwith` | J | 2 | 2 | WorkWithService.cs, CommandDispatcher.cs | B9, J1 |
| `genexus_apply_pattern` | J | 6 | 7 | FeatureScaffoldService.cs, ObjectService.cs, PatternApplyService.cs | B9, J1 |
| `genexus_api` | J | 9 | 1 | none found | B9, J1 |
| `genexus_k2b_designer` | J | 7 | 1 | CommandDispatcher.cs | B4, J4 |
| `genexus_gam` | J | 3 | 2 | GamService.cs | J2 |
| `genexus_security` | J | 4 | 0 | none found | J2 |
| `genexus_module` | J | 10 | 1 | none found | A4, J3 |
| `genexus_transfer` | J | 3 | 2 | TransferService.cs | J3 |
| `genexus_kb_import` | J | - | 0 | none found | J3 |
| `genexus_gxserver` | J | 14 | 1 | GxServerSyncService.cs | A5, J3 |
| `genexus_kb_version` | J | 6 | 1 | KbVersionService.cs | J3 |
| `genexus_merge` | J | 1 | 0 | none found | J3 |
| `genexus_versioning` | J | 9 | 4 | HistoryService.cs, WriteService.cs | I4 |
| `genexus_io` | J | 20 | 1 | LayoutService.cs | I3 |
| `genexus_deploy` | J | 2 | 0 | none found | F5, J4 |

Every tool is `none` in gx-engine: no route exists, so the per-tool column would be
redundant. The matrix rows above give the engine status for each area.
