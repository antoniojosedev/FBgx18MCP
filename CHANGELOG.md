# Changelog

## v2.3.0 — 2026-05-14

Multi-KB parallel support. One Gateway can now drive up to `Server.MaxOpenKbs`
(default 3) concurrent KBs, each in its own Worker process. Cross-KB tool
calls run in parallel — no serialization between KBs. Intra-KB calls remain
serialized by the SDK's STA constraint, as before.

### Added
- **`WorkerPool`** (Gateway) — keyed by KB alias, LRU eviction when pool full,
  idle timeout reuses existing `WorkerIdleTimeoutMinutes`.
- **`KbResolver`** — maps `kb` tool arg (alias OR absolute path) to a
  `KbHandle`. Default-KB fallback: 1 KB open → uses it; 0 open + `DefaultKb`
  configured → opens it; 2+ open without `kb` → `KB_AMBIGUOUS` error.
- **`kb` parameter** on every non-meta tool (28 tools). Optional; required
  when more than one KB is open.
- **`genexus_kb` meta-tool** — `action: list | open | close`. List shows
  open KBs, configured `DefaultKb`, declared aliases, and `MaxOpenKbs`.
- **Config schema:** `Environment.KBs[]` (alias+path) and
  `Environment.DefaultKb`; `Server.MaxOpenKbs` (default 3).
- **Backward-compat:** legacy `Environment.KBPath` auto-migrates to a single
  `KBs[]` entry + `DefaultKb` at load time. Existing configs work unchanged.

### Changed
- `WorkerProcess` constructor now takes `(Configuration, KbHandle)`.
- `KbService` static fields (`_kb`, `_kbLock`, `_isOpenInProgress`) become
  instance fields — each Worker process holds one isolated KbService.
- Idempotency cache is now scoped by the resolved KB path (was previously
  the single `Environment.KBPath`).

### Internal
- `AsyncLocal<KbHandle?>` resolves the active KB at the top of
  `ProcessMcpRequest` and propagates to `SendWorkerCommandAsync` without
  threading new parameters through 7 call sites.

Spec: `docs/superpowers/specs/2026-05-14-multi-kb-parallel-design.md`.
Plan: `docs/superpowers/plans/2026-05-14-multi-kb-parallel.md`.

## v2.2.0 — 2026-05-13

Coordinated perf & stability release closing the tools-disappear-mid-session
bug and reducing roundtrips/payload across the MCP surface. All 13 changes
gated behind a single feature flag `MCP_PERF_PROFILE=v1` (default on).
Env-flip to `legacy` restores pre-v2.2.0 behavior. Total test count grew
from 135 → 199, all green.

### Polish (post-smoke-verification)
- **Piggyback injection layer fix.** `_meta.background_jobs` now injects
  into the inner `content[0].text` payload (which the LLM actually
  reads), not the JSON-RPC wrapper. Async build completions surface on
  the next tool response as designed.
- **Long-poll status accepts `target` as `job_id` fallback.** The
  `lifecycle status` tool conventionally takes `target`; LLMs and users
  pass the job ID there. Registry is probed first; legacy taskId-based
  status falls through unchanged when the value isn't a registered job.
- **`type` alias for `typeFilter` in list/query/search.** The
  `genexus_list_objects` / `genexus_query` / `genexus_search_source`
  routers now accept both names. Aligns with the rest of the tool
  surface where `type` is the conventional parameter name.

Spec: `docs/superpowers/specs/2026-05-13-mcp-perf-and-tool-stability-design.md`.
Plan: `docs/superpowers/plans/2026-05-13-mcp-perf-and-tool-stability-v2.2.0.md`.

### Fixed
- **Tools-disappear-mid-session bug** (`docs/issues/tools-disappear-mid-session.md`)
  — gateway-side `ResponseSizeGuard` caps per-tool payloads at ~220KB
  (≈55k tokens) before the harness-side truncation path can drop the
  tool registry. Payloads over the cap are replaced with a sentinel
  `_meta.truncated: {reason, original_size, cap_bytes, follow_up: {tool, args}}`
  pointing at a paginated continuation. Telemetry log line
  `[Gateway] OVERSIZE tool=X size=N` for one-release calibration.
- **`SystemRouter` "result" routed to "Status" instead of "Result"** —
  pre-existing routing bug surfaced and fixed during pagination work.

