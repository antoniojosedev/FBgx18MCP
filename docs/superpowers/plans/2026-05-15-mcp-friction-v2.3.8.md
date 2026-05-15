# MCP Friction-Report 2026-05-15 Sweep (v2.3.8) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close all 16 items + 5 quick-wins from `docs/mcp-friction-report-2026-05-15.md` in a single v2.3.8 release; baseline includes the current WIP (validate_payload, bulk_edit, apply_template, diff, export_unified, async edit, indexed source-search, PatternVirtual fallback).

**Architecture:** Worker (.NET 4.8) holds the GeneXus SDK and exposes services via stdio JSON; Gateway (.NET 8) is a stdio MCP front-end that routes calls and shapes tool schemas. Changes are layered: foundation (caller graph + index state) → discovery → edit/write → variables → build → output → i18n+cancel. Each phase produces working, tested software.

**Tech Stack:** C# (.NET 4.8 worker, .NET 8 gateway), xUnit + Moq, Newtonsoft.Json, Artech GeneXus 18 SDK.

**Spec:** [`docs/superpowers/specs/2026-05-15-mcp-friction-v2.3.8-design.md`](../specs/2026-05-15-mcp-friction-v2.3.8-design.md)

---

## File Structure (planned changes)

### Worker (.NET 4.8) — `src/GxMcp.Worker/`

| File | Action | Responsibility |
|---|---|---|
| `Services/CallerGraphService.cs` | **Create** | Single source for callers/callees; replaces ad-hoc graph in AnalyzeService and inspect |
| `Services/SourceSearchService.cs` | Modify | A2: IndexCold/Timeout envelopes |
| `Services/ListService.cs` | Modify | A3: nameFilter, descriptionFilter, pathPrefix |
| `Services/AnalyzeService.cs` | Modify | A4: delegate impact to CallerGraphService; waitForIndex flag |
| `Services/IndexCacheService.cs` | Modify | A1: expose `IndexState { Status, LastIndexedAt, TotalObjects, Progress, EtaMs }` |
| `CommandDispatcher.cs` (Gateway side, see `Routers/SystemRouter.cs`) | Modify | A1: WhoAmI response gains `index` block |
| `Services/WriteService.cs` | Modify | B1, B3, B4, C2 (modify_variable), C3, C5 (patch-window rollback) |
| `Helpers/XmlEquivalence.cs` | Modify | B1: EOL/whitespace normalization in comparison only |
| `Helpers/DiffBuilder.cs` | Modify | B2: `ByteLevelDivergence` helper |
| `Helpers/VariableTypeResolver.cs` | **Create** | C1: synonym map → canonical GeneXus type |
| `Helpers/WebFormSchemaHints.cs` | Modify | C4: `[var:N]` resolver; F2 used by ErrorMessages |
| `Structure/PartAccessor.cs` | Modify | C3: symmetric variable part dispatch |
| `Services/BuildService.cs` | Modify | D1/D2: csproj gen with auto-callee `<Reference>` + buildPlan |
| `Services/ObjectService.cs` | Modify | E2: read pagination default |
| `Helpers/ErrorMessages.cs` | **Create** | F1: PT-BR→EN canonical message translation |

### Gateway (.NET 8) — `src/GxMcp.Gateway/`

| File | Action | Responsibility |
|---|---|---|
| `tool_definitions.json` | Modify | All tool schema changes |
| `McpRouter.cs` | Modify | Wire new tool `genexus_modify_variable` |
| `Routers/OperationsRouter.cs` | Modify | C2 routing; expose new flags |
| `Routers/AnalyzeRouter.cs` | Modify | A4 waitForIndex flag |
| `Routers/SystemRouter.cs` (or Program.cs whoami endpoint) | Modify | A1 index block |
| `BackgroundJobRegistry.cs` | Modify | E3 notified flag + F3 cancel token |
| `WorkerPool.cs` / `WorkerProcess.cs` | Modify | F3 propagate cancel to worker |
| `ResponseSizeGuard.cs` | Modify | E1 compact lifecycle status response |

### Tests

| File | Action | Phase |
|---|---|---|
| `src/GxMcp.Worker.Tests/CallerGraphServiceTests.cs` | Create | 1 |
| `src/GxMcp.Worker.Tests/IndexStateTests.cs` | Create | 1 |
| `src/GxMcp.Worker.Tests/SourceSearchEnvelopeTests.cs` | Create | 2 |
| `src/GxMcp.Worker.Tests/ListDiscoveryTests.cs` | Create | 2 |
| `src/GxMcp.Worker.Tests/WriteServiceEolNormalizationTests.cs` | Create | 3 |
| `src/GxMcp.Worker.Tests/WriteServiceNearMatchHintTests.cs` | Create | 3 |
| `src/GxMcp.Worker.Tests/PatchShapesTests.cs` | Create | 3 |
| `src/GxMcp.Worker.Tests/VariableTypeResolverTests.cs` | Create | 4 |
| `src/GxMcp.Worker.Tests/ModifyVariableTests.cs` | Create | 4 |
| `src/GxMcp.Worker.Tests/DeleteVariableSymmetryTests.cs` | Create | 4 |
| `src/GxMcp.Worker.Tests/PatchWindowRollbackTests.cs` | Create | 4 |
| `src/GxMcp.Worker.Tests/BuildSegmentedCsprojTests.cs` | Create | 5 |
| `src/GxMcp.Worker.Tests/ReadPaginationTests.cs` | Modify (extend existing) | 6 |
| `src/GxMcp.Gateway.Tests/CompactStatusTests.cs` | Create | 6 |
| `src/GxMcp.Gateway.Tests/BackgroundJobsDedupTests.cs` | Create | 6 |
| `src/GxMcp.Worker.Tests/ErrorMessagesTests.cs` | Create | 7 |
| `src/GxMcp.Worker.Tests/VarBindingResolverTests.cs` | Create | 7 |
| `src/GxMcp.Gateway.Tests/CancelLifecycleTests.cs` | Create | 7 |

---

## Conventions

- **Run a single test:** `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~<TestName>" -v n`
- **Run all worker tests:** `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj`
- **Run all gateway tests:** `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj`
- **Build all:** `dotnet build`
- **Commit cadence:** at the end of every task (TDD red→green→commit). Use `feat:`, `fix:`, `refactor:`, `test:` prefixes.

---

## Phase 1 — Foundation: caller graph + index state (#1, #8)

Unifies the two index/caller paths so everything downstream can read one source of truth.

### Task 1.1 — Extract `IndexState` from `IndexCacheService`

**Files:**
- Modify: `src/GxMcp.Worker/Services/IndexCacheService.cs`
- Create: `src/GxMcp.Worker/Models/IndexState.cs`
- Test: `src/GxMcp.Worker.Tests/IndexStateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// src/GxMcp.Worker.Tests/IndexStateTests.cs
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Tests
{
    public class IndexStateTests
    {
        [Fact]
        public void GetState_BeforeIndex_ReturnsCold()
        {
            var svc = new IndexCacheService();
            var s = svc.GetState();
            Assert.Equal("Cold", s.Status);
            Assert.Null(s.LastIndexedAt);
            Assert.Equal(0, s.TotalObjects);
        }

        [Fact]
        public void GetState_AfterIndex_ReturnsReadyWithCount()
        {
            var svc = new IndexCacheService();
            svc.MarkIndexComplete(totalObjects: 42);
            var s = svc.GetState();
            Assert.Equal("Ready", s.Status);
            Assert.NotNull(s.LastIndexedAt);
            Assert.Equal(42, s.TotalObjects);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~IndexStateTests"`
Expected: FAIL — `IndexState`, `GetState`, `MarkIndexComplete` not defined.

- [ ] **Step 3: Implement `IndexState` model**

```csharp
// src/GxMcp.Worker/Models/IndexState.cs
using System;

namespace GxMcp.Worker.Models
{
    public class IndexState
    {
        public string Status { get; set; }       // "Cold" | "Reindexing" | "Ready"
        public DateTime? LastIndexedAt { get; set; }
        public int TotalObjects { get; set; }
        public double? Progress { get; set; }    // 0..1, only when Reindexing
        public int? EtaMs { get; set; }          // only when Reindexing
    }
}
```

- [ ] **Step 4: Wire state tracking into `IndexCacheService`**

Add to `src/GxMcp.Worker/Services/IndexCacheService.cs`:

```csharp
private IndexState _state = new IndexState { Status = "Cold", TotalObjects = 0 };
private readonly object _stateLock = new object();

public IndexState GetState()
{
    lock (_stateLock) {
        return new IndexState {
            Status = _state.Status,
            LastIndexedAt = _state.LastIndexedAt,
            TotalObjects = _state.TotalObjects,
            Progress = _state.Progress,
            EtaMs = _state.EtaMs
        };
    }
}

public void MarkReindexStarted(int totalEstimated)
{
    lock (_stateLock) {
        _state.Status = "Reindexing";
        _state.Progress = 0;
        _state.TotalObjects = totalEstimated;
    }
}

public void MarkReindexProgress(double progress, int etaMs)
{
    lock (_stateLock) {
        _state.Progress = progress;
        _state.EtaMs = etaMs;
    }
}

public void MarkIndexComplete(int totalObjects)
{
    lock (_stateLock) {
        _state.Status = "Ready";
        _state.LastIndexedAt = DateTime.UtcNow;
        _state.TotalObjects = totalObjects;
        _state.Progress = null;
        _state.EtaMs = null;
    }
}
```

