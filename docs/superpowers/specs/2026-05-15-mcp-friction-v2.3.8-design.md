# Design — MCP friction-report 2026-05-15 sweep (v2.3.8)

**Status:** Draft — pending approval
**Source report:** [`docs/mcp-friction-report-2026-05-15.md`](../../mcp-friction-report-2026-05-15.md)
**Target release:** v2.3.8 (folds in current WIP)
**Date:** 2026-05-15
**Author:** lucassouza@univali.br

## Goal

Close all 16 items + 5 quick-wins from the 2026-05-15 friction report in a single v2.3.8 release. The WIP currently uncommitted (~780 LOC: `validate_payload`, `bulk_edit`, `apply_template`, `diff`, `export_unified`, async edit, indexed source-search, PatternVirtual fallback) is the baseline; this spec adds the report fixes on top.

The session that produced the report spent ~75% of turns on workarounds rather than domain work. Concrete target: turn that ratio inverse — the "ideal workflow" at the bottom of the report (~15 turns for the ComissaoLiberaPareceres / ComissaoAgendaDetalhe edit) must become reachable.

## Cross-cutting principles

These four themes run through every category. When in doubt during implementation, defer to them:

1. **Never silence a failure as an empty success.** `count:0` on a cold index, `{status:"Success"}` on a wrong-typed variable, build OK with a broken csproj — all forbidden. Return structured status with `status: "IndexCold|Timeout|UnknownType|..."` instead.
2. **Compact-by-default on payloads that grow with KB size.** Build errors, large part reads, job notifications. Caller can opt into the raw payload explicitly.
3. **Symmetric behaviour across object kinds.** `delete_variable` works the same on Procedure / WebPanel / Transaction / DataProvider. `analyze impact` reads the same caller graph as `inspect callers`. No "Part X not found in WebPanel" asymmetry.
4. **Structured diff/diagnosis on rejection.** When an edit fails, return *what* differs (byte offset, EOL kind, similarity, ghost binding location), not "adjust 'context'". When a variable can't be deleted, name the controls holding it.

## Category A — Discovery / search robustness (#1, #2, #3, #8)

**Affected tools:** `genexus_whoami`, `genexus_search_source`, `genexus_list_objects`, `genexus_analyze` (mode=impact).

### A1. Index state exposed (#1, #8)

`genexus_whoami` response gains an `index` block, single source of truth:

```json
{
  "index": {
    "status": "Ready" | "Cold" | "Reindexing",
    "lastIndexedAt": "2026-05-15T14:22:08Z",
    "totalObjects": 4821,
    "progress": 0.62,
    "etaMs": 8000
  }
}
```

`progress` and `etaMs` present only when status is `Reindexing`.

### A2. `search_source` never silently empty (#1)

When index is `Cold` or `Reindexing`: return `{ status: "IndexCold", retryAfterMs: <int> }` and **no** `hits`/`count`. Caller can branch.

Hard timeout 30s on the regex pass: `{ status: "Timeout", partialHits: [...], totalScanned: N, totalObjects: M }`. Never silent.

The WIP's indexed pre-filter (literal-token extraction) stays — it's a perf win, orthogonal to status reporting.

### A3. `list_objects` discovery (#2, #3)

New parameters:

- **`nameFilter`** — substring match on object name only.
- **`descriptionFilter`** — substring match on description only.
- **`pathPrefix`** — `"Root Module/ClickSign/"` lists folder children. Backed by a new `parentFolderPath` field indexed per object.
- **`filter`** (legacy) — kept; docstring explicitly states it matches both name and description. Phase out planned for v2.4 (not this release).

### A4. `analyze impact` unified with `inspect callers` (#8)

`AnalyzeService.ImpactAnalysis` reads from the same in-memory caller graph used by `inspect callers`. Two-path bug eliminated.

New flag `waitForIndex: bool` (default `true`) blocks up to 30s waiting for index ready; structured timeout on exceeded. When `false`, returns `{ status: "Reindexing", etaMs }` immediately.

### A acceptance

1. `whoami` returns `index.status` on every call.
2. Cold-index `search_source` returns `IndexCold` envelope; never `{count:0, hits:[]}` when there are matches.
3. `list_objects nameFilter="ComissaoLiberaPareceres"` finds the procedure.
4. `list_objects pathPrefix="Root Module/ClickSign/"` returns children of folder.
5. `analyze impact name=X` returns same callers as `inspect callers name=X include=["callers"]` for any X (golden test).

## Category B — Edit/Write reliability (#4, #11, #13)

**Affected tools:** `genexus_edit`, `Helpers/XmlEquivalence`, `Helpers/DiffBuilder`.

### B1. EOL-normalized matching (#4)