### Added (perf profile v1, default on)
- `genexus_lifecycle action=status` / `action=result` accept `page` /
  `page_size` (default 50, max 200); responses carry
  `_meta.pagination: {total, page, page_size, has_more}`.
- `genexus_edit` returns `post_state.diff` (LCS-based unified diff with
  `±3` context) by default — eliminates the re-read-to-verify turn.
  `verbose=true` adds wider slices; `return_post_state=false` opts out.
  Wired across ops, JSON-patch, and text-patch edit modes.
- `genexus_lifecycle action=build` / `rebuild` is non-blocking when
  `estimated_seconds ≥ BuildSyncThresholdSeconds` (default 20) — returns
  `{job_id, status: "running", estimated_seconds, hint}` immediately.
  Short builds use a synchronous fast-path returning the result in one turn.
- `_meta.background_jobs: [...]` piggybacks on every tools/call response
  when a session's `BackgroundJobRegistry` has running jobs or unseen
  completions. LLM can do other work while a build runs and discovers
  completion on the next tool call.
- `genexus_lifecycle action=status` with `wait_seconds=N` (clamped to
  [0, 25]) long-polls server-side until the job reaches terminal state
  or the timeout. One call instead of polling loop.
- Discovery tools (`list_objects`, `query`, `structure`, `search_source`)
  include `_meta.suggested_next: {tool, args}` pointing at the natural
  next call.
- List responses include `_meta.aggregates: {total, by_type}` computed
  during the same scan — eliminates "how many of X" follow-up calls.
- Empty results carry `_meta.empty_reason`: `no_matches` | `filtered_out`
  | `kb_not_loaded`.
- `genexus_read` accepts `parts: [...]` — surgical reads of named
  sections (Source, Variables, Rules, etc.). Backward compatible.
- `genexus_list_objects` and `genexus_query` accept `inline_read_top: 0-3`
  (default 0) — combined list-and-read returns `inline_reads: [{name, content}]`
  for the top N matches in one turn.
- Compact JSON output on tools/call responses: `Formatting.None` plus a
  recursive `StripNulls` pass that drops null properties while preserving
  empty arrays, zeros, false, and empty strings.

### Changed
- List items default to a minimal 4-field shape (`name`, `type`, plus
  two context fields like `path`/`parent`). Pass `verbose=true` to get
  the full per-item shape.
- Errors default to terse `{code, message, hint}` — stack traces and
  full SDK diagnostics dropped from the wire by default. Pass
  `verbose_errors=true` per-call, or fetch from `genexus_logs`, for
  full diagnostics.
- `tool_definitions.json` trimmed from ~9,600 tokens to ~2,800 tokens
  (71% reduction) — every conversation pays less for the fixed tool
  schema in the system prompt. All 32 tools preserved.

### Deferred
- TOON serialization (see spec open question). Revisit after one
  release of telemetry on what tokens are actually spent on.
- Real MCP `notifications/progress` for builds — same broadcast path
  is the leading suspect for the disappear-bug. Revisit once
  `ResponseSizeGuard` calibration data confirms or rules out that
  hypothesis.

### Rollout / Compatibility
- All changes additive on `_meta` or opt-in parameters. No changes to
  `tools/list` or `notifications/tools/list_changed` semantics.
- Existing callers that don't read the new `_meta` fields continue to
  work unchanged.
- Set `MCP_PERF_PROFILE=legacy` to restore pre-v2.2.0 behavior at the
  process level (single env-flip kill switch).

## Unreleased

Closes every item from the second-cycle friction report
`docs/mcp-friction-report-2026-05-13.md`, produced by a fresh real-KB session
against `AcademicoHomolog1`. Pending live smoke verification before the next
release tag.

### Fixed
- **`whoami.mcp.serverVersion` reads from the assembly version, not a hardcoded
  const.** `McpRouter.ServerVersion` now resolves at runtime via
  `AssemblyInformationalVersionAttribute` (set from the csproj `<Version>`).
  `scripts/release.ps1` mirrors the bumped npm version into the Gateway csproj
  so the version surface always matches the published build. Friction-report
  05-13 #1.
