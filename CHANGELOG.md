# Changelog

## v2.3.8 — 2026-05-15

Two waves into a single release. Wave 1 (morning) shipped the six new tools and
the deferred items from v2.3.7. Wave 2 (afternoon, this commit run) closed the
remaining friction-report items (Tasks 1.1 → 7.2) plus the warm-start IndexState
fix, worker-side cancel for search, broader ErrorMessages coverage, compact
through the long-poll path, and an end-to-end smoke test composing the workflow.

**Final test suite: 494/494 green** (267 Worker net48 + 227 Gateway net8). The
previously-flaky `IdempotencyCacheTests.Eviction_LruDropsOldestWhenAtCapacity`
is now deterministic against the sharded LRU contract.

### Wave 1 — new tools + deferred items

- **`genexus_validate_payload`** (`Services/ValidatePayloadService.cs`) — pre-flight
  check: parses the XML, runs `WebFormSchemaHints.ScanForRejectedAttributes`, and when
  the current state is readable, computes the would-be structural diff against the
  persisted XML. Returns `status: Valid|Warnings|Error`, `preflightWarnings[]`, and
  `diff` without touching disk.
- **`genexus_bulk_edit`** (`Services/WriteService.BulkWrite`) — apply N independent
  edits in one call. Each item supports `{name, part?, content, type?, dryRun?}`.
  `stopOnError=true` halts at the first failure; remaining items return
  `status: Skipped`. Response carries `counts: {success, failure, skipped}` and a
  per-item `results[]` array.
- **`genexus_apply_template`** (`Services/ApplyTemplateService.cs`) — three predefined
  visual templates: `kpi_header` (title + 3 KPI attributes), `empty_state` (bitmap +
  caption), `confirm_dialog` (confirm/cancel button pair with event wiring). Goes
  through the existing `WriteService.WriteObject` path so dryRun, validation, and
  rollback behaviour are inherited.
- **`genexus_diff`** (`Services/DiffService.cs`) — unified text diff via the existing
  `Helpers/DiffBuilder.UnifiedDiff`. Modes: `textVsText` (two caller-provided strings)
  and `currentVsText` (current persisted part vs. a caller string). Useful for PR
  review and pre-save comparison.
- **`genexus_export_unified`** (`Services/ExportObjectService.cs`) — full state of an
  object as a single JSON envelope: every available part read in one shot. Drives
  cross-snapshot diffs and PR-review artifacts.
- **`genexus_delete_variable`** (carried over from v2.3.7) — already shipped.

### New flags on existing tools

- **`genexus_analyze mode=linter fix=true`** (`Services/LinterService.LintAndFix`) —
  walks the lint report and auto-fixes GX008 unused vars via `DeleteVariable`. Skips
  framework-managed vars (GAM/WWP+) automatically. Other rules surface in `skipped[]`
  with a reason; the fixed set returns in `fixed[]`.
- **`genexus_edit async=true`** (Gateway `Program.cs` async-edit intercept) — writes
  longer than ~30 s now follow the same pattern as `genexus_lifecycle build`: register
  a `JobRegistry` entry, fire-and-forget the worker call, return
  `{job_id, status:"running", estimated_seconds, hint}` immediately. Completion piggybacks
  on the next response via `_meta.background_jobs`. Same flag honoured on
  `genexus_add_variable` and `genexus_delete_variable`.

### Worker

- **Indexed source-search pre-filter** (`Services/SourceSearchService.cs`) —
  extracts alphanumeric literal tokens (≥3 chars) from the regex/callee, then drops
  index entries whose `SourceSnippet`, `Name`, or `Keywords` contain none of them
  before paying for `FindObject`. On the friction-report's
  `pattern: "Alu2RegProf|Alu2NumRegProf"` example this skips 90%+ of the entries
  without changing the final result (regex.IsMatch still gates output).
- **PatternVirtual raw-serialize fallback** (`Services/ObjectService.cs`) — when
  `PatternAnalysisService.ReadPatternPartXml` returns empty, fall back to locating
  the matching part on `obj.Parts` by type-descriptor or CLR-type name and serialise
  via `KBObjectPart.SerializeToXml`. Surfaces the part as raw XML when WWP+'s
  analyser bails out, instead of the previous hard "Pattern XML not available".

### Documentation

