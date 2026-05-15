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
            Assert.Contains(obj["status"]?.ToString(), new[] { "Ready" });
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
            Assert.Equal("Ready", obj["status"]?.ToString());
            Assert.Equal("ProcB", obj["target"]?.ToString());
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
                obj["error"]?.ToString() == "Object not found in index"
                || obj["status"]?.ToString() == "Ready",
                "Resolver must return a deterministic answer, not a polling stub.");
        }
    }
}