`WriteService` normalizes both `context` and source to `\n` + trims trailing whitespace per line **before comparison only**. Source bytes preserved on disk untouched.

Match becomes EOL-insensitive: a multi-line `context` containing `\r\n` literal, `\n`, or even mixed line endings all match the same source window.

### B2. Byte-level `nearMatchHint` (#4)

When best-window similarity ≥ 0.80 but no exact match, response includes:

```json
"nearMatchHint": {
  "similarity": 0.93,
  "topWindow": {
    "startLine": 142,
    "endLine": 145,
    "contextNormalized": "...",
    "sourceWindowNormalized": "...",
    "firstDivergenceAt": { "line": 143, "column": 17 },
    "divergenceKind": "EOL" | "Whitespace" | "Content"
  }
}
```

Caller sees exactly which byte/line/kind differs.

### B3. Patch shapes audited (#11)

- `{find, replace}` JSON: fixed to work for multi-line + CRLF. Test fixture added.
- RFC 6902 array: removed from docstring (no real use case found, zero callers).
- `operation=Replace + context + content` (separate params): kept; remains canonical.

### B4. Hash + snippet on every response (#13)

Edit response (success, partial, rollback) always carries:

```json
"persistedHash": "sha256:abc...",
"persistedSnippet": "...20 lines around edit point..."
```

Side-effect normalizations (GX normalized other lines on save) reported in `_meta.sideEffectNormalizations: [{ line, before, after }]` instead of triggering rollback — see C5 below for the rollback policy change.

### B acceptance

1. Multi-line CRLF `context` round-trip test passes (`WriteServiceTests.MultilineCrlfContext_Replace_Works`).
2. `{find, replace}` patch test passes for multi-line.
3. `nearMatchHint.byteDiff` present whenever similarity ≥ 0.80.
4. After rollback, `persistedHash` matches a re-read SHA of the file; caller doesn't need to re-read.

## Category C — Variable lifecycle (#5, #6, #12)

**Affected tools:** `genexus_add_variable`, `genexus_delete_variable`, new `genexus_modify_variable`, `Helpers/VariableTypeResolver.cs` (new), `Helpers/WebFormSchemaHints` (ghost-binding resolver).

### C1. `typeName` validation (#5)

New helper `VariableTypeResolver` maps synonyms to canonical GeneXus types:

| Canonical | Accepted aliases |
|---|---|
| `Character` | `Character`, `Char`, `String`, `VarChar` |
| `Numeric` | `Numeric`, `Number`, `Decimal`, `Int`, `Integer` |
| `Boolean` | `Boolean`, `Bool` |
| `Date` | `Date` |
| `DateTime` | `DateTime`, `Timestamp` |
| `Time` | `Time` |
| `LongVarChar` | `LongVarChar`, `Text` |
| `Blob` | `Blob`, `Binary` |
| `Image` | `Image` |
| `GUID` | `GUID`, `Uuid` |

Length/precision parsed from parentheses: `Character(120)`, `Numeric(10,2)`. Domain references (`&PesCod`-style based-on) pass through unchanged.

Unknown type → `{ status: "Error", code: "UnknownType", message: "Unknown typeName 'VarChar(120)'. Did you mean 'Character(120)'?", accepted: [...canonical list] }`. **Never default-to-Numeric silently.**

### C2. `genexus_modify_variable` (#6)

New tool. Signature: `name=<obj>, varName=<v>, typeName=<new>, basedOn?=<domain>`.

Implementation: locate variable, delete it, re-add with same name + new type, re-resolve any control bindings that referenced it by name. Atomic — rollback on any step failure. Works in **all** object kinds.

### C3. Symmetric `delete_variable` (#12)

Backend uses `PartAccessor.GetVariablesPart(obj)` which dispatches per object kind rather than hardcoding the part name `"DeleteVariable"`. Test matrix: Procedure, WebPanel, Transaction, WorkPanel, DataProvider.

### C4. Ghost-binding diagnostics (#6)

When delete/modify is rejected because GeneXus reports the variable bound to a control:

```json
{
  "status": "Error",
  "code": "BoundToControls",
  "message": "Variable &PareceresStatusLabel is bound to controls",
  "bindings": [
    { "location": "form", "controlId": "[var:64]", "controlName": "ParecerStatusLbl", "line": null },
    { "location": "event", "controlId": "[var:64]", "controlName": "ParecerStatusLbl", "line": 142 }
  ]
}
```

`[var:N]` resolved to symbolic name by scanning Variables part. Unresolved → `[var:64 (unresolved)]`.

### C5. Patch-window-only rollback verification (#13, #6)