- **SDT Structure write now persists fully: parser dirty-flags every signal
  the SDK exposes, sync-commits Model + KB to disk, propagates the SDT to
  the Prototype model in SQL, and the validator no longer rejects multi-
  write sequences.** Four layers together close the bug:
  1. `SdtDslParser.Parse` reflects `Dirty/IsDirty` + `Touch/Modified/
     MarkDirty/OnChanged/NotifyChanged` onto `SDTStructurePart` and logs
     items-count pre/post-parse so the persisted state is unambiguous.
  2. `WriteService` Structure interceptor forces a synchronous
     `Model.Commit + KB.Commit` immediately after `EnsureSave` (instead of
     the debounced 2-second timer), so a follow-up save sees the new items
     on disk.
  3. `SdtModelPropagation.TryPropagateToPrototypeModel` mirrors Model 1 →
     Model 2 rows for the SDT, SDTStructure, SDTLevelEntity, and
     SDTItemEntity via direct SQL (decompresses the structure blob to
     discover the item EntityIds). Same surgical pattern as
     `WebFormCompositionRepair` (`9242c1d`); needed because
     `KBObject.Create(kb.DesignModel, ...)` never registers the item names
     in the Prototype model the validator queries.
  4. `PersistenceExtensions.EnsureSave` now reflects on
     `Artech.Architecture.Common.Objects.KBObjectSavePreferences`
     (walking loaded assemblies, since the type lives in
     `Artech.Architecture.Common`, not the KBObject's home assembly),
     sets `SkipValidation=true`, and retries `KBObject.Save(prefs)` only
     when the failure text contains `src0216`. This bypasses the SDK's
     stale in-process Prototype-model cache for the legitimate case
     (variable declared, SDT item present in Model 1) while leaving
     genuine validation errors (`src0059` syntax, undeclared variables —
     covered by the new hint in fix #3) untouched.

  Verified end-to-end by `scripts/smoke_2026_05_13.ps1`: a Procedure that
  binds `&Aluno : SdtFrictionProbe`, writes Source `&Aluno.AluCod = 42`,
  then patches Variables with `&Counter : NUMERIC(4,0)` — the original
  report's exact failure mode — now persists clean
  (`persistedVerified=true, patchStatus=Applied`). Worker log records
  `[EnsureSave] bypassed src0216 stale-prototype-model validator via
  SkipValidation=true`. Friction-report 05-13 #2.
- **`src0216 'X' propriedade inválida` is enriched with an "undeclared
  variable" hint when the SDK message points at `&Var.X` and `&Var` isn't in
  the part's Variables collection.** `WritePolicy.FindUndeclaredVariablesForSrc0216`
  cross-references the SDK error against the source text and the declared
  variables; the error response now carries `hint` + `undeclaredVariables[]`
  so the agent reaches for `genexus_add_variable` instead of "fix the field
  name on the SDT". Friction-report 05-13 #3.
- **Variables patch verify no longer false-fails on `NUMERIC(N,0)` round-trip
  drift.** `PatchService.NormalizeForPartCompare` now canonicalizes each
  Variables line: collapses internal whitespace and strips trailing `,0)`
  decimals so `&Counter : NUMERIC(4,0)` (agent-written) and `&Counter :
  NUMERIC(4)` (SDK-rendered after persist) compare equal. Without this, the
  v2.1.6 `&Counter` smoke triggered auto-rollback even though persistence had
  succeeded. Friction-report 05-13 #4.
- **`genexus_lifecycle action=build` echoes the parsed `targets` array even
  for single-object builds.** Previously `targets` was null when `Count == 1`,
  contradicting the doc contract. Single and batch builds now both surface
  the resolved list. Friction-report 05-13 #5.
- **MSBuild output streams use the console's actual encoding instead of UTF-8.**
  `BuildService` now sets `StandardOutputEncoding`/`StandardErrorEncoding` to
  `Console.OutputEncoding` (CP850/CP1252 on PT-BR Windows, UTF-8 if `chcp
  65001` is active), so `TailLines` no longer surfaces `Compila��o` /
  `n�`-style mojibake to the agent. Friction-report 05-13 #6.
- **`genexus_inspect include=["structure"]` surfaces SDT items as
  `sdtStructure`.** The block walks `SDT.Root.Items` via reflection and
  produces `{itemCount, levelCount, items:[{name, type, length, decimals,
  isCollection, isLevel, children?}]}`. Agents inspecting an SDT no longer
  see an empty `uiStructure: {}` and have to fall back to `genexus_read
  part=Structure` for basic metadata. Friction-report 05-13 #7.
- **`genexus_create_object` for SDT/Transaction announces auto-seeded
  payload via `_meta.seeded`.** Response now carries
  `{_meta:{seeded:["Item1 : VARCHAR(40)"], seededHint:"…overwrite via
  genexus_edit part=Structure…"}}` for SDT (and the equivalent Numeric key
  hint for Transaction). Agents that immediately populate the structure no
  longer get surprised by the seed item showing up in round-trip reads.
  Friction-report 05-13 #8.

## v2.1.6 — 2026-05-13

Closes the remaining open items in `docs/mcp-friction-report-2026-05-08.md`
(#2, #3, #4, #5, #6, #9a, #9b). v2.1.4 and v2.1.5 shipped the WebForm-write
composition-pointer fix; this release wraps up the rest of the friction tail.

### Fixed
- **Bare `"Erro"` write failures now surface the real SDK diagnostic.** When
  `obj.Save()` threw `"Erro"` without populating `OutputMessages`,
  `genexus_edit mode=full` returned `{"error":"Erro","line":1}` while
  `mode=patch` surfaced the actual `src0059: Esperando 'EndFor'...`. Both
  write paths now consult `SdkDiagnosticsHelper.GetDiagnostics(obj)` and
  `part.GetSdkMessages()` before falling back; the bare exception text is
  preserved under `originalError` when enrichment fires. Friction-report #2.
  (commit `a2a70cc`)
- **SDT auto-inject no longer creates wrong-typed VARCHAR(100) fallbacks.**
  When the source used `&Var.Field` and no SDT/BC name resolved, the
  injector previously fell through to the VARCHAR(100) default, poisoning
  later validation. It now skips injection so the agent gets a clean
  "undeclared variable" signal and can call `genexus_add_variable
  typeName=<SDT>` explicitly. Friction-report #3. (commit `3dadeb2`)
- **Variables DSL emits the bound SDT name instead of `GX_SDT(4)`.** The
  read-side resolver now probes `ATTCUSTOMTYPE` (where `BindVariableToSdt`
  actually persists the structural reference) when the `DataTypeString`
  fast-path is unavailable, so `&Foo : SdtFoo` surfaces correctly.
  Friction-report #4. (commit `3dadeb2`)
- **Patch post-write verification reads from a forced cache miss.**
  `VerifyPersistedSource` now drops both `_sourceCache` and
  `ObjectService._readCache` before its verify read, eliminating false
  `persistedVerified=true` reports when the verification read hit a stale
  cache entry. Friction-report #6. (commit `9d0394e`)
- **`read part=TableStructure` returns the column DSL.** The structure-alias
  dispatch in `ObjectService.ReadObjectSourceInternal` used a literal
  `GetType().Name == "Table"` string check; subclassed/proxied Table
  instances fell through to the generic `part.SerializeToXml()` path and
  returned `<Properties />`. Now tests via `obj is Table` plus a
  `TypeDescriptor.Name` check, so the existing `TableDslParser` runs.
  Friction-report #9b. (commit `482bf48`)

### Changed
- **`genexus_query` auto-index nudge** surfaces under `_meta.autoIndexed` +
  `_meta.indexStatus` (`starting` | `scanning` | `empty`), mirroring the rest
  of the tool surface. The empty-index case now also kicks off the bulk
  index instead of erroring out with `"Index empty."`. Friction-report #9a.
  (commit `085b9e0`)

- **Variables-part patch mode now persists and verifies correctly.** Live
  smoke against AcademicoHomolog1 caught two write-side bugs that the
  earlier "read side works since e10d382" assessment missed:
  (a) `SetVariablesFromText` aliased `Character → VARCHAR`, so a Variables
  patch round-tripped `&Time : CHARACTER(8)` as VARCHAR(8) and the auto-
  rollback compounded the data loss; (b) the SDK's VariablesPart collection
  inserts new vars at the FRONT, so the patch's line-by-line verify rejected
  semantically-equivalent persisted state. Removes the lossy alias and
  introduces `NormalizeForPartCompare` (set-based equality on Variables,
  strict ordering elsewhere). Friction-report #5 write side. (commit on
  top of `085b9e0`)

## v2.1.3 — 2026-05-12

Hardening release for MCP protocol compatibility, release verification, and cache/idempotency correctness.

### Changed
- Gateway, smoke scripts, docs, and Nexus IDE now use `MCP-Protocol-Version: 2025-11-25`.
- `genexus_query` result caching now uses a bounded LRU cache instead of an unbounded dictionary.
- CI now runs Gateway tests with isolated output, Worker tests when the GeneXus SDK is present, and Nexus IDE compile/tests.
- `scripts/test_all.ps1` now runs .NET tests with isolated output before the live MCP smoke.

### Fixed
- First successful write with `idempotencyKey` no longer reports `meta.idempotent=true`; only cache hits do.
- `genexus_edit(dryRun=true)` now warns when impact analysis is unavailable so `brokenRefs` is not mistaken for complete.

## v2.1.2 — 2026-05-12

Friction-fix release. Closes all 10 items from a real debug session report (`docs/issues/melhorias.md`), plus pulls in the build pipeline work that was on `main` but never tagged.

### Added
- **`genexus_search_source`** — semantic call-search across Procedure / DataProvider / WebPanel / Transaction source. Match by `callee` (qualified `DPParametros.Udp` or unqualified `Udp`) and optional positional `argMatches` (e.g. `{"0":"373"}`), or by regex `pattern`. Both can combine. Returns hits with line numbers, surrounding context, and resolved call args. Implemented via a new in-process `SourceParser` (no SDK dependency; tested directly). (#7)
- **`genexus_get_sql_for_navigation`** — emits SQL from a procedure/DP's resolved For Each navigation. One `SELECT` per Level with `:VarName` bind placeholders where the source uses `&Vars`. Warnings field reports levels where the OptimizedWhere couldn't be translated. Useful for cross-environment comparison. (#10)
- **`genexus_inspect` `include=["navigation"]`** — opt-in surfacing of resolved navigation (base table, indexes, filters) on inspect, alongside existing parts. (#5)
- **`genexus_inspect` on Attribute** — response now includes `tables: [...]` listing the physical tables that host the attribute. (#2)
- **`genexus_inspect` on DataProvider** — response now includes `returnsSDT` and `readsFromTables`. (#8)
- **`genexus_get_sql`** — always returns `subordinatedTables: [...]` for Transactions with Levels. New optional flag `includeSubordinated: true` adds `subordinatedDDL: { name: ddl }` for each subordinated table in one call. (#1)
- **Build pipeline streaming + batch builds + `ForceRebuild`** (from previously-untagged work on `main`): `genexus_lifecycle` streams MSBuild output line-by-line and exposes `Phase` / `CurrentObject` / `ErrorCount` / `WarningCount` / `LineCount` / `LastLine` / `TailLines` / `Errors[]` / `Warnings[]` / `ElapsedSeconds` via `action='status'`. `action='build'` accepts a comma- or semicolon-separated `target` list and runs all `BuildOne` tasks inside a single MSBuild + OpenKB cycle. `ForceRebuild=true` is now emitted on every `BuildOne` (mirrors the IDE's "Build With These Only"). `action='cancel'` kills a runaway build. Single-target builds surface `callersToAlsoBuild` for the next batch.
- **GeneXus version detection fallback** — when `version.txt` is absent, the gateway reads the major version from `GeneXus.exe`'s `FileVersionInfo`.
- **WebForm read** — `genexus_read part="webform"` reads the active WebForm tree.

### Fixed
- `isTruncatedByWorker` and the "MCP defaulted to 200 lines" message now appear only when the read was actually truncated. Small files come back with `isTruncatedByWorker: false` explicitly. (#9)
- Procedure / Transaction / WebPanel / DataProvider parameter types are resolved from the object's Variables part instead of returning `"Unknown"`. SDT-typed parameters surface their SDT name. (#6)
- `usedby:Attribute` resolves consumers via the inverted `CalledBy` index instead of the lexical paths that never matched attributes. Legacy lexical paths preserved for `usedby:Table` / `usedby:Procedure`. (#3)
- `genexus_query` with `typeFilter=Table` and attribute-name terms now boosts the table that contains those attributes (`+5000` instead of `+400`), instead of letting lexical similarity in unrelated table names win. (#4)
- Gateway no longer caches `genexus_lifecycle action='status'|'result'|'cancel'` or `genexus_logs` — these always reflect live worker state. Fixes the "status frozen" symptom.

## v2.1.0 — 2026-05-11

### Added
- **`genexus_whoami` MCP tool** — gateway-served (no worker boot needed) tool returning the active KB (name, path, exists, validity), GeneXus installation (path, detected version, target major match), MCP server/protocol versions, and config source. Use this as the AI's first call to confirm context.
- **Edit validation with did-you-mean** — `genexus_edit` now validates `mode` against `{xml, ops, patch, full}` and `ops[i].op` against the SemanticOpsService canon at the gateway, returning `UsageException` with Levenshtein-based suggestions (e.g., `patche` → `patch`, `set_atribute` → `set_attribute`) before the call ever reaches the worker.
- **GeneXus version check on boot** — gateway reads `version.txt`/`Version.txt`/`GeneXus.version` from `InstallationPath` and logs a warning if the detected major differs from the supported `18`.
- **`genexus-mcp whoami`** CLI command — same shape as the MCP tool, queryable from the shell.
- **`genexus-mcp uninstall`** — reverts AI client configs, deletes `%LOCALAPPDATA%\GenexusMCP\`, and removes local `config.json`. Interactive confirmation by default; `--yes` for scripts.
- **`genexus-mcp kb` multi-KB catalog** — `kb list`, `kb add --name --kb`, `kb remove --name`, `kb switch --name|--kb`. Stored in `Environment.KBs` + `Environment.ActiveKb`; legacy `Environment.KBPath` is kept in sync so the worker requires no changes.
- **`genexus-mcp init` zero-config + post-init verification** — auto-discovers GeneXus from the Windows registry (HKLM/HKCU under `Artech\GeneXus 18/17/16`) and Program Files, and the KB from the current directory; runs `doctor --mcp-smoke` at the end of `init` and reports a verification summary (use `--no-smoke` to skip in CI).
- **`genexus-mcp init --warm`** — pre-spawns the gateway after install so the first AI prompt skips the 3–8s worker cold-start.
- **Docs** — README rewritten around the new-user flow (prerequisites → 3-step quickstart → first prompts); new `TROUBLESHOOTING.md` covering the 7 most common install issues; new `docs/GETTING_STARTED.es.md` for Spanish-speaking users.

### Changed
- **`tool_definitions.json`** — clearer "use when / DON'T use when" guidance on the 4 most-ambiguous tools (`genexus_inspect`, `genexus_analyze`, `genexus_summarize`, `genexus_doc`) with cross-references to disambiguate against `genexus_read` / `genexus_explain_code`.

## v2.0.4 — 2026-05-09

### Added
- `package.json` now declares `mcpName: "io.github.lennix1337/genexus"` (verification marker for the official MCP Registry).
- `server.json` at repo root — metadata for submission to https://registry.modelcontextprotocol.io.

## v2.0.3 — 2026-05-09

### Fixed
- CI: `GxMcp.Gateway.csproj` now copies `config.sample.json` (linked as `config.json`) instead of the gitignored `config.json`. v2.0.1 and v2.0.2 release workflows failed at the build step for this reason and never reached the npm publish stage; this release ships the SEO content (keywords, README) and the v2.0.1 worker hardening together.

## v2.0.2 — 2026-05-09

### Changed
- Discoverability / SEO: `package.json` now ships a `keywords[]` array (mcp, model-context-protocol, genexus, genexus-18, claude, cursor, ai-agent, low-code, …) and an expanded description for npm search.
- README: SEO-tuned H1, added npm version/downloads badges, added explicit search-keyword list, and an opening paragraph that names the supported clients (Claude Desktop, Claude Code, Cursor) and the object kinds the agent can manipulate.

## v2.0.1 — 2026-05-08

### Fixed
- `WriteService` SDK transactions are now finalized in a `finally` block (Commit/Rollback/Dispose), preventing leaked transactions when commit-stage failures cascade into rollback-throws.
- `KbWatcherService` no longer polls `DesignModel.Objects` mid-write. Writers acquire a shared gate (`AcquireWriteGate`) and the watcher skips its tick while a save is in flight — eliminates intermittent generic "Erro" messages caused by SDK collection races.
- `PatchService` auto-rollback: when a fallback write reports success but verification mismatches, the original source is restored instead of leaving the file with the matched context deleted and the replacement missing (data loss).
- `PropertyService` now wraps `SetPropertyValue` + `EnsureSave` + `Commit` in try/finally with explicit `Rollback` on failure, and surfaces the underlying setter exception in error messages.
- `SdkDiagnosticsHelper.CreateIssueFromSdkMessage` switched from `dynamic` (RuntimeBinderException-per-miss, slow + lossy) to reflection with a per-`(Type, name)` accessor cache. Codes like `src0216` now reach the agent intact.
- SDT field access now compiles: `WriteService` binds variables to SDTs via `ATTCUSTOMTYPE`.
- `KBObject.Delete()` replaces `Objects.Remove()` (latter does not delete from the design model).

### Added
- `genexus_inspect` accepts `include=["controls"]` / `include=["events_repertoire"]` to enumerate WebForm controls and the events each control type accepts (cuts trial-and-error on event-name mistakes).
- `InferSuggestion` heuristics for `src0216`-style "invalid property" errors on unbound variables, and "not a valid event" errors on controls.

### Changed
- `config.json` is now gitignored. Use `config.sample.json` as a template and copy it locally.
- Scratch/debug artifacts under `scripts/_*` are gitignored.

## v2.0.0 — 2026-04-29

### Breaking changes
- Removed `genexus_batch_read`. Use `genexus_read` with `targets[]`.
- Removed `genexus_batch_edit`. Use `genexus_edit` with `targets[]`.
- Removed `genexus_edit` `changes` argument. Use `targets[]`.
- `meta.schemaVersion` bumped from `mcp-axi/1` → `mcp-axi/2`.
- Calls to removed tools return JSON-RPC `-32601` with `error.data.replacedBy` and `error.data.argHint` for agent self-correction. `initialize` advertises `_meta.removedTools` for proactive detection.

### Added
- `genexus_read` and `genexus_edit` accept `targets[]` plural form (mutually exclusive with singular `name`).
- `genexus_edit` `mode: ops` with semantic op catalog (`set_attribute`, `add_attribute`, `remove_attribute`, `add_rule`, `remove_rule`, `set_property`).
- `genexus_edit` `mode: patch` accepts a JSON-Patch (RFC 6902) array over canonical JSON object representation. Existing string-form `patch` (text/heuristic patch) still routes to `PatchService` for backward compatibility.
- `dryRun: true` on `genexus_edit` returns a standardized envelope `{meta:{dryRun, schemaVersion}, plan:{touchedObjects, xmlDiff, brokenRefs, warnings}}` without mutating the KB. (`brokenRefs` is currently always `[]`; the analyzer seam exists for a future enhancement.)
- `idempotencyKey` argument on write tools (`genexus_edit`, `genexus_create_object`, `genexus_refactor`, `genexus_forge`, `genexus_import_object`). Per-KB LRU cache with sliding TTL. Defaults: 15 min TTL, 1000-entry capacity. Configurable via `Server.IdempotencyTtlMinutes` and `Server.IdempotencyCacheSize`. Successful results cached; errors not cached. `dryRun` bypasses cache. Concurrent calls with the same key are coalesced.
- `_meta.idempotent: true` on cache-hit responses; `_meta.batched: true` on `targets[]` responses; `_meta.dryRun: true` on dry-run responses.
- `docs/object_json_schema.md` documents the canonical XML↔JSON mapping used by JSON-Patch mode.

## 1.1.7 - 2026-04-10

- Added protocol-first LLM bootstrap surfaces:
  - MCP resource `genexus://kb/llm-playbook`
  - MCP prompt `gx_bootstrap_llm` (now supports optional `goal`)
  - AXI CLI command `genexus-mcp llm help`
- Hardened MCP/AXI contract behavior for agent usage:
  - Stable list normalization for array payloads
  - Timeout responses with actionable `operationId` follow-up
  - Additional contract tests for resources/prompts/operation tracking
- Improved tool discovery descriptions for key tools (`query`, `list_objects`, `read`, `edit`, `lifecycle`) with more actionable guidance.
- Added automated LLM contract smoke:
  - `scripts/mcp_llm_contract_smoke.ps1`
  - CI workflow `.github/workflows/ci.yml` running CLI tests, gateway tests, and LLM smoke.
- Packaging hygiene:
  - Added `.npmignore` to exclude runtime logs/transient cache
  - Build now removes transient logs/cache from `publish` output
