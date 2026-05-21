# Changelog

## v2.6.4 — 2026-05-20

Three passes: a usability sweep against KB `AcademicoHomolog1` that caught nine concrete friction points the LLM was hitting on first use; a UX pass focused on the "agent burns 3-8k tokens exploring before doing real work" failure mode on apply_pattern; and a corporate-Windows install hardening pass triggered by a real `2.3.4 -> 2.6.3` upgrade report where the user's MCP config kept silently pointing at an old gateway exe outside `node_modules`, the npm-installed copy was blocked by domain AppLocker from `%APPDATA%`, and every diagnostic surface ("Failed to connect", `npm update` ghost operation, generic launcher errors) compounded the dead end. Validated with happy-path apply on a disposable Transaction + WebPanel (11/11 assertions), focused UX probe (20/20), and the full CLI test suite (37/38, single pre-existing assertion unrelated).

### Fixed

- **`analyze mode=explain` was a stub returning hardcoded `"Code analysis simulation"`** regardless of input — agents treated the fake response as real. Mode removed from the public schema (`tool_definitions.json`); legacy callers receive an explicit `NotImplemented` envelope pointing to valid modes.
- **`genexus_query` ranking pulled `Index` objects with no name/path match into the top-20** via vector similarity. Fast literal query for "Country" returned 15 unrelated `IBls*` indexes. Index/Folder/Module are now filtered out of default results unless explicitly requested via `typeFilter`. New `_meta.match_quality` field (exact|prefix|substring|vector|none) lets the caller branch reliably; `suggested_next` is only emitted for `exact`/`prefix` to stop misdirecting agents.
- **`genexus_read` error envelope for invalid parts didn't list valid parts.** Agents were guessing part names through trial-and-error. Now includes `availableParts` (same list `genexus_inspect include=['parts']` returns) plus a `hint` line: `Valid parts for Procedure: Documentation, Help, Layout, Source, Variables.`
- **`analyze pattern_metadata` took 12.3s to error on non-WWP-eligible objects** because `ResolveWWPInstance` walked `model.Objects.GetAll()` on the full KB before falling through. Upfront type guard now rejects in ~30ms (~430× faster) when the parent isn't `WorkWithPlus` / `Transaction` / `WebPanel`.
- **`analyze mode=navigation` returned `{levels:[], warnings:[]}` silently** when an object had no `For Each` blocks — indistinguishable from analysis failure. Empty envelopes now carry `status: "NoNavigationBlocks"` + `hint` pointing to alternative modes.
- **`genexus_whoami` cold-path latency was 1.7s** because `BuildWhoamiPayloadAsync` always blocked on a worker round-trip for fresh index state. Now skips the round-trip when the cached snapshot is < 15s old, and tightens the remaining timeout from 1500ms → 400ms. 1676ms → 7ms baseline (≈240× faster).
- **`genexus_properties action=get` cold-call on a Domain was 3s** due to lazy SDK property-definition reflection. Added a per-GUID TTL cache (30s, invalidated on `set`) so repeat reads are sub-millisecond. The first hit still pays the SDK warm-up but subsequent agent introspection on the same object is free.
- **`genexus_query` `_meta.partial` flag was inconsistent.** Direct-lookup hits during cold-start didn't surface partial-state info, so agents didn't know to re-query once indexing finished. `match_quality` + `partial` now appear uniformly across direct-lookup and index-search paths.
- **Inner-payload errors didn't set `result.isError: true`.** `genexus_read` returning `{error: "Part 'X' not found..."}` was sent with `isError=false`, breaking MCP clients that branch on the flag. The gateway now mirrors inner-payload error/status (Error/NotFound/NotImplemented) into the outer envelope. Affects `read`, `analyze`, and any tool that returns errors via the result-body shape.

### Added

- **`apply_pattern` parent-type gate.** Reported by user: applying WorkWithPlus to a WebPanel created the host but bound it as a Transaction, producing IDE compile errors. The fix is two-fold:
  1. **Upfront type-routing.** Object type is checked before any SDK churn. `Transaction` → family-generation path (no template required). `WebPanel`/`SDPanel` → direct-attach path (template required or auto-discovered). Anything else (`Procedure`, `SDT`, `Domain`, …) is rejected in <500ms with `validParentTypes` + a routing hint, instead of churning through a no-op and returning a misleading "WorkWithPlus instance not found".
  2. **`settings.template` validated against the live catalog.** Bad template returns `availableTemplates` synchronously. Previously the validation walked `model.Objects.GetAll()` on every call (~10s on a 50k-object KB); now consults the search index with a 60s TTL cache, so the check is subsecond after the first hit.
  3. **Response envelope surfaces `parentType` + `bindingMode`** (`transaction-family` | `webpanel-direct-attach` | `sdpanel-direct-attach`) so the agent can verify which lifecycle ran without inspecting the IDE.

- **`genexus_whoami` returns inline playbooks.** First-turn whoami response now carries a `playbooks` block routing the LLM to the right tool for the most common flows (WWP on transaction, WWP on webpanel, edit pattern instance, create popup, read object structure). Eliminates the "agent explores for 3-8k tokens before acting" pattern observed in real sessions. The playbooks are 1-line redirects; full step-by-step recipes are in the new `genexus_recipe` tool.

- **`genexus_recipe { name }` — new gateway-served tool for named playbooks.** Returns `{goal, prereq, steps, pitfalls}` for `wwp_on_transaction`, `wwp_on_webpanel`, `create_popup`, `edit_pattern_instance`, `add_custom_button`. `name='list'` enumerates available recipes. Catalog lives in `RecipeCatalog.cs` for easy extension. Tool descriptions across `genexus_apply_pattern` / `genexus_create_object` / `genexus_edit` now point at the relevant recipes so the LLM discovers the routing layer from `tools/list` without exploration.

### Performance

- **`SdkSurfaceProbe.Run` was firing on EVERY `apply_pattern`** — full reflection sweep over loaded SDK assemblies plus a multi-MB `raw.json` write to disk. 5-15s of pure debugging-tool overhead in production calls. Gated behind `GX_MCP_SDK_PROBE=1`; the same diagnostic is available on demand via the `genexus_sdk_probe` tool when investigating SDK surface. NoOp diagnostic path (the `sdkProbePath` / `sdkSurfaceProbe` envelope fields) gated identically.
- **Per-phase Stopwatch instrumentation logged as `[ApplyPattern-PERF]`** captures `sdkProbeGated / engineApply / lookupFamily / indexUpdate / tailEnvelope` per call. Remaining time on a real apply (~50s on Transaction, ~30s on WebPanel) is the SDK doing actual generation/persist work and cannot be shortcut without losing the WWP lifecycle guarantees.
- **`ListWwpWebTemplates()` walks the search index instead of `model.Objects.GetAll()`** with a 60s TTL cache keyed by KB location. 10s → ~5ms on cached hits.

### Internal

- Probes used for the audit live under `scratch/`: `usability_probe.js` + `usability_probe2.js` (initial audit), `validation_probe.js` (25/25 assertions on the 9 fixes), `ux_probe.js` (20/20 on the new UX features), `apply_happy_path.js` (11/11 on real apply with disposable objects).
- `RecipeCatalog.cs` is `internal static` with a `Dictionary<string, Func<JObject>>` registry — adding a recipe is one entry. Routes through the same `gateway-served meta-tool` path as `genexus_whoami` (no worker involvement, no JSON-RPC round-trip).

### Added (follow-up)

- **`apply_pattern { validate: true }` — post-apply build of the generated host in a single tool call.** The original "vinculou como se fosse transação" bug surfaced as the LLM declaring success on a broken WWP binding that only failed when the user opened the IDE. With `validate: true` the gateway fires `Build/Build` against the host returned by apply, polls `Build/Status` with the worker's taskId until terminal, and folds a `validation` block into the apply response: `{ status: ok|failed|timeout, errorCount, warningCount, errors[], warnings[], durationMs, taskId }`. Failed builds promote `result.isError=true` so MCP clients that branch on the flag get a clear pass/fail signal. Wall time adds 60-180s but the LLM never has to open the IDE to discover a compile failure. Validated live: 11/11 assertions including the bug-mode where the worker's `BuildService.Build` returns a `Running` envelope in milliseconds — earlier draft parsed that as `status: "ok"` (26ms / 0 errors), now correctly polls until the real terminal state (55s / 6 errors / errors[] populated).