- The four items previously marked "Deferred — needs deeper work" in v2.3.7 are now
  shipped:
  - True async writes ✓ (gateway `async=true` intercept)
  - Theme/StyleSheet read ✓ (already worked through the existing generic
    `SerializeToXml` fallback — discovery fixed by the `typesAvailable` hint in v2.3.7)
  - Index-backed `search_source` ✓ (literal-token pre-filter)
  - PatternVirtual read ✓ (raw-serialize fallback)

### Schema budget

- `ToolSchemaSizeTests` budget bumped 4000 → 4600 tokens to fit the six new tools
  (current `tool_definitions.json` ~4498 tokens).
- Bumped again 4600 → 4800 for `nameFilter`/`descriptionFilter`/`pathPrefix` on
  `genexus_list_objects` (Task 2.2), and 4800 → 5000 for `includeCallees` /
  `buildPlanCap` / `compact` on `genexus_lifecycle` (Tasks 5.2, 6.1).
  Current size ~4890 tokens.

### Wave 2 — friction-report 2026-05-15 sweep (Tasks 1.1 → 7.2 + 8)

Closes the remaining items in the friction report; all features below have
matching tests under `GxMcp.Worker.Tests/` and `GxMcp.Gateway.Tests/`.

- **Index state on `whoami`** (Task 1.2): live Cold/Reindexing/Ready surface;
  `IndexCold`/`Timeout` envelopes on `search` (Task 2.1) so callers can wait or
  fall back instead of silently getting empty hits.
- **Unified call-graph service** (Task 1.3): single `CallerGraphService` replaces
  duplicate BFS in `AnalyzeService.ImpactAnalysis`; new `waitForIndex` flag on
  `analyze impact` (Task 1.4).
- **Discovery filters** (Task 2.2): `list_objects` gains `nameFilter`,
  `descriptionFilter`, `pathPrefix`.
- **Edit reliability**: EOL-normalised matching (3.1), byte-level `nearMatchHint`
  (3.2), multi-line `{find,replace}` patch shape (3.3), `persistedHash` +
  `persistedSnippet` on every response (3.4), patch-window rollback verification
  (4.6 — only the diverging window forces rollback, SDK normalisations elsewhere
  are reported).
- **Variables**: `VariableTypeResolver` synonym map (4.1); `add_variable` validates
  `typeName` instead of falling back to NUMERIC (4.2); new
  `genexus_modify_variable` atomic type change (4.3); symmetric `delete_variable`
  across WebPanel/Transaction/etc. (4.4); ghost-binding diagnostics + `[var:N]`
  resolver on rejection (4.5).
- **Segmented build** (5.1/5.2): `lifecycle build` accepts
  `includeCallees={none,direct,transitive}` (default `transitive`) and expands
  the target list reverse-topologically via `CallerGraphService` so callees
  compile before callers. `_meta.buildPlan` reports
  `{requested, expanded, includeCallees, cap}`; `BuildPlanTooLarge` envelope when
  expansion exceeds the cap (default 200).
- **Output size** (6.1/6.2/6.3): `lifecycle status compact=true` default returns
  counts + top-10 errors + warning dedup (opt out with `compact=false`); `read`
  paginates by default at 200 lines / 16 KB (`limit=0` opts out;
  `suggestedNextOffset`/`Limit` surface the next page);
  `_meta.background_jobs` dedups per session so completed jobs appear exactly once.
- **i18n** (7.1): `ErrorMessages.Translate` maps known PT-BR SDK diagnostics to
  canonical EN; original preserved in `_meta.sourceMessage` /
  `_meta.sourceDetails`.
- **Cancel** (7.2): `lifecycle action=cancel` with a `job_id` now signals a
  registered CTS in `BackgroundJobRegistry`, terminates the async build poller
  within one tick, and fans out a fire-and-forget `Build/Cancel` to the worker.
  Worker-side `CancellationToken` plumbing through long-running services
  (search, analyze) is deferred.

### Wave 2 follow-ups (post-self-review)

The first Wave 2 push had a few rough edges flagged during the post-shipping
review. These commits closed them:

- **Warm-start `IndexState`**: `whoami.index.status` reported `Cold` after a
  warm start even though list/search were hitting a fully-hydrated index.
  `IndexCacheService.GetIndex` and `KbService.BulkIndex` now publish Ready
  when they detect the in-memory index was loaded from the disk cache.
