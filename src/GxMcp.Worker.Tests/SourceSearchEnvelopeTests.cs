using System.Collections.Generic;
using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 2.1): SourceSearchService must surface index readiness and
    // hard-timeout state in structured envelopes instead of returning silent
    // empty hits.
    public class SourceSearchEnvelopeTests
    {
        [Fact]
        public void Search_OnColdIndex_ReturnsIndexColdEnvelope()
        {
            var index = new IndexCacheService(); // Cold by default — never loaded.
            var svc = new SourceSearchService(index, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "Clicksign" });
            var obj = JObject.Parse(json);

            Assert.Equal("IndexCold", obj["status"]?.ToString());
            Assert.NotNull(obj["retryAfterMs"]);
            Assert.Null(obj["hits"]); // never silent empty
            Assert.Null(obj["count"]);
        }

        [Fact]
        public void Search_OnReadyIndex_ReturnsHitsEnvelope()
        {
            var fixture = TestFixtures.SmallCallGraph();
            fixture.Index.MarkIndexComplete(3);
            // ObjectService is null; the per-entry FindObject call will throw and
            // be swallowed, leaving hits empty — but the envelope shape itself
            // (status=Ready, hits present) is what we assert here.
            var svc = new SourceSearchService(fixture.Index, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria { Pattern = "B" });
            var obj = JObject.Parse(json);

            Assert.Equal("Ready", obj["status"]?.ToString());
            Assert.NotNull(obj["hits"]);
        }

        [Fact]
        public void Search_HardTimeoutExceeded_ReturnsTimeoutEnvelope()
        {
            // Pathological fixture: enough Procedure entries to make the
            // per-entry loop measurably exceed a 0ms budget. We use TimeoutMs=0
            // so the very first loop iteration trips the timeout regardless of
            // host speed — the assertion targets the envelope shape, not a real
            // wall-clock race.
            var entries = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < 1000; i++)
            {
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = "P" + i,
                    Type = "Procedure",
                    Calls = new List<string>(),
                    CalledBy = new List<string>(),
                    SourceSnippet = "match"
                });
            }
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            idx.MarkIndexComplete(entries.Count);

            var svc = new SourceSearchService(idx, null);
            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "match",
                TimeoutMs = 0
            });
            var obj = JObject.Parse(json);

            Assert.Equal("Timeout", obj["status"]?.ToString());
            Assert.NotNull(obj["partialHits"]);
            Assert.NotNull(obj["totalScanned"]);
            Assert.NotNull(obj["totalObjects"]);
        }
    }
}