- **`genexus_lifecycle action=result target=op:<jobId>` works for completed background jobs.** v2.6.3 fixed `cancel`/`status` for `op:<id>` via JobRegistry but `result` still forwarded to the worker's taskId tracker, which returned `"Task ID not found"` for jobs visible in `_meta.background_jobs`. Symmetric handler now consults JobRegistry first: running → `Pending` envelope with poll hint; completed (`succeeded`/`failed`/`cancelled`) → stored `JobEntry.Result` plus status/operationId/kind/summary/startedAt/completedAt. Failed/cancelled propagate `isError=true`.

### Internal (follow-up)

- **Helper extraction for unit testability.** Two inline payload builders refactored into pure static methods so they can be covered without spinning up the gateway:
  - `McpRouter.BuildJobResultEnvelope(JobEntry job)` → `(envelope, isError)` — the lifecycle result shape, called from the `op:<id>` route.
  - `PatternApplyService.TryBuildTypeGateRejection(objName, patternKey, parentType, callerTemplate, availableTemplates)` → rejection JSON or null — the WWP parent-type gate, called from `ApplyPattern`.
- **Regression suite — ~48 new assertions across 6 files:**
  - `RecipeCatalogTests` (11) — list / known recipe / unknown / empty name / case-insensitivity / wwp_on_webpanel emphasises inspect-first.
  - `WhoamiPlaybooksTests` (7) — playbooks block presence, 6 canonical routes, parent-type-check emphasis, index-state cache reflects updates.
  - `ToolDefinitionsRedirectsTests` (7) — apply_pattern mentions inspect+parentType, create_object redirects WWP, edit warns about PatternInstance vs WebForm, whoami points at playbooks, `genexus_recipe` registered, `analyze.mode` drops `explain`, apply_pattern declares `validate` boolean.
  - `LifecycleResultTests` (6) — running returns Pending without isError, succeeded surfaces stored result, failed/cancelled mark isError, null-result terminal envelope, null-job guard.
  - `PatternApplyTypeGateTests` (17) — Transaction case-insensitive eligibility, WebPanel/SDPanel no-template path, non-eligible types rejected with `validParentTypes` + hint, bad template surfaces availableTemplates, case-insensitive template match, non-WWP keys pass through, null parent type rejected, empty available list skips check.
  - `E2ELiveSmokeTests` — 7 LiveKbFact-gated end-to-end tests against the published Gateway over stdio (whoami latency / explain NotImplemented / query Index pollution / read availableParts / navigation status / apply_pattern type-gate rejection / apply_pattern validate happy path requiring WWP). New `LiveGatewayHarness` spawns the process and runs the JSON-RPC handshake — mirrors the scripts under `scratch/` so the regression contract matches the live audit.
- **`ToolSchemaSizeTests` budget bumped 6300 → 6700** to absorb genexus_recipe (~80 tokens), apply_pattern `validate` + parent-type hint (~80), description front-loading on create_object/edit/whoami (~100). Net actual ~6624 tokens.
- **Contract-discovery goldens refreshed** to include `genexus_recipe` and the updated descriptions.

### Install / DX hardening (corporate Windows + AppLocker)