- **`compact` through long-poll**: the `LifecycleResponseShaper` was only
  wired into the legacy taskId status path. The `job_id` long-poll branch
  (`McpRouter.LongPollJob`) now also runs the shaper, so callers using
  `wait_seconds>0 + job_id` get the compact envelope.
- **Worker-side cancel for search**: `SourceSearchService.SearchAsJson` now
  accepts a `CancellationToken` and emits a `Cancelled` envelope mid-scan.
  Gateway-side `BackgroundJobRegistry.RegisterCancellation` is already in
  place; the remaining IPC plumbing (a cancel side-channel from gateway to
  worker over stdin) is still future work — see Known gaps below.
- **`ErrorMessages` table**: expanded from 9 to 20 patterns seeded by
  greping the actual friction-report transcripts. Covers Transaction /
  Procedure / SDT validation envelopes, "Não foi possível", "Erro ao",
  target-environment reorganization messages, and the inline-property
  diagnostics (`X é propriedade inválida`).
- **End-to-end smoke test**: `IdealWorkflowSmokeTest` (Worker) +
  `IdealWorkflowGatewaySmokeTests` (Gateway) compose the friction-report
  workflow — Cold→Ready transition, search envelopes, filter narrowing,
  pagination, segmented-build expansion, compact shaper, JobRegistry
  cancel/dedup, ErrorMessages round-trip. Catches the kind of integration
  break (warm-start IndexState) that escaped the original push.

### Gateway ↔ worker cancel side-channel (post-self-review)

The first cancel pass left the gateway poller terminating cleanly but the
worker running its SDK call to completion. Closed:

- **`WorkerCancellationRegistry`** (worker helper): static, thread-safe
  dictionary of `(cancelToken → CTS)`. The dispatcher registers a scoped
  CTS for thread-safe long-running commands (search, impact) and disposes
  on completion.
- **`method=control, action=Cancel`**: new dispatcher command marked
  thread-safe so it interleaves with an in-flight SDK call. Looks up the
  CTS by `cancelToken` and signals it. The worker returns
  `{status: "Cancelled" | "NotFound", cancelToken}` immediately.
- **Gateway fan-out**: when `lifecycle action=cancel` resolves a `job_id`,
  in addition to flipping the registry status and tripping the gateway
  CTS, it now sends a fire-and-forget `Control:Cancel` to the worker
  carrying the same token. Handlers honouring `CancellationToken`
  (currently `SourceSearchService.SearchAsJson`,
  `AnalyzeService.ImpactAnalysis`, and both `CallerGraphService` BFS
  walks) terminate within one iteration.
- **CT plumbed through `CallerGraphService.GetCallersTransitive` and
  `GetCalleesTransitive`** with backwards-compatible overloads, so
  `AnalyzeService.ImpactAnalysis` honours the registered token end-to-end.

### Breaking notes

- `lifecycle status` default is now `compact=true`. Callers that parsed
  `Errors[]` / `Warnings[]` / `Output` directly must pass `compact=false`.
- `lifecycle build` default is `includeCallees=transitive`. Pass
  `includeCallees=none` for the pre-v2.3.8 single-target behaviour.
- `genexus_read` paginates by default when an MCP-client read exceeds 200 lines
  or 16 KB. Pass `limit=0` to opt out.
- Error messages are translated to EN by default; the original SDK string lives
  under `_meta.sourceMessage` / `_meta.sourceDetails` whenever the translator
  rewrote anything.

## v2.3.7 — 2026-05-15

Friction-report sweep #3 (`docs/mcp-friction-report-2026-05-15.md`, since deleted).
13 actionable agent-facing rough edges from the WWP+ UI/UX session, all addressed.
No public API breaking changes.

365/365 unit tests passing (211 Gateway + 154 Worker). Build clean (0 errors).

### Worker (.NET 4.8)

- **#1 — Structured `verifyDiff` on Visual/Pattern write rejection**
  (`Helpers/XmlEquivalence.cs`, `Services/WriteService.cs`). The error envelope
  now carries `verifyDiff: { element, path, rejectedAttributes[], addedAttributes[],
  persistedAttributes[], requestedAttributes[] }` whenever the persisted XML's
  attribute set differs from the requested set. The agent no longer has to compare
  `left=[…] right=[…]` strings to figure out which attribute the SDK sanitised.
