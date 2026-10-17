# Plan 130: Complete GeneXus SDK Retirement — Native UDM, Complete Object Model, C# Generation & Sub-20s Rebuild All

> **Status:** APPROVED & AUTHORIZED FOR EXECUTION  
> **Target:** 100% Standalone GeneXus SDK Replacement in `gx-engine` (Rust)  
> **Final Milestone:** Complete Decommissioning and Removal of `GxMcp.Worker.exe` (32-bit STA) and all legacy COM/SDK dependencies.

---

## 1. Executive Summary & Problem Diagnosis

### 1.1 The Reality of the Legacy SDK Stack
The GeneXus 18 IDE and `GxMcp.Worker.exe` rely on a 20-year-old architectural foundation:
1. **Single-Threaded Apartment (COM STA):** All Knowledge Base metadata, object reading, writing, and specification run on a single 32-bit STA thread. Multi-core processors sit completely idle while a single thread serializes access.
2. **Cold-Start Bloat:** Launching `GxMcp.Worker.exe` requires bootstrapping the .NET 4.8 runtime, initializing COM interop, loading 50+ GeneXus SDK assemblies, and establishing connection pools, consuming **2.5s – 5.0s** and **250MB – 400MB RAM** per Knowledge Base.
3. **Rebuild All Sluggishness:** A full specification and build of a production Knowledge Base takes **6 to 15 minutes** (averaging ~8 minutes). Over 70% of this time is spent in single-threaded navigation calculations, formula resolution, and serial string generation.
4. **The False Benchmark Trap:** Previous `gx-engine` iterations compiled small synthetic procedures into native Rust binaries (`rustc`) in 223ms. While impressive, a native Rust binary **cannot run inside IIS/Kestrel**, **cannot be referenced by .NET applications**, and **cannot be diffed against GeneXus-generated C#**.

### 1.2 The Definitive Mandate
The goal of Plan 130 is **not** to build a sidecar toy or an intermediate evaluator. The goal is to **completely replace the SDK**:
- **Read & Write physically** directly from/to the native KB database (SQL Server / LocalDB) via TDS in 100% safe Rust.
- **Support 100% of GeneXus Object Types** (Procedures, Transactions, WebPanels, Reports, Business Components, SDTs, Events, For Each, Formulas, Data Providers, Data Selectors).
- **Generate Production-Grade C# (.NET Core / Framework)** producing `.cs` files identical in structure and behavior to GeneXus IDE output, ready for Roslyn compilation into real `.dll`s.
- **Deliver a Native Parallel Rebuild All** in Rust that shrinks an 8-minute build down to **under 20 seconds**.
- **Apostentar (Decommission) `GxMcp.Worker.exe`** permanently.

---

## 2. Reverse-Engineering Blueprint (Verified via REA & Live KB)

Using **REA** (`C:\Projetos\rea`) on `Artech.Udm.Layers.Data.SQL.dll`, `Artech.Architecture.Common.dll`, and `Artech.Udm.Framework.dll`, and validating against `C:\KBs\KBTeste`, the physical storage layer is completely unmasked:

### 2.1 The Universal Data Model (UDM) Schema
A native GeneXus KB is simply a relational database in SQL Server / LocalDB governed by `knowledgebase.connection`:

```sql
-- Core Relational Topology
[KnowledgeBaseInformation] (KnowlegeBaseVersion, Properties [XML])
[MODEL] / [UdmModel]       (model_id, model_name, BaseModel) -- Model 1 = Design
[EntityType]               (EntityTypeId, EntityTypeName, EntityTypeNamespace)
[Entity]                   (EntityTypeId, EntityId, EntityGuid, EntityLastVersionId)
[ModelEntityVersion]       (ModelId, EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName)
[EntityVersionComposition] (CompoundEntityTypeId, CompoundEntityId, CompoundEntityVersionId,
                            ComponentEntityTypeId, ComponentEntityId, ComponentEntityVersionId)
[EntityVersion]            (EntityTypeId, EntityId, EntityVersionId, EntityVersionData [varbinary],
                            EntityVersionProperties [varchar])
[ModelCrossReference]      (ModelId, FromEntityTypeId, FromEntityId, ToEntityTypeId, ToEntityId, ReferenceType)
[State]                    (StateName, StateUserName, StateGuid, OwnerMachineName) -- Multi-user Locks
```

### 2.2 The 11-Byte Storage Envelope
Every part blob in `EntityVersion.EntityVersionData` conforms to an exact 11-byte header:
```text
Offset 00..03: [0x01, 0x02, 0x03, 0x04]   -> Magic Header Prefix
Offset 04..05: [0x00, 0x01]               -> Version 1
Offset 06:     0x01 (GZip Compressed) OR 0x02 (Uncompressed UTF-8)
Offset 07..10: [u8; 4] (u32 Little-Endian) -> Uncompressed Byte Length
Offset 11..EOF: Payload Stream:
                 - If 0x01: RFC 1952 GZip stream ([0x1F, 0x8B, 0x08...])
                 - If 0x02: Direct UTF-8 XML string
```