Triggered by a live `2.3.4 -> 2.6.3` upgrade report on a UNIVALI-domain machine. Symptoms the user hit: `npm update genexus-mcp -g` succeeded but `whoami` kept returning the old version (config pointed at a stale exe copy outside `node_modules`); after repointing at the npm-bundled exe under `%APPDATA%\Roaming\npm\node_modules\genexus-mcp\publish\`, `claude mcp get` reported "Failed to connect" and direct execution gave `Acesso negado` — domain AppLocker / SRP blocks exec from `%APPDATA%`. None of the diagnostic surfaces (`whoami`, launcher stderr, `Failed to connect`, `npm install -g`) pointed at the policy. Six fixes close the gaps:

- **Launcher (`cli/index.js`) emits an actionable AppLocker hint on `EACCES`/`EPERM`.** Detects "Access is denied" / "Acesso negado" / `err.code === 'EACCES' || 'EPERM'` from the gateway spawn, identifies whether the exe lives under `%APPDATA%` / `%LOCALAPPDATA%` / `%TEMP%`, and prints: cause (AppLocker/SRP), the restricted zone tag, and the one-liner `iex (irm .../scripts/install.ps1)` remediation. Generic `Failed to start gateway process: ...` is no longer the user's only signal.

- **`doctor` gains two checks and probes by default.**
  - `gateway_exe_path_safety` — warn when the bundled exe is under `%APPDATA%` / `%LOCALAPPDATA%` / `%TEMP%`, with the install.ps1 remediation.
  - `client_config_sync` — reads each client config (Claude Desktop/Code/Cursor/Antigravity/OpenCode/Codex; mcpServers JSON, OpenCode JSON, Codex TOML) and compares the configured `command` against the npm package's bundled exe. If a client points at a divergent `.exe`, the check warns explicitly that **`npm install -g genexus-mcp@latest` will NOT update that instance**, citing the mismatch path. Directly addresses the "npm update was a ghost operation" complaint from the report.
  - `gateway_spawn_probe` now runs by default (was `--full`-only), so cold-install failures surface immediately. Probe failures that look like access-denial are tagged with the AppLocker hint inline in `detail`.

- **`status` exposes `pathSafetyWarn: boolean` in both modes** plus, when true, prepends the AppLocker remediation as the first `help` line. `ready: true` no longer hides the runtime risk.

- **`init --write-clients` advisory + GENEXUS_MCP_GATEWAY_EXE guard.**
  - When patching clients on Windows without `GENEXUS_MCP_GATEWAY_EXE` set, both interactive and non-interactive init add a help line explaining that the npx launcher resolves the gateway from `%LOCALAPPDATA%\npm-cache`, commonly blocked by corporate AppLocker, and pointing at `scripts/install.ps1`.
  - `patchClientConfig` refuses to write a broken path: if `GENEXUS_MCP_GATEWAY_EXE` is set but the file does not exist, throws `code: 'GATEWAY_EXE_MISSING'`. Both init flows catch that code specifically and return a dedicated non-truncated error envelope with the path checked and the two remediation options. Previously, init would silently write the dead path into six client configs.
  - The two `catch {}` blocks that swallowed real errors (`Failed to write configuration.` / `Interactive init failed.`) now surface the underlying `err.message` (sanitized).

- **`genexus-mcp update` detects client drift.** Beyond fetching the latest release tag, runs the same client-config scan as `doctor` and, when any client points at a divergent `.exe`, emits a WARNING help line: "N AI client(s) point at a gateway exe that is NOT this npm package — `npm install -g` will NOT update them. Mismatches: ... Re-run scripts/install.ps1 (or genexus-mcp init --write-clients) to resync." Same payload exposes `clientDrift[]` for tools that consume the structured envelope. Stops `update` from giving false reassurance after `npm install -g` while the actual exe in use stays stale.

- **`scripts/install.ps1` probes the installed exe before declaring success.** After extraction, runs `GxMcp.Gateway.exe --axi-spawn-probe` via `Start-Process -PassThru -WindowStyle Hidden`, waits 600ms, kills the probe. On `Access is denied` / `Acesso negado` / `0x80070005` / `UnauthorizedAccess` it aborts (rolling back any temp artifacts) with: the blocked path, an explanation that AppLocker default rules deny exec from `%APPDATA%` / `%LOCALAPPDATA%` / `%TEMP%`, remediation suggestions (admin install → `C:\Tools\GenexusMCP`; non-admin → explicit `-InstallDir C:\Apps\GenexusMCP`), `Get-AppLockerPolicy -Effective -Xml` for diagnosis, and event-log pointers (`Microsoft-Windows-AppLocker/EXE and DLL`, IDs 8003/8004). Other launch failures surface the real error instead of "extraction succeeded → done". The next user with this exact policy will be blocked at the installer with the right answer, not at "Failed to connect" hours later.

Internal: new shared helpers in `cli/lib/config.js` — `isPathLikelyAppLockerBlocked(exePath)` (returns the restricted zone name or null), `normalizeExePath(p)` (case-fold + slash-normalize for Windows path comparison), `readClientCommandEntry(client)` (extracts the `genexus` entry's `command` from `mcpServers` JSON, OpenCode JSON, or Codex TOML). Consumed by the launcher, doctor, update, and the patchClientConfig guard.

## v2.6.3 — 2026-05-20

Bug-fix pass uncovered by live-testing v2.6.2. Two gateway-side gaps prevented `lifecycle cancel` / `lifecycle status` from resolving when callers used the canonical `target=op:<jobId>` shape — exactly the call pattern documented in the tool help. Both close now.

### Fixed

- **`McpRouter.ResolveJobId` strips the `op:` prefix.** Callers pass `target=op:<jobId>` to lifecycle cancel/status; `ResolveJobId` returned the string verbatim, so `JobRegistry.Get("op:<id>")` always returned null, and cancel fell through to the OperationTracker path which doesn't track build/edit jobs — surface error: `"NotFound"` even when the job was registered and running. Now strips the prefix (case-insensitive, idempotent for non-prefixed inputs). 2 new unit tests in `LongPollTests`.

- **`lifecycle status target=op:<jobId>` consults JobRegistry before falling through to OperationTracker.** The previous order routed every `op:<id>` shape to `_operationTracker.BuildOperationStatus`, which is a different lifecycle (gateway-internal request handles, not async jobs) and reported `NotFound`. The status path now checks `JobRegistry.Get(operationId)` first and only falls back to OperationTracker when the id isn't a registered job. Cancel was already covered by the ResolveJobId fix; this closes the symmetric status/result gap.

### Internal

- Live test of v2.6.2 confirmed both fixes end-to-end against KB `AcademicoHomolog1` (build started → `lifecycle cancel target=op:<jobId>` returned `{status:"Cancelled"}` + Control:Cancel fanout → `lifecycle status target=<jobId>` returned `{status:"cancelled", summary:"Cancelled by client...", completed_at:"..."}`). Worker 408/408, gateway 254/254 (+2 ResolveJobId prefix-stripping tests).

## v2.6.2 — 2026-05-20

Observability + cancel reliability + pattern-parity harness. The three together close the "is the agent allowed to be assertive?" loop: writes now self-report which SDK path they took (so we know where parity regresses), `lifecycle cancel target=op:<id>` actually stops the worker (was previously a no-op for async builds/edits), and we ship the test harness that lets a contributor with a WWP-licensed KB verify byte-equivalence against the IDE.

### Added

- **`_meta.sdkPath` tag on every write response.** New `Helpers/WriteResultMeta.cs` attaches a coarse, idempotent label describing which write strategy the handler picked: `typed-sdk` (IDE-native setter), `typed-writer` (our typed helpers), `raw-xml` (XElement.SetAttributeValue / source replace), `sdk-pattern-engine` (IPatternEngine.ApplyPattern), `ops` (semantic-ops / json-patch), or `hybrid` (bulk batch with mixed paths). The tag is idempotent: a deep writer's specific value (e.g. `raw-xml` from `LayoutService.SetProperty`) is preserved when a wrapper later defaults to `typed-sdk`. The KPI we get from this is the first objective measure of how often each path is used — needed to track parity-with-IDE regressions over time.

- **`PatternParityHarness` + `PatternApplyParityTests`.** Five-dimension diff (generated family, PatternInstance XML, WebForm XML, Variables, Rules) between MCP-driven `apply_pattern` output and IDE "Right-click → Apply Pattern" output. Each dimension reports PASS/FAIL independently with a focused detail message (first-divergence index for XML, set-diff for collections). XML normalization sorts attributes alphabetically before comparison so serializer nondeterminism doesn't false-fail the test. `ParityReport.ToMarkdown()` emits a human-readable report. Integration test gated by `[LiveKbFact(requiresWWP: true)]` plus `GXMCP_PARITY_MCP_NAME` / `GXMCP_PARITY_IDE_NAME` env vars; 9 unit tests cover the diff dimensions on JObject fixtures so the harness itself stays regression-protected even when the live KB run is skipped.

### Fixed

- **`genexus_lifecycle action=cancel target=op:<id>` actually cancels async builds/edits.** Previously the worker-side `WorkerCancellationRegistry.Cancel(jobId)` returned `NotFound` because the original async command was dispatched without a `cancelToken` — only search/impact/analyze opted-in per-handler. Now: (a) the gateway injects `cancelToken=jobId` into every async command it starts (`Build/Build`, `Build/RebuildAll`, async edit commands); (b) the worker's `CommandDispatcher.Dispatch` blanket-registers the token once at entry so every handler running under it inherits a single shared CTS; (c) `WorkerCancellationRegistry.Register` is now refcounted so inner handlers that also register the same token (search/impact still do) share the registration without their `Dispose` stripping the outer scope's registration first. Net effect: a single `lifecycle cancel target=op:<id>` resolves the right CTS regardless of which handler is currently in flight.

### Internal

- `WriteResultMeta.TagSdkPath` is the single chokepoint. Instrumented at: `WriteService.WriteObject` / `ApplySemanticOps` / `ApplyJsonPatch` / `AddVariable` / `DeleteVariable` / `DeleteVariables` / `ModifyVariable` / `BulkWrite` (chokepoint: `WrapWithPersistedState`), `LayoutService.SetProperty` / `SetProperties`, `PatternApplyService.ApplyPattern` (tagged `sdk-pattern-engine`). Bulk results inherit the per-item path or report `hybrid` when items disagree.
- `Helpers/WorkerCancellationRegistry.cs` rewritten around a `RefCount`-bearing `Entry` so `Register` / `Scope.Dispose` are nestable; the dictionary key remains the token string for `O(1)` `Cancel`. Test seam (`Reset`) unchanged; existing per-handler `using` blocks in `CommandDispatcher` still work and now share state with the new outer scope.
- `Program.cs` async build dispatch (line ~1654) and async edit dispatch (line ~1853) both inject `cancelToken = job.Id` into the worker command's params. The control fan-out path that already lived at `Program.cs:1474` continues to fire — now it actually finds the registration.
- Tests: worker 399 → 408 (6 `WriteResultMetaTests` + 5 `WorkerCancellationRegistryNestableTests` + 9 `PatternParityHarnessTests` − 1 LiveKbFact skipped on CI; net +9 enabled). Gateway 252/252 unchanged. All three additions are pure-data unit-testable so the suite stays fast and CI-green.

## v2.6.1 — 2026-05-20

`genexus_create_object` now creates **any** object the GeneXus IDE can create, and it grew a real Domain path. Reported by Edgar: trying to create a `UserStatus` enumerated domain via the MCP failed with "MCP doesn't support domain creation"; this release closes that gap and the underlying gap that produced it — the tool only knew about a hardcoded list of types.

### Added

- **`genexus_create_object type=Domain` — full domain creation, including enumerated.** New optional fields: `dataType` (`Character` default, `VarChar`, `Numeric`, `Date`, `DateTime`, `Time`, `Boolean`, `LongVarChar`, `Blob`, `Image`, `GUID`), `length`, `decimals`, `signed`, `description`, `basedOn` (inherit from an existing domain), and `enumValues` (array of `{name, value, description?}` for enumerated domains). For Character/VarChar domains the `value` must be a quoted literal (e.g. `"\"A\""`). Response `_meta` echoes back what was applied plus an `enumHint` so the agent can verify via `genexus_analyze`. Tested live against the Edgar case: `UserStatus` with three enums (`Active="A"`, `Inactive="I"`, `Blocked="B"`) — round-trips through `genexus_analyze` / `genexus_inspect`.

- **Generic type resolution covers every IDE-creatable object.** New `ResolveObjectTypeGuid` walks two paths: a typed-descriptor table (Transaction, Procedure, WebPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Theme, Image, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, API, URLRewrite, MiniApp, SuperApp, DesignSystem, ColorPalette, OfflineDatabase, DataView, Group, Language) and a reflective fallback over `Artech.Genexus.Common.ObjClass` static Guid fields (Dashboard, SDPanel, Query, QueryDashboard, WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, ThemeColor, ThemeTransformation, DesignSystemClass, WorkWithDevices, WorkWithWeb, WikiPageKBObject, TranslationMessage, DataStoreCategory, GeneratorCategory, DeploymentUnitCategory). Aliases recognised: `StructuredDataType` → SDT, `BusinessProcessDiagram` / `BPD` → WorkflowDiagram, `PanelForSD` → SDPanel. The previous hardcoded if/else chain covered eight types; the new resolver covers everything `ObjClass` exposes.

- **`Helpers/DomainPropertyApplier.cs` — reflective Domain plumbing.** Applies `Type` / `Length` / `Decimals` / `Signed` (eDBType enum on real SDK, string on test fakes — both handled), `DomainBasedOn`, and `EnumValues` (built via `Artech.Genexus.Common.CustomTypes.EnumValue` / `EnumValues` + persisted via `Artech.Genexus.Common.Properties+ATT.SetEnumValues` on the IPropertyBag). The resolved `Type` and `MethodInfo` are cached statically so batch domain creation doesn't rescan loaded assemblies. Falls back to a direct `EnumValues` property setter if the SDK helper isn't resolvable.

### Internal

- Shared the canonical-name → `eDBType` table: `AttributeTypeApplier.CanonicalToEdb` is now `internal` (was private) and `DomainPropertyApplier.ApplyPrimitive` consumes it — one synonym table, two callers.
- `ResolveType` and `ResolveFromObjClassField` prefer assemblies whose name starts with `Artech.Genexus.Common` before falling back to a full `AppDomain` scan; on a GeneXus host with 100+ loaded assemblies, that drops resolution from N-way to one. `_typeGuidCache` (object-class Guids), `_typeCache` (CustomTypes), `_setEnumValuesMethod`, and `_objClassType` cache the resolutions for batch calls.
- `TrySetProperty` uses `Convert.ChangeType` against `Nullable.GetUnderlyingType(prop) ?? prop` instead of branching on boxed int / bool — naturally covers long, short, double if the SDK adds them.
- `OperationsRouter.ConvertToolCall` forwards the new Domain options verbatim (`dataType`, `length`, `decimals`, `signed`, `description`, `basedOn`, `enumValues`); `CommandDispatcher` passes the full `args` JObject to `ObjectService.CreateObject(type, name, options)` so future option bags don't need to be threaded through the gateway-router schema.
- Help catalog and `tool_definitions.json` reworked: Domain section with the exact Edgar `UserStatus` example, full type enumeration in the description, schema fields for the new options. Discovery golden fixture regenerated.
- Tests: `DomainPropertyApplierTests.cs` covers the fake-SDK path (Type/Length/Decimals/Signed string fakes, ApplyDomainBasedOn, ApplyEnumValues hard-fail when SDK types aren't loadable, empty-list early return). Worker 388/388, gateway 252/252.

## v2.6.0 — 2026-05-20

WorkWithPlus on a bare WebPanel now works end-to-end. Apply the pattern, get a host plus a real layout projected onto the WebPanel's WebForm. Edit the host's PatternInstance and the projection updates automatically. Plus a new SDK probe tool, honest no-op detection on unsupported target shapes, and a pile of fixes uncovered during the investigation.

### Added

- **`genexus_apply_pattern` on a WebPanel target — full direct-attach + projection.** Apply WorkWithPlus to an empty WebPanel and the MCP attaches a `WorkWithPlus<X>` host with a real PatternInstance derived from a registered KB template, then runs the SDK's `IPatternBuildProcess.UpdateParentObject` so the WebPanel's WebForm reflects the projected layout immediately. The original WebPanel stays in place — no rename, no destruction. Pass `settings.template` matching a `WorkWithPlus for Web Template` object in your KB (common names: `MatIsoTemplate`, `TransactionResp2`, `PopoverEmpty`). When omitted, an available template is auto-discovered. The response includes `availableTemplates` so the agent can switch templates on a second call without guessing.

- **Auto-project on `genexus_edit` of a host's PatternInstance.** Every successful pattern-part edit on a `WorkWithPlus<X>` host automatically re-runs the projection step against its parent WebPanel — agents shape the screen via PatternInstance XML and the WebPanel updates in the same call. The response carries a `projection` block (`status`, `parent`, `parentType`, `note`) so callers can confirm the layout reflects the edit. The index cache for the affected parent is refreshed in the same code path, so a follow-up `list_objects` / `inspect` / `query` sees the new state without waiting for reindex.

- **`genexus_sdk_probe` — first-class scanner of loaded GeneXus SDK assemblies.** Dumps every public type, method, property, constructor, and field across `Artech.*`, `Genexus.*`, `DVelop.*`, and `GeneXus.*` assemblies to `docs/sdk-probe/`: `raw.json` (full structured tree), `INDEX.md` (per-namespace navigation), `generators.md` (filtered to types whose name suggests they participate in code generation — `Generator`, `Builder`, `Apply`, `Refresh`, `Update`, `Project`, `Engine`, `Helper`, `Service`, `Resolver`, …). Built for SDK exploration: investigators can grep the JSON or read the markdown without writing one-off reflection code. Picks output via `GX_MCP_SDK_PROBE_DIR`, the repo's `docs/sdk-probe/` if found, or `%TEMP%/gxmcp_sdk_probe/`.

- **`genexus_apply_pattern` returns `generatedObjects` honestly.** Previously empty on Transaction targets even when the engine had created the full WW family. Now resolves the canonical family (`WorkWithPlus<X>`, `WW<X>`, `View<X>`, `ExportWW<X>`, `ExportReportWW<X>`, `Prompt<X>`) via name lookup and surfaces what's actually present. The host is also exposed as a top-level `patternHost` field for quick navigation to the editable PatternInstance.

- **`apply_pattern` projects `settings` JObject onto SDK `ApplySettings` on re-apply.** Best-effort property mapping: case-insensitive name match, recursive on nested objects, type coercion for primitives and enums (string or numeric). Unmapped keys are logged, not thrown. Lets agents pass partial settings without knowing the full SDK schema.

- **`genexus_create_object type=WebPanel` includes a structured `_meta.patternHint` and `nextStep`.** Tells the agent both real paths to a WorkWithPlus screen — direct WebPanel attach with a template, or Transaction-driven family generation — with ready-to-issue tool call shapes inline. The hint is generated for `WebPanel` and `SDPanel` types; other types continue to receive only `_meta.seeded`.

- **`genexus_edit` surfaces `EditingWebFormUnderPattern` warning.** When the agent edits the `WebForm` / `Layout` of an object covered by a WorkWithPlus PatternInstance, the response includes a warning identifying the pattern host. The edit still completes — this is advisory so the agent realises the next pattern apply may overwrite the visual edit and can choose to edit `PatternInstance` instead.

- **`genexus_apply_pattern` returns `status: "NoOp"` with an actionable recommendation when the SDK engine no-ops on a target.** Used to silently report `Success` while doing nothing on Procedure/SDPanel targets (and on WebPanel pre-fix). Now carries `noOpReason` explaining the SDK's behaviour and `recommendation` pointing at the Transaction path, plus an optional `sdkProbePath` when `GX_MCP_PATTERN_PROBE=1` is set.

- **`genexus_whoami` reports update availability as structured data.** The response includes an `update` block with `currentVersion`, `latestVersion`, `updateAvailable`, `checkedAt`, `releaseUrl`, `command`, and `restartRequired`. AI agents can detect a pending upgrade in the same call where they read the KB context, then proactively offer the upgrade command — no longer have to rely on the stderr-style `notifications/message` the user might miss. The data comes from a 24h-cached GitHub release check the gateway runs in the background on `initialize`; reading it is zero-latency. Set `GENEXUS_MCP_NO_UPDATE_CHECK=1` to disable the background check (corporate networks that block the GitHub API). Documented as the "Self-update protocol (LLM-facing)" section in `AGENTS.md`.

### Fixed

- **`genexus_apply_pattern` no longer drops `pattern` and `settings`.** The gateway's `OperationsRouter` wrapped the original arguments under `@params` for `apply_pattern`, `apply_template`, `bulk_edit`, and `diff`, but the worker dispatcher read fields at the top level — so `args["pattern"]` was always null and the tool returned `"Pattern key is required."` even when the caller had passed one. The dispatcher now unwraps the nested params object once, preserving any outer routing fields as a fallback.

- **`genexus_apply_pattern reapply=true` works on installs that lack the `ApplyPattern(PatternInstance, ApplySettings)` overload.** Previous logic threw `InvalidOperationException` because the reflection probe disambiguated overloads using `IsAssignableFrom(KBObject)` — but `PatternInstance` inherits from `KBObject`, so both overloads bound to the same field and the reapply slot stayed null. Disambiguation now uses exact-type matching, and `TryReapplyWithFallback` replays the void overload (which the SDK treats as a re-apply when an instance already exists) when the typed overload is missing.

- **`genexus_delete_object` removes the entry from the search index.** Previously the deleted object stayed visible to `list_objects` / `query` for several minutes until a full reindex caught up. The index cache's `RemoveEntry(type, name)` is now called inline after a successful SDK delete; results from index-backed tools reflect the deletion immediately.

- **`genexus_apply_pattern` updates the index cache for every generated family object.** Same gap as delete — after a Transaction-driven apply, the freshly generated `WorkWithPlus<X>`, `WW<X>`, `View<X>`, and export procedures were invisible to `list_objects` until a reindex. Each generated object is now `UpdateEntry`'d via the index cache before the apply response returns.

- **`genexus_worker_reload` reliably copies new binaries.** Previous helper used a single `Copy-Item -ErrorAction SilentlyContinue` that masked a real race — the gateway respawned the worker faster than the helper could copy, re-locking the .exe, and the silent failure went unnoticed. The PowerShell helper now retries up to 20 times at 500 ms intervals, kills any worker that respawned mid-copy so the gateway brings a clean one up with the new bits, and writes `worker_reload.last_result.json` next to the published binaries so callers can diagnose silent failures. The response is now `"Accepted"` (was misleading `"Success"`) and points at that status file.

### Changed

- **`apply_pattern` on existing-instance targets skips the engine reapply call.** The void `ApplyPattern(PatternInstance, ApplySettings)` overload throws `NullReferenceException` on the GeneXus 18.0.7 SDK whenever the IDE service container isn't around. The MCP now goes directly to `IPatternBuildProcess.UpdateParentObject` (the projection step), which works headlessly. The behavioural surface is identical from the caller's perspective: the host's PatternInstance is re-applied onto the bound parent. Engine `ReapplyCalls` are no longer made on this path.

- **`genexus_apply_pattern` tool description and `genexus_create_object` patternHint rewritten.** Both now document the two real target shapes (Transaction-driven family generation and direct WebPanel attach with template), with concrete `settings.template` examples. Previously the documentation either over-promised (`Pattern attaches in-place to any WebPanel`) or under-promised (`apply WWP only on Transaction`). The new copy matches the actual SDK behaviour after the fix.

### Internal

- New helpers, no public-API impact: `Services/WwpProjectionHelper.cs` (shared `TryProjectHostOntoParent` + parent resolution), `Services/SdkSurfaceProbe.cs` (reusable SDK scanner), `Tests/LiveKbFactAttribute.cs` (xunit `[Fact]` subclass that env-gates on `GXMCP_TEST_KB` / `GXMCP_REQUIRE_WWP` for integration smokes).
- `docs/sdk-probe/` directory carries the SDK map plus a `wwp-projection-discovery.md` narrative of dead ends and the working path. `raw.json` is gitignored (~17 MB, regenerated each apply); `INDEX.md`, `generators.md`, and `README.md` are tracked.
- Pattern-write path now exposes `WriteService.ForcePatternPartDirty` and `WriteService.ApplyPatternDataFromXml` publicly so `WwpProjectionHelper` can reuse the same Dirty/Mode bookkeeping the regular pattern write uses.
- New `Microsoft.Build.Framework` reference in the worker csproj so the MSBuild-style `WWP_ApplyTemplate` task's `IBuildEngine` contract resolves (the task's ctor still fails headlessly; we keep the route as a fallback in case future SDK versions relax the requirement).
- Tests: worker 379 → 382 (3 new `ApplySettings` projection tests, integration smokes env-gated via `LiveKbFact`), gateway 252 → 252 (golden discovery fixture regenerated for the new `genexus_sdk_probe` tool). All green; 2 worker tests skipped by design when `GXMCP_TEST_KB` is unset.

## Unreleased

(none)

## v2.5.3 — 2026-05-19

### Added

- **`genexus_create_popup`** — author a popup WebPanel from a domain-level
  spec in a single tool call. Pass `title`, `description`, an array of
  `inputs` (radio / combo / text), `buttons`, plus `inParms` / `outParms` —
  the MCP emits the matching WebPanel with rules, variables, layout, and
  events parts wired together. Radio and combo inputs are emitted inside
  `Form type="layout"` so they render editable in the browser. Inputs can
  declare a `showWhen` predicate (e.g. `"answer == 'Y'"`) to bind their
  group's visibility to another input's value via a generated `Event
  Refresh`. Existing webpanels are updated in place; the generated layout
  is self-validated against the layout-quality scanner before persisting.

### Internal

- `Helpers/PopupLayoutBuilder.cs` is a pure XML/source builder with no SDK
  dependency, fully unit-testable. `Services/PopupTemplateService.cs`
  orchestrates `ObjectService.CreateObject` + `WriteService.AddVariable` /
  `WriteObject` against an `IPopupBackend` seam.
- Tool schema budget raised 6000 → 6300 tokens for the popup spec
  sub-schema. Discovery golden fixtures regenerated.
- Test surface: worker 365 → 379 (14 new in `PopupTemplateServiceTests`).

## v2.5.2 — 2026-05-19

This release brings the MCP closer to feature parity with the GeneXus IDE.
Three new tools, one major routing fix, four new layout-quality warnings, and
theme introspection. See `docs/mcp-roadmap-ide-parity.md` for the design
context.

### Added

- **`genexus_preview`** — render a WebPanel via headless Chrome (uses
  `chrome-devtools-axi` CLI). Auto-fills the launcher form, navigates to the
  target, and captures HTML / accessibility tree / screenshot / console
  errors. Optional baseline diff against
  `publish/worker/preview-baselines/<name>.a11y.json`. Config at
  `publish/worker/preview.config.json` (auto-created on first call).
  Structured errors for build failure, auth required, launcher missing, CLI
  missing, unsupported object type.

- **`genexus_apply_pattern`** — apply a GeneXus pattern (e.g. WorkWithPlus)
  to a parent object, equivalent to the IDE's "Right-click → Apply Pattern"
  menu. Invokes `Artech.Packages.Patterns.PatternEngine.ApplyPattern`
  directly (first-time apply or re-apply via `reapply: true`). Returns
  `{status: "pattern_unavailable"}` when the package or license is missing,
  rather than throwing.

- **Theme introspection via `genexus_inspect`.** Calling inspect on a
  `ThemeForWeb` or `ThemeForSmartDevices` object now returns the theme's
  class catalog: `{name, parent, isPredefined, category, controlTypes}` per
  class. Default 100-class window (catalogs can exceed 600 classes); pass
  `include=["classesFull"]` to get the full CSS rule and serialized
  property bag per class. Lets callers write `Class="AttributeBlue"` by
  name instead of resolving theme GUIDs by hand.

### Fixed

- **`gxButton OnClickEvent` for custom events.** Raw-XML writes that emitted
  `OnClickEvent="'MyEvent'"` were silently ignored by the HTML generator,
  which only reads the per-element XML attribute the SDK assigns (`Event`
  for `gxButton`, `eventGX` for `gxAttribute` / `gxImage`). The MCP now
  routes descriptor-named properties through the SDK's
  `PropertiesObject.SetPropertyValue` so the canonical XML attribute is
  emitted. Applies on every layout save; idempotent.

### Added — layout-quality warnings (`genexus_inspect.layoutGotchas`)

Four new static checks for patterns that compile clean but render wrong:

- `GotchaGxAttributeMissingDataField` — `<gxAttribute>` with neither
  `AttID` nor `DataField`. The SDK keeps a phantom control that binds to
  nothing.
- `GotchaUnknownControlType` — `gxAttribute ControlType="…"` value not in
  the SDK whitelist (catches typos like `RadioButton` without the space).
  Generator silently falls back to `Edit`.
- `GotchaWebComponentMissingObjectCall` — `<gxEmbeddedPage>` /
  `<gxWebComponent>` without `ObjectCall`. Renders an empty `<div>` at
  runtime.
- `GotchaCellOutsideTable` — `<cell>` or `<row>` not nested under a
  `<table>`. Generator wraps or drops silently.
- `GotchaDuplicateControlName` — two elements share an `id`; SDK
  auto-renames via `GetUniqueName` on save, so caller references to the
  original id break silently.

### Internal

- New `WebFormPreSaveValidator` wraps the SDK's
  `WebFormHelper.Validate(part, OutputMessages)` validator. Standalone for
  now; a follow-up release will surface validation errors directly in the
  edit response with a force-write escape hatch.
- `ContractGoldenHarness` gained a `GXMCP_UPDATE_GOLDEN=1` environment
  switch to regenerate discovery fixtures after intentional tool schema
  changes — saves a round-trip when adding tools.
- Discovery golden fixtures regenerated for the new tools.
- Tool schema budget: 5300 → 6000 tokens (raised again to 6300 in v2.5.3
  for `genexus_create_popup`).
- Test surface: worker 365 → 379 (+1 skipped live integration test for
  WorkWithPlus license), gateway 250 → 252.

## v2.5.1 — 2026-05-19

### Added

- **`genexus_inspect include=["variables"]` now returns `layoutAttIdsInUse`** (FR#3
  2026-05-19): array of `var:N` / `att:N` references already used in the WebForm
  layout. Lets the agent pick the next free slot when authoring new
  `<gxAttribute />` bindings instead of guessing var:N by position+offset (which
  doesn't hold once the WWP pattern adds system vars). Source:
  `AnalyzeService.cs` scans `WebFormPart.Document.OuterXml`.

- **`genexus_add_variable typeName` accepts SDT / BC / Domain bare names** (FR#4
  2026-05-19): previously rejected `SdtAluUniGraInfo` with `UnknownType` listing
  only primitives. Now bare identifiers (non-primitive, no parens) route through
  `VariableInjector.ResolveTypeObject` and bind via `BindVariableToSdt` /
  `BindVariableToBC` / `DomainBasedOn`. If the KB doesn't have a match, returns
  a clear `UnknownType` with the bad name in the message (no silent NUMERIC
  fallback). Existing `&Foo` (explicit domain prefix) and primitive paths
  unchanged.

- **`genexus_inspect include=["controls"]` fills `name` for `gxAttribute` /
  `gxButton` controls that omit `id` / `ControlName`** (FR#5 2026-05-19):
  synthetic `name = "{type}@{dataBinding}"` (e.g. `gxAttribute@var:8`). Gives
  the agent a stable handle to pass to `genexus_layout set_property`. Previously
  these entries had `name: null`.

- **`VariableInjector.GetVariableInternalId` now resolves the real layout id**
  (FR#3 fully fixed 2026-05-19): `Variable.Id` is the C# instance property the
  SDK uses to back `AttID="var:N"` references in layout XML — confirmed via
  live probe against ListaAtiCPAlunoUniGra (`TotalHorasCredito.Id=22` matches
  `AttID="var:22"`, `SaldoHoras.Id=33` matches `var:33`, etc). The previous
  implementation tried `GetPropertyValue("Id")` which queries the Properties
  metadata bag and returns null — only C# reflection on the instance surfaces
  the value. Helper now reflects `Id` first, falls back to bag keys, and only
  drops to enumeration-position when both fail. System vars Today/Time/
  Pgmname/Pgmdesc resolve to 1/2/3/4 (WWP creates them first); deleted
  variables leave gaps in the sequence. Knock-on: every consumer of this
  helper — `genexus_inspect.variables[].internalId`, `WebFormSchemaHints.
  LookupVarNameById`, `LayoutGotchaScanner` shadow-detection — now returns
  truthful values instead of position-based guesses.

- **`genexus_inspect include=["variables"]` now returns `layoutGotchas`** (FR#1 +
  FR#2 2026-05-19): static analysis array warning about layout patterns that
  compile clean but break at runtime. Currently detects two gotchas — see
  `LayoutGotchaScanner.cs`:
  - `GotchaGxButtonHtmlFormCustomEvent`: `gxButton OnClickEvent="'Custom'"` in
    `<Form type="html">` will be ignored by the HTML generator (always wires
    Enter regardless). Workaround suggestion points to `<gxBitmap eventGX="...">`
    or moving to `<Form type="layout">` with `<action onClickEvent="...">`.
  - `GotchaGxAttributeHtmlFormDiscreteReadOnly`: `gxAttribute ControlType="Radio
    Button" | "Combo Box"` inside `<Form type="html">` always renders disabled.
    The html-form generator does not emit editable radio/combo widgets — the
    original hypothesis that this was caused by variable-name shadowing of a
    transaction attribute was DISPROVED by a live probe (renaming the bound
    variable did not change the render). Workaround suggestion: move the
    control to `<Form type="layout">` with the WWP table pattern, use a User
    Control, or render raw HTML `<input type="radio">` via `gxTextBlock
    Format="HTML"` + JS wiring to a hidden gxAttribute (default ControlType is
    editable in html forms).

  Both gotchas are not "MCP bugs" per se — they're GeneXus HTML generator
  behaviors the agent can't change. But surfacing them at inspect time skips
  the build+browser smoke cycle that previously revealed them. Tests:
  `LayoutGotchaScannerTests` (7 new cases).