Then find existing index-rebuild call sites (search for `Objects.Add(` or `BuildIndex(` in `IndexCacheService.cs` and `KbWatcherService.cs`) and call `MarkReindexStarted` at entry, `MarkReindexProgress` per N entries, `MarkIndexComplete` on exit.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~IndexStateTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/GxMcp.Worker/Models/IndexState.cs src/GxMcp.Worker/Services/IndexCacheService.cs src/GxMcp.Worker.Tests/IndexStateTests.cs
git commit -m "feat(worker): expose IndexState (Cold/Reindexing/Ready) from IndexCacheService"
```

### Task 1.2 — Surface `index` block in `whoami`

**Files:**
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs` (whoami handler) OR `src/GxMcp.Gateway/Routers/SystemRouter.cs`
- Test: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs` (extend existing)

- [ ] **Step 1: Add failing test**

Locate `WhoamiVersionTests.cs`; append:

```csharp
[Fact]
public void Whoami_IncludesIndexBlock()
{
    var json = SystemRouter.Whoami(/* mock worker returning index state */);
    var obj = JObject.Parse(json);
    Assert.NotNull(obj["index"]);
    Assert.Contains(obj["index"]["status"].ToString(), new[] { "Cold", "Reindexing", "Ready" });
    Assert.NotNull(obj["index"]["totalObjects"]);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "Whoami_IncludesIndexBlock"`
Expected: FAIL — `index` missing.

- [ ] **Step 3: Wire index state through whoami**

In Worker `CommandDispatcher.HandleWhoAmI` (or wherever whoami is constructed), inject `_indexCache.GetState()` into the response JObject under key `index`. Gateway `SystemRouter` passes through unchanged.

```csharp
// Worker whoami construction
var state = _indexCache.GetState();
result["index"] = new JObject {
    ["status"] = state.Status,
    ["totalObjects"] = state.TotalObjects,
    ["lastIndexedAt"] = state.LastIndexedAt?.ToString("o"),
    ["progress"] = state.Progress,
    ["etaMs"] = state.EtaMs
};
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "Whoami_IncludesIndexBlock"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(whoami): surface index status block (closes #1 partially, #8 prereq)"
```

### Task 1.3 — Create unified `CallerGraphService`

**Files:**
- Create: `src/GxMcp.Worker/Services/CallerGraphService.cs`
- Test: `src/GxMcp.Worker.Tests/CallerGraphServiceTests.cs`

The existing caller graph lives ad-hoc inside `AnalyzeService.ImpactAnalysis` and again inside `ObjectService.GetCallers` (used by `inspect`). Goal: one canonical service both delegate to.

- [ ] **Step 1: Write failing tests**

```csharp
// src/GxMcp.Worker.Tests/CallerGraphServiceTests.cs
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class CallerGraphServiceTests
    {
        [Fact]
        public void GetCallers_ReturnsSameSet_AsInspectCallers()
        {
            // Fixture: small in-memory index with A→B→C call chain
            var fixture = TestFixtures.SmallCallGraph();
            var svc = new CallerGraphService(fixture.Index, fixture.ObjectService);
            var callers = svc.GetCallers("C");
            Assert.Contains("B", callers);
            Assert.Equal(2, callers.Count); // direct A→B caller? if A→B→C then only B is direct caller of C
        }

        [Fact]
        public void GetCallees_Transitive_StopsAtCap()
        {
            var fixture = TestFixtures.LargeCallChain(depth: 250);
            var svc = new CallerGraphService(fixture.Index, fixture.ObjectService);
            var result = svc.GetCalleesTransitive("Root", maxNodes: 200);
            Assert.True(result.Truncated);
            Assert.Equal(200, result.Nodes.Count);
        }
    }
}
```

Note: `TestFixtures` is a helper to create — add it under `src/GxMcp.Worker.Tests/TestFixtures.cs` if missing. Minimal stub:

```csharp
public static class TestFixtures
{
    public class CallGraphFixture
    {
        public IndexCacheService Index;
        public ObjectService ObjectService;
    }
    public static CallGraphFixture SmallCallGraph() {
        // build index where A.SourceSnippet contains "B(...)", B.SourceSnippet contains "C(...)"
        // ... return fixture
        throw new NotImplementedException("Implement in Step 3");
    }
    public static CallGraphFixture LargeCallChain(int depth) { throw new NotImplementedException(); }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~CallerGraphServiceTests"`
Expected: FAIL — class not defined.

- [ ] **Step 3: Implement `CallerGraphService`**

```csharp
// src/GxMcp.Worker/Services/CallerGraphService.cs
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class TransitiveResult
    {
        public List<string> Nodes { get; set; } = new List<string>();
        public bool Truncated { get; set; }
        public int Depth { get; set; }
    }

    public class CallerGraphService
    {
        private readonly IndexCacheService _index;
        private readonly ObjectService _objectService;

        public CallerGraphService(IndexCacheService index, ObjectService objectService)
        {
            _index = index;
            _objectService = objectService;
        }

        public List<string> GetCallers(string targetName)
        {
            var idx = _index.GetIndex();
            var callers = new List<string>();
            var pattern = new Regex(@"\b" + Regex.Escape(targetName) + @"\s*\(", RegexOptions.IgnoreCase);
            foreach (var e in idx.Objects.Values)
            {
                if (e.Name == targetName) continue;
                if (!string.IsNullOrEmpty(e.SourceSnippet) && pattern.IsMatch(e.SourceSnippet))
                    callers.Add(e.Name);
            }
            return callers;
        }

        public List<string> GetCallees(string objectName)
        {
            // Parse SourceSnippet of objectName for FunctionCalls
            var idx = _index.GetIndex();
            if (!idx.Objects.TryGetValue(objectName, out var entry)) return new List<string>();
            var callees = new HashSet<string>();
            if (entry.Callees != null) foreach (var c in entry.Callees) callees.Add(c);
            return new List<string>(callees);
        }

        public TransitiveResult GetCalleesTransitive(string root, int maxNodes = 200)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((root, 0));
            int maxDepth = 0;
            var result = new TransitiveResult();
            while (queue.Count > 0 && visited.Count < maxNodes)
            {
                var (name, d) = queue.Dequeue();
                if (!visited.Add(name)) continue;
                if (name != root) result.Nodes.Add(name);
                maxDepth = System.Math.Max(maxDepth, d);
                foreach (var callee in GetCallees(name)) queue.Enqueue((callee, d + 1));
            }
            result.Truncated = queue.Count > 0;
            result.Depth = maxDepth;
            return result;
        }
    }
}
```

Note: if `SearchIndexEntry.Callees` doesn't exist yet, add `public List<string> Callees { get; set; }` to `src/GxMcp.Worker/Models/SearchIndex.cs` and populate it during indexing alongside `SourceSnippet`.

- [ ] **Step 4: Implement fixtures + run tests**

In `src/GxMcp.Worker.Tests/TestFixtures.cs` implement `SmallCallGraph` and `LargeCallChain` by directly populating `IndexCacheService` via a new test-only `LoadFromEntries(IEnumerable<SearchIndexEntry>)` method (add it as `internal` + `[InternalsVisibleTo("GxMcp.Worker.Tests")]`).

Run: `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~CallerGraphServiceTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/GxMcp.Worker/Services/CallerGraphService.cs src/GxMcp.Worker/Models/SearchIndex.cs src/GxMcp.Worker.Tests/CallerGraphServiceTests.cs src/GxMcp.Worker.Tests/TestFixtures.cs
git commit -m "feat(worker): unified CallerGraphService (#8 prereq)"
```

### Task 1.4 — Delegate `AnalyzeService.ImpactAnalysis` to `CallerGraphService` + add `waitForIndex`

**Files:**
- Modify: `src/GxMcp.Worker/Services/AnalyzeService.cs`
- Modify: `src/GxMcp.Gateway/Routers/AnalyzeRouter.cs` (expose `waitForIndex`)
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Test: `src/GxMcp.Worker.Tests/CallerGraphServiceTests.cs` (extend)

- [ ] **Step 1: Add failing test**

Append to `CallerGraphServiceTests.cs`:

```csharp
[Fact]
public void AnalyzeImpact_AndInspectCallers_ReturnIdenticalCallers()
{
    var fixture = TestFixtures.SmallCallGraph();
    var analyze = new AnalyzeService(fixture.Index, fixture.ObjectService, new CallerGraphService(fixture.Index, fixture.ObjectService));
    var impactJson = analyze.ImpactAnalysis("C", waitForIndex: true);
    var impact = JObject.Parse(impactJson);
    var callersFromImpact = impact["callers"].Select(j => j.ToString()).OrderBy(x => x);
    var callersFromGraph = new CallerGraphService(fixture.Index, fixture.ObjectService).GetCallers("C").OrderBy(x => x);
    Assert.Equal(callersFromGraph, callersFromImpact);
}

[Fact]
public void AnalyzeImpact_IndexReindexing_AndNotWaiting_ReturnsReindexingEnvelope()
{
    var fixture = TestFixtures.SmallCallGraph();
    fixture.Index.MarkReindexStarted(100);
    var analyze = new AnalyzeService(fixture.Index, fixture.ObjectService, new CallerGraphService(fixture.Index, fixture.ObjectService));
    var json = analyze.ImpactAnalysis("C", waitForIndex: false);
    Assert.Contains("\"status\":\"Reindexing\"", json);
}
```

- [ ] **Step 2: Run test, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "AnalyzeImpact_"`
Expected: FAIL — signature mismatch or stale impl.

- [ ] **Step 3: Refactor `AnalyzeService.ImpactAnalysis`**

```csharp
// src/GxMcp.Worker/Services/AnalyzeService.cs
private readonly CallerGraphService _graph;

public AnalyzeService(IndexCacheService index, ObjectService objSvc, CallerGraphService graph)
{
    _index = index; _objectService = objSvc; _graph = graph;
}

public string ImpactAnalysis(string targetName, bool waitForIndex = true, int waitTimeoutMs = 30000)
{
    var state = _index.GetState();
    if (state.Status != "Ready")
    {
        if (!waitForIndex)
            return new JObject {
                ["status"] = state.Status,
                ["etaMs"] = state.EtaMs,
                ["progress"] = state.Progress
            }.ToString();
        // wait
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_index.GetState().Status != "Ready" && sw.ElapsedMilliseconds < waitTimeoutMs)
            System.Threading.Thread.Sleep(200);
        if (_index.GetState().Status != "Ready")
            return new JObject { ["status"] = "Timeout", ["waitedMs"] = sw.ElapsedMilliseconds }.ToString();
    }
    var callers = _graph.GetCallers(targetName);
    var calleesGraph = _graph.GetCalleesTransitive(targetName, 200);
    return new JObject {
        ["status"] = "Ready",
        ["callers"] = new JArray(callers),
        ["callees"] = new JArray(calleesGraph.Nodes),
        ["calleesTruncated"] = calleesGraph.Truncated,
        ["maxDepth"] = calleesGraph.Depth
    }.ToString();
}
```

Update Worker `Program.cs` / DI to inject `CallerGraphService` into `AnalyzeService`.

- [ ] **Step 4: Expose `waitForIndex` through gateway**

In `src/GxMcp.Gateway/Routers/AnalyzeRouter.cs`, add `waitForIndex` to the impact mode parameter pass-through; in `tool_definitions.json`, add the property under `genexus_analyze.inputSchema.properties` with default `true`.

- [ ] **Step 5: Run tests**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "AnalyzeImpact_"`
Expected: PASS.

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "ToolSchemaSizeTests"`
Expected: PASS (or bump budget; see Final task 8.2).

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(analyze): impact delegates to CallerGraphService + waitForIndex flag (#8)"
```

---

## Phase 2 — Discovery: search & list (#1, #2, #3)

### Task 2.1 — `SourceSearchService` returns `IndexCold` envelope

**Files:**
- Modify: `src/GxMcp.Worker/Services/SourceSearchService.cs`
- Test: `src/GxMcp.Worker.Tests/SourceSearchEnvelopeTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/SourceSearchEnvelopeTests.cs
using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class SourceSearchEnvelopeTests
    {
        [Fact]
        public void Search_OnColdIndex_ReturnsIndexColdEnvelope()
        {
            var index = new IndexCacheService(); // Cold by default
            var objs = TestFixtures.EmptyObjectService();
            var svc = new SourceSearchService(index, objs);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "Clicksign" });
            var obj = JObject.Parse(json);
            Assert.Equal("IndexCold", obj["status"]?.ToString());
            Assert.NotNull(obj["retryAfterMs"]);
            Assert.Null(obj["hits"]); // never silent empty
        }

        [Fact]
        public void Search_OnReadyIndex_ReturnsHitsEnvelope()
        {
            var fixture = TestFixtures.SmallCallGraph();
            var svc = new SourceSearchService(fixture.Index, fixture.ObjectService);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "B" });
            var obj = JObject.Parse(json);
            Assert.NotNull(obj["hits"]);
        }

        [Fact]
        public void Search_HardTimeoutExceeded_ReturnsTimeoutEnvelope()
        {
            var fixture = TestFixtures.PathologicalIndex(entries: 10000);
            var svc = new SourceSearchService(fixture.Index, fixture.ObjectService);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = ".*", TimeoutMs = 100 });
            var obj = JObject.Parse(json);
            Assert.Equal("Timeout", obj["status"]?.ToString());
            Assert.NotNull(obj["partialHits"]);
            Assert.NotNull(obj["totalScanned"]);
        }
    }
}
```

- [ ] **Step 2: Run test, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "SourceSearchEnvelopeTests"`
Expected: FAIL — envelope keys missing.

- [ ] **Step 3: Add envelope + timeout to `SearchAsJson`**

In `SourceSearchService.cs`:

```csharp
public class SourceSearchCriteria
{
    // ... existing properties
    public int TimeoutMs { get; set; } = 30000;
}

public string SearchAsJson(SourceSearchCriteria c)
{
    var state = _index.GetState();
    if (state.Status != "Ready")
    {
        return new JObject {
            ["status"] = state.Status == "Cold" ? "IndexCold" : "Reindexing",
            ["retryAfterMs"] = state.EtaMs ?? 5000,
            ["progress"] = state.Progress
        }.ToString();
    }

    // ... existing pattern/regex setup ...

    var sw = System.Diagnostics.Stopwatch.StartNew();
    int scanned = 0;
    foreach (var e in entries)
    {
        if (sw.ElapsedMilliseconds > c.TimeoutMs)
        {
            return new JObject {
                ["status"] = "Timeout",
                ["partialHits"] = hits,
                ["totalScanned"] = scanned,
                ["totalObjects"] = entries.Count
            }.ToString();
        }
        scanned++;
        // ... existing match logic ...
    }
    return new JObject {
        ["status"] = "Ready",
        ["count"] = hits.Count,
        ["hits"] = hits
    }.ToString();
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "SourceSearchEnvelopeTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(search): IndexCold + Timeout envelopes; never silent empty (#1)"
```

### Task 2.2 — `list_objects` discovery: nameFilter, descriptionFilter, pathPrefix

**Files:**
- Modify: `src/GxMcp.Worker/Services/ListService.cs`
- Modify: `src/GxMcp.Worker/Models/SearchIndex.cs` (add `ParentFolderPath`)
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Test: `src/GxMcp.Worker.Tests/ListDiscoveryTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// src/GxMcp.Worker.Tests/ListDiscoveryTests.cs
using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class ListDiscoveryTests
    {
        [Fact]
        public void NameFilter_MatchesName_NotDescription()
        {
            var fixture = TestFixtures.IndexWithFolders();
            // entries: Name="ComissaoLiberaPareceres" + Name="PSPContParecer" desc="...pareceres"
            var svc = new ListService(fixture.Index);
            var json = svc.List(new ListCriteria { NameFilter = "Libera" });
            var hits = JArray.Parse(JObject.Parse(json)["objects"].ToString());
            Assert.Contains(hits, h => h["name"].ToString() == "ComissaoLiberaPareceres");
            Assert.DoesNotContain(hits, h => h["name"].ToString() == "PSPContParecer");
        }

        [Fact]
        public void DescriptionFilter_MatchesDescription_NotName()
        {
            var fixture = TestFixtures.IndexWithFolders();
            var svc = new ListService(fixture.Index);
            var json = svc.List(new ListCriteria { DescriptionFilter = "pareceres" });
            var hits = JArray.Parse(JObject.Parse(json)["objects"].ToString());
            Assert.Contains(hits, h => h["name"].ToString() == "PSPContParecer");
        }

        [Fact]
        public void PathPrefix_ListsFolderChildren()
        {
            var fixture = TestFixtures.IndexWithFolders();
            // fixture has objects under Root Module/ClickSign/
            var svc = new ListService(fixture.Index);
            var json = svc.List(new ListCriteria { PathPrefix = "Root Module/ClickSign/" });
            var hits = JArray.Parse(JObject.Parse(json)["objects"].ToString());
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.StartsWith("Root Module/ClickSign/", h["parentFolderPath"].ToString()));
        }
    }
}
```

- [ ] **Step 2: Run test, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "ListDiscoveryTests"`
Expected: FAIL — properties / criteria fields missing.