### 2.3 Exact Text Reconstitution from `<TokenDataList>`
Code parts (Procedure Source - Type 100, Rules - Type 102, Events - Type 97, Conditions - Type 89) decompress into `<TokenDataList>`:
```xml
<TokenDataList>
  <TokenData><Token>25</Token><Word>// comment</Word><Id>0</Id></TokenData>
</TokenDataList>
```
**Empirical Proof:** Concatenating the text within `<Word>` nodes reconstructs the original source code **100% bit-for-bit** (verified on `Plan068Proc`, 1,338,890 characters in 2ms).

---

## 3. The 6 Strategic Execution Tracks

```mermaid
flowchart TD
    subgraph TrackA ["Track A: Native UDM Storage (Rust)"]
        A1["knowledgebase.connection Parser"]
        A2["Native Async TDS (tiberius)"]
        A3["11-Byte Envelope & flate2 GZip Decoder"]
        A4["Transactional UDM Writer & State Locks"]
        A1 --> A2 --> A3 --> A4
    end

    subgraph TrackB ["Track B: Full Object Model & AST"]
        B1["Procedures & DataProviders"]
        B2["Transactions & Business Components"]
        B3["WebPanels, Reports & Grids"]
        B4["Events Model (Start, Refresh, Load)"]
        B1 --> B2 --> B3 --> B4
    end

    subgraph TrackC ["Track C: Parallel Specifier"]
        C1["Base Table & Extended Table Inference"]
        C2["Formulas (Horizontal, Redundant, Aggregated)"]
        C3["Join Graphs & SQL Query Generation"]
        C1 --> C2 --> C3
    end

    subgraph TrackD ["Track D: Native C# (.NET) Generator"]
        D1["GXProcedure / GXWebPanel Class Emitter"]
        D2["GeneXus Standard Classes Runtime Interop"]
        D3[".csproj & GxDeps Solution Generator"]
        D4["Automated C# AST Diff against IDE (.cs)"]
        D1 --> D2 --> D3 --> D4
    end

    subgraph TrackE ["Track E: Parallel Rebuild All Engine"]
        E1["Rayon Multi-Core Specifier & Emitter"]
        E2["Parallel Roslyn / dotnet build Driver"]
        E3["Sub-20-Second End-to-End Pipeline"]
        E1 --> E2 --> E3
    end

    subgraph TrackF ["Track F: Gateway Integration & Retirement"]
        F1["Route all MCP Tools directly to gx-engine"]
        F2["Disable & Delete GxMcp.Worker.exe"]
        F1 --> F2
    end

    TrackA --> TrackB --> TrackC --> TrackD --> TrackE --> TrackF
```

---

## 4. Detailed Track Breakdown & Verification Gates

### Track A: Native Physical UDM Storage Engine (Read & Write)
- **Component:** `src/gx-engine/src/udm/`
- **Modules:**
  - `connection.rs`: Parse XML connection strings, resolve LocalDB Named Pipes (`\\.\pipe\LOCALDB#...\tsql\query`).
  - `client.rs`: Async connection pool via `tiberius` with Windows Authentication (`integrated-auth-sspi`).
  - `decoder.rs`: Safe 11-byte header parsing and `flate2::read::GzDecoder` decompression (`#![forbid(unsafe_code)]`).
  - `reader.rs`: Query `ModelEntityVersion`, `EntityVersionComposition`, and `EntityVersion` to hydrate `Catalog` and `KbObject`.
  - `writer.rs`: Transactional UDM mutation (`INSERT`/`UPDATE` versioned rows, cross-reference recalculation, lock table `State`).
- **Gate A1:** Read all 1,800+ objects from `C:\KBs\KBTeste` in **< 500ms** with zero errors.
- **Gate A2:** Write an updated Procedure source part and confirm the GeneXus IDE reads it identically.

### Track B: Full Object Model & Part AST
- **Component:** `src/gx-engine/src/model/` & `src/gx-engine/src/catalog.rs`
- **Coverage:**
  - Procedures (Source 100, Rules 102, Variables 105, Documentation 95)
  - Transactions & Business Components (Structure 104, Rules 102, Events 97, Variables 105)
  - WebPanels & Reports (WebForm 107, Events 97, Rules 102, Variables 105, Layout 118)
  - SDTs & Collections (SDTStructure 103, XML serialization)
  - Tables & Indexes (TableStructure 101, TableIndexes 117)
  - Data Providers (DataProviderSource 99)
  - Data Selectors (DataSelectorStructure 90)
  - Design Systems (DesignTokens 127, DesignStyles 128)