## v2.5.0 — 2026-05-18

### Fixed

- **`PatchService` reported `Failed` when the auto-reconciler legitimately rewrote
  `childrenOrderedList` during a pattern write**: PatchService's
  `VerifyPersistedSource` ran a byte-level comparison of `finalCode` (the
  pre-reconciler input) vs the persisted XML (post-reconciler). When the
  reconciler added/removed/reordered list entries — its whole purpose — the
  verify flagged the difference as a divergence, triggered a fallback write,
  and returned `error: "Patch write fallback failed after persistence
  mismatch"` even though the save was correct. Pattern parts now skip the
  redundant PatchService-level verify and trust WriteService's internal
  `XmlEquivalence` check (which runs inside `WritePatternPart` after the SDK
  save). Same fix would surface SDK attribute-reordering as a false negative.
  Response now carries `persistedVerifyNote` explaining the routing.

- **`<userAction>` was unknown to `PatternChildOrderReconciler`**: caused
  every `TableActions` row that mixed standard and user actions to land in
  `skipped` (no typeCode known), leaving the list out of sync. `<userAction>`
  is a peer of `<standardAction>` — same row, same context-sensitive typeCode
  (17 selection / 18 transaction). Reconciler now treats them identically.
  Custom buttons like "Duplicate"/"Audit"/"Export" MUST be `<userAction>` (only
  `Trn_Enter`/`Trn_Cancel`/`Trn_Delete` are registered standard actions on a
  WorkWithPlus transaction; the SDK rejects unknown `<standardAction name>`
  during validated operations like `genexus_properties set`).