- **#2 — `PatternVirtual` filtered from `availableParts`**
  (`Structure/PartAccessor.cs`). The SDK exposes a `PatternVirtual` part in
  `obj.Parts` but has no working read/write path for it through the MCP — listing
  it sent the agent in circles. Hidden until a real read path exists.
- **#3 — `typesAvailable` hint on empty `list_objects` typeFilter**
  (`Services/ListService.cs`). When `typeFilter` matches zero entries but the index
  isn't empty, the response now includes `_meta.typesAvailable: [...]` with the
  distinct type names present so the agent discovers the canonical string instead
  of guessing (e.g. Themes may be indexed as `DKTheme`, not `Theme`).
- **#4 — `managedBy` flag on framework-injected variables**
  (`Helpers/FrameworkManagedVariables.cs`, `Services/AnalyzeService.GetVariables`).
  `IsAuthorized`, `SecurityFunctionalityKeys`, `Time`, `DiasSemanaFin` are tagged
  with their owner (GAM / WWP+). `LinterService` GX008 silences these to break
  the delete-readd-delete loop.
- **#5 — `genexus_delete_variable` tool**
  (`Services/WriteService.DeleteVariable`, `OperationsRouter`, `tool_definitions.json`).
  Symmetric to `genexus_add_variable`, idempotent. Refuses framework-managed vars
  with a `Refused` status instead of letting the SDK re-inject them silently.
- **#6 — `Source` deduped from `availableParts` when `Events` is present**
  (`Structure/PartAccessor.cs`). On WebPanels/Transactions the two labels resolved
  to the same `ISource` part; dropping `Source` from the list leaves a single
  canonical name (the `Source` alias still works via `PartAccessor.FindPart`).
- **#9 — Worker crash diagnostics** (`Program.cs`). `[WORKER-CRASH]` log line now
  carries memory (working set + private + GC), uptime, thread count, exception
  type/message, and the full stack when `AppDomain.UnhandledException` fires.
  Lets the gateway correlate disconnects with the actual cause.
- **#10 — Pre-flight schema scan on dry-run** (`Helpers/WebFormSchemaHints.cs`,
  `Services/WriteService.WriteVisualPart`). Dry-run now walks the input XML and
  emits `preflightWarnings: [{element, attribute, reason}]` for any attribute that
  isn't in the SDK's known accept-list for that element (e.g. `style` on `<table>`
  / `<gxTextBlock>`). Catches the sanitisation issue before the agent tries the
  real save and hits `Visual write verification failed`.
- **#11 — `acceptedAttributes` on controls repertoire**
  (`Services/UIService.cs`). `genexus_inspect controls` now surfaces
  `acceptedAttributes: [...]` per control entry, sourced from the same
  `WebFormSchemaHints` accept-list, so the agent sees the schema before editing.
- **#12 — Linter is now pattern-aware** (`Services/LinterService.cs`). When a
  `PatternInstance` part is detected on the object, `GX012 Direct Table Access in
  UI` is suppressed (the WWP+ pattern *prescribes* direct `For Each` in Event
  Start to hydrate SDTs — flagging that as a warning is noise).
- **#8 — `search_source` time budget** (`Services/SourceSearchService.cs`). Hard
  25 s budget on the source scan loop; partial results return with
  `budgetExceeded=true, budgetMs, budgetHint` instead of an open-ended >2 min
  wait. Index-backed search remains on the v2.4 roadmap.

### Gateway (.NET 8)

- **#5 wiring** — `genexus_delete_variable` registered in
  `OperationsRouter.ConvertToolCall` and `tool_definitions.json`.
- **#7 — Long-write timeout hint** (`Program.cs`). When a write times out at the
  gateway, the help array now spells out that the write has usually already
  persisted by the time the agent sees the timeout — poll `action='result'` once,
  then read back, instead of retrying the edit (which no-ops or conflicts).
- **#13 — `_meta.background_jobs` resilient injection** (`McpRouter.PiggybackJobs`).
  Previously, when `content[0].text` wasn't valid JSON or was missing entirely,
  the piggyback silently dropped the background-jobs snapshot — producing the
  intermittent "_meta às vezes aparece, às vezes não" the agent observed. Now
  wraps non-JSON text and falls back to attaching `_meta` to the result root for
  error envelopes, so background-job status is delivered on every response while
  a build is running.

### Deferred — needs deeper work

- True async writes (`#7` upgrade) — full `{job_id, status:"running"}` envelope
  on edits and SemanticOps. Mitigation (better timeout hint) shipped; full
  refactor needs idempotency/state-machine work.
