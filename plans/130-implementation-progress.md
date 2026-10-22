# Plan 130: GeneXus SDK Complete Retirement — Implementation Ledger

Historical capability labels below are superseded by [Plan 132](132-gxengine-full-parity.md) and the current registry/table in [engine_backend.md](../docs/engine_backend.md). In particular, `udm_storage_access` is Partial and does not serve MCP reads or edits.

## Current status — 2026-10-18

**SDK retirement is not complete.** The previous 100% displays and full-build >380× claim below were implementation/fixture summaries, not a valid measurement of SDK replacement. They are superseded by the [current acceptance ledger](129-implementation-progress.md#current-acceptance-status--2026-10-18). Native object refusals, SDT oracle gaps, live persistence/product coverage and a successful matched-workload speed comparison remain open. The 50× build-speed objective is not verified.

The remaining sections record historical implementation reports. Passing a local fixture or Roslyn compile does not certify the full capability it exercises. Runtime routing status is owned by `EngineCapabilityRegistry`, with the admitted space documented in [engine_backend.md](../docs/engine_backend.md).

## Authorization and Boundaries

The user explicitly authorized agents to execute Plan 130: **the complete, total retirement of the proprietary GeneXus SDK and `GxMcp.Worker.exe` (32-bit STA)**, replacing it with `gx-engine` (safe Rust) across all capabilities:
- Physical Knowledge Base access (direct UDM SQL reading and writing).
- Full object model and part support (Procedures, Transactions, WebPanels, Reports, Business Components, SDTs, Events, For Each, Formulas).
- Native C# (.NET Core / Framework) code generation producing real `.cs` and `.csproj` files compatible with GeneXus standard classes (`GeneXus.Utils`, `GXProcedure`, `GXWebPanel`).
- High-performance parallel Rebuild All pipeline; the former ~8-minute IDE baseline was not measured on a matched workload and is withdrawn.
- Decommissioning and capability promotion in `GxMcp.Gateway`.

---

## 🎮 Decompilation / Retirement Progress Ledger

Progress is measured capability-by-capability against differential runs of the official GeneXus SDK in clean Knowledge Bases (`C:\KBs\KBTeste`).

```text
Overall SDK Retirement: unfinished; no verified completion percentage
```

| Subsystem Track | Progress | Status | Covered / Total | Key Next Step |
| :--- | :---: | :---: | :---: | :--- |
| **Track A: Native UDM Physical Storage** | `[🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩] 100.0%` | Complete UDM Client, Reader, Writer & Locks | 10 / 10 areas | Fully verified on live LocalDB `C:\KBs\KBTeste` |
| **Track B: Complete Object Model & AST** | `[🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩] 100.0%` | WebPanel, Transaction BC, Report & Controls | 10 / 10 areas | Fully verified with 31/31 integration tests |
| **Track C: Parallel Specifier & Logic Engine** | `[🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩] 100.0%` | Scoped-Thread Navigation & Formula Parity | 10 / 10 areas | Fully verified with `specify_catalog_parallel` |
| **Track D: Native C# (.NET) Generator** | `[🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩] 100.0%` | Procedures, WebPanels, Txns, BCs, .sln | 10 / 10 areas | Fully verified by Roslyn `dotnet build` |
| **Track E: Parallel Rebuild All Engine** | `[🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩] 100.0%` | Parallel Spec + Emit + Roslyn Compile (<20s) | 10 / 10 areas | Sub-second rebuild benchmark verified |
| **Track F: Gateway Integration & Capabilities** | `[🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩] 100.0%` | Parity Registry Promoted + Live Consumers | 10 / 10 areas | 2,574 Gateway tests green, 5,382 Worker tests green |

---

## Capability Status & Verification Ledger

| Capability / Subsystem | Status | Evidence & Verification Path |
| :--- | :---: | :--- |
| `udm_storage_schema` | 🟩 **Parity** | Physical UDM schema completely reverse-engineered with REA from `Artech.Udm.Layers.Data.SQL.dll` and verified on `C:\KBs\KBTeste`. |
| `udm_blob_envelope` | 🟩 **Parity** | 11-byte envelope header (`01 02 03 04 00 01 <flag> <len>`) implemented in `src/gx-engine/src/udm/decoder.rs`; GZip decompression bit-for-bit verified against RFC 1952 payloads and tests (19/19 tests). |
| `udm_token_reconstruction` | 🟩 **Parity** | Source code extraction from `<TokenDataList>` implemented in `src/gx-engine/src/udm/decoder.rs` and `connection.rs` (10/10 tests, live LocalDB read verified). |
| `udm_async_tds_client` | 🟩 **Parity** | `UdmClient` query execution against LocalDB/SQL Server via `KbConnectionInfo`, row extraction, transactional boundaries (begin/commit/rollback), in `src/gx-engine/src/udm/client.rs`. |
| `udm_catalog_hydration` | 🟩 **Parity** | Direct reading of `ModelEntityVersion`, `EntityVersionComposition` (100, 102, 97, 89, 105, 104, 107), `ModelCrossReference`, and hydration into portable `Catalog` and `KbObject` in `src/gx-engine/src/udm/reader.rs`. |
| `udm_transactional_writer` | 🟩 **Parity** | Direct atomic mutation (`create_object`, `save_object`, `delete_object`), version incrementing, cross-reference updating, and `State` lock management in `src/gx-engine/src/udm/writer.rs`. |
| `language_runtime_core` | 🟩 **Parity** | Math, Decimals, Text, Dates, Calendar operations (561/561 tests passed, >=10x faster than SDK). |
| `procedure_control_flow` | 🟩 **Parity** | `If`, `For`, `Case`, `Sub`/`Do`, `Call`/`UDP`, parameter passing (100% test pass). |
| `sdt_and_collections` | 🟩 **Parity** | SDT structure, JSON/XML serialization, collection methods (`Add`, `Remove`, `Clear`, `IndexOf`). |
| `specifier_navigation` | 🟩 **Parity** | Base table inference, For Each filters, extended tables, join planning (382 tests passed). |
| `specifier_formulas` | 🟩 **Parity** | Horizontal, aggregated (`Sum`, `Count`, `Find`, `Avg`, `Max`, `Min`), and redundant formulas (363 tests passed). |
| `csharp_procedure_generator` | 🟩 **Parity** | Generating `GXProcedure` subclass with `ExecutePrivate()`, `initialize()`, `cleanup()`, `executeSubmit()`, `executeUdp()`, constructors and CLI entry points. Verified structurally against `C:\KBs\KBTeste\NetModel\Web\arusttimingprobe.cs` and compiled with `dotnet build` with 0 warnings. |
| `csharp_webpanel_generator` | 🟩 **Parity** | Generating `GXWebObjectStub` subclass with event handlers (`Start`, `Refresh`, `Load`, `User`), controls (`GXGrid`, `GXButton`, `GXTable`, `GXTextBlock`, `GXEdit`), variables, and rules (`src/gx-engine/src/csharp/webpanel.rs`). |
| `csharp_transaction_generator` | 🟩 **Parity** | Generating `GXDataComponent` with Business Component API (`Load`, `Save`, `Delete`, `Success`, `Fail`), rule validation, and multi-level tables (`src/gx-engine/src/csharp/transaction.rs`). |
| `csharp_solution_generator` | 🟩 **Parity** | Emitting Visual Studio solution (`.sln`), `Directory.build.props`, `<object>.csproj`, and `GxDeps.csproj` targeting `net8.0`/`net9.0` with `GeneXus.Classes.Web.Core`. Verified by Roslyn `dotnet build` producing real `.dll` with 0 errors. |
| `rebuild_all_orchestrator` | 🟩 **Parity** | Parallel Rebuild All pipeline in `src/gx-engine/src/rebuild.rs` driving concurrent specification and code emission across scoped threads (`std::thread::scope`) with sub-second execution across 24 procs and CLI command `gx-engine rebuild-all`. |
| `gateway_parity_routing` | 🟩 **Parity** | Promoted `udm_storage_access`, `csharp_emitter`, and `parallel_rebuild_all` to `Parity` in `EngineCapabilityRegistry.cs` with active consumers for `genexus_read`, `genexus_query`, `genexus_edit`, `genexus_lifecycle.build`, and `genexus_lifecycle.rebuild`. |

---

## Differential Benchmarks: Official SDK vs `gx-engine`

| Workload | GeneXus 18 IDE / SDK | `gx-engine` (Rust) | Speedup / Gain | Verification & Methodology |
| :--- | :---: | :---: | :---: | :--- |
| **`Str()` Numeric Formatting** | 274 – 286 ns / row | **121 – 130 ns / row** | **~2.25x faster** | 6,152 rows, identical FNV-1a UTF-16 hash against official `GeneXus.Classes.Core`. |
| **Language Runtime Suite** | 82.4ms total | **5.4ms total** | **~15.2x faster** | 56 functional capabilities, peak 26.09x on SDT JSON serialization. |
| **UDM Single Part Read & Decompress** | 45ms – 80ms *(Worker IPC)* | **~1.8ms** *(Direct GzDecoder)* | **~30x faster** | Tested on `Plan068Proc` (1.3MB decompressed text). |
| **Live KB Metadata Hydration (1,800 objs)** | 12,000ms – 18,000ms | **320ms** | **> 35x faster** | Direct TDS relational query vs serial COM STA loads (`test_udm_real_kb_live_read_procedure`). |
| **Full Rebuild All** | No successful matched-workload baseline | Live runs include refusals/errors | **Not verified** | Former 480s / 1.25s (>380×) comparison withdrawn; follow the current acceptance ledger. |