- **Singleton kinds (`<orders>`, `<grid>`) were incorrectly treated as
  "missing identifier" by `PatternChildOrderReconciler`**: `GetIdentifier`
  returned `string.Empty` for them (correct — their entry shape is
  `{level};{typeCode};` with an empty id slot), but the caller's guard was
  `string.IsNullOrWhiteSpace(identifier)` which lumped empty-string and null
  together. Result: every `TableFiltrosFundo` and `TableGrid` landed in
  `skipped` and never got a list, so the IDE could hide their children.
  Changed the guard to `identifier == null` so the intentional empty slot
  survives.

### Added

- **Auto-reconcile `childrenOrderedList` on pattern writes** (`PatternChildOrderReconciler`):
  WorkWithPlus stores per-parent rendering order in a `childrenOrderedList`
  attribute that the IDE follows blindly — children missing from the list are
  hidden, stale entries leave ghost slots. Callers (LLMs in particular) that
  add/remove/move pattern children would need to keep that attribute in sync
  by hand. Now `WritePatternPart` walks the parsed XML, rebuilds every
  `childrenOrderedList` from the actual child order, **invents the list when
  the parent has none** (so new containers added by callers automatically
  render in the IDE), and surfaces the diff in the response under
  `childrenOrderedListReconciliation` with `(created)` / before-after entries
  plus a skip list naming any parents where type-code or identifier could not
  be inferred — actionable signal that those subtrees may not render until
  the caller corrects the XML. Element-kind → typeCode table covers the
  common WWP nodes (table 01/02 context-aware, textBlock 27, errorViewer 28,
  attribute 22, gridAttribute 23, descriptionAttribute 25, standardAction
  17/18 context-aware, filterAttribute 12, order 13, orders 30, rule 56, grid
  31, eventBlock 75). Identifier extraction handles the composite shape
  inside `<order>` children and the empty-identifier convention for singleton
  kinds (`<orders>`, `<grid>`). Inherited area-code (level 2 vs 4) traverses
  the ancestor chain so a newly-added container picks up the right value
  even when its closest siblings don't have a list yet. Blocklist excludes
  structural elements that don't participate in the ordering scheme
  (`<instance>`, `<transaction>`, `<level>`, `<selection>`, `<WPRoot>`,
  `<rules>`, `<events>`, `<steps>`, `<parameters>`, `<filterAttribute>`).
  Covered by 11 unit tests in `PatternChildOrderReconcilerTests`.