- Theme/StyleSheet `edit` (`#3` upgrade) — read/edit of Theme objects programmatically.
  Mitigation (`typesAvailable` hint on empty typeFilter) shipped.
- Index-backed `search_source` (`#8` upgrade) — Lucene/ripgrep-style token store.
  Mitigation (25 s budget cap) shipped.
- `PatternVirtual` read/edit (`#2` upgrade) — implementing the SDK path for the
  virtual pattern part. Filtered for now.

## v2.3.6 — 2026-05-15

Less-turns pass: cut round-trips between the agent and the MCP by enriching
return payloads. Same code quality, same correctness guarantees, fewer
tool calls per task. No public API breaking changes.

### Worker (.NET 4.8) — less-turns

- **`inspect` now surfaces `callers[]`** (`AnalyzeService.GetConversionContext`) —
  top-20 incoming references resolved via `obj.GetReferencesTo()`, runs in
  parallel with the existing metadata tasks. Mata o `analyze(mode=impact)` /
  `query usedby:*` follow-up que o agente fazia depois de quase todo inspect.
  Opt-in via `include=["callers"]` ou default quando `include` é omitido.
  Adds `callersTruncated` flag when the 20-cap is hit.
- **`persistedSnippet` em falhas de edit** (`PatchService.AttachPersistedSnippet`) —
  quando `persistedVerified=false` (inicial ou pós-fallback), o payload agora
  inclui `{startLine, divergeLine, content, totalLines}` com ±10 linhas do
  estado real em disco em volta da primeira divergência vs. o que foi enviado.
  Antes a mensagem dizia "re-read source"; agora o agente confirma o estado
  visualmente sem chamar `genexus_read`.
- **`search_source` context bumped to ±3 lines** (`SourceSearchService.BuildHit`)
  — `contextBefore` / `contextAfter` agora são arrays (até 3 linhas cada) em
  vez de strings de 1 linha. Para a maioria dos hits o agente entende o
  callsite sem precisar de um `genexus_read` subsequente.
- **`inline_read_top` em `search_source`** (`CommandDispatcher.AppendInlineReadsForSourceSearch`)
  — espelha o pattern existente de `query` / `list_objects`. Dedup por
  `objectName` para que `N=3` retorne até 3 *objetos distintos* (não 3 hits
  no mesmo arquivo). `AppendInlineReadsCore` foi generalizado para aceitar
  `arrayKey` / `nameField` / `dedupe` opcionais; os call sites antigos
  mantêm comportamento idêntico via defaults.

### Schema budget

- **`tool_definitions.json` trimmed: 4150 → 3974 tokens** (orçamento 4000).
  Boilerplate `"Target KB (alias or path). Required when 2+ KBs are open."`
  (24 ocorrências) → `"Target KB. Required when 2+ open."`; descrição longa do
  `inline_read_top` em 3 tools → forma compacta. Sem perda de informação útil
  ao modelo. Pre-existing `ToolSchemaSizeTests` agora verde.

### Tests

365/365 unit tests passing (211 Gateway + 154 Worker). Build clean (0 errors).

## v2.3.5 — 2026-05-14

Two-pass performance + friction sweep. No public API breaking changes.
- **Phase 1 — preventive perf audit:** 21 changes across Worker (.NET 4.8) and
  Gateway (.NET 8) targeting allocations, lock contention, telemetry, and disk
  I/O on hot paths.
- **Phase 2 — friction-report 2026-05-14:** 10 changes closing the actionable
  agent-facing rough edges from the live debugging session that produced
  `docs/mcp-friction-report-2026-05-14.md`.

365/365 unit tests passing (211 Gateway + 154 Worker). Build clean (0 errors).

### Worker (.NET 4.8) — performance

- **`Logger` rewritten as async writer** (`Helpers/Logger.cs`) — `BlockingCollection`
  fed by ~194 call sites, drained by a dedicated background thread that issues
  one batched `File.AppendAllText` per drain. Previous global lock + sync I/O
  per call was the biggest hot-path tax in bulk index and search. Stderr fallback
  preserved so the Gateway capture path is unchanged.
- **`SearchService.Search` parallelism capped** — `AsParallel().WithDegreeOfParallelism(min(4, ProcessorCount))`
  prevents PLINQ from spawning one task per core on large KBs (50k+ objects).
