# SDK-to-Rust investigation: first binary findings

## Mandate and status

The user requested deeper analysis of the SDKs with the largest potential Rust gains, before revising roadmap 128. This is an investigation report, not an implementation plan or a claim of measured acceleration. It changes the earlier recommendation: **direct read-only UDM ingestion deserves a first-class experiment now, alongside SDK extraction, not a distant prerequisite-heavy research phase.**

Repository inspected: `b9de0a5ae4cdf0e3932b486b2f01702a60e7e3cd`. Installed target: GeneXus18 under `C:/Program Files (x86)/GeneXus/GeneXus18`.

### Work actually performed

- Fingerprinted 10 installed assemblies; inventoried classes in nine (Architecture.Common was inspected through selected types).
- Statically decompiled 20 selected types, spanning storage, object model, source conversion, conditions, parser and specifier.
- Used existing `ilspycmd 11.0.0.9375`. REA source checkout has neither dependencies nor a compiled CLI; it was not run. No global installation/update was needed.
- Correlated findings with MCP consumers, including `AnalyzeService`, `ListService`, native export pipeline and the in-process build runner.
- Target assemblies were not executed. No KB opened, database connection made, SQL executed, or business data extracted.
- Raw decompilation and fingerprints stay outside Git at `C:/projetos/scratchpad/rea-evidence/sdk18/`; `evidence-manifest.json` identifies every inspected assembly and selected-type output. This report contains analysis, not copied proprietary implementation.

## Highest-value opportunities

The rankings below are engineering judgments based on removable work and consumer reach, **not benchmark results**.

| Rank | SDK layer | Rust opportunity | Why it ranks here |
|---|---|---|---|
| 1 | `Artech.Udm.Layers.Data.SQL` + UDM metadata | Bulk object catalog, model/version selection, persisted dependency graph, model diff | Readable storage mappers expose useful data without first decoding source blobs or constructing KBObject instances |
| 2 | `Artech.Common.Language` + `Artech.Genexus.Common.Parts` | Persisted-token ingestion, symbol resolution and source rendering | The SDK stores source tokens and performs model lookups while reconstructing readable text; Rust can potentially consume the token representation directly |
| 3 | `Artech.Udm.Framework` + `Artech.Architecture.Common` | Replace read-side object hydration with compact immutable records and indexes | Benefits cold discovery, references, source inventory and parallel queries; some core method bodies are not trustworthy static evidence |
| 4 | `Artech.Genexus.Common` structures | Native SDT/transaction/table/DataSelector analysis and diffs | Structured data can support richer semantic analysis than source regexes; each part's persisted format still needs characterization |
| 5 | `Artech.Common.Language.Parser` | Rust lexical/syntax front end for static analysis and rapid diagnostics | Native parsing is visible, but parser replacement alone does not remove model/context resolution; persisted-token ingestion may bypass more work |
| 6 | `Artech.Specifier` | Dependency scheduling, output interpretation, incremental planning and preflight | Specification delegates to native C++ and uses model-wide context. Replacing the compiler is a much larger semantic project than replacing read/query paths |

## 1. UDM SQL: catalog, graph and model diff without source decoding

### Verified static findings

Local evidence files and line anchors:

- `Artech.Udm.Layers.Data.SQL.Mappers.ModelCrossReferenceMapper.cs:181`: a model-wide enumeration opens a reader by model ID. Its row mapping retains model, source/target type and ID, link type, link detail, link info and reference type.
- `Artech.Udm.Layers.Data.SQL.Mappers.ModelEntityVersionMapper.cs:173`: model-wide current mappings are separately enumerable. Other methods select by type, parent, name and model timestamp.
- `Artech.Udm.Layers.Data.SQL.Mappers.EntityVersionMapper.cs:59`: version records include name, description, version identity, type version, properties and a separate data blob.
- `Artech.Udm.Layers.Data.SQL.Mappers.EntityVersionCompositionMapper.cs:28`: composition explicitly links component and compound entity/version identities. A native reader cannot assume one object equals one standalone source blob.
- `Artech.Udm.Layers.Data.SQL.SQLCommands.cs:479–526`: existing model comparisons distinguish version changes AND parent type/ID changes. Incremental ingestion must recognize moves, not only source edits.
- `SQLCommands.cs:353`: a dependency query already combines persisted references, current names and module hierarchy. Therefore some supposedly SDK-only graph answers are already relational data operations.
- Several SDK SQL read paths use `NOLOCK`. This is an observation of the SDK, not a recommendation for our reader; it does not demonstrate coherent reads during concurrent saves.