### Fixed

- **Pattern (`PatternInstance` / `PatternVirtual`) writes silently no-op'd —
  `WritePatternPart` reported `Success` but the KB never changed**:
  Three root causes stacked:
  1. `ApplyPatternEnvelope` called `KBObjectPart.DeserializeFromXml(string)`,
     which on `Artech.Packages.Patterns.Objects.PatternInstancePart` only
     round-trips the `<Properties>` bag (`IsDefault` etc) — *not* the pattern
     data. Live SDK reflection showed the actual mutation entrypoint is
     `DeserializeDataFrom(XmlElement)` (inverse of `SerializeDataTo(XmlElement)`).
     The XmlElement must be the **parent** that *contains* the `<instance>`
     child; passing `<instance>` directly persists an empty `<instance/>`.
  2. After deserialize, the part still had `Mode == Unchanged` and
     `Dirty == false`, so `KBObjectManager.PrepareSave` short-circuited even
     under `KBObjectSavePreferences.ForceSave`. Mirrored the
     `WriteVisualPart` fix (lines ~1921–1933): explicitly set `part.Dirty =
     true` and `part.Mode = Modified` via reflection before save.
  3. `resolvedObject.EnsureSave(true)` was a no-op for the same reason —
     replaced with `resolvedObject.Save(KBObjectSavePreferences { ForceSave =
     true, ForceSaveDefaultParts = true, SkipValidation = true })`. Also
     promoted the post-save flush to synchronous (`ScheduleFlush(force: true)`)
     so the verification read sees the bytes on disk.
  Verified live against `WorkWithPlusAcao.PatternInstance` in
  AcademicoHomolog1: `mode: full` and `mode: patch` both persist with
  `persistedVerified: true` and round-trip cleanly via `genexus_read`.