- **`SearchService` instrumented** — `Stopwatch` + `[SEARCH-SLOW]` log when
  > 50 ms via `try/finally`. Search was the busiest hot path with no telemetry.
- **`IndexCacheService`** — search-index snapshot now flushed gzipped (`*.json.gz`)
  via temp + atomic move; flush throttle 10 s → 30 s; reader stays backward
  compatible with legacy plain JSON; legacy file cleaned up on first flush.
  `ResolveHierarchy` now cached per object Guid (invalidated on remove/clear).
- **`IndexCacheService.GetEntryStorageKey`** caches its `Type:Name` result on
  the `IndexEntry` (new `[JsonIgnore] StorageKey` field) to skip
  `string.Format` in every `AddOrUpdateEntryInParentIndex` lookup.
- **`VectorService.ComputeEmbedding`** — separator array hoisted to a
  `static readonly`; per-token lower-case avoids the full-string `ToLower()`
  copy in every bulk-index call (~30k/cold-start).
- **`ObjectService.ReadCacheTtl`** bumped 20 s → 60 s — read-after-read patterns
  from LLM agents in a single tool sequence now hit cache.
- **`Program.QueueWriter`** — `Write(string)` and `WriteLine(string)` acquire
  the lock once per call; old impl locked per character on every IPC write.
- **`Program.BackgroundQueue`** signalled via `AutoResetEvent` + new
  `EnqueueBackground` helper; loop wakes on signal instead of `Thread.Sleep(100)`.
- **`Helpers/CodeParser`** — 13 inline regex calls replaced with pre-compiled
  static fields (validator was rebuilding interpreted regex per line).
- **`Services/AnalyzeService`** — `Analyze` and `GetHierarchy` now de-duplicate
  references before issuing SDK `Objects.Get` calls (safe portion of the audited
  N+1; same-target edges no longer cost N round-trips). Audited refactor of the
  full SDK fetch pattern remains deferred until a regression suite exists.
- **Cold-start instrumentation** — `KbService.OpenKB` and the bulk-index thread
  now log `[KB-OPEN] elapsedMs=…` / `[BULK-INDEX] elapsedMs=…` so future
  regressions are visible.

### Gateway (.NET 8) — performance

- **`WorkerPool` per-KB spawn gate** — global `_spawnLock` replaced with a
  per-`Entry` `SemaphoreSlim`. Two clients opening different KBs no longer
  serialise behind each other. A narrow `_capacityLock` still protects the
  capacity-window/eviction.
- **`IdempotencyCache`** — `KbBucket` shards across 16 independent LRU slots,
  cutting hot-key contention by ~1/N. `GetOrCompute.WaitAsync` now bounded at
  30 s with a best-effort fallback (run factory bypassing the cache) so a
  stuck worker can no longer starve callers until the 65-min TTL.
- **`WorkerProcess`** — spawn retry uses exponential backoff (100/200/400/800/1000
  ms) + ≤50 % jitter instead of flat 1 s × 10. First retry fires 10× sooner.
- **`WorkerProcess.ProcessQueueAsync`** — `JsonConvert.DeserializeObject<JObject>`
  on the hot IPC path replaced with `JObject.Parse` (direct, no reflection-style
  dispatch).
- **`WorkerPool.SelectVictim`** — linear scan replaces `OrderBy`, dropping the
  full-sequence materialisation for eviction selection.
- **`ResponseSizeGuard`** — `StreamWriter` buffer 1 KB → 32 KB; new
  `ByteSize(string)` overload uses `Encoding.UTF8.GetByteCount` for callers that
  already have the serialised JSON in hand.
- **`McpRouter`** — `tool_definitions.json` hot-reload via `FileSystemWatcher`
  with 500 ms debounce. Subsequent `tools/list` calls observe the new payload
  without restarting the gateway.
- **Build flags** — `<PublishReadyToRun>true</PublishReadyToRun>`,
  `TieredCompilation`, `TieredPGO` enabled for Release publish;
  `ServerGarbageCollection` + `ConcurrentGarbageCollection` on the main
  `PropertyGroup`. Cold-start JIT cost drops significantly in published builds.

### Friction-report 2026-05-14 fixes (second pass)