### MCP work this could remove

`src/GxMcp.Worker/Services/AnalyzeService.cs:123–130` enumerates references and fetches each distinct target object to obtain display metadata. De-duplication already exists; **the remaining per-target object resolution is still a candidate for replacement by a catalog join**. Other `GetReferences`/`GetReferencesTo` consumers include hierarchy, conversion context, 360 context, data context and DB optimization.

`ListService` already prefers an in-memory index; its SDK enumeration is a guarded fallback. A Rust catalog should target **index acquisition/refresh and unserved consumers**, not claim a new win for filtering that already happens in memory.

### Architecture to test

```text
current-model/version mappings + entity/type metadata + persisted references
                  -> one bulk read phase
                  -> Rust catalog + forward/reverse adjacency + module tree
                  -> concurrent list/search/impact/hierarchy/diff queries
```

The first experiment needs neither a complete source decoder nor a new GeneXus compiler. Persisted graph results must remain distinguishable from unsaved IDE state and newly inferred source edges. Preserve reference kinds/details; not every edge is a procedure call.

### What is not established

No live schema parity, row counts, read isolation, permissions, latency or saved-vs-unsaved behavior has been tested. Model inheritance/start-model behavior also needs characterization; an active model is not simply the maximum version ID. Deletion reconciliation must be explicit.

## 2. Source storage: tokens first, text second

### Verified static findings

- `Artech.Genexus.Common.Parts.SourcePart.cs:78–85`: readable `Source` is reconstructed lazily when its cached string is absent.
- `SourcePart.cs:103–119`: serialization/deserialization delegates to `SourceHelper` rather than treating persisted data as the visible source string.
- `Artech.Genexus.Common.Parts.SourceHelper.cs:413–495`: storage conversion goes through token conversion and `StringStorage`; the BinaryStream source-part path uses a UTF-8 reader.
- `Artech.Common.Language.ComponentModel.TokenConverter.cs:56–72`: persisted storage is deserialized as **XML TokenDataList**. The plain-string branch instead invokes tokenization with parser/model context.
- `TokenConverter.cs:149–151`: token lists serialize to StringStorage through the token-list XML serializer.
- `TokenConverter.cs:165–269`: rendering walks tokens and resolves object names, module context, ambiguous names, GUIDs, attributes and structured-type names. Some branches retrieve actual objects.

This establishes the format of this selected source-part path, **not that the complete EntityVersionData blob is a plain UTF-8 source document**. Outer composition/framing and other part formats remain unresolved.

### Stronger hypothesis than a standalone source parser

A Rust reader may ingest stored tokens, retain their object identities, bulk-resolve names against the catalog, and build reference/source indexes without rendering everything first. Render readable text only when requested. This avoids the proposed pipeline of SDK-rendered text -> disk export -> Rust re-tokenization for covered persisted parts.

Candidate pipeline:

```text
version/composition map -> selected source-part payload -> XML token decoder
  -> identity-bearing token stream -> semantic indexes + lazy source rendering
```

The difficult parts are name qualification, missing-object semantics, module scopes, formatting and exact token/schema coverage. A token list is not automatically an AST. The token serializer implementation and token IDs still need a dedicated format pass. SDK text export remains a useful oracle and a fallback for unsupported parts, not the only architecture worth pursuing.

## 3. Object model: shrink the read-side bridge

