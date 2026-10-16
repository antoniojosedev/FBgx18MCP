# Engine backend switch (`gx-engine`, experimental)

The Gateway can point at [gx-engine](../src/gx-engine/README.md) — the
independent Rust implementation — as an alternative backend to the GeneXus SDK.
The switch exists so the replacement can be exercised from the real product
before the SDK is removed: flip one setting, see the engine from
`genexus_doctor`, and let capabilities move to the engine as they reach measured
parity.

**Status.** No product operation is served by the engine today. The capabilities
measured at full parity (`str_format`, `numeric_render`, `text_functions`,
`text_extended_functions`, `numeric_math_functions`, `collection_operations`,
and `calendar_operations`) have no product consumer yet, and the gate is built
so nothing partial can route. This page documents the seam, its rules, and how
the first real swap lands.

## Enabling it

`config.json` (all documents):

| Key | Values | Default | Meaning |
| :--- | :--- | :--- | :--- |
| `Engine.Backend` | `sdk`, `gxengine` | `sdk` | `gxengine` lets the engine serve the capabilities the gate marks at measured parity. |
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

Invalid non-strict values are logged and ignored (the SDK default wins); a
strict document fails at parse time. The Gateway hot-reloads `config.json` and
re-resolves these settings on use, so `genexus_doctor` reflects an edit without
a restart.

## What changes today

`genexus_doctor` result gains an `engine` block:

- always: `backend`, `fallback`, and `capabilities` — the `parity` / `partial` /
  `unsupported` counts plus `routedOperations`, the operations currently served
  by the engine (empty today);
- with `Backend=gxengine`: `executable`, `available`, `version` from the
  `gx-engine --version` handshake (ten-second budget) or a structured `error`
  with one of `EngineNotFound`, `EngineStartFailed`, `EngineProbeFailed`,
  `EngineVersionUnrecognized`, `EngineTimeout`.

When the switch is enabled and the binary is unusable, doctor appends a warning
naming the error code and the configured `Engine.Fallback`. Everything else is
unchanged: with no parity capability carrying a consumer, every tool call still
routes through the SDK/Worker exactly as before. The switch is observable first
and behavioral last, in that order.

## The capability gate

`EngineCapabilityRegistry` (Gateway) is the single source of truth for what the
engine may serve, and `EngineRouter.Decide(capabilityId, settings)` is the
single entry point a swap must call:

- `Backend=sdk` → the SDK serves everything; the engine is never invoked.
- `Backend=gxengine` + capability at `Parity` → the engine serves it.
- anything else (partial, unsupported, unknown capability) → the SDK serves it,
  or with `Fallback=error` the call is refused with `EngineCapabilityNotParity`
  and the reason. There is no silently-partial engine path.

Status is a claim with evidence: a capability may only move to `Parity` after a
measured native differential, and `EngineBackendTests` fails when an entry cites
a repository path that does not exist. Snapshot (source of truth is
`EngineCapabilityRegistry`):