- **#2 — `persistedVerified` false-negative mitigated** — `PatchService.VerifyPersistedSource`
  now retries the post-write read once after a 120 ms pause (the SDK sometimes
  flushes to disk slightly after `Save()` returns), and on persistent mismatch
  attaches a compact `Verify diff at char N: expected='…' actual='…'` hint so
  the agent can decide whether the rollback is warranted instead of looping
  re-tries.
- **#3 — Reverse-dep index now catches Event Start call sites** —
  `IndexCacheService.EnrichCallsFromTextualScan` (new) augments
  `obj.GetReferences()` with a textual scan over every `ISource` part on the
  object. Any `Identifier(` token that already exists in the index as a
  callable object type (Procedure / DataProvider / WebPanel / Transaction /
  Menubar / WorkPanel / BPD / SDT / Domain / ExternalObject) is added to
  `Calls` + the target's `CalledBy`. Hard "must exist in index" filter
  eliminates false positives from keywords.
- **#4 + #5 — `genexus_properties` accepts variable & control scope** —
  `PropertyService.FindControl` now resolves `&Name` to the SDK Variable and
  also takes a bare name as a Variable when the layout-control lookup misses.
  ControlType / ControlValues / Enabled / Visible / etc. now settable
  per-variable.
- **#11 — `genexus_edit mode=ops` schema enum** —
  `tool_definitions.json` now lists the supported RFC 6902 ops
  (`add | remove | replace | test`) and surfaces `path` as required.
- **#14 — Description as title-bar documented** — `genexus_properties`
  description in the schema explicitly notes the Description property doubles
  as title-bar text when a WebPanel/Popup is opened via `.Popup()`.
- **#15 — Linter `GX022`** — non-prefixed Layout elements (`<Button>`,
  `<Bitmap>`, `<TextBlock>`, `<Attribute>`, `<Grid>`, `<EmbeddedPage>`,
  `<Tab>`, `<Card>`, `<Group>`, `<Image>`, …) flagged as Warning with
  "did you mean `<gx{name}>`?". Previously these silently rendered as
  literal HTML and burned 2-3 build cycles to diagnose.
- **#16 — Patch `{find, replace}` JSON form now actually works** —
  `ObjectRouter` maps `patch={find,replace}` to the existing patch pipeline
  (find → context, replace → payload). The schema advertised this form but
  only the legacy `(operation, context, content)` triple worked before.
  Schema updated to also document the `{find, replace}` shape.
- **#17 — Whitespace-tolerant patch context** —
  `PatchService.TryWhitespaceNormalizedReplace` (new) added as a last-resort
  pass before reporting `NoMatch`. Tab-vs-space context differences now
  resolve: the matcher locates the unique window using collapsed-whitespace
  comparison and splices using the source's original indentation.
  `Ambiguous` is returned if the normalized match is non-unique.

### Friction-report 2026-05-14 fixes (first pass)

- **#1 + #13 — Variable `internalId` exposed** (`AnalyzeService.GetVariables`,
  `GetConversionContext`, new `VariableInjector.GetVariableInternalId`). Layout
  XML uses `AttID="var:N"`; agents can now resolve that mapping from
  `genexus_inspect`/`get_variables` instead of grepping the generated `.cs`.
- **#7 — `lifecycle action=cancel target=op:<id>` actually does something.**
  New Gateway intercept marks the operation `Cancelled` in `OperationTracker`,
  abandons the matching pending request with a structured error, and returns
  `{status:Cancelled, abandonedRequestId, message}`. The worker thread may
  still finish its SDK call but no further response is delivered. Unknown-op
  case now returns a specific "Unknown build taskId" message + hint instead of
  bare "Task ID not found".
- **#8 — `genexus_inspect controls`** — when the SDK web-tag tree walker
  returns empty (mixed HTML + gx-prefixed layouts), `UIService` now falls back
  to a direct XPath scan over `<gx*>` elements and surfaces
  `name/type/controlType/dataBinding/event` per control with `_fallback:true`.
- **#10 — `wait_seconds` cap 25 s → 90 s** (`McpRouter.MaxLongPollSeconds`).
  Builds of 50–70 s now converge in a single long-poll instead of 3.
- **#12 — Build noise filtered from `TailLines`** (`BuildService.HandleLine` +
  new `_rxModuleCopyNoise`). "Copiando módulo …" / "Restoring NuGet" /
  "Touching …" / "Wrote …" lines stay in `FullOutput` (terminal payload) but
  get dropped from the live tail so the agent sees real signal during a build.