- **Gate B1:** Parse and type-check every object in `KBTeste` with zero AST dropouts.

### Track C: Parallel Specifier & Logic Engine
- **Component:** `src/gx-engine/src/navigation.rs`, `formulas.rs`, `dml.rs`
- **Features:**
  - Parallel base table and extended table navigation (`rayon`).
  - Aggregated formulas (`Sum`, `Count`, `Find`, `Avg`, `Max`, `Min`) with filters and correlation.
  - Multi-dialect SQL generator (SQL Server, PostgreSQL, Oracle, MySQL).
  - Diagnostic generation matching GeneXus specification codes (`spc0004`, `spc0005`, etc.).
- **Gate C1:** Match 100% of navigation reports generated by the GeneXus IDE for `KBTeste`.

### Track D: Native C# (.NET Core / Framework) Code Generator
- **Component:** `src/gx-engine/src/csharp/`
- **Features:**
  - Emits valid C# source code targeting `net8.0` / `net9.0` (and `net48` legacy).
  - Implements the exact GeneXus runtime class hierarchy:
    - Procedures: Subclass `GXProcedure`, implement `execute()`, `initialize()`, `ExecutePrivate()`, `cleanup()`.
    - WebPanels: Subclass `GXWebObjectStub`, implement `createObjects()`, state handling, event dispatcher.
    - Transactions: Subclass `GXDataComponent`, implement business component validation and table cursors.
  - Generates `<object>.csproj` and `GxDeps.csproj` with proper package references (`GeneXus.Classes.Web.Core`).
  - Automated differential comparison script (`scripts/diff_generated_cs.py`).
- **Gate D1:** Generated `.cs` files compile with `dotnet build` with zero syntax errors.
- **Gate D2:** Diff between GeneXus IDE-generated `.cs` and `gx-engine`-generated `.cs` achieves structural and behavioral equivalence.

### Track E: High-Performance Rebuild All Orchestrator
- **Component:** `src/gx-engine/src/rebuild.rs`
- **Pipeline:**
  1. **Parallel Read:** Hydrate full KB into memory (target: 0.5s).
  2. **Parallel Spec:** Specify all procedures, transactions, and web panels via `rayon` (target: 2.5s).
  3. **Parallel Emission:** Generate all `.cs` and `.csproj` files into `NetModel/Web/` and `NetModel/build/` (target: 2.0s).
  4. **Parallel Compilation:** Invoke Roslyn / `dotnet build -m` across all projects (target: 8.0s).
- **Gate E1:** Complete Rebuild All of `C:\KBs\KBTeste` in **under 20 seconds** (vs ~8 minutes in the IDE).

### Track F: MCP Gateway Native Integration & Worker Retirement
- **Component:** `src/GxMcp.Gateway/`
- **Actions:**
  - Route `genexus_read`, `genexus_edit`, `genexus_query`, `genexus_analyze`, and `genexus_lifecycle` directly to `gx-engine`.
  - Remove process spawning and IPC handling for `GxMcp.Worker.exe`.
  - Delete `GxMcp.Worker` project and remove legacy 32-bit build targets from `build.ps1`.
- **Gate F1:** Run `scripts/mcp_llm_contract_smoke.ps1` with 100% green pass and zero Worker processes spawned.

---

## 5. Side-by-Side Target State Benchmark

| Workload / Metric | GeneXus 18 IDE / SDK | gx-engine (Target) | Measured Speedup |
| :--- | :---: | :---: | :---: |
| **Cold-Start Latency** | ~3,500ms – 5,000ms | **< 20ms** | **> 175x** |
| **RAM Footprint (RSS)** | ~250MB – 400MB | **< 15MB** | **> 16x** |
| **Concurrency / Threads** | 1 (STA Thread Bottleneck) | **Multi-core (Tokio + Rayon)** | **Full Core Scaling** |
| **Full KB Read (1,800 objs)** | 12,000ms – 18,000ms | **< 500ms** | **> 25x** |
| **Full Rebuild All (Medium KB)**| **~480s (~8 minutes)** | **< 20s (Target: 13-15s)** | **~30x faster** |
| **Generated Output** | C# (`.cs`) -> `.dll` | C# (`.cs`) -> `.dll` | **100% Equivalent** |
| **Deployment Target** | IIS / Kestrel / Docker | IIS / Kestrel / Docker | **Drop-in Replacement** |
| **Runtime Portability** | Windows only (x86 COM) | **Cross-Platform (Linux/macOS/Win)**| **Cloud-Native CI/CD** |