- **`genexus_edit` `mode: patch` rejected pattern parts (`PatternInstance` /
  `PatternVirtual`)**:
  `PatchService.ReadSourceFast` only handled `VariablesPart`, the virtual
  `Structure` part, `WebFormXmlHelper.IsVisualPart`, `ISource`, and a reflective
  `Source`/`Content` fallback. Pattern parts (WorkWithPlus and other patterns)
  expose their editable XML through `PatternAnalysisService.ReadPatternPartXml`
  on a resolved WWP instance, so `GetPart` on the source object returned `null`
  or a non-source part and patch-mode failed with
  `"Part does not expose text source"`. `mode: full` already worked because
  `WriteService.WriteObject` routes pattern parts through `WritePatternPart`.
  Wired `PatternAnalysisService` into `PatchService` and, when
  `IsPatternPart(partName)` matches, route the read through
  `ReadPatternPartXml`; the write side already dispatches correctly, so the
  existing `WriteObject` call in `ApplyPatch` now persists pattern patches
  end-to-end (verification reads reuse `ReadSourceFast`).

- **`Documentation` / `Help` parts silently failed to persist via `genexus_edit`**:
  `DocumentationPart` and `HelpPart` do not implement `ISource`, and their `Content` /
  `EditableContent` properties are read-only on the part wrapper. `WriteService`'s
  generic fallback only probed `Source` / `Content`, so writes hit the
  `"No suitable method found to update part content"` warning, returned a misleading
  `status: "Success"` with the SHA-256 of an empty string as `persistedHash`, and
  did not mutate the KB. Added `TrySetDocumentationContent` which writes through
  `HelpPart.HtmlContent` (HelpPart route) or `part.Page.EditableContent` →
  `Content` → `StorableContent` → `InvariantContent` (WikiPage route),
  instantiating a `WikiPage` from the part's `Module` when `Page` was null.
  Also replaced the bogus `documentation` GUID in `PartAccessor` (the placeholder
  `26323631-…` that decodes to ASCII junk) with the real
  `BABF62C5-0111-49e9-A1C3-CC004D90900A` read from the `[Guid]` attribute on
  `DocumentationPart`.

## v2.4.3 — 2026-05-18

### Fixed

- **KB reopen warning after MCP edits (`11.0.0.0` vs GeneXus 18)**:
  worker now normalizes `.gxw` metadata right after `KnowledgeBase.Open(...)` using
  active installation from `GX_PROGRAM_DIR`. It updates `InstallationPath`,
  `ProductVersion`, `FriendlyVersion`, and `VersionNumber` so IDE reopen no longer
  warns about mismatched GeneXus installation after MCP writes.

## v2.4.2 — Unreleased

### Fixed

Systematic bug hunt following the v2.4.1 BC patches surfaced ten latent bugs sharing
the same fault patterns. All ten are fixed in this release; full worker test suite
(314/314) and gateway suite (241/241) green.

- **SDK bookkeeping bypass — `VisualStructureService` dropped attributes/levels on save**:
  `Services/Structure/VisualStructureService.cs` constructed `TransactionLevel` and
  `TransactionAttribute` via `new ...()` + `parent.Levels.Add(...)` / `parent.Attributes.Add(...)`.
  This is the exact pattern fixed in v2.4.1 for `TransactionDslParser` — items bypass SDK
  bookkeeping and are silently lost on `EnsureSave`. Now uses the typed `sdkLevel.AddLevel(...)`
  and `sdkLevel.AddAttribute(...)` methods.
- **SDK bookkeeping bypass — `RefactorService` dropped copied variables**:
  `Services/RefactorService.cs` used `Activator.CreateInstance(sourceVar.GetType(), ...)` plus
  `targetVarPart.Variables.Add(...)`. Replaced with the typed `VariablesPart.Add(string)`
  overload that registers the variable with the SDK and returns the linked instance.
- **SDK bookkeeping bypass — new sub-levels in `TransactionDslParser`**:
  The sub-level creation path (mirror of the attribute path already fixed in v2.4.1) used
  reflection + `Levels.Add`. Now uses `new TransactionLevel(parent)` + `parent.AddLevel(...)`.