| Capability | Status | Evidence / note |
| :--- | :--- | :--- |
| `str_format` | parity | Numeric `Str` pictures: native `double`→`(decimal)` (15-digit) bound pipeline reproduced; 36,246 in-domain native rows across six scans, 0 mismatches; `src/gx-engine/tests/fixtures/str_matrix.tsv` (6,152 rows) plus the full bound table in `src/gx-engine/fixtures/str-bounds-native.tsv`. No consumer yet. |
| `numeric_render` | parity | Implicit `&var.ToString()`/`Print(&numericVar)`: native measured equal to `Str(value, precision, scale)` (padding included) over 120 KBTeste cases across precision 1..18 boundary families, scale corners and the width 15..18 narrowed-bound regime; `src/gx-engine/tests/fixtures/numeric_render_native.tsv` + `src/gx-engine/tests/numeric_render_native.rs`. Expressions/record members/date components keep the untyped display path. No consumer yet. |
| `text_functions` | parity | `Substr`/`Upper`/`Lower`/`StrReplace` plus `.Substring`/`.ToUpper`/`.ToLower`/`.Replace`: 63 recorded KBTeste rows (raw-value substringing with 1-based out-of-range rules, per-character case mapping that keeps characters whose mapping expands, case-sensitive left-to-right non-overlapping replace); `src/gx-engine/tests/fixtures/text_functions_native.tsv` + `src/gx-engine/tests/text_functions.rs`. Non-BMP positions and database-column semantics outside the claim. No consumer yet. |
| `text_extended_functions` | parity | `LTrim`/`RTrim`/`PadL`/`PadR`/`Space`/`Concat`/`StartsWith`/`EndsWith`/`Contains`/`StrSearch` plus method forms: 95 recorded `GeneXus.Utils.StringUtil` rows (exact ASCII space trimming, left/right padding with truncation, single-sided concat trimming, ordinal comparisons, 1-based index clamping); `src/gx-engine/tests/fixtures/text_extended_native.tsv` + `src/gx-engine/tests/text_extended.rs`. No consumer yet. |
| `numeric_math_functions` | parity | `Int`/`Round`/`Trunc`/`Val`/`Left`/`Right`/`Asc`/`Chr`/`NotNumeric`/`LTrimStr` plus method forms: 107 recorded `GeneXus.Utils.NumberUtil` and `StringUtil` rows (`Math.Floor`, `AwayFromZero` with negative digit support, truncation toward zero, numeric string parsing, ASCII char conversions); `src/gx-engine/tests/fixtures/numeric_math_native.tsv` + `src/gx-engine/tests/numeric_math.rs`. No consumer yet. |
| `collection_operations` | parity | `Add`/`Remove`/`Clear`/`IndexOf`, `For In` iteration and `Exit`: 24 recorded `GeneXus.Utils.GxSimpleCollection` and `GXBaseCollection` rows (1-based index, index insertion, silent bounds removal, element aliasing in loops, loop exit); `src/gx-engine/tests/fixtures/collection_ops_native.tsv` + `src/gx-engine/tests/collection_ops.rs`. No consumer yet. |
| `calendar_operations` | parity | `Day`/`Month`/`Year`/`Hour`/`Minute`/`Sec`/`Dow`/`AddYr`/`AddMth`/`DAdd`/`TAdd`/`DDiff`/`TDiff`/`Age`/`EndOfMonth`/`ResetTime` plus method forms: 83 recorded `GeneXus.Utils.DateTimeUtil` rows (components, leap day clamping, day of week indexing 1=Sunday, forward/backward date and second arithmetic, day/second differences, completed years age calculation, end-of-month and time reset); `src/gx-engine/tests/fixtures/calendar_ops_native.tsv` + `src/gx-engine/tests/calendar_ops.rs`. No consumer yet. |
| `subroutine_control_flow` | parity | Local `Sub 'Name' ... EndSub` definitions and `Do 'Name'` calls: native C# `S111()` and `returnInSub` generation verified in `NetModel/web/general/ui/listprograms.cs`; `src/gx-engine/tests/subroutines.rs` (13/13 tests: shared procedure-scoped variable mutation, case-insensitive resolution, quote styles, nested subroutine calls, early `Return` propagation exiting caller procedure, recursion depth limit 128, and compile-time validation). No consumer yet. |
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

## Adding the first routed capability

1. Measure the native differential inside the admitted space; record the
   evidence in `plans/129-implementation-progress.md` and the capability matrix.
2. Move/add the entry in `EngineCapabilityRegistry`: status `Parity`, evidence
   path, and the consumer operation key.
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
- Native KB access stays SDK-only (`native_kb_read` is `unsupported`); the
  engine never touches a native Knowledge Base.
- Raw SDK exports and private KB data never enter Git; engine evidence lives as
  the recorded differentials, fixtures and hashes already committed under
  `src/gx-engine/tests` and the ledger.
- `genexus_wire` (gxwire) is a separate Rust binary for file-tree indexing; this
  switch is about the compiler/runtime replacement, not the indexer.