- [ ] **Step 3: Extend `SearchIndexEntry` with `ParentFolderPath`**

In `src/GxMcp.Worker/Models/SearchIndex.cs`:

```csharp
public class SearchIndexEntry
{
    // ... existing fields
    public string ParentFolderPath { get; set; }
}
```

Populate during indexing — find where `SearchIndexEntry` is constructed in `IndexCacheService` (`BuildEntry` or equivalent), add:

```csharp
entry.ParentFolderPath = ResolveFolderPath(obj); // walks obj.Parent up to root
```

Add helper:

```csharp
private string ResolveFolderPath(KBObject obj)
{
    var parts = new List<string>();
    var cur = obj.Parent;
    while (cur != null) {
        parts.Insert(0, cur.Name);
        cur = cur.Parent;
    }
    return string.Join("/", parts);
}
```

- [ ] **Step 4: Add criteria + filtering to `ListService`**

```csharp
public class ListCriteria
{
    public string NameFilter { get; set; }
    public string DescriptionFilter { get; set; }
    public string PathPrefix { get; set; }
    public string Filter { get; set; }       // legacy — matches both
    public string TypeFilter { get; set; }
    public int Limit { get; set; } = 200;
    public int Offset { get; set; } = 0;
}

public string List(ListCriteria c)
{
    var idx = _index.GetIndex();
    IEnumerable<SearchIndexEntry> q = idx.Objects.Values;
    if (!string.IsNullOrEmpty(c.NameFilter))
        q = q.Where(e => e.Name != null && e.Name.IndexOf(c.NameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
    if (!string.IsNullOrEmpty(c.DescriptionFilter))
        q = q.Where(e => e.Description != null && e.Description.IndexOf(c.DescriptionFilter, StringComparison.OrdinalIgnoreCase) >= 0);
    if (!string.IsNullOrEmpty(c.PathPrefix))
        q = q.Where(e => e.ParentFolderPath != null && e.ParentFolderPath.StartsWith(c.PathPrefix, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrEmpty(c.Filter))
        q = q.Where(e =>
            (e.Name?.IndexOf(c.Filter, StringComparison.OrdinalIgnoreCase) >= 0) ||
            (e.Description?.IndexOf(c.Filter, StringComparison.OrdinalIgnoreCase) >= 0));
    if (!string.IsNullOrEmpty(c.TypeFilter))
        q = q.Where(e => string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase));
    // ... existing pagination
}
```

- [ ] **Step 5: Update tool schema**

In `src/GxMcp.Gateway/tool_definitions.json`, find `genexus_list_objects.inputSchema.properties` and add:

```json
"nameFilter": { "type": "string", "description": "Substring match on object name only" },
"descriptionFilter": { "type": "string", "description": "Substring match on description only" },
"pathPrefix": { "type": "string", "description": "Folder path prefix, e.g. 'Root Module/ClickSign/'" }
```

Update description of `filter` to say "matches name OR description (legacy; prefer nameFilter/descriptionFilter)".

- [ ] **Step 6: Run tests**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "ListDiscoveryTests"`
Expected: PASS (3 tests).

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "ToolSchemaSizeTests"`
Expected: PASS (may need budget bump in Task 8.2).

- [ ] **Step 7: Commit**

```bash
git commit -am "feat(list): nameFilter + descriptionFilter + pathPrefix (#2, #3)"
```

---

## Phase 3 — Edit / write reliability (#4, #11, #13)

### Task 3.1 — EOL-normalized matching in `WriteService`

**Files:**
- Modify: `src/GxMcp.Worker/Helpers/XmlEquivalence.cs` (add `NormalizeForCompare` if missing) OR new `Helpers/EolNormalizer.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (Replace operation)
- Test: `src/GxMcp.Worker.Tests/WriteServiceEolNormalizationTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/WriteServiceEolNormalizationTests.cs
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class WriteServiceEolNormalizationTests
    {
        const string CRLF = "\r\n";
        const string LF = "\n";

        [Fact]
        public void Replace_MultilineContext_MixedCrlf_Matches()
        {
            var source = "line1" + CRLF + "    Where IdAgenda = &IdAgenda" + CRLF +
                         "    Where ParecerFinal <> 3" + CRLF + CRLF +
                         "    &ListaIdsPareceres.Add(IdParecer)" + CRLF + "end";
            var ctxLF =
                "    Where IdAgenda = &IdAgenda" + LF +
                "    Where ParecerFinal <> 3" + LF + LF +
                "    &ListaIdsPareceres.Add(IdParecer)";
            var result = WriteService.TryMatch(source, ctxLF, out var startIdx, out var endIdx);
            Assert.True(result);
            Assert.True(startIdx >= 0);
        }

        [Fact]
        public void Replace_MultilineContext_TrailingWhitespace_Matches()
        {
            var source = "    Where ParecerFinal <> 3   \r\n    next line";
            var ctx = "    Where ParecerFinal <> 3\n    next line";
            var result = WriteService.TryMatch(source, ctx, out _, out _);
            Assert.True(result);
        }
    }
}
```

Note: `WriteService.TryMatch` is a new helper (or internal) returning the start/end indices in the original source of the matched window. Expose via `[InternalsVisibleTo("GxMcp.Worker.Tests")]`.

- [ ] **Step 2: Run test, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "WriteServiceEolNormalizationTests"`
Expected: FAIL — method missing or returns false.

- [ ] **Step 3: Implement normalized matching**

Add to `WriteService.cs`:

```csharp
internal static string NormalizeForCompare(string s)
{
    if (s == null) return null;
    // Unify CRLF → LF, strip per-line trailing whitespace
    var lines = s.Replace("\r\n", "\n").Split('\n');
    for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
    return string.Join("\n", lines);
}

internal static bool TryMatch(string source, string context, out int startIdx, out int endIdx)
{
    startIdx = endIdx = -1;
    var normSource = NormalizeForCompare(source);
    var normCtx = NormalizeForCompare(context);
    var normIdx = normSource.IndexOf(normCtx, StringComparison.Ordinal);
    if (normIdx < 0) return false;

    // Map normalized index back to original source via line counting
    int sourceLine = 0, normLine = 0;
    int origPos = 0;
    while (normLine < CountLinesBefore(normSource, normIdx) && origPos < source.Length)
    {
        var nl = source.IndexOfAny(new[] { '\r', '\n' }, origPos);
        if (nl < 0) break;
        origPos = nl + (source[nl] == '\r' && nl + 1 < source.Length && source[nl+1] == '\n' ? 2 : 1);
        normLine++;
    }
    startIdx = origPos;
    // Walk end
    int ctxLines = CountLinesBefore(normCtx, normCtx.Length);
    endIdx = startIdx;
    for (int i = 0; i <= ctxLines && endIdx < source.Length; i++)
    {
        var nl = source.IndexOfAny(new[] { '\r', '\n' }, endIdx);
        if (nl < 0) { endIdx = source.Length; break; }
        endIdx = nl + (source[nl] == '\r' && nl + 1 < source.Length && source[nl+1] == '\n' ? 2 : 1);
    }
    return true;
}

private static int CountLinesBefore(string s, int idx)
{
    int c = 0; for (int i = 0; i < idx; i++) if (s[i] == '\n') c++; return c;
}
```