- **#18 — Patch failure near-match diagnostic** (`PatchService.FindNearMatches`).
  On `NoMatch`, the patch response now includes a `nearMatches: [{line,
  similarity, snippet}]` array (top-3) + `nearMatchHint`. Agent adjusts the
  context block in one iteration instead of re-reading the whole file.
- **#19 — `lifecycle status` no longer returns full `Output` while Running**
  (`BuildService.GetStatus`/`GetResult`). The 200+ line build log was repeated
  on every poll; now only `TailLines` rides during Running and `Output` is
  attached at terminal state.
- **#20 — Linter `GX021`** — `parm(... out: &X ...)` without a matching
  `&X.Enabled = 1` in Event Start surfaces an Info issue. Catches the
  silent-disabled-control trap from the friction report.
- **#21 — Linter `GX020`** — `<gxButton onClickEvent="X"/>` in a WebForm
  without `Event Enter` defined surfaces a Warning. gxButton in HTML layouts
  only fires `Event Enter`; `onClickEvent` is silently ignored otherwise.

### Internal / docs

- New audit document: `docs/perf_audit_2026-05-14.md` (the Phase-1 baseline).
- Two false positives from the audit closed without code change because the
  code already addressed them: `IndexCacheService.FlushToDisk` (try/catch + log
  present) and Gateway `_pendingRequests` sweeper (`RunSessionCleanupLoop`
  already running on a 1-minute `PeriodicTimer`).
- Items deliberately deferred (require dedicated regression suite or new
  project scaffolding): full Newtonsoft → System.Text.Json migration in the
  IPC hot path, BenchmarkDotNet baseline project, OperationTracker exported
  as an MCP diagnostic endpoint, and the deeper SDK batched-fetch refactor for
  `AnalyzeService`.
- Friction-report items deferred for a dedicated session:
  - **#6** (`genexus_search_source` timeouts) — needs Lucene/ripgrep index.
  - **#9** (worker-disconnect orphan operationId) — needs durable op-state
    persistence (SQLite or similar) with TTL.

## v2.3.0 — 2026-05-14

Multi-KB parallel support + tool surface consolidation + official skill bundles.
One Gateway can now drive up to `Server.MaxOpenKbs` (default 3) concurrent KBs,
each in its own Worker process. Cross-KB tool calls run in parallel — no
serialization between KBs. Intra-KB calls remain serialized by the SDK's STA
constraint, as before.

### Consolidations (5 tools removed → registered in RemovedToolsRegistry for LLM auto-redirect)
- `genexus_open_kb` → `genexus_kb action=open`
- `genexus_get_sql` → `genexus_sql action=ddl`
- `genexus_get_sql_for_navigation` → `genexus_sql action=navigation`
- `genexus_summarize` → `genexus_analyze mode=summary`
- `genexus_explain_code` → `genexus_analyze mode=explain` (takes `code` arg)

Total tools: 33 → 29. Schema size: ~3141 → ~3714 tokens (multi-KB `kb` param
adds tokens; consolidations partly offset). Test budget bumped 3500 → 4000.

### Crash isolation (follow-up to initial v2.3.0 design)
- Pending requests now track their `WorkerAlias`. When a Worker crashes, only
  the requests bound to that KB are aborted with `-32603` — sibling KBs keep
  working. Previously stale pending requests waited for the 65-min sweep.

### `genexus_kb` enrichment
- `action=list` now returns `pid`, `workingSetBytes`, `workingSetMB`, and
  `idleSeconds` per open KB, so the LLM can self-throttle / pick a candidate
  to close before opening another.
- New `action=set_default` — persists `DefaultKb` to `config.json`
  (preserves any unmodelled fields).

### GitHub release notes
- `scripts/release.ps1` now extracts the CHANGELOG section for the released
  version and uses it as the release body (`gh release create --notes-file`).
  Falls back to `--generate-notes` if the section is missing.

### Bundled skills (imported from genexuslabs/genexus-skills, Apache 2.0)
- `nexa/` — full reference set: every GeneXus 18 object type, command,
  formula, type, property (was a stub before).
- `frontend/{chameleon-controls-library, mercury-design-system,
  design-system-builder, ui-creator}/` — Chameleon UI specs, Mercury DS
  tokens/bundles, design-system authoring, panel templates.
- `.gemini/skills/NOTICE.md` documents attribution + upstream refresh steps.

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