`KBObject`, `KBModel`, `Entity` and `UdmKnowledgeBase` expose the relevant load/serialize/reference/model operations. However, several selected static bodies appear as empty/default-return stubs with obfuscated initialization. **Do not interpret those outputs as actual runtime behavior or zero-cost operations.** These assemblies support API-shape evidence, not reliable internal algorithms from this pass.

The Rust opportunity is not 'port each SDK class'. It is to stop requiring the rich mutable SDK object graph for read-only answers. Keep a small .NET command bridge for mutations, unsupported decoding, official validation and build tasks. Let a native service own compact catalog/token/reference data and concurrent analysis. Whether this reduces total process memory depends on which .NET retained structures it actually replaces; a duplicate Rust cache could increase memory.

## 4. Structured parts and DataSelector

`Artech.Genexus.Common.Parts.DataSelectorCondition.cs` represents condition source and delegates validation/reference extraction through ParserInfo and SourceHelper. `DataSelectorLevel.cs` exposes levels and typed condition/order/defined-by collections.

This provides a concrete investigation surface for structure-aware Rust analysis. It does **not** establish that SDK DataSelector levels are the same thing as a general Boolean AND/OR expression tree. The earlier roadmap's nested-condition conclusions need this distinction: inspect persisted structure and expression semantics before proposing an edit schema.

Potential native services: structured diff, dependency extraction, condition diagnostics, SDT/transaction shape indexes and precomputed context bundles. Official persistence can remain in .NET while analysis moves out.

## 5. Parser and specifier: different ambitions

`Artech.Common.Language.Parser.CParserEngine.cs:34–66` allocates a native lexer manager and enters native parsing. A managed inventory cannot reconstruct the native grammar/implementation. Rust parsing for static analysis is still viable, but source/token ingestion and catalog resolution should be measured separately from grammar work.

`Artech.Genexus.Specifier.BusinessExpert.cs:146–174` prepares model context, adds selected objects and invokes native specification. It also uses a bounded scoped model-object cache. This is evidence against assuming the expensive compiler is merely a C# loop Rust can trivially accelerate.

High-value Rust roles around it: determine impacted targets, compare versioned inputs, inspect generated/specification outputs and schedule minimal necessary work. A future semantic compiler front end is a separate opportunity requiring compiler-output parity; it is not excluded, but is not the quickest route to SDK independence.

## Architecture now worth testing

```text
                     Rust KB read engine
     catalog / model versions / typed graph / persisted tokens
          source search / impact / diff / context bundles
                   ^                       ^
       read-only UDM provider       narrow .NET extraction bridge
       for characterized data       for unsupported parts/live state

        .NET SDK command host: save / import / validate / specify / build
```

The two read providers are competing/measurable paths, not an obligation to build both into production. Choose per capability on evidence. Keep native/legacy and GeneXus-major differences explicit.

## Next experiments to discuss

1. **Catalog + graph parity and cost:** on a disposable KB, compare model-scoped bulk records/edges with current SDK outputs; measure cold acquisition, target metadata resolution and repeated graph queries. This directly tests removal of object hydration and repeated boundary calls.
2. **One persisted source-part decode:** characterize composition/framing and token XML for an authored Procedure, including module-qualified references, attribute references, comments, Unicode, missing references and renames. Compare identity extraction and rendered source with the SDK. Then add Events/Rules only after their differences are known.
3. **End-to-end consumer replacement prototype:** select one `AnalyzeService` query, produce its answer from the admitted catalog/graph, and compare response semantics and total request latency. Count SDK object fetches and database round trips, not just Rust loop time.

These are concrete next experiments, not implemented features. No gain percentage is defensible yet.

## Validation ledger

- **Verified:** installed assembly identities; static selected types and storage/conversion/control-flow observations above; cited MCP consumer paths; raw evidence kept outside Git.
- **Not verified:** live database structure/parity, source blob framing, native parser implementation, runtime effects of protected bodies, SDK-version portability and actual speed/memory gains.
- **Unchanged:** roadmap 128 remains proposed; this companion report is input to the next discussion. No source-code changes, KB writes, commit or push.