Wire `Replace` operation in WriteService to use `TryMatch` before falling back to exact `IndexOf`.

- [ ] **Step 4: Run tests**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "WriteServiceEolNormalizationTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(edit): EOL/whitespace normalized matching (#4)"
```

### Task 3.2 — Byte-level `nearMatchHint`

**Files:**
- Modify: `src/GxMcp.Worker/Helpers/DiffBuilder.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (Replace path on no-match)
- Test: `src/GxMcp.Worker.Tests/WriteServiceNearMatchHintTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/WriteServiceNearMatchHintTests.cs
using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class WriteServiceNearMatchHintTests
    {
        [Fact]
        public void NearMatch_OneCharDifferent_ReturnsContentDivergence()
        {
            var source = "    Where IdAgenda = &IdAgenda\n    Where ParecerFinal = 3\n";
            var context = "    Where IdAgenda = &IdAgenda\n    Where ParecerFinal <> 3\n";
            var hint = DiffBuilder.ByteLevelDivergence(source, context);
            Assert.True(hint["similarity"].Value<double>() >= 0.80);
            Assert.Equal("Content", hint["divergenceKind"].ToString());
            Assert.Equal(2, hint["firstDivergenceAt"]["line"].Value<int>());
        }

        [Fact]
        public void NearMatch_OnlyEolDiffers_ReturnsEolKind()
        {
            var source = "foo\r\nbar\r\n";
            var context = "foo\nbar\n";
            var hint = DiffBuilder.ByteLevelDivergence(source, context);
            Assert.Equal("EOL", hint["divergenceKind"].ToString());
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "WriteServiceNearMatchHintTests"`
Expected: FAIL — `ByteLevelDivergence` missing.

- [ ] **Step 3: Implement `ByteLevelDivergence`**

In `Helpers/DiffBuilder.cs`:

```csharp
public static JObject ByteLevelDivergence(string sourceWindow, string context)
{
    var normSource = WriteService.NormalizeForCompare(sourceWindow);
    var normCtx = WriteService.NormalizeForCompare(context);

    var sim = ComputeSimilarity(normSource, normCtx);
    var divKind = ClassifyDivergence(sourceWindow, context, normSource, normCtx);
    int firstLine = 1, firstCol = 1;
    for (int i = 0; i < Math.Min(normSource.Length, normCtx.Length); i++)
    {
        if (normSource[i] != normCtx[i]) {
            // count line/col in source up to i
            firstLine = normSource.Substring(0, i).Count(ch => ch == '\n') + 1;
            int lastNL = normSource.LastIndexOf('\n', i - 1);
            firstCol = i - (lastNL < 0 ? -1 : lastNL);
            break;
        }
    }
    return new JObject {
        ["similarity"] = sim,
        ["topWindow"] = new JObject {
            ["contextNormalized"] = normCtx,
            ["sourceWindowNormalized"] = normSource,
            ["firstDivergenceAt"] = new JObject { ["line"] = firstLine, ["column"] = firstCol },
            ["divergenceKind"] = divKind
        }
    };
}

private static string ClassifyDivergence(string src, string ctx, string normSrc, string normCtx)
{
    if (normSrc == normCtx) {
        if (src != ctx && src.Replace("\r\n", "\n") != ctx.Replace("\r\n", "\n"))
            return "EOL";
        return "Whitespace";
    }
    if (normSrc.Replace(" ", "").Replace("\t", "") == normCtx.Replace(" ", "").Replace("\t", ""))
        return "Whitespace";
    return "Content";
}

private static double ComputeSimilarity(string a, string b)
{
    if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
    var dist = LevenshteinDistance(a, b);
    return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
}
// LevenshteinDistance — if not already present in DiffBuilder, add a standard O(n*m) DP implementation.
```

Wire from `WriteService` Replace path: when no exact/normalized match, find best window via existing similarity scan, and when similarity ≥ 0.80 include `nearMatchHint = DiffBuilder.ByteLevelDivergence(bestWindow, context)` in the error envelope.

