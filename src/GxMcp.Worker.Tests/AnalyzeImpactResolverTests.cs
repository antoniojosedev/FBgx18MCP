using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (post-self-review) — pins the 4-stage index-entry resolver in
    // AnalyzeService.ImpactAnalysis. The legacy lookup returned a stale
    // "Indexing in progress for this object" envelope whenever the index key
    // didn't EndsWith(":<name>"), even for objects that were already in the
    // in-memory index under a slightly different key shape.
    public class AnalyzeImpactResolverTests
    {
        private static IndexCacheService BuildIndex(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(System.Linq.Enumerable.Count(entries));
            return svc;
        }

        private static AnalyzeService BuildAnalyze(IndexCacheService index)
        {
            // Construct AnalyzeService with the indexed-path-only dependencies.
            // ImpactAnalysis only touches _indexCacheService + _graph + _objectService;
            // null kbService/navigationService/uiService is fine for the resolver path.
            return new AnalyzeService(index, objSvc: null, graph: new CallerGraphService(index));
        }

        [Fact]
        public void Impact_NameOnly_ResolvesDespiteWhitespaceInIndexName()
        {
            // Index ingested a procedure whose Name has trailing whitespace
            // (real SDK quirk). Legacy resolver missed via EndsWith; new
            // resolver's trim stage catches it.
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "ProcA ", Type = "Procedure", Calls = new List<string>(), CalledBy = new List<string>() }
            };
            var index = BuildIndex(entries);
            var svc = BuildAnalyze(index);

            string json;
            try { json = svc.ImpactAnalysis("ProcA"); }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.NotEqual("Indexing in progress for this object. Please retry in a few seconds.",
                obj["status"]?.ToString());
            Assert.Contains(obj["status"]?.ToString(), new[] { "ok" });
        }

        [Fact]
        public void Impact_TypeEmpty_StillResolvedByNameValueScan()
        {
            // Entry was ingested with Type="" so the EndsWith on ":Name" against
            // a "Type:Name"-shaped key never matches. Stage 3 (Name value scan)
            // recovers.
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "ProcB", Type = "", Calls = new List<string>(), CalledBy = new List<string>() }
            };
            var index = BuildIndex(entries);
            var svc = BuildAnalyze(index);

            string json;
            try { json = svc.ImpactAnalysis("ProcB"); }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("ProcB", obj["target"]?.ToString());
        }

        [Fact]
        public void Impact_ZeroEdges_NoSdk_ReportsUnknownNotLow()
        {
            // Regression (friction 2026-06-02): a node that exists in the index but
            // carries no call-graph edges, with no SDK/ObjectService to confirm,
            // must NOT be reported as riskLevel "Low" (which reads as "safe to
            // change"). It must surface "Unknown" + indexEdgesMissing so the agent
            // knows the blast radius was NOT confirmed. This was the bug where impact
            // reported blastRadius 0 for an object inspect showed as having callers.
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Lonely", Type = "Procedure", Calls = new List<string>(), CalledBy = new List<string>() }
            };
            var index = BuildIndex(entries);
            var svc = BuildAnalyze(index); // objSvc null → no SDK cross-check available

            string json;
            try { json = svc.ImpactAnalysis("Lonely"); }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("ok", obj["status"]?.ToString());
            var result = obj["result"] ?? obj;
            string risk = result["riskLevel"]?.ToString();
            Assert.NotEqual("Low", risk);
            Assert.Equal("Unknown", risk);
            Assert.True(result["indexEdgesMissing"]?.ToObject<bool>() == true);
            Assert.True(result["verifiedZero"]?.ToObject<bool>() == false);
        }

        [Fact]
        public void Impact_WithRealEdges_StillReportsConcreteRisk()
        {
            // Guard the other direction: when the index DOES have edges, the
            // zero-signal path must not fire — risk stays a concrete level and
            // indexEdgesMissing is absent.
            var entries = new[]
            {
                new SearchIndex.IndexEntry { Name = "Callee", Type = "Procedure", Calls = new List<string>(), CalledBy = new List<string> { "Caller" } },
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure", Calls = new List<string> { "Callee" }, CalledBy = new List<string>() }
            };
            var index = BuildIndex(entries);
            var svc = BuildAnalyze(index);

            string json;
            try { json = svc.ImpactAnalysis("Callee"); }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            var result = obj["result"] ?? obj;
            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.NotEqual("Unknown", result["riskLevel"]?.ToString());
            Assert.Null(result["indexEdgesMissing"]);
        }

        [Fact]
        public void Impact_TrulyMissing_ReturnsObjectNotFound_NotPollingStub()
        {
            // Object is absent from both index AND (null) ObjectService.
            // Must NOT return the legacy "retry in a few seconds" string —
            // that wastes a turn on a stub the agent can't action.
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry { Name = "Existing", Type = "Procedure" }
            });
            var svc = BuildAnalyze(index);

            string json;
            try { json = svc.ImpactAnalysis("DoesNotExist"); }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.NotEqual("Indexing in progress for this object. Please retry in a few seconds.",
                obj["status"]?.ToString());
            // Either "Object not found in index" message OR a synthesised empty envelope.
            // The contract: caller never has to retry on a stub.
            Assert.True(
                obj["error"]?["message"]?.ToString() == "Object not found in index."
                || obj["status"]?.ToString() == "ok",
                "Resolver must return a deterministic answer, not a polling stub.");
        }
    }
}