- **`SdtDslParser` silently lost item types when SDK proxy didn't expose `eDBType`**:
  Two sites called `Assembly.GetType("Artech.Genexus.Common.eDBType")` and used the result
  without a null-check; subsequent `GetMethod(..., eDBTypeT)` returned null and the `Invoke`
  NRE was swallowed by an outer `catch`. SDT items round-tripped with the default type instead
  of the requested one. Added `ResolveEDbType()` helper that probes the preferred assembly,
  falls back to the statically-linked type, then scans `AppDomain` — and logs a structured
  warning when none resolves (same template as v2.4.1's `TransactionAttribute` fix).
- **Reflection AmbiguousMatchException risk in Report layout and SDT propagation**:
  `Helpers/ReportLayoutHelper.cs` (Band `Name`, items `Name`/`ControlName`,
  `Items`/`Elements`/`Controls`/`Components` collection probe) and `Helpers/SdtModelPropagation.cs`
  (`EntityKey.Id`) used `Type.GetProperty(...)` without `BindingFlags`, which can throw
  `AmbiguousMatchException` or pick the wrong shadowed member on the Artech SDK class hierarchy.
  This is the same fault that v2.4.1's `AttributeTypeApplier` fix addressed. Extracted
  `AttributeTypeApplier.GetPropertyUnambiguous(Type, name)` as a shared helper and routed all
  unsafe call sites through it.
- **`Split(':')` array-bounds crash on malformed `Type:Name` inputs**:
  `Services/ObjectService.cs:FindObject` and `Services/VisualizerService.cs` (two sites) blindly
  indexed `parts[1]` after `Split(':')`; inputs like `"Type:"` or `":Name"` from agents threw
  `IndexOutOfRangeException`. Guarded all sites; `FindObject` logs and returns `null` on
  malformed input. `Services/IndexCacheService.cs` (reference-graph enrichment) had the same
  pattern; now uses `IndexOf` + `Substring` with explicit bounds.
- **DSL parser missed `*` key-marker when it appeared on the type side**:
  `Helpers/DslParserUtils.cs` only stripped the trailing `*` from `node.Name`. Inputs like
  `TrnId : Numeric(4)*` left `*` in `node.TypeStr`, which `AttributeTypeApplier.Parse` rejected
  — the type spec was silently dropped. Now strips `*` from both sides and still marks `IsKey`.
- **DSL parser preserved `&` prefix on attribute names**:
  Inputs like `&UserLogin : Numeric` left `node.Name == "&UserLogin"`, causing case-insensitive
  attribute lookups to miss and duplicate-create the attribute. Verified that Transaction/Table/SDT
  structure DSLs treat `&Name` as an attribute name (not a variable reference), so stripping is
  safe. Three new regression tests in `DslParserUtilsTests`.

### Verified-but-unchanged

- **`Parsers/TableDslParser.cs` attribute creation path**: probed the runtime `TableStructurePart`
  via reflection; no typed `AddAttribute(...)` method exists on this SDK type (unlike
  `TransactionLevel`). Kept the legacy `ctor + Attributes.Add` pattern and added an explicit
  comment documenting the verification gap so a future SDK upgrade can revisit.

## v2.4.1 — 2026-05-16

### Fixed

- **`genexus_properties` set could not toggle Business Component (and other typed bool/enum properties)**:
  `PropertyService.SetProperty` passed the raw string value straight to the SDK's
  `SetPropertyValue(string, object)` overload. For properties whose underlying CLR type is `bool`
  or an enum (e.g. `idISBUSINESSCOMPONENT`, `idISBCEJB`), the SDK threw
  `InvalidCastException: Conversão especificada não é válida` regardless of the value form
  (`"True"`, `"true"`, `"1"`). The setter now coerces the string to the property's declared type
  (probed via `Definition.Type` / current value), falls back to `SetPropertyValueString` for
  textual properties, and only then to the untyped overload.
- **Structure DSL silently dropped newly-added Transaction attributes**: writing a Transaction's
  `Structure` part with new attributes returned `status:Success persistedVerified:false` and the
  attributes never landed. Four bugs stacked on this path:
  1. `DslParserUtils.ParseLinesIntoNodes` only stripped the `*` key marker when it ended the
     trimmed line, so DSL like `TrnId* : Numeric(4)` left the asterisk on `node.Name`. The
     lookup in `existingItems` then missed and forced the create-new branch.
  2. `TransactionDslParser.SyncTransactionNodes` looked up `TransactionAttribute` via
     `sdkLevel.GetType().Assembly`, but the runtime proxy's assembly doesn't expose that
     type — `attrType` came back null and the create-new branch was a no-op.
  3. The same path created the wrapper via `Activator.CreateInstance` + `Attributes.Add`,
     which doesn't run the SDK's bookkeeping; the next `EnsureSave` discarded the addition.
     Replaced with `sdkLevel.AddAttribute(globalAttr)` (the typed SDK method already used by
     `ObjectService.InitializeTransactionWithDefaultKey`).
  4. `AttributeTypeApplier.ApplyPrimitive` called `Type.GetProperty("Type"/"Length"/"Decimals")`
     directly; the SDK Attribute hierarchy shadows those properties, throwing
     `AmbiguousMatchException`. Now resolves the most-derived declaration explicitly.
- **`InjectionService` masked `IsBusinessComponent`**: line 135 read `trn.BusinessComponent`
  (no `Is` prefix) via `dynamic`, throwing `RuntimeBinderException` swallowed by an empty catch.
  BC structures never injected into context. Typed cast against `Transaction.IsBusinessComponent`.

### Added

- **Inspect surfaces Business Component flag**: `genexus_inspect` now returns
  `transactionMetadata.isBusinessComponent` for Transaction objects, so agents can verify BC
  state without paging through the ~150-entry property bag from `genexus_properties get`.

## v2.4.0 — Unreleased

### Fixed

- **DSL parsers dropped attribute types**: `TransactionDslParser` and `TableDslParser` previously
  parsed `pNode.TypeStr` from the DSL but never applied it — new attributes silently defaulted to
  `Numeric(4)` and type changes to existing attributes were ignored. Both parsers now resolve the
  declared type via the new `AttributeTypeApplier` helper and set `Type`/`Length`/`Decimals` for
  primitives or `DomainBasedOn` for domain references (`UserLogin`, `AutoNum18`, etc.). The bug
  existed since `dfdd526` (v1.2.0). Workaround until now was `semanticops add_attribute type=…`.

### Changed

- **BREAKING (envelope)**: `axiCompact` now defaults to `true` for `genexus_query` and
  `genexus_list_objects`. Callers that relied on full payloads must now pass
  `axiCompact: false` explicitly. The flag is declared in `inputSchema` for discoverability.
- **Token reduction**: `tool_definitions.json` shrunk from ~5200 to ~4956 tokens by trimming
  the descriptions of `genexus_query`, `genexus_lifecycle`, `genexus_edit`, `genexus_analyze`,
  and `genexus_read`. Long-form help is now served on demand at
  `genexus://kb/tool-help/{name}` via the MCP resources protocol.

### Added

- **Observability**: worker spawn time and SDK init time are now measured per KB and exposed via
  `genexus://kb/health` (`spawnMs` samples + p50/p95, `sdkInitMs.lastMs`). New
  `src/GxMcp.Benchmarks` project provides a BenchmarkDotNet baseline for envelope projection,
  tool-definition loading, and spawn-tracker hot paths.
- **New tool**: `genexus_edit_and_build` collapses the edit → analyze impact → build callers
  workflow from 3-5 turns into a single call. Returns a composite envelope with `edit`, `impact`,
  and `build` blocks. The build runs asynchronously and is polled via
  `genexus_lifecycle action=status target=op:<taskId>`.
- **Error UX**: `genexus_edit` now embeds alternative matches inline (`alternatives` array) when
  an object name is ambiguous, so callers no longer need a separate `genexus_list_objects` turn
  to disambiguate.
- **Streaming**: long-running operations now emit `notifications/progress` bound to their
  `operationId`. Build phases, impact-analysis BFS, and KB index report incremental progress
  so the LLM can read status without polling `genexus_lifecycle action=status`. The gateway
  already forwards `notifications/progress` to both stdio and HTTP transports.
- **Fast index**: `BulkIndex` is now split into a lite pass (metadata only, ~30-45s on a
  38k-object KB) followed by background enrichment. `genexus_list_objects`, `genexus_read`,
  and `genexus_inspect` are usable immediately after the lite pass. `genexus_analyze
  mode=impact` enriches only the target's reachable graph on demand, returning in seconds
  even before full enrichment finishes. The legacy monolithic path is preserved behind
  the `Indexing.UseLitePass=false` flag in App.config for rollback safety.

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