When persisted XML differs from requested XML, rollback fires only if the **divergence is inside the edited window**. Normalizations on untouched lines (`DATETIME(10,5)→(8,5)`, `Messages, GeneXus.Common → Messages`) are surfaced in `_meta.sideEffectNormalizations[]` and the write **succeeds**.

Implementation: track edit window line range in `WriteService`; diff source pre/post save, classify each hunk as `in-window` (rollback trigger) or `out-of-window` (side effect).

### C acceptance

1. `add_variable typeName="VarChar(120)"` returns `UnknownType` error with suggestion. Test asserts no variable was created.
2. `add_variable typeName="Character(120)"` succeeds.
3. `modify_variable` test: existing `&X NUMERIC(4)` → `CHARACTER(200)`, references still resolve, build clean.
4. `delete_variable` works in all 5 object kinds (parametrized test).
5. Bound-variable delete returns `BoundToControls` with resolved control names.
6. Edit on Variables with side-effect normalization succeeds (no rollback) and reports normalizations in `_meta`.

## Category D — Build pipeline / segmented refs (#7)

**Affected tools:** `genexus_lifecycle` (action=build), `LifecycleService.BuildSegmentedCsproj`, `CallerGraphService` (unified with A).

### D1. Auto-included callee references

`BuildSegmentedCsproj` algorithm:

1. For each object in `target`, query caller graph for transitive callees (capped at 200 nodes to prevent explosion in pathological KBs).
2. Resolve each callee to its DLL path: `<KB_OUTPUT_DIR>/<ModelName>/<Module>/<objectname>.dll`.
3. Emit `<Reference Include="objectname"><HintPath>$(OutputPath)\<objectname>.dll</HintPath></Reference>` per resolved callee.
4. Callees without a DLL yet (new objects in this build) are added to `target` and ordered topologically.

### D2. `lifecycle build` flag

New parameter `includeCallees: "none" | "direct" | "transitive"` (default `transitive`). `none` preserves the pre-v2.3.8 behaviour for debugging.

Response gains `_meta.buildPlan`:

```json
{
  "targetExpanded": ["ComissaoLiberaPareceres", "ComissaoAgendaDetalhe", "ComissaoPautaRelatorio", "..."],
  "callees": [
    { "name": "ComissaoGerarAta", "dllPath": "...", "resolved": true },
    { "name": "ComissaoBlockAgenda", "dllPath": null, "resolved": false, "reason": "Not yet built" }
  ],
  "skipped": []
}
```

When expansion exceeds the 200-node cap: `{ status: "BuildPlanTooLarge", suggested: "Build All from IDE", graph: { nodes: N, depth: D } }`.

### D acceptance

1. `LifecycleServiceTests.SegmentedBuild_WebPanelWithProcedureCalls_ResolvesAllRefs` — fixture WebPanel calls 3 procedures; generated csproj contains all 3 `<Reference>` tags.
2. The session's failing target (20 objects, WebPanel + 19 procedures) builds clean with `includeCallees=transitive`.
3. `includeCallees=none` reproduces the pre-fix CS0246 failure (regression-prevention test).
4. Docstring updated; CHANGELOG notes the new default.

## Category E — Output size & UX (#9, #10, #14)

**Affected tools:** `genexus_lifecycle` (status), `genexus_read`, `JobRegistry`.

### E1. `lifecycle status compact=true` default

Default response shape:

```json
{
  "status": "Failed",
  "errorCount": 30,
  "warningCount": 24,
  "errors": [...first 10 distinct, full detail...],
  "warnings": [{ "message": "GAM não será reorganizado", "count": 6, "sampleLocations": [...] }, ...],
  "summary": "30 errors / 24 warnings",
  "truncated": true,
  "rawAvailableViaJobId": "abc-123"
}
```

Errors appear **once** (the top-level `Errors[]` array). `result.Errors` and `result.Output` removed from the default envelope. `compact=false` keeps the legacy raw payload for local debugging.

Warning dedup key: exact message match. Sample locations cap at 3.

### E2. `genexus_read` default pagination

When a part exceeds `200 lines` or `16 KB`, response returns first page plus:

```json
{
  "truncated": true,
  "totalLines": 1240,
  "totalBytes": 55304,
  "suggestedNextOffset": 200,
  "suggestedNextLimit": 200
}
```

`limit=0` opts out (caller assumes risk). First call **never** errors due to overflow.

### E3. `background_jobs` dedup (#14)

`JobRegistry` tracks `notified: bool` per job. On the first response after a job completes, `_meta.background_jobs` includes the completion notification; subsequent responses omit it. Active (running/pending) jobs continue to appear until terminal.

### E acceptance

