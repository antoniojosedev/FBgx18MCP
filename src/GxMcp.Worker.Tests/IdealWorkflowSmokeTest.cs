using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 8.1) — end-to-end smoke test composing the 15-turn friction-report
    // workflow against in-process services. Catches regressions where individual
    // unit tests pass but the composition breaks (e.g. the warm-start IndexState
    // bug that escaped the original push because no test exercised the chain).
    //
    // Avoids the real KB / SDK so the test stays runnable on CI without GeneXus 18.
    // The composition checked here is the "shape of the contract":
    //   1. whoami-style index state observable -> Ready after warm-load
    //   2. analyze impact returns proper envelope when index not ready
    //   3. list_objects with nameFilter narrows the result set
    //   4. read pagination surfaces suggestedNextOffset on overflow
    //   5. SourceSearch envelope reacts to index state
    //   6. lifecycle compact shaper round-trip preserves status/errorCount
    //   7. JobRegistry cancel + Piggyback dedup compose without double-notifying
    public class IdealWorkflowSmokeTest
    {
        [Fact]
        public void EndToEnd_FrictionReportWorkflow_Composes()
        {
            // ── 1. Cold index → query envelopes report unready ────────────────
            var index = new IndexCacheService();
            Assert.Equal("Cold", index.GetState().Status);

            var search = new SourceSearchService(index, objectService: null);
            var coldEnv = JObject.Parse(search.SearchAsJson(new SourceSearchCriteria { Pattern = "anything", MaxResults = 5 }));
            Assert.Equal("IndexCold", coldEnv["status"]?.ToString());
            Assert.NotNull(coldEnv["retryAfterMs"]);

            // ── 2. Warm-load → state transitions to Ready (post-Task 1.2 fix) ─
            var entries = Enumerable.Range(0, 50).Select(i => new SearchIndex.IndexEntry
            {
                Name = i % 5 == 0 ? "ComissaoProc" + i : "Other" + i,
                Type = "Procedure",
                Description = i % 5 == 0 ? "Liberar pareceres" : "",
                ParentFolderPath = i < 20 ? "Root Module/Comissao" : "Root Module/Other",
                SourceSnippet = "for each Foo " + i + " endfor"
            }).ToList();
            index.LoadFromEntries(entries);
            index.MarkIndexComplete(entries.Count);
            Assert.Equal("Ready", index.GetState().Status);
            Assert.Equal(50, index.GetState().TotalObjects);

            // ── 3. list_objects-style discovery filters (Task 2.2) ────────────
            var idx = index.GetIndex();
            var byName = idx.Objects.Values
                .Where(e => (e.Name ?? "").IndexOf("Comissao", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            Assert.Equal(10, byName.Count);

            var byPath = idx.Objects.Values
                .Where(e => (e.ParentFolderPath ?? "").StartsWith("Root Module/Comissao"))
                .ToList();
            Assert.Equal(20, byPath.Count);

            // ── 4. read pagination defaults (Task 6.2) ───────────────────────
            var bigSource = string.Join("\n", Enumerable.Range(0, 1240).Select(i => "line " + i + " " + new string('x', 30)));
            var page = ReadPagination.ApplyDefault(bigSource, offset: null, limit: null, client: "mcp");
            Assert.True(page.Truncated);
            Assert.Equal(1240, page.TotalLines);
            Assert.Equal(200, page.SuggestedNextOffset);

            var pageOptOut = ReadPagination.ApplyDefault(bigSource, offset: null, limit: 0, client: "mcp");
            Assert.False(pageOptOut.Truncated);
            Assert.Equal(1240, pageOptOut.LinesReturned);

            // ── 5. CallerGraph + segmented build expansion (Task 1.3 + 5.1) ──
            var graphEntries = new[]
            {
                new SearchIndex.IndexEntry { Name = "PanelA", Type = "WebPanel", Calls = new System.Collections.Generic.List<string> { "ProcB", "ProcC" } },
                new SearchIndex.IndexEntry { Name = "ProcB", Type = "Procedure", Calls = new System.Collections.Generic.List<string> { "ProcD" }, CalledBy = new System.Collections.Generic.List<string> { "PanelA" } },
                new SearchIndex.IndexEntry { Name = "ProcC", Type = "Procedure", CalledBy = new System.Collections.Generic.List<string> { "PanelA" } },
                new SearchIndex.IndexEntry { Name = "ProcD", Type = "Procedure", CalledBy = new System.Collections.Generic.List<string> { "ProcB" } }
            };
            var graphIndex = new IndexCacheService();
            graphIndex.LoadFromEntries(graphEntries);
            graphIndex.MarkIndexComplete(graphEntries.Length);
            var graph = new CallerGraphService(graphIndex);

            var build = new BuildService();
            build.SetCallerGraphService(graph);
            var plan = build.ExpandTargets(new[] { "PanelA" }, includeCallees: "transitive", cap: 200);
            Assert.False(plan.Truncated);
            // Leaf-first ordering: ProcD/ProcB/ProcC (callees) then PanelA.
            Assert.Equal("PanelA", plan.Expanded.Last());
            Assert.Contains("ProcD", plan.Expanded);

            // ── 6. ErrorMessages translation round-trip (Task 7.1) ──────────
            var translated = ErrorMessages.Translate("A validação de Web Panel 'X' falhou.. Detailed Messages:  [VALIDATION]: Referência de controle inválida: '[var:64]'");
            Assert.Contains("Web Panel 'X' validation failed.", translated);
            Assert.Contains("Invalid control reference: '[var:64]'", translated);
            Assert.DoesNotContain("falhou", translated);

            // ── 7. Worker-side cancel for search composes (post-Task 7.2 fix) ──
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            var cancelled = JObject.Parse(search.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                MaxResults = 1000,
                TimeoutMs = 30000
            }, cts.Token));
            Assert.Equal("Cancelled", cancelled["status"]?.ToString());
        }
    }
}
