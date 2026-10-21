# Engine backend switch (`gx-engine`, experimental)

The Gateway can point at [gx-engine](../src/gx-engine/README.md) — the
independent Rust implementation — as an alternative backend to the GeneXus SDK.
The switch exists so the replacement can be exercised from the real product
before the SDK is removed: flip one setting, see the engine from
`genexus_doctor`, and let capabilities move to the engine as they reach measured
parity.

**Default policy.** `Engine.Backend=gxengine` (also when the section is absent)
uses gx-engine for every call inside the admitted space: blocking
`genexus_lifecycle` `build`/`rebuild` requests (through `parallel_rebuild_all` /
`rebuild-all`) and part-scoped `genexus_read` / `genexus_query` (through
`kb-read`/`kb-query`). A blocking directed `build` is also admitted for
`mode=compile_check`, explicit `callers=false`, and transitive callees, using a
simple or module-qualified object name. Edits, async builds, and unsupported
request shapes fall back to the SDK (`Engine.Fallback=sdk`). This selects the
existing admitted space; it does not expand whole-KB compatibility or guarantee
that arbitrary objects compile. Build errors/partial reports remain errors,
not a trigger to replay a started build through the SDK.

`gxengine` replaced `sdk` as the default in the 4.0.0 line (a breaking default change:
set `Engine.Backend=auto` to route only builds, or `Engine.Backend=sdk` to keep the
previous behavior and never invoke the engine). The evidence behind it is
the disposable `KBTeste` KB: whole-KB `rebuild-all` generates and compiles 346/346
targeted objects with 0 refusals, its 328 C# files match the SDK's output byte for byte,
and whole-KB generation runs in ~0.62 s against ~36 s for the SDK (paired; see the
[engine README](../src/gx-engine/README.md#measured-local-sdk-comparison)). A KB with forms
outside that evidence gets refusals by name, reported as a partial build.

`Engine.Backend=auto` narrows routing to admitted builds; reads and queries go
back to the SDK. Explicit `Engine.Backend=sdk` disables all engine routing,
including builds. `genexus_edit` is a registered consumer but stays outside the
engine space: the engine writer has no live persistence yet, so an engine edit
would report a save that never reaches the Knowledge Base database
(`Fallback=error` refuses it, `Fallback=sdk` serves it with the SDK). Everything
else — and every call outside the admitted space documented below — stays on the
SDK/Worker path, or is refused when `Engine.Fallback=error`. The remaining
parity capabilities (`str_format`, `numeric_render`, …) still have no product
consumer, and the gate is built so nothing partial can route. The physical
storage facts behind the read path are recorded in
[`udm_storage_notes.md`](udm_storage_notes.md).

## Enabling it

`config.json` (all documents):

| Key | Values | Default | Meaning |
| :--- | :--- | :--- | :--- |
| `Engine.Backend` | `gxengine`, `auto`, `sdk` | `gxengine` | `gxengine`: admitted builds, reads and queries use the engine; the rest uses SDK. `auto`: only admitted builds use the engine. `sdk`: SDK only, the engine is never invoked. |
| `Engine.Fallback` | `sdk`, `error` | `sdk` | What happens when a routed capability is not at parity: keep the GeneXus SDK, or refuse the call. `error` is the verification mode. |
| `Engine.Executable` | path | unset | Explicit `gx-engine.exe`. Authoritative: when the path is missing the probe reports `EngineNotFound` instead of running a different binary. |
| `Engine.TimeoutSeconds` | positive integer | `120` | Wall-clock budget for one engine invocation (matches the engine's native-compilation deadline). |

Environment overrides (non-strict documents; a **strict** document rejects all
four at load time, because the engine backend is a behavioral/execution switch
and the file must stay authoritative — the same rule as `GXMCP_PROFILE`):

| Variable | Equivalent |
| :--- | :--- |
| `GXMCP_ENGINE_BACKEND` | `Engine.Backend` |
| `GXMCP_ENGINE_FALLBACK` | `Engine.Fallback` |
| `GXMCP_ENGINE_EXE` | `Engine.Executable` |
| `GXMCP_ENGINE_TIMEOUT_SECONDS` | `Engine.TimeoutSeconds` |

Invalid non-strict backend values are logged and resolve to SDK-only; a
strict document fails at parse time. The Gateway hot-reloads `config.json` and
re-resolves these settings on use, so `genexus_doctor` reflects an edit without
a restart.

## What changes today

With default `Backend=gxengine`, admitted builds, reads and queries are engine
candidates. `Backend=auto` keeps only the builds.
Every other tool is untouched. `genexus_doctor` result gains an `engine` block:

- always: `backend`, `fallback`, and `capabilities` — the `parity` / `partial` /
  `unsupported` counts plus `routedOperations`, the operations currently served
  enabled as engine candidates by the selected policy (argument gates still apply);
- with `Backend=gxengine|auto`: `executable`, `available`, `version` from the
  `gx-engine --version` handshake (ten-second budget) or a structured `error`
  with one of `EngineNotFound`, `EngineStartFailed`, `EngineProbeFailed`,
  `EngineVersionUnrecognized`, `EngineTimeout`.

When the switch is enabled and the binary is unusable, doctor appends a warning
naming the error code and the configured `Engine.Fallback`. Routing is
observable in every answer: an engine-served payload carries a top-level
`engine` block (`{ backend: "gxengine", capability, operation, fallback,
executable? }`). The switch remains observable first and behavioral last, in
that order.

## The capability gate

`EngineCapabilityRegistry` (Gateway) is the single source of truth for what the
engine may serve, and `EngineRouter.Decide(capabilityId, settings)` is the
single entry point a swap must call:

- `Backend=auto` → only `parallel_rebuild_all` is eligible; all other
  capabilities remain SDK-only, even with `Fallback=error`. Builds still require
  the same parity and argument gates as explicit engine mode.
- `Backend=sdk` → the SDK serves everything; the engine is never invoked.
- `Backend=gxengine` (default) + capability at `Parity` → the engine serves it.
- anything else (partial, unsupported, unknown capability) → the SDK serves it,
  or with `Fallback=error` the call is refused with `EngineCapabilityNotParity`
  and the reason. There is no silently-partial engine path.

Status is a claim with evidence: a capability may only move to `Parity` after a
measured native differential, and `EngineBackendTests` fails when an entry cites
a repository path that does not exist. The entries below keep the evidence
write-up for their differential; the current status of every capability is owned
by `EngineCapabilityRegistry` (and the live `genexus_doctor` view), and rows are
not re-annotated here when a later differential moves a status:

| Capability | Status | Evidence / note |
| :--- | :--- | :--- |
| `udm_storage_access` | parity (routed) | Direct native UDM storage access: connection parser + envelope decoder + token reconstructor + reader/writer; `src/gx-engine/src/udm/mod.rs`, `src/gx-engine/tests/udm_decoder.rs` (19/19), `src/gx-engine/tests/udm_reader_writer.rs` (10/10), `src/gx-engine/tests/kb_ops.rs` (10/10) plus a live KBTeste read. Serves `genexus_read`/`genexus_query`; `genexus_edit` is registered but held outside the served space while engine writes have no live persistence (see below). |
| `parallel_rebuild_all` | parity (routed) | Parallel specification + Roslyn compilation; `src/gx-engine/src/rebuild.rs`, `src/gx-engine/tests/rebuild_pipeline.rs` (5/5: sub-second 24-proc parallel rebuild). Serves `genexus_lifecycle` `build`/`rebuild`. |
| `str_format` | parity | Numeric `Str` pictures: native `double`→`(decimal)` (15-digit) bound pipeline reproduced; 36,246 in-domain native rows across six scans, 0 mismatches; `src/gx-engine/tests/fixtures/str_matrix.tsv` (6,152 rows) plus the full bound table in `src/gx-engine/fixtures/str-bounds-native.tsv`. No consumer yet. |
| `numeric_render` | parity | Implicit `&var.ToString()`/`Print(&numericVar)`: native measured equal to `Str(value, precision, scale)` (padding included) over 120 KBTeste cases across precision 1..18 boundary families, scale corners and the width 15..18 narrowed-bound regime; `src/gx-engine/tests/fixtures/numeric_render_native.tsv` + `src/gx-engine/tests/numeric_render_native.rs`. Expressions/record members/date components keep the untyped display path. No consumer yet. |
| `text_functions` | parity | `Substr`/`Upper`/`Lower`/`StrReplace` plus `.Substring`/`.ToUpper`/`.ToLower`/`.Replace`: 63 recorded KBTeste rows (raw-value substringing with 1-based out-of-range rules, per-character case mapping that keeps characters whose mapping expands, case-sensitive left-to-right non-overlapping replace); `src/gx-engine/tests/fixtures/text_functions_native.tsv` + `src/gx-engine/tests/text_functions.rs`. Non-BMP positions and database-column semantics outside the claim. No consumer yet. |
| `text_extended_functions` | parity | `LTrim`/`RTrim`/`PadL`/`PadR`/`Space`/`Concat`/`StartsWith`/`EndsWith`/`Contains`/`StrSearch` plus method forms: 95 recorded `GeneXus.Utils.StringUtil` rows (exact ASCII space trimming, left/right padding with truncation, single-sided concat trimming, ordinal comparisons, 1-based index clamping); `src/gx-engine/tests/fixtures/text_extended_native.tsv` + `src/gx-engine/tests/text_extended.rs`. No consumer yet. |
| `numeric_math_functions` | parity | `Int`/`Round`/`Trunc`/`Val`/`Left`/`Right`/`Asc`/`Chr`/`NotNumeric`/`LTrimStr` plus method forms: 107 recorded `GeneXus.Utils.NumberUtil` and `StringUtil` rows (`Math.Floor`, `AwayFromZero` with negative digit support, truncation toward zero, numeric string parsing, ASCII char conversions); `src/gx-engine/tests/fixtures/numeric_math_native.tsv` + `src/gx-engine/tests/numeric_math.rs`. No consumer yet. |
| `collection_operations` | parity | `Add`/`Remove`/`Clear`/`IndexOf`, `For In` iteration and `Exit`: 24 recorded `GeneXus.Utils.GxSimpleCollection` and `GXBaseCollection` rows (1-based index, index insertion, silent bounds removal, element aliasing in loops, loop exit); `src/gx-engine/tests/fixtures/collection_ops_native.tsv` + `src/gx-engine/tests/collection_ops.rs`. No consumer yet. |
| `calendar_operations` | parity | `Day`/`Month`/`Year`/`Hour`/`Minute`/`Sec`/`Dow`/`AddYr`/`AddMth`/`DAdd`/`TAdd`/`DDiff`/`TDiff`/`Age`/`EndOfMonth`/`ResetTime` plus method forms: 83 recorded `GeneXus.Utils.DateTimeUtil` rows (components, leap day clamping, day of week indexing 1=Sunday, forward/backward date and second arithmetic, day/second differences, completed years age calculation, end-of-month and time reset); `src/gx-engine/tests/fixtures/calendar_ops_native.tsv` + `src/gx-engine/tests/calendar_ops.rs`. No consumer yet. |
| `subroutine_control_flow` | parity | Local `Sub 'Name' ... EndSub` definitions and `Do 'Name'` calls: native C# `S111()` and `returnInSub` generation verified in `NetModel/web/general/ui/listprograms.cs`; `src/gx-engine/tests/subroutines.rs` (13/13 tests: shared procedure-scoped variable mutation, case-insensitive resolution, quote styles, nested subroutine calls, early `Return` propagation exiting caller procedure, recursion depth limit 128, and compile-time validation). No consumer yet. |
| `conditional_and_state_operations` | parity | `Iif(cond, trueExpr, falseExpr)` short-circuit conditional, universal `.IsEmpty()`/`IsEmpty()` and `.SetEmpty()` across `Numeric`, `Text`, `Boolean`, `Date`, `DateTime`, `Collection`, and `Record` members, `Nullvalue(&var)`, `.UDP(...)` calls, and `Format`/`NewLine`/`Replicate`/`CharAt`/`StrSearchRev`/`.LastIndexOf`/`BoolToStr`/`ToBoolean`/`StrToBool`: 57 recorded `GeneXus.Utils.StringUtil` and state oracle rows; `src/gx-engine/tests/fixtures/conditional_state_native.tsv` + `src/gx-engine/tests/conditional_state.rs`. No consumer yet. |
| `numeric_core` | partial | Signed 64-bit arithmetic and checked overflow; not a Numeric/Decimal compatibility claim. |
| `text_core` | partial | Len/Trim/comparisons over CP1252 bytes; known encoding divergence on high bytes. |
| `calendar_values` | partial | Date/DateTime component subset; no timezone or fractional-second coverage. |
| `records_collections` | partial | Bounded record/collection runtime; invalid index is a controlled error where native crashes (documented divergence). |
| `procedure_pipeline` | partial | Admitted Procedure compile+run; bounded reachability. |
| `xml_projection` | partial | SDK `ExportFile` XML projection; bounded depth/item-alias rules. |
| `kb_catalog` | partial | The engine's own portable catalog format, not a native KB reader. |
| `native_kb_read` | unsupported | No native KB decoder exists; SDK-only. |
| `sql_navigation` | unsupported | No specifier/navigation/SQL planning. |
| `general_gx_surface` | unsupported | Rules, Events and forms outside the admitted subset fail closed. |

The parity claim is also measurable: `python src/gx-engine/scripts/benchmark.py`
runs one corpus through the native runtime helper (official
`genexus.classes.core` package) and the engine bench target, re-checking live
output parity (row count, character count, FNV-1a/UTF-16 digest) before
reporting per-pass timings - after two hot-path fixes the measured medians are
~121-130 ns/row engine against ~274-286 ns/row native (see
`src/gx-engine/VALIDATION.md`, "SDK-vs-engine benchmark").

## Served operations and admitted space

Each routed operation admits a deliberately narrow argument space, declared as
one list of supported arguments per operation (`EngineAdmittedSpace` in
`src/GxMcp.Gateway/EngineRoutedOperations.cs`). Any other requested argument
— including one the engine does not know yet — is outside the space; the refusal
message names it and lists the supported arguments. `null`, blank, empty and
`false` values request nothing. A call
outside it is never approximated: with `Fallback=sdk` it continues on the SDK
path unchanged, and with `Fallback=error` it is refused with
`EngineOperationOutsideAdmittedSpace` naming the reason. The same rule applies
when a read/query invocation fails (`EngineNotFound`, `EngineStartFailed`,
`EngineTimeout`, `EngineCommandFailed`). Builds may fall back only on
`EngineNotFound` or `EngineStartFailed`: after process start, missing or
unrecognized reports return an error with `reconciliationRequired: true`, even
with `Fallback=sdk`. A failed or timed-out process may already have changed build
artifacts, so the Gateway must not replay that build on the SDK. A successful
report paired with a nonzero exit is also uncertain, not a zero-error build
failure. Exceptions while observing a started process return
`EngineObservationFailed`; cleanup kills the process tree and waits up to five
seconds for the owned process to exit, reporting whether that exit was observed.
Unknown build outcomes additionally carry `mutationOutcomeUnknown: true`, which
preserves a keyed request's durable retry fence. A generic reconciliation flag
alone does not retain the new request's fence: it may be a pre-dispatch refusal
requiring reconciliation of an earlier write. Reconcile the original operation
before retrying; a new key does not make an uncertain build safe.
Request cancellation is different:
it propagates cancellation without SDK fallback, whether it arrives before
launch or during an engine read/build. Nothing routes through the engine unless
the registry marks the capability `Parity`.

### `genexus_read` → `kb-read`

Admitted: one object by `name` (or its `objectName` alias), with an explicit
`part` or `parts[]` (`Source`, `Rules`, `Events`, `Conditions`, `Variables`,
`Structure`, `WebForm`, `PatternInstance`), an optional `type` checked against
the stored kind, and `refresh` (an engine read is always fresh).

Outside: `guid`/`entityKey`/`path` lookups, `targets[]`, `ifUnchangedSince`,
`offset`/`limit` paging, full-object reads (no `part`, or `all`/`full`/
`summary`/`360`), and a `kb` naming a different Knowledge Base.

Shape: single part → the ExtractSource-shaped payload (`name`, `type`, `part`,
`source`, `isBase64`, `isEmpty`, `truncated`, `offset`, `limit`, `totalLines`,
`totalBytes`); `parts[]` → a `parts` object with lowercase keys. There is no
`versionToken` (the SDK token cannot be computed off the engine, and an engine
write does not take one). A part the object does not have fails with
`ObjectPartNotFound` listing `availableParts` — unlike the SDK path, the engine
resolves no part aliases (`Layout` does not resolve to `WebForm`). Only the eight
mapped part kinds are served and listed; other stored parts (`Documentation`,
`Help`, `Layout`, …) have no engine part kind and report as missing rather than
being decoded approximately. See [`udm_storage_notes.md`](udm_storage_notes.md).

### `genexus_query` → `kb-query`

Admitted: one exact object name, `type:X`, `usedby:Y`, `typeFilter`, and
`limit` (default 50, engine cap 200), combined freely out of name/type/usedby.
`exactMatch` may be absent or `true`.

Outside: fuzzy/text queries (`exactMatch:false`, multi-term, wildcards,
`description:`/`parent:`/`folder:` prefixes), an empty query, `domainFilter`,
`sort`, `since`/`modifiedBefore`, `cursor`/`cursors`, `kbs` federation and its
budget arguments. Documented deviation: the SDK path falls back to fuzzy index
search after a direct-lookup miss, and the engine answers exact-only, so
`exactMatch:false` stays outside the space instead of returning a subset.

Shape: `results[]` with `name`/`type` (no `guid`/`path`/`lastUpdate`), `count`,
`total`, `hasMore`/`truncated`, and the `pagination` block (offset 0, no
cursors). Results are name-sorted.

### `genexus_edit` → `kb-write` (held outside the served space)

Not served today. The capability registers `genexus_edit` as a consumer, but
`UdmWriter::save_object` mutates the engine's in-process store only: it issues no
SQL, so an engine-served edit against a live Knowledge Base would report a save
that is lost when the process exits. Every call is therefore outside the space:
`Fallback=error` refuses it with `EngineOperationOutsideAdmittedSpace` naming the
reason, and `Fallback=sdk` serves it with the SDK unchanged. `gx-engine kb-write`
independently refuses a live client for the same reason.

When live persistence lands (version bump plus part replacement through the same
composition structure, verified by an independent read-back), the intended v1
space is one existing object by `name`, one `part` (default `Source`), the full
replacement text in `content` (an empty string clears the part), an optional
`type` checked against the stored kind before any write, and `mode` absent or
`full`. `patch`/`ops`/`scope`, `parts[]`/`targets[]`, `dryRun`, version
tokens/change sets, validation and verification modes, object creation and
`return_post_state` would remain outside it.

### `genexus_lifecycle` `build`/`rebuild` → `rebuild-all`

Admitted: `action=build|rebuild` with the blocking contract —
`wait_until_done=true`, or an `estimated_seconds` the Gateway itself would run
synchronously. Builds are serialized inside the Gateway process. Cancellation while
waiting for that gate is pre-dispatch; after engine launch it does not imply rollback.
A keyed request cancelled before dispatch leaves its idempotency key reusable.
Cancellation after dispatch retains the unknown-outcome fence: inspect the operation
and generated outputs before deciding whether another build is safe.

A directed compile-only build is admitted with `action=build`, a simple or
module-qualified `target` name, `mode=compile_check`, explicit `callers=false`,
and `includeCallees` omitted or `transitive`. The same blocking intent is required.
It uses `--object <name> --closure`: the engine builds that object and its
transitive dependency closure, not unrelated objects in the KB. Output is isolated
at `<KB>/.gx-engine/build-targets/<SHA-256-of-target>`. The answer identifies
`engine.scope=target-closure` and `engine.target`; whole-KB answers identify
`engine.scope=whole-kb`. A successful target build does not imply whole-KB success.

```json
{"action":"build","target":"MyProcedure","mode":"compile_check","callers":false,"wait_until_done":true}
```

Outside: the bare build/rebuild (the SDK path answers it with an accepted async
job; the engine has no job registry), `wait_seconds`, other directed builds,
targeted `rebuild`, `environment`, other `mode` values, `deploy`,
`skipFullDeploy: false`, `fastIncremental`, `force`, `dryRun`, `callerCap`,
`buildPlanCap`, and `limit`. Directed builds with the default `callers=true`,
non-transitive callees, typed selectors, GUIDs or paths remain on SDK; the engine
must not silently drop caller expansion or approximate a selector.

Shape: `status: "Success"` only for a recognized, error-free complete report
(or the supported legacy pipeline report without a status field), plus `report`
with the engine report projection (`total_ms`, `catalog_load_ms`, phase times, object counts,
`emitted_file_count`, `errors`, `warnings`, and the `status`, `generated_count`,
`compiled_count`, `skipped_count`, `skipped_objects`, `not_generated_count`,
`not_generated_objects`, `refused_count` and `refused_objects` fields).

A run the engine reports as `partial` (exit 3: web UI objects skipped, a code
kind with no generator, or a project that did not build) returns
`status: "Partial"`, `error: "EngineBuildPartial"` and `isError=true`. It is
never `Success`: the report lists what was left out, and the message gives the
counts. A build whose report carries errors returns `status: "Failed"` with
`isError=true` and the report attached — that is the build's answer, not
an engine outage, so it is surfaced even under `Fallback=sdk`. An exit code of zero
does not override a partial report or reported errors. If handling a returned build
report throws, the Gateway returns an engine error, not an SDK replay of the build.
An empty JSON object, unknown status or malformed errors field is not a build
report: it requires reconciliation, not a success response or SDK replay. Such
unknown outcomes carry a reconciliation hint, not advice to change fallback and
retry blindly.
`catalog_load_ms` measures loading before the pipeline and is not included in
`total_ms`; compare both phases and the outer request wall time when profiling.
Whole-KB `build` and `rebuild` both run the full pipeline; `rebuild` adds `--clean`
to the output directory (`<KB>\.gx-engine\build`). `--clean` removes every generated
file and build output and forces a full compile, but keeps `obj/` as `dotnet clean`
does (restore outputs and the kept compiler command, each checked against its key).
A `build` whose generated files and compiler inputs are all unchanged skips the
compile (`compile path: csc-direct (up to date)`); a web class that differs only in
its generation stamp keeps its earlier stamp. Directed `compile_check` builds
run the same pipeline only on the admitted target closure.

### Documented v1 deviations

- No `versionToken`/post-state on reads or edits, and no concurrency tokens.
- Query results are name-sorted, exact-only, and carry no SDK-only identity
  fields (`guid`, `path`, `lastUpdate`).
- An engine-served read does not confirm SDK write-recovery fences; a fence
  left by an uncertain SDK write still clears on an SDK read.
- A semantic-cache hit can answer a routed read with an SDK-shaped payload
  cached before the switch; engine results are not written to that cache.
- Live reads are `sqlcmd`-backed and list only the twelve servable object entity
  types: `ModelEntityVersion` also contains object parts and KB-tree nodes, which
  are filtered out ([notes](udm_storage_notes.md)).
- `rebuild-all` hydrates the full servable catalog. Native catalog snapshots remain
  disabled: row counts and timestamps alone do not safely identify content edits.
  Measure catalog loading separately rather than inferring whole-KB speed from a
  warm compiler cache.
- No engine write is persisted to live storage; `genexus_edit` stays outside the
  served space until an independent read-back verifies a live write.

## Executable discovery

Order: `Engine.Executable`/`GXMCP_ENGINE_EXE` (explicit; missing →
`EngineNotFound`, never a different binary), the Gateway directory, `publish/`,
the checkout `src/gx-engine/target/release` then `.../debug`, the current
directory layouts, then `PATH`. This mirrors the gxwire locator
(`LocateGxwireExecutable`). Build the engine for a checkout with:

```powershell
cargo build --release --manifest-path src/gx-engine/Cargo.toml
```

`publish/gx-engine.exe` only exists when packaging copies it; the experimental
switch is a checkout/operator path today, not a shipped artifact.

## Adding a routed capability

1. Measure the native differential inside the admitted space; record the
   evidence in `plans/129-implementation-progress.md` and the capability matrix.
2. Move/add the entry in `EngineCapabilityRegistry`: status `Parity`, evidence
   path, and the consumer operation key. Routing follows the registry — the
   gateway maps a tool call to its operation key and looks up which capability
   declares that consumer.
3. Route the call site through `EngineRouter.Decide` — never invoke the engine
   from a tool path directly.
4. Add tests for the routing decision, the engine path, and the fallback/error
   behavior; mutation-check each guard (revert it, confirm red, restore).
5. Update this page, the CHANGELOG and the doctor view.

## Safety rules

- Fail closed. The engine may only serve what was measured; nothing is
  approximated or silently delegated mid-operation.
- `Fallback=error` is the experiment mode that proves the engine carried the
  whole call instead of the SDK.
- A write may only route once its persistence is verified end to end (engine
  write, then an independent read-back); until then `genexus_edit` stays outside
  the engine space.
- Native KB object access is served through the engine's own UDM storage
  adapter (`udm_storage_access`, measured against a live KB); a generic native
  KB decoder (`native_kb_read`) remains unsupported, and the engine never
  claims an SDK-only surface.
- Raw SDK exports and private KB data never enter Git; engine evidence lives as
  the recorded differentials, fixtures and hashes already committed under
  `src/gx-engine/tests` and the ledger.
- `genexus_wire` (gxwire) is a separate Rust binary for file-tree indexing; this
  switch is about the compiler/runtime replacement, not the indexer.
