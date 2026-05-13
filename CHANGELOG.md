# Changelog

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

### Verified already-closed
- **Variables-part patch mode** has worked since `e10d382` —
  `PatchService.ReadSourceFast` exposes the DSL via `GetVariablesAsText` and
  the write back routes through `WriteService.WriteObject` →
  `SetVariablesFromText`. Friction-report #5 closure noted in
  `docs/mcp-friction-report-2026-05-08.md`.

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