- [ ] **Step 4: Run tests**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "WriteServiceNearMatchHintTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(edit): byte-level divergence hint on near-match failures (#4)"
```

### Task 3.3 — `{find, replace}` patch shape works for multi-line; remove dead RFC6902 path

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (patch dispatch)
- Modify: `src/GxMcp.Worker/Services/PatchService.cs` (or wherever patch shapes are parsed)
- Modify: `src/GxMcp.Gateway/tool_definitions.json` (remove RFC6902 from `edit.patch` description)
- Test: `src/GxMcp.Worker.Tests/PatchShapesTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/PatchShapesTests.cs
using Xunit;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    public class PatchShapesTests
    {
        [Fact]
        public void FindReplace_Multiline_Crlf_Succeeds()
        {
            var src = "foo\r\nWhere x = 1\r\nWhere y = 2\r\nbar\r\n";
            var patch = new JObject {
                ["find"] = "Where x = 1\nWhere y = 2",
                ["replace"] = "Where x = 1 and y = 2"
            };
            var (ok, result) = PatchService.Apply(src, patch);
            Assert.True(ok);
            Assert.Contains("Where x = 1 and y = 2", result);
            Assert.DoesNotContain("Where x = 1\r\nWhere y = 2", result);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "PatchShapesTests"`
Expected: FAIL — likely NoMatch or method missing.

- [ ] **Step 3: Implement multi-line aware `{find, replace}`**

In `PatchService.Apply`, when patch is `{find, replace}` JSON:

```csharp
public static (bool ok, string result) Apply(string source, JObject patch)
{
    var find = patch["find"]?.ToString();
    var replace = patch["replace"]?.ToString();
    if (find == null) return (false, source);

    if (WriteService.TryMatch(source, find, out var s, out var e))
    {
        return (true, source.Substring(0, s) + replace + source.Substring(e));
    }
    return (false, source);
}
```

Remove RFC6902 array branch entirely (search for `JsonPatch` usages in PatchService — likely unused already given report says it doesn't work).

In `tool_definitions.json`, update `genexus_edit.inputSchema.properties.patch.description` to:

> `Legacy string replacement OR {find, replace} JSON object. Prefer separate operation/context/content params for new code.`

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "PatchShapesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(edit): {find,replace} patch shape works with multi-line + CRLF; drop dead RFC6902 (#11)"
```

### Task 3.4 — `persistedHash` + `persistedSnippet` on every response

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (response envelope)
- Test: `src/GxMcp.Worker.Tests/EditPostStateTests.cs` (extend existing)

- [ ] **Step 1: Write failing test**

Append to `EditPostStateTests.cs`:

```csharp
[Fact]
public void Edit_AnyOutcome_ResponseIncludesPersistedHashAndSnippet()
{
    var (ok_response) = SimulateEditSuccess();
    Assert.NotNull(ok_response["persistedHash"]);
    Assert.StartsWith("sha256:", ok_response["persistedHash"].ToString());
    Assert.NotNull(ok_response["persistedSnippet"]);

    var rollback_response = SimulateEditRollback();
    Assert.NotNull(rollback_response["persistedHash"]);
    Assert.NotNull(rollback_response["persistedSnippet"]);
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "Edit_AnyOutcome_ResponseIncludesPersistedHashAndSnippet"`
Expected: FAIL.

- [ ] **Step 3: Wire hash + snippet into every response path**

In `WriteService` response construction (success and rollback branches):

```csharp
private static string ComputeSha256(string content)
{
    using (var sha = System.Security.Cryptography.SHA256.Create()) {
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? ""));
        return "sha256:" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}

private JObject AppendPersistedState(JObject response, string finalSource, int? editLine)
{
    response["persistedHash"] = ComputeSha256(finalSource);
    response["persistedSnippet"] = ExtractSnippet(finalSource, editLine ?? 0, contextLines: 10);
    return response;
}
```

Call `AppendPersistedState` on every return path in `WriteObject`, `Replace`, `BulkWrite` items, `AddVariable`, `DeleteVariable`.

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "Edit_AnyOutcome_ResponseIncludesPersistedHashAndSnippet"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(edit): persistedHash + persistedSnippet on every response (#13)"
```

---

## Phase 4 — Variable lifecycle (#5, #6, #12)

### Task 4.1 — `VariableTypeResolver` synonym map

**Files:**
- Create: `src/GxMcp.Worker/Helpers/VariableTypeResolver.cs`
- Test: `src/GxMcp.Worker.Tests/VariableTypeResolverTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// src/GxMcp.Worker.Tests/VariableTypeResolverTests.cs
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class VariableTypeResolverTests
    {
        [Theory]
        [InlineData("Character", "Character", null, null)]
        [InlineData("VarChar(120)", "Character", 120, null)]
        [InlineData("String(50)", "Character", 50, null)]
        [InlineData("Int", "Numeric", null, null)]
        [InlineData("Numeric(10,2)", "Numeric", 10, 2)]
        [InlineData("Bool", "Boolean", null, null)]
        [InlineData("DateTime", "DateTime", null, null)]
        public void Resolve_KnownSynonyms_ReturnsCanonical(string input, string expType, int? expLen, int? expDec)
        {
            var r = VariableTypeResolver.Resolve(input);
            Assert.True(r.Recognized);
            Assert.Equal(expType, r.CanonicalType);
            Assert.Equal(expLen, r.Length);
            Assert.Equal(expDec, r.Decimals);
        }

        [Fact]
        public void Resolve_Unknown_ReturnsNotRecognizedWithSuggestion()
        {
            var r = VariableTypeResolver.Resolve("Bogus(99)");
            Assert.False(r.Recognized);
            Assert.NotNull(r.Suggestion);
            Assert.NotEmpty(r.AcceptedList);
        }

        [Fact]
        public void Resolve_DomainReference_PassesThrough()
        {
            var r = VariableTypeResolver.Resolve("&PesCod");
            Assert.True(r.Recognized);
            Assert.Equal("DomainReference", r.CanonicalType);
            Assert.Equal("PesCod", r.DomainName);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "VariableTypeResolverTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `VariableTypeResolver`**

```csharp
// src/GxMcp.Worker/Helpers/VariableTypeResolver.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public class TypeResolution
    {
        public bool Recognized { get; set; }
        public string CanonicalType { get; set; }
        public int? Length { get; set; }
        public int? Decimals { get; set; }
        public string DomainName { get; set; }
        public string Suggestion { get; set; }
        public List<string> AcceptedList { get; set; }
    }

    public static class VariableTypeResolver
    {
        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Character", "Character" }, { "Char", "Character" }, { "String", "Character" }, { "VarChar", "Character" },
            { "Numeric", "Numeric" }, { "Number", "Numeric" }, { "Decimal", "Numeric" }, { "Int", "Numeric" }, { "Integer", "Numeric" },
            { "Boolean", "Boolean" }, { "Bool", "Boolean" },
            { "Date", "Date" },
            { "DateTime", "DateTime" }, { "Timestamp", "DateTime" },
            { "Time", "Time" },
            { "LongVarChar", "LongVarChar" }, { "Text", "LongVarChar" },
            { "Blob", "Blob" }, { "Binary", "Blob" },
            { "Image", "Image" },
            { "GUID", "GUID" }, { "Uuid", "GUID" }
        };

        public static TypeResolution Resolve(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new TypeResolution { Recognized = false, Suggestion = "Character(40)", AcceptedList = GetAccepted() };

            input = input.Trim();
            if (input.StartsWith("&"))
                return new TypeResolution { Recognized = true, CanonicalType = "DomainReference", DomainName = input.Substring(1) };

            var m = Regex.Match(input, @"^([A-Za-z]+)(?:\((\d+)(?:,(\d+))?\))?$");
            if (!m.Success)
                return new TypeResolution { Recognized = false, Suggestion = SuggestClosest(input), AcceptedList = GetAccepted() };

            var typeWord = m.Groups[1].Value;
            if (!Synonyms.TryGetValue(typeWord, out var canonical))
                return new TypeResolution { Recognized = false, Suggestion = SuggestClosest(typeWord), AcceptedList = GetAccepted() };

            int? len = m.Groups[2].Success ? (int?)int.Parse(m.Groups[2].Value) : null;
            int? dec = m.Groups[3].Success ? (int?)int.Parse(m.Groups[3].Value) : null;
            return new TypeResolution { Recognized = true, CanonicalType = canonical, Length = len, Decimals = dec };
        }

        private static List<string> GetAccepted()
        {
            return new List<string>(Synonyms.Values);
        }

        private static string SuggestClosest(string input)
        {
            // Simple: find synonym key with smallest Levenshtein
            string best = null; int bestDist = int.MaxValue;
            foreach (var k in Synonyms.Keys)
            {
                int d = Levenshtein(input.ToLowerInvariant(), k.ToLowerInvariant());
                if (d < bestDist) { bestDist = d; best = k; }
            }
            return best;
        }

        private static int Levenshtein(string a, string b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    dp[i, j] = Math.Min(Math.Min(dp[i-1, j] + 1, dp[i, j-1] + 1), dp[i-1, j-1] + (a[i-1] == b[j-1] ? 0 : 1));
            return dp[a.Length, b.Length];
        }
    }
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "VariableTypeResolverTests"`
Expected: PASS (8 tests via theory).

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(worker): VariableTypeResolver with synonym map + suggestion (#5)"
```

### Task 4.2 — Wire resolver into `genexus_add_variable`

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (AddVariable method)
- Test: `src/GxMcp.Worker.Tests/PartAccessorAndWriteServiceTests.cs` (extend)

- [ ] **Step 1: Write failing test**

Append to existing test file:

```csharp
[Fact]
public void AddVariable_UnknownType_ReturnsErrorWithSuggestion()
{
    var svc = TestFixtures.WriteServiceForKb();
    var json = svc.AddVariable(objectName: "TestProc", varName: "X", typeName: "VarChar(120)" /* not canonical */, dryRun: true);
    var obj = JObject.Parse(json);
    Assert.Equal("Error", obj["status"]?.ToString());
    Assert.Equal("UnknownType", obj["code"]?.ToString());
    Assert.NotNull(obj["suggestion"]);
}

[Fact]
public void AddVariable_KnownSynonym_AcceptsAndCreatesAsCanonical()
{
    var svc = TestFixtures.WriteServiceForKb();
    var json = svc.AddVariable("TestProc", "Y", "Character(120)", dryRun: false);
    var obj = JObject.Parse(json);
    Assert.Equal("Success", obj["status"]?.ToString());
    // Re-read and confirm canonical type
    var listed = svc.ListVariables("TestProc");
    Assert.Contains("Character(120)", listed);
}
```

Wait — clarification: report says `"VarChar(120)"` silently became NUMERIC, so we want `VarChar` synonyms to *succeed* via the resolver (mapping to `Character(120)`). The test "UnknownType" example should use a truly unknown like `Bogus(99)`:

```csharp
[Fact]
public void AddVariable_UnknownType_ReturnsErrorWithSuggestion()
{
    var json = svc.AddVariable("TestProc", "X", "Bogus(99)", dryRun: true);
    var obj = JObject.Parse(json);
    Assert.Equal("Error", obj["status"]?.ToString());
    Assert.Equal("UnknownType", obj["code"]?.ToString());
}

[Fact]
public void AddVariable_VarCharSynonym_CreatesAsCharacter()
{
    var json = svc.AddVariable("TestProc", "Y", "VarChar(120)", dryRun: false);
    var obj = JObject.Parse(json);
    Assert.Equal("Success", obj["status"]?.ToString());
    // No more silent NUMERIC creation
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "AddVariable_"`
Expected: FAIL — current behavior silently succeeds with NUMERIC.

- [ ] **Step 3: Resolver gate in `WriteService.AddVariable`**

```csharp
public string AddVariable(string objectName, string varName, string typeName, bool dryRun = false)
{
    var resolution = VariableTypeResolver.Resolve(typeName);
    if (!resolution.Recognized)
    {
        return new JObject {
            ["status"] = "Error",
            ["code"] = "UnknownType",
            ["message"] = $"Unknown typeName '{typeName}'. Did you mean '{resolution.Suggestion}'?",
            ["suggestion"] = resolution.Suggestion,
            ["accepted"] = new JArray(resolution.AcceptedList)
        }.ToString();
    }
    // Use resolution.CanonicalType + resolution.Length + resolution.Decimals to construct the SDK variable
    // ... existing add logic with canonical values ...
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "AddVariable_"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(add_variable): validate typeName, never default to NUMERIC silently (#5)"
```

### Task 4.3 — `genexus_modify_variable` tool

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (new `ModifyVariable` method)
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs` (dispatch new command)
- Modify: `src/GxMcp.Gateway/tool_definitions.json` (new tool def)
- Modify: `src/GxMcp.Gateway/McpRouter.cs` (route `genexus_modify_variable`)
- Modify: `src/GxMcp.Gateway/Routers/OperationsRouter.cs` (handle)
- Test: `src/GxMcp.Worker.Tests/ModifyVariableTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/ModifyVariableTests.cs
using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class ModifyVariableTests
    {
        [Fact]
        public void ModifyVariable_ChangesType_PreservesName()
        {
            var svc = TestFixtures.WriteServiceWithVariable("Obj1", "X", "Numeric(4)");
            var json = svc.ModifyVariable("Obj1", "X", "Character(200)");
            var obj = JObject.Parse(json);
            Assert.Equal("Success", obj["status"]?.ToString());
            var listed = svc.ListVariables("Obj1");
            Assert.Contains("Character(200)", listed);
            Assert.Contains("X", listed);
        }

        [Fact]
        public void ModifyVariable_UnknownType_ReturnsUnknownTypeError()
        {
            var svc = TestFixtures.WriteServiceWithVariable("Obj1", "X", "Numeric(4)");
            var json = svc.ModifyVariable("Obj1", "X", "Bogus");
            Assert.Contains("UnknownType", json);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "ModifyVariableTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ModifyVariable` atomically (delete + add + rebind)**

```csharp
public string ModifyVariable(string objectName, string varName, string newTypeName, string basedOn = null)
{
    var resolution = VariableTypeResolver.Resolve(newTypeName);
    if (!resolution.Recognized) return UnknownTypeError(newTypeName, resolution);

    var obj = _objectService.FindObject(objectName);
    if (obj == null) return ObjectNotFound(objectName);

    // Capture current value/properties of the var before delete
    var existing = CaptureVariable(obj, varName);
    if (existing == null) return VariableNotFound(varName);

    // Atomic: try delete + add, rollback on failure
    var savedXml = SerializeVariablesPart(obj);
    try {
        DeleteVariableInternal(obj, varName);
        AddVariableInternal(obj, varName, resolution, basedOn, existing.Description);
        obj.Save();
        return AppendPersistedState(new JObject { ["status"] = "Success" }, ReadVariablesPart(obj), null).ToString();
    } catch (Exception ex) {
        RestoreVariablesPart(obj, savedXml);
        return new JObject { ["status"] = "Error", ["message"] = ex.Message }.ToString();
    }
}
```

- [ ] **Step 4: Add tool schema + routing**

`tool_definitions.json` — add `genexus_modify_variable`:

```json
"genexus_modify_variable": {
  "name": "genexus_modify_variable",
  "description": "Change the type of an existing variable, preserving its name and (where possible) bindings.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "name": { "type": "string", "description": "Object name" },
      "varName": { "type": "string" },
      "typeName": { "type": "string" },
      "basedOn": { "type": "string", "description": "Domain name (optional)" }
    },
    "required": ["name", "varName", "typeName"]
  }
}
```

`McpRouter.cs` — wire to `OperationsRouter.ModifyVariable`. `OperationsRouter` calls the Worker via `CommandDispatcher` with command `"modify_variable"`. `CommandDispatcher.HandleCommand` adds case `"modify_variable"`.

- [ ] **Step 5: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "ModifyVariableTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat: genexus_modify_variable (atomic type change) (#6)"
```

### Task 4.4 — Symmetric `delete_variable` (WebPanel, Transaction, etc.)

**Files:**
- Modify: `src/GxMcp.Worker/Structure/PartAccessor.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (DeleteVariable)
- Test: `src/GxMcp.Worker.Tests/DeleteVariableSymmetryTests.cs`

- [ ] **Step 1: Write failing parametrized test**

```csharp
// src/GxMcp.Worker.Tests/DeleteVariableSymmetryTests.cs
using Xunit;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    public class DeleteVariableSymmetryTests
    {
        [Theory]
        [InlineData("Procedure")]
        [InlineData("WebPanel")]
        [InlineData("Transaction")]
        [InlineData("WorkPanel")]
        [InlineData("DataProvider")]
        public void DeleteVariable_AcrossObjectKinds_Succeeds(string objKind)
        {
            var svc = TestFixtures.WriteServiceForKind(objKind, withVariable: "X", type: "Character(10)");
            var json = svc.DeleteVariable("TestObj", "X");
            var obj = JObject.Parse(json);
            Assert.Equal("Success", obj["status"]?.ToString());
            Assert.DoesNotContain("Part 'DeleteVariable' not found", json);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "DeleteVariableSymmetryTests"`
Expected: FAIL for WebPanel (and possibly others).

- [ ] **Step 3: Replace part-name dispatch with kind-aware accessor**

In `PartAccessor.cs`, add:

```csharp
public static VariablesPart GetVariablesPart(KBObject obj)
{
    // Tries multiple known part type-names per object kind
    var candidates = new[] {
        "Variables",            // Procedure, DataProvider
        "ProcedureVariables",
        "WebFormVariables",     // WebPanel
        "TransactionVariables", // Transaction
        "WorkPanelVariables",
        "Rules"                  // some kinds nest vars under Rules
    };
    foreach (var name in candidates)
    {
        var part = obj.Parts?.FirstOrDefault(p => string.Equals(p.GetType().Name, name, StringComparison.OrdinalIgnoreCase) || p.Name == name);
        if (part != null) return part as VariablesPart;
    }
    // Last resort — reflect over Parts and find any IVariablesContainer-like
    var fallback = obj.Parts?.FirstOrDefault(p => p.GetType().GetProperty("Variables") != null);
    return fallback as VariablesPart;
}
```

(Adjust `VariablesPart` and casts to match the actual GeneXus SDK types in the codebase. Search `Artech.*Variables` in existing code to find the right interface.)

In `WriteService.DeleteVariable`, replace the existing `obj.Parts["DeleteVariable"]` access with `PartAccessor.GetVariablesPart(obj)`. Inside the resolved part, locate the named variable and remove it.

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "DeleteVariableSymmetryTests"`
Expected: PASS (5 theory cases).

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(delete_variable): symmetric across all object kinds (#12)"
```

### Task 4.5 — Ghost-binding diagnostics on delete/modify rejection

**Files:**
- Modify: `src/GxMcp.Worker/Helpers/WebFormSchemaHints.cs` (ResolveVarBindings)
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (DeleteVariable rejection path)
- Test: `src/GxMcp.Worker.Tests/VarBindingResolverTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/VarBindingResolverTests.cs
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class VarBindingResolverTests
    {
        [Fact]
        public void ResolveVarBindings_KnownId_SubstitutesName()
        {
            var obj = TestFixtures.WebPanelWithVariable("PareceresStatusLabel", internalId: 64);
            var msg = "Invalid control reference: '[var:64]'";
            var resolved = WebFormSchemaHints.ResolveVarBindings(msg, obj);
            Assert.Contains("&PareceresStatusLabel", resolved);
        }

        [Fact]
        public void ResolveVarBindings_UnknownId_AppendsUnresolvedMarker()
        {
            var obj = TestFixtures.EmptyWebPanel();
            var msg = "Invalid control reference: '[var:999]'";
            var resolved = WebFormSchemaHints.ResolveVarBindings(msg, obj);
            Assert.Contains("[var:999 (unresolved)]", resolved);
        }

        [Fact]
        public void DeleteVariable_BoundToControl_ReturnsBoundToControlsError()
        {
            var svc = TestFixtures.WriteServiceWithBoundVariable("X");
            var json = svc.DeleteVariable("Panel", "X");
            var obj = JObject.Parse(json);
            Assert.Equal("BoundToControls", obj["code"]?.ToString());
            Assert.NotNull(obj["bindings"]);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "VarBindingResolverTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ResolveVarBindings`**

In `Helpers/WebFormSchemaHints.cs`:

```csharp
public static string ResolveVarBindings(string message, KBObject obj)
{
    if (string.IsNullOrEmpty(message) || obj == null) return message;
    return Regex.Replace(message, @"\[var:(\d+)\]", m => {
        var id = int.Parse(m.Groups[1].Value);
        var name = LookupVarNameById(obj, id);
        return name != null ? "&" + name : $"[var:{id} (unresolved)]";
    });
}

private static string LookupVarNameById(KBObject obj, int id)
{
    var part = PartAccessor.GetVariablesPart(obj);
    if (part == null) return null;
    // Iterate vars, return one whose internal id equals `id`
    foreach (var v in part.Variables) {
        if ((int?)v.GetType().GetProperty("Id")?.GetValue(v) == id)
            return v.Name;
    }
    return null;
}
```

- [ ] **Step 4: Hook into `DeleteVariable` rejection path**

When the SDK throws or refuses with a binding-related error, catch and build:

```csharp
return new JObject {
    ["status"] = "Error",
    ["code"] = "BoundToControls",
    "message" = "Variable bound to controls; cannot delete",
    "bindings" = BuildBindingList(obj, varName)
}.ToString();
```

`BuildBindingList` scans the WebForm XML for `[var:N]` references and returns a JArray of `{ location, controlId, controlName, line? }`.

- [ ] **Step 5: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "VarBindingResolverTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git commit -am "feat: ghost-binding diagnostics + [var:N] resolver (#6, #15 prereq)"
```

### Task 4.6 — Patch-window-only rollback verification

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs` (verification + rollback)
- Modify: `src/GxMcp.Worker/Helpers/XmlEquivalence.cs` (classify in-window vs out-of-window)
- Test: `src/GxMcp.Worker.Tests/PatchWindowRollbackTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/PatchWindowRollbackTests.cs
using Xunit;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    public class PatchWindowRollbackTests
    {
        [Fact]
        public void Edit_SdkNormalizesUntouchedLine_NoRollback_ReportsSideEffect()
        {
            // Fixture: source has `DATETIME(10,5)` on line 30; we edit line 10.
            // SDK normalizes line 30 to `DATETIME(8,5)` on save.
            var svc = TestFixtures.WriteServiceWithSdkNormalization();
            var json = svc.Replace(/* edit at line 10 */);
            var obj = JObject.Parse(json);
            Assert.Equal("Success", obj["status"]?.ToString());
            Assert.NotNull(obj["_meta"]?["sideEffectNormalizations"]);
            var notes = (JArray)obj["_meta"]["sideEffectNormalizations"];
            Assert.Contains(notes, n => n["line"].Value<int>() == 30);
        }

        [Fact]
        public void Edit_InWindowDivergence_RollsBack()
        {
            var svc = TestFixtures.WriteServiceWithSdkSanitizingEditedLine();
            var json = svc.Replace(/* edit at line 10 that SDK rejects */);
            var obj = JObject.Parse(json);
            Assert.Equal("RolledBack", obj["status"]?.ToString());
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Expected: FAIL.

- [ ] **Step 3: Implement window-classification logic**

In `WriteService.Replace`, after `obj.Save()` and re-read:

```csharp
var diffHunks = XmlEquivalence.HunkDiff(beforeSave, afterSave);
var (inWindow, outOfWindow) = diffHunks.Partition(h => OverlapsWindow(h, editLineStart, editLineEnd));
if (inWindow.Any(h => !IsExpected(h, requestedContent)))
{
    Rollback();
    return new JObject { ["status"] = "RolledBack", /* ... */ }.ToString();
}
// out-of-window normalizations: success + report
return new JObject {
    ["status"] = "Success",
    ["_meta"] = new JObject { ["sideEffectNormalizations"] = SerializeHunks(outOfWindow) }
}.ToString();
```

`XmlEquivalence.HunkDiff` returns a list of `{ Line, Before, After }`; if helper doesn't exist, add it (line-by-line diff using existing similarity util).

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "PatchWindowRollbackTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(edit): rollback only on in-window divergence; report SDK normalizations (#13, #6)"
```

---

## Phase 5 — Build pipeline / segmented refs (#7)

### Task 5.1 — `BuildService` auto-includes callee `<Reference>` in csproj

**Files:**
- Modify: `src/GxMcp.Worker/Services/BuildService.cs` (csproj generation)
- Test: `src/GxMcp.Worker.Tests/BuildSegmentedCsprojTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Worker.Tests/BuildSegmentedCsprojTests.cs
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class BuildSegmentedCsprojTests
    {
        [Fact]
        public void Generate_WebPanelWithThreeProcedureCalls_IncludesAllReferences()
        {
            var graph = TestFixtures.CallerGraphForWebPanel(
                webPanel: "PanelA",
                callees: new[] { "ProcB", "ProcC", "ProcD" }
            );
            var builder = new BuildService(graph, TestFixtures.FakeKbContext());
            var csproj = builder.GenerateSegmentedCsproj(
                target: new[] { "PanelA" },
                includeCallees: "transitive"
            );
            Assert.Contains("<Reference Include=\"ProcB\"", csproj);
            Assert.Contains("<Reference Include=\"ProcC\"", csproj);
            Assert.Contains("<Reference Include=\"ProcD\"", csproj);
        }

        [Fact]
        public void Generate_IncludeCalleesNone_OmitsReferences()
        {
            var graph = TestFixtures.CallerGraphForWebPanel("PanelA", new[] { "ProcB" });
            var builder = new BuildService(graph, TestFixtures.FakeKbContext());
            var csproj = builder.GenerateSegmentedCsproj(new[] { "PanelA" }, includeCallees: "none");
            Assert.DoesNotContain("<Reference Include=\"ProcB\"", csproj);
        }

        [Fact]
        public void Generate_OverCapNodes_ReturnsBuildPlanTooLarge()
        {
            var graph = TestFixtures.LargeCalleeGraph(rootCallees: 250);
            var builder = new BuildService(graph, TestFixtures.FakeKbContext());
            var ex = Assert.Throws<BuildPlanTooLargeException>(
                () => builder.GenerateSegmentedCsproj(new[] { "Root" }, includeCallees: "transitive")
            );
            Assert.Equal(200, ex.NodeCap);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "BuildSegmentedCsprojTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `GenerateSegmentedCsproj`**

In `src/GxMcp.Worker/Services/BuildService.cs`:

```csharp
public class BuildPlanTooLargeException : Exception {
    public int NodeCap { get; set; }
    public int RequestedNodes { get; set; }
}

public class BuildPlanResult {
    public List<string> TargetExpanded { get; set; }
    public List<JObject> Callees { get; set; }
    public List<JObject> Skipped { get; set; }
}

public string GenerateSegmentedCsproj(IEnumerable<string> target, string includeCallees = "transitive")
{
    var targetSet = new HashSet<string>(target);
    var allCallees = new HashSet<string>();
    if (includeCallees != "none")
    {
        foreach (var t in target)
        {
            var trans = _graph.GetCalleesTransitive(t, maxNodes: 200);
            if (trans.Truncated) throw new BuildPlanTooLargeException { NodeCap = 200, RequestedNodes = trans.Nodes.Count };
            foreach (var c in trans.Nodes) allCallees.Add(c);
            if (includeCallees == "direct") break;
        }
    }
    var sb = new StringBuilder();
    sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
    sb.AppendLine("  <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>");
    sb.AppendLine("  <ItemGroup>");
    foreach (var t in targetSet)
        sb.AppendLine($"    <Compile Include=\"{t}.cs\" />");
    foreach (var callee in allCallees.Where(c => !targetSet.Contains(c)))
    {
        var dll = ResolveDllPath(callee);
        if (dll != null)
            sb.AppendLine($"    <Reference Include=\"{callee}\"><HintPath>{dll}</HintPath></Reference>");
    }
    sb.AppendLine("  </ItemGroup>");
    sb.AppendLine("</Project>");
    return sb.ToString();
}

private string ResolveDllPath(string callee)
{
    var path = Path.Combine(_kbContext.OutputDir, _kbContext.ModelName, $"{callee}.dll");
    return File.Exists(path) ? path : null;
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "BuildSegmentedCsprojTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(build): segmented csproj auto-includes callee References (#7)"
```

### Task 5.2 — Expose `includeCallees` flag + `_meta.buildPlan` in lifecycle build

**Files:**
- Modify: `src/GxMcp.Worker/Services/BuildService.cs` (Build entry point response)
- Modify: `src/GxMcp.Gateway/tool_definitions.json` (lifecycle.build params)
- Modify: `src/GxMcp.Gateway/Routers/OperationsRouter.cs` (or lifecycle router; pass flag through)

- [ ] **Step 1: Add to tool schema**

In `tool_definitions.json` under `genexus_lifecycle.inputSchema.properties`:

```json
"includeCallees": { "type": "string", "enum": ["none", "direct", "transitive"], "default": "transitive" }
```

- [ ] **Step 2: Pass through gateway**

In whichever router (likely `OperationsRouter` or `Program.cs` `genexus_lifecycle` handler), forward `includeCallees` arg to the worker `lifecycle build` command.

- [ ] **Step 3: Worker side: surface buildPlan**

In `BuildService.RunBuild`, after `GenerateSegmentedCsproj`, capture `BuildPlanResult` and include in response under `_meta.buildPlan`. When `BuildPlanTooLargeException` thrown, return:

```csharp
return new JObject {
    ["status"] = "BuildPlanTooLarge",
    ["suggested"] = "Build All from IDE",
    ["graph"] = new JObject { ["requestedNodes"] = ex.RequestedNodes, ["cap"] = ex.NodeCap }
}.ToString();
```

- [ ] **Step 4: Run all build tests + schema test**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "BuildSegmented"`
Expected: PASS.

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "ToolSchemaSizeTests"`
Expected: PASS (budget bump in 8.2 if needed).

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(lifecycle): includeCallees flag + _meta.buildPlan response (#7)"
```

---

## Phase 6 — Output size & UX (#9, #10, #14)

### Task 6.1 — Compact lifecycle status default

**Files:**
- Modify: `src/GxMcp.Worker/Services/BuildService.cs` (status response)
- Modify: `src/GxMcp.Gateway/tool_definitions.json` (`compact` param, default `true`)
- Test: `src/GxMcp.Gateway.Tests/CompactStatusTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Gateway.Tests/CompactStatusTests.cs
using Xunit;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    public class CompactStatusTests
    {
        [Fact]
        public void Status_CompactTrue_OmitsResultErrorsAndOutput()
        {
            var rawJson = TestFixtures.BuildFailureRawJson(errors: 30, warnings: 24);
            var compact = LifecycleResponseShaper.Compact(rawJson, compact: true);
            var obj = JObject.Parse(compact);
            Assert.Null(obj["result"]?["Errors"]);
            Assert.Null(obj["result"]?["Output"]);
            Assert.Equal(30, obj["errorCount"].Value<int>());
            Assert.True(((JArray)obj["errors"]).Count <= 10);
        }

        [Fact]
        public void Status_CompactTrue_DedupsRepeatedWarnings()
        {
            var raw = TestFixtures.BuildFailureWithDuplicateWarning("GAM não será reorganizado", count: 6);
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);
            var w = ((JArray)obj["warnings"])[0];
            Assert.Equal(6, w["count"].Value<int>());
        }

        [Fact]
        public void Status_CompactFalse_PreservesLegacyShape()
        {
            var raw = TestFixtures.BuildFailureRawJson(errors: 30, warnings: 24);
            var compact = LifecycleResponseShaper.Compact(raw, compact: false);
            Assert.Equal(raw, compact);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "CompactStatusTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `LifecycleResponseShaper`**

Add `src/GxMcp.Gateway/LifecycleResponseShaper.cs`:

```csharp
public static class LifecycleResponseShaper
{
    public static string Compact(string rawJson, bool compact)
    {
        if (!compact) return rawJson;
        var obj = JObject.Parse(rawJson);
        var errors = (JArray)obj["Errors"] ?? new JArray();
        var warnings = (JArray)obj["Warnings"] ?? new JArray();
        var grouped = warnings.GroupBy(w => w["message"]?.ToString())
            .Select(g => new JObject {
                ["message"] = g.Key,
                ["count"] = g.Count(),
                ["sampleLocations"] = new JArray(g.Take(3).Select(x => x["location"]))
            });
        return new JObject {
            ["status"] = obj["status"],
            ["errorCount"] = errors.Count,
            ["warningCount"] = warnings.Count,
            ["errors"] = new JArray(errors.Take(10)),
            ["warnings"] = new JArray(grouped),
            ["summary"] = $"{errors.Count} errors / {warnings.Count} warnings",
            ["truncated"] = errors.Count > 10,
            ["rawAvailableViaJobId"] = obj["jobId"]
        }.ToString();
    }
}
```

In `OperationsRouter` (lifecycle status handler), wrap response: `return LifecycleResponseShaper.Compact(workerJson, compactArg);`. Default `compactArg = true`.

- [ ] **Step 4: Update tool_definitions.json**

In `genexus_lifecycle.inputSchema.properties`, add `"compact": { "type": "boolean", "default": true }`.

- [ ] **Step 5: Run, verify pass**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "CompactStatusTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(lifecycle): compact=true default for status; warning dedup (#9)"
```

### Task 6.2 — `genexus_read` default pagination

**Files:**
- Modify: `src/GxMcp.Worker/Services/ObjectService.cs` (Read method)
- Test: `src/GxMcp.Worker.Tests/LifecycleResultPaginationTests.cs` (rename or extend)

- [ ] **Step 1: Extend existing pagination test**

In `LifecycleResultPaginationTests.cs` (or new `ReadPaginationTests.cs`):

```csharp
[Fact]
public void Read_LargePart_DefaultPaginates_NeverOverflows()
{
    var huge = TestFixtures.LargeWebPanelEvents(linesTarget: 1240, bytesTarget: 55304);
    var svc = TestFixtures.ObjectServiceFor(huge);
    var json = svc.Read("BigPanel", part: "Events");
    var obj = JObject.Parse(json);
    Assert.True(obj["truncated"].Value<bool>());
    Assert.Equal(1240, obj["totalLines"].Value<int>());
    Assert.NotNull(obj["suggestedNextOffset"]);
}

[Fact]
public void Read_LimitZero_OptsOutOfPagination()
{
    var huge = TestFixtures.LargeWebPanelEvents(1240, 55304);
    var svc = TestFixtures.ObjectServiceFor(huge);
    var json = svc.Read("BigPanel", part: "Events", limit: 0);
    var obj = JObject.Parse(json);
    Assert.Null(obj["truncated"]);
    Assert.Equal(55304, obj["content"].ToString().Length); // full content returned
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "Read_LargePart"`
Expected: FAIL.

- [ ] **Step 3: Implement default pagination**

In `ObjectService.Read`:

```csharp
public string Read(string name, string part = "Source", int offset = 0, int? limit = null)
{
    var content = ReadPartRaw(name, part);
    const int DefaultLines = 200;
    const int DefaultBytes = 16 * 1024;
    if (limit == 0) {
        return new JObject { ["content"] = content }.ToString();
    }
    var lineLimit = limit ?? DefaultLines;
    var lines = content.Split('\n');
    if (lines.Length > lineLimit || content.Length > DefaultBytes) {
        var paged = string.Join("\n", lines.Skip(offset).Take(lineLimit));
        return new JObject {
            ["content"] = paged,
            ["truncated"] = true,
            ["totalLines"] = lines.Length,
            ["totalBytes"] = content.Length,
            ["suggestedNextOffset"] = offset + lineLimit,
            ["suggestedNextLimit"] = lineLimit
        }.ToString();
    }
    return new JObject { ["content"] = content }.ToString();
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "Read_LargePart|Read_LimitZero"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(read): default pagination for large parts; limit=0 opts out (#10)"
```

### Task 6.3 — `background_jobs` dedup notification

**Files:**
- Modify: `src/GxMcp.Gateway/BackgroundJobRegistry.cs` (notified flag)
- Test: `src/GxMcp.Gateway.Tests/BackgroundJobsDedupTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// src/GxMcp.Gateway.Tests/BackgroundJobsDedupTests.cs
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class BackgroundJobsDedupTests
    {
        [Fact]
        public void Completion_Notification_AppearsOnce()
        {
            var reg = new BackgroundJobRegistry();
            var id = reg.Register("build", payload: null);
            reg.MarkCompleted(id, result: "{}", status: "Success");
            var first = reg.GetUnnotifiedCompleted();
            var second = reg.GetUnnotifiedCompleted();
            Assert.Single(first);
            Assert.Empty(second);
        }

        [Fact]
        public void Active_Jobs_AppearEveryCall()
        {
            var reg = new BackgroundJobRegistry();
            var id = reg.Register("build", payload: null);
            Assert.Single(reg.GetActive());
            Assert.Single(reg.GetActive());
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "BackgroundJobsDedupTests"`
Expected: FAIL.

- [ ] **Step 3: Add `Notified` flag + new methods**

In `BackgroundJobRegistry.cs`:

```csharp
public class JobEntry {
    public string Id; public string Kind; public string Status; public string Result;
    public bool Notified; public DateTime Started; public DateTime? Completed;
}

public List<JobEntry> GetUnnotifiedCompleted() {
    lock (_lock) {
        var result = _jobs.Values.Where(j => j.Status != "Running" && !j.Notified).ToList();
        foreach (var j in result) j.Notified = true;
        return result;
    }
}

public List<JobEntry> GetActive() {
    lock (_lock) {
        return _jobs.Values.Where(j => j.Status == "Running" || j.Status == "Pending").ToList();
    }
}
```

Wire into `_meta.background_jobs` construction: include both `GetActive()` and `GetUnnotifiedCompleted()`, merged.

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "BackgroundJobsDedupTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(jobs): completion notification appears once; active jobs still pushed (#14)"
```

---

## Phase 7 — Error/i18n consistency + cancel (#15, #16)

### Task 7.1 — `ErrorMessages` PT-BR → EN canonical translation

**Files:**
- Create: `src/GxMcp.Worker/Helpers/ErrorMessages.cs`
- Test: `src/GxMcp.Worker.Tests/ErrorMessagesTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// src/GxMcp.Worker.Tests/ErrorMessagesTests.cs
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class ErrorMessagesTests
    {
        [Theory]
        [InlineData("A validação de Web Panel 'X' falhou.", "Web Panel 'X' validation failed.")]
        [InlineData("Referência de controle inválida: '[var:64]'", "Invalid control reference: '[var:64]'")]
        [InlineData("'Vazio' não é um valor válido", "'Empty' is not a valid value")]
        [InlineData("GAM não será reorganizado", "GAM will not be reorganized")]
        public void Translate_KnownPrefix_ReturnsEn(string ptbr, string expected)
        {
            Assert.Equal(expected, ErrorMessages.Translate(ptbr));
        }

        [Fact]
        public void Translate_PreservesSourceInMeta()
        {
            var (en, src) = ErrorMessages.TranslateWithSource("A validação de Web Panel 'X' falhou.");
            Assert.Equal("A validação de Web Panel 'X' falhou.", src);
            Assert.StartsWith("Web Panel", en);
        }

        [Fact]
        public void Translate_NormalizesDoubleDot()
        {
            Assert.Equal("Validation failed.", ErrorMessages.Translate("Validation failed.."));
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Expected: FAIL.

- [ ] **Step 3: Implement `ErrorMessages`**

```csharp
// src/GxMcp.Worker/Helpers/ErrorMessages.cs
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public static class ErrorMessages
    {
        private static readonly List<(Regex Pattern, string Replacement)> Table = new List<(Regex, string)>
        {
            (new Regex(@"^A validação de Web Panel '([^']+)' falhou\.?"), "Web Panel '$1' validation failed."),
            (new Regex(@"^Referência de controle inválida"), "Invalid control reference"),
            (new Regex(@"'Vazio'"), "'Empty'"),
            (new Regex(@"não é um valor válido"), "is not a valid value"),
            (new Regex(@"não será reorganizado"), "will not be reorganized"),
            (new Regex(@"^Detailed Messages:?"), "Detailed messages:")
        };

        public static string Translate(string ptbr)
        {
            if (string.IsNullOrEmpty(ptbr)) return ptbr;
            var s = ptbr;
            foreach (var (rx, repl) in Table) s = rx.Replace(s, repl);
            // Punctuation normalization
            s = Regex.Replace(s, @"\.\.+", ".");
            s = s.TrimEnd();
            return s;
        }

        public static (string En, string Source) TranslateWithSource(string ptbr)
        {
            return (Translate(ptbr), ptbr);
        }
    }
}
```

Apply at every Worker error response construction: where the code currently embeds an SDK message into `message`, wrap with `ErrorMessages.Translate(...)` and add `_meta.sourceMessage = originalPtbr`.

Identify call sites: `Grep` for `falhou` and `Referência` in `src/GxMcp.Worker/`:

```bash
rg -l "falhou|Referência" src/GxMcp.Worker
```

Update each.

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "ErrorMessagesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(i18n): canonical EN error messages; preserve PT-BR source in _meta (#15)"
```

### Task 7.2 — `lifecycle action=cancel` propagates cancellation token

**Files:**
- Modify: `src/GxMcp.Gateway/BackgroundJobRegistry.cs` (Cancel method)
- Modify: `src/GxMcp.Gateway/WorkerProcess.cs` (cancel stdin/stdout request)
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs` (handle Cancel command)
- Modify: `src/GxMcp.Worker/Services/SourceSearchService.cs`, `AnalyzeService.cs`, `BuildService.cs` (accept CancellationToken)
- Test: `src/GxMcp.Gateway.Tests/CancelLifecycleTests.cs`

- [ ] **Step 1: Write failing integration test**

```csharp
// src/GxMcp.Gateway.Tests/CancelLifecycleTests.cs
using Xunit;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    public class CancelLifecycleTests
    {
        [Fact]
        public async Task Cancel_LongRunningSearch_ReturnsCancelledWithinTwoSeconds()
        {
            var router = TestFixtures.GatewayWithLargeFixture(); // 10k indexed entries
            var jobIdTask = Task.Run(() => router.SearchSource(new { pattern = ".*" }));
            await Task.Delay(500);
            var cancelJson = router.LifecycleCancel(jobIdTask.Result.JobId);
            var obj = JObject.Parse(cancelJson);
            Assert.Equal("Cancelled", obj["status"]?.ToString());
        }
    }
}
```

- [ ] **Step 2: Run, verify fail**

Expected: FAIL.

- [ ] **Step 3: Plumb CancellationToken**

`BackgroundJobRegistry`:

```csharp
private readonly Dictionary<string, CancellationTokenSource> _ctsMap = new Dictionary<string, CancellationTokenSource>();

public CancellationToken Register(string id, string kind, object payload)
{
    var cts = new CancellationTokenSource();
    _ctsMap[id] = cts;
    // ... existing register
    return cts.Token;
}

public bool Cancel(string jobId)
{
    if (_ctsMap.TryGetValue(jobId, out var cts)) {
        cts.Cancel();
        MarkCompleted(jobId, result: "{\"cancelled\":true}", status: "Cancelled");
        return true;
    }
    return false;
}
```

Worker side — `CommandDispatcher` accepts a `cancelJobId` param on long-running commands. `SourceSearchService.SearchAsJson` accepts a `CancellationToken ct = default` and checks `ct.IsCancellationRequested` between regex iterations:

```csharp
foreach (var e in entries) {
    if (ct.IsCancellationRequested) return new JObject { ["status"] = "Cancelled" }.ToString();
    // ...
}
```

`BuildService` checks between target invocations. `AnalyzeService` between graph nodes.

In Gateway `OperationsRouter.LifecycleCancel(jobId)` → calls `_registry.Cancel(jobId)` and sends a JSON-RPC `{"method":"cancel","params":{"jobId":...}}` to the active worker stdin. Worker `CommandDispatcher` looks up the in-flight job and signals its CTS.

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "CancelLifecycleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(lifecycle): cancel propagates CancellationToken to worker (#16)"
```

---

## Phase 8 — Integration, schema budget, release

### Task 8.1 — End-to-end "ideal workflow" smoke test

**Files:**
- Create: `src/GxMcp.Worker.Tests/IdealWorkflowSmokeTest.cs`

- [ ] **Step 1: Write the test**

Simulates the spec's 15-turn workflow on a fixture KB:

```csharp
// src/GxMcp.Worker.Tests/IdealWorkflowSmokeTest.cs
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IdealWorkflowSmokeTest
    {
        [Fact]
        public void EndToEnd_ProcedureEdit_BuildsClean()
        {
            var harness = TestFixtures.FixtureKb(); // includes WebPanel calling 3 procedures + folder + bound variable

            // 1. analyze impact
            var impact = harness.Analyze("ComissaoLiberaPareceres", mode: "impact");
            Assert.Equal("Ready", impact["status"].ToString());
            var buildTarget = impact["callers"].Concat(new[] { (JToken)"ComissaoLiberaPareceres" });

            // 2. read part=Source
            var src = harness.Read("ComissaoLiberaPareceres", part: "Source");
            Assert.False(src["truncated"]?.Value<bool>() ?? false);

            // 3. edit operation=Replace (multi-line)
            var edit = harness.Edit("ComissaoLiberaPareceres", part: "Source",
                operation: "Replace",
                context: "    Where ParecerFinal <> 3",
                content: "    Where ParecerFinal <> 3 and Status = 'A'");
            Assert.Equal("Success", edit["status"].ToString());
            Assert.NotNull(edit["persistedHash"]);

            // 4. lifecycle build target=... compact=true
            var build = harness.LifecycleBuild(buildTarget, compact: true);
            Assert.Equal("Success", build["status"].ToString());
        }
    }
}
```

- [ ] **Step 2: Run, verify pass**

Run: `dotnet test src\GxMcp.Worker.Tests --filter "IdealWorkflowSmokeTest"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git commit -am "test: end-to-end ideal-workflow smoke covering #1-#16"
```

### Task 8.2 — `ToolSchemaSizeTests` budget recheck

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/ToolSchemaSizeTests.cs`

- [ ] **Step 1: Run the test and check current size**

Run: `dotnet test src\GxMcp.Gateway.Tests --filter "ToolSchemaSizeTests" -v n`

Expected: shows current token count. If exceeded, bump budget by ~10-15% (target ≤ 4800).

- [ ] **Step 2: Edit budget constant**

Locate the constant (e.g. `const int Budget = 4600;`), bump if needed:

```csharp
const int Budget = 4800;
```

- [ ] **Step 3: Commit**

```bash
git commit -am "test: bump tool schema budget for v2.3.8 (new flags + modify_variable)"
```

### Task 8.3 — CHANGELOG and version bump

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `package.json` (version bump)
- Modify: `src/GxMcp.Gateway/Configuration.cs` (if version embedded)

- [ ] **Step 1: Add v2.3.8 section to CHANGELOG**

Update the existing draft (in WIP) to also list:

- **Discovery:** index state on whoami; IndexCold/Timeout envelopes on search; `nameFilter`/`descriptionFilter`/`pathPrefix` on list; `analyze impact` unified with caller graph.
- **Edit:** EOL-normalized matching; byte-level `nearMatchHint`; `{find,replace}` patch works for multi-line; `persistedHash` + `persistedSnippet` on every response; window-only rollback verification.
- **Variables:** `genexus_modify_variable` (new tool); `add_variable` validates typeName via `VariableTypeResolver`; `delete_variable` symmetric across all object kinds; `[var:N]` resolver in error messages.
- **Build:** segmented csproj auto-includes callee `<Reference>` tags; new `includeCallees` flag (default `transitive`); `_meta.buildPlan` response.
- **UX:** `lifecycle status compact=true` default; `read` default pagination; `background_jobs` notifications dedup.
- **i18n:** canonical EN messages; PT-BR source preserved in `_meta.sourceMessage`; `lifecycle action=cancel` works.

Add **Breaking notes** subsection (from spec):

```markdown
### Breaking notes

- `lifecycle status` default is now `compact=true`. Callers parsing `result.Errors` / `result.Output` should set `compact=false`.
- `genexus_read` paginates by default when a part exceeds 200 lines or 16 KB. Pass `limit=0` to opt out.
- `lifecycle build` default is `includeCallees=transitive`. Pass `includeCallees=none` for legacy.
- Error messages migrated to EN. Original SDK message in `_meta.sourceMessage`.
```

- [ ] **Step 2: Bump version**

In `package.json`: `"version": "2.3.8"`.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test`
Expected: all green (newly-added + existing 154 Worker + 211 Gateway, except the pre-existing `IdempotencyCacheTests.Eviction_LruDropsOldestWhenAtCapacity` failure noted in CHANGELOG).

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md package.json
git commit -m "chore(release): v2.3.8"
```

### Task 8.4 — Final spec coverage check

- [ ] **Step 1: Re-read spec, tick off every acceptance bullet**

Open `docs/superpowers/specs/2026-05-15-mcp-friction-v2.3.8-design.md` and confirm each numbered acceptance criterion in sections A-F is covered by at least one test. If any miss, add the test in the appropriate phase before tagging the release.

- [ ] **Step 2: Run release script (do NOT publish without user approval)**

Run: `pwsh -File .\scripts\release.ps1 -NoBump`
Verify the resulting artifact builds and bundles correctly.

Stop here. **Do not publish to npm or push the tag** — wait for user confirmation.

---

## Self-Review Notes

**Spec coverage check:**
- A1 (index state) → Task 1.1 + 1.2 ✓
- A2 (search envelopes) → Task 2.1 ✓
- A3 (list discovery) → Task 2.2 ✓
- A4 (analyze unified + waitForIndex) → Task 1.4 ✓
- B1 (EOL norm) → Task 3.1 ✓
- B2 (byteDiff hint) → Task 3.2 ✓
- B3 (patch shapes) → Task 3.3 ✓
- B4 (hash + snippet) → Task 3.4 ✓
- C1 (typeName resolver) → Task 4.1 + 4.2 ✓
- C2 (modify_variable) → Task 4.3 ✓
- C3 (symmetric delete) → Task 4.4 ✓
- C4 (ghost bindings) → Task 4.5 ✓
- C5 (window rollback) → Task 4.6 ✓
- D1 (auto-callee refs) → Task 5.1 ✓
- D2 (includeCallees flag + buildPlan) → Task 5.2 ✓
- E1 (compact status) → Task 6.1 ✓
- E2 (read pagination) → Task 6.2 ✓
- E3 (job dedup) → Task 6.3 ✓
- F1 (EN messages) → Task 7.1 ✓
- F2 (var:N resolver) → Task 4.5 ✓ (shared with C4)
- F3 (cancel) → Task 7.2 ✓
- Quick-wins → covered by their parent items.
- End-to-end smoke → Task 8.1.

No placeholders detected. Method signatures (`TryMatch`, `NormalizeForCompare`, `GetCallers`, `Resolve`, `ResolveVarBindings`, `GenerateSegmentedCsproj`, `Translate`, `Cancel`) are consistent across tasks.