1. Build-failure status response < 8 KB for a 30-error build (vs ~60 KB before).
2. `read part=Events` on `ComissaoAgendaDetalhe` (55 KB) returns first page + `truncated:true` on first call; no error.
3. Job completion notification appears exactly once across the session (transcript-replay test).

## Category F — Error/i18n consistency + cancel (#15, #16)

**Affected tools:** `Helpers/ErrorMessages.cs` (new), `Helpers/WebFormSchemaHints` (var binding resolver), `JobRegistry`, `WorkerCommandDispatcher`.

### F1. Canonical EN error messages

New `Helpers/ErrorMessages.cs` centralizes translation. Table-based lookup of common GeneXus SDK PT-BR prefixes:

| GX SDK (PT-BR) | Canonical (EN) |
|---|---|
| `"A validação de Web Panel '%s' falhou"` | `"Web Panel '%s' validation failed"` |
| `"Referência de controle inválida"` | `"Invalid control reference"` |
| `"'Vazio' não é um valor válido para a propriedade 'Control Name'"` | `"'Empty' is not a valid value for property 'Control Name'"` |
| `"não será reorganizado"` | `"will not be reorganized"` |

Original SDK message preserved in `_meta.sourceMessage` for forensics. Punctuation lint: collapse `..` → `.`, normalize trailing whitespace.

### F2. `[var:N]` resolver

Pre-render hook `WebFormSchemaHints.ResolveVarBindings(message, obj)`:

1. Regex-match `\[var:(\d+)\]` in messages.
2. For each match, scan Variables part of `obj` for entry with internal id `N`.
3. Substitute `[var:64]` → `&PareceresStatusLabel`. Unresolved → `[var:64 (unresolved)]`.

### F3. `lifecycle action=cancel` works (#16)

`JobRegistry.Cancel(jobId)` propagates a `CancellationToken` to the `WorkerCommandDispatcher`. Long-running operations check the token at yield points:

- `SourceSearchService` — between regex iterations on each indexed entry.
- `AnalyzeService` — between caller-graph nodes.
- `LifecycleService` (build) — between MSBuild target completions.

Cancel response: `{ status: "Cancelled", partialResultJobId: <id-of-partial-result-if-any> }`.

### F acceptance

1. Snapshot test of error messages: every error envelope produced by Worker is checked against the canonical EN table; no PT-BR leak.
2. `[var:N]` resolved in all messages mentioning bindings; fixture verifies.
3. Cancel test: dispatch `search_source` on a fixture KB, send `lifecycle cancel job_id=X` after 500ms, assert response < 2s and `status: "Cancelled"`.

## Out of scope (non-objectives)

- GeneXus runtime quirks (`spc####` codes, KB-side validation rules).
- Build All parallelization / multi-core MSBuild orchestration.
- New object types or new GeneXus features.
- v2.4 architectural refactors (collapsing `filter` legacy param; deferred).

## Compatibility & migration

These default changes are **observable** to existing callers:

| Change | Risk | Mitigation |
|---|---|---|
| `lifecycle status` default `compact=true` | Caller parsing `result.Errors` or `result.Output` will see them gone | Document in CHANGELOG breaking section; `compact=false` restores legacy |
| `genexus_read` default pagination | Caller assuming full content on first call hits `truncated:true` | `limit=0` opt-out; clear in docstring |
| `lifecycle build` default `includeCallees=transitive` | Caller relying on segmented-only build (rare given current breakage) | `includeCallees=none` flag |
| Error messages switch PT-BR → EN | Caller string-matching on PT-BR breaks | Source message preserved in `_meta.sourceMessage` |

CHANGELOG v2.3.8 includes a `### Breaking notes` subsection covering the four above.

## Test gate

- All 154 Worker + 211 Gateway existing tests stay green.
- Minimum one new test per item #N (16 items → ≥16 new tests). Categories C, D, E warrant ≥3 each.
- `ToolSchemaSizeTests` budget raised once more if needed (target ≤ 4800 tokens).
- New fixture KB `tests/fixtures/friction-2026-05-15-kb/` minimal but containing: 1 WebPanel calling 3 procedures (for D), 1 Procedure with a variable bound to a control (for C4), one folder with children (for A3).

## Implementation order (preview for plan)

The plan (next skill: `writing-plans`) will sequence these. Suggested order, dependency-aware:

1. **A1, A4** (index state + caller graph unification) — foundation for D.
2. **A2, A3** (search/list discovery).
3. **B** (edit reliability).
4. **C** (variable lifecycle) — depends on B4 (rollback hash).
5. **D** (build refs) — depends on A4 (caller graph).
6. **E** (output size).
7. **F** (error/i18n + cancel).

Each category lands as one PR/commit batch with the new test fixtures co-located.
