using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #27 item 4: genexus_search_source gains an objectName scope (search
    // inside one known object O(object) not O(KB)), a tunable timeoutMs, and a
    // resumable nextCursor on Timeout/Cancel. objectService is null here — the
    // FindObject call is swallowed, so these assert the scoping/cursor envelope
    // structure, not hit content.
    public class SourceSearchScopeTests
    {
        private static IndexCacheService Build10kIndex()
        {
            var svc = new IndexCacheService();
            var entries = Enumerable.Range(0, 10000).Select(i => new SearchIndex.IndexEntry
            {
                Name = "Proc" + i,
                Type = "Procedure",
                SourceSnippet = "for each Foo " + i + " endfor"
            }).ToList();
            // A non-whitelisted type to prove objectName bypasses the type gate.
            entries.Add(new SearchIndex.IndexEntry { Name = "MyPanel", Type = "SDPanel", SourceSnippet = "Foo" });
            svc.LoadFromEntries(entries);
            svc.MarkIndexComplete(entries.Count);
            return svc;
        }

        [Fact]
        public void ObjectName_ScopesToSingleObject_TotalObjectsIsOne()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                ObjectName = "Proc42",
                MaxResults = 1000,
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal("SourceSearchCompleted", obj["code"]?.ToString());
            // Scoped to exactly one object out of 10k+ — O(object), not O(KB).
            Assert.Equal(1, obj["result"]!["totalObjects"]!.Value<int>());
        }

        [Fact]
        public void ObjectName_BypassesTypeWhitelist()
        {
            // MyPanel is an SDPanel — not in the Procedure/DataProvider/WebPanel/
            // Transaction whitelist — yet an explicit objectName reaches it.
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                ObjectName = "MyPanel",
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal(1, obj["result"]!["totalObjects"]!.Value<int>());
        }

        [Fact]
        public void ObjectName_CommaSeparated_ScopesToEach()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                ObjectName = "Proc1, Proc2 , Proc3",
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal(3, obj["result"]!["totalObjects"]!.Value<int>());
        }

        [Fact]
        public void Cancelled_CarriesResumableNextCursor()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                MaxResults = 1000,
                StartIndex = 250,
                TimeoutMs = 30000
            }, cts.Token);

            var obj = JObject.Parse(json);
            Assert.Equal("Cancelled", obj["code"]?.ToString());
            // Pre-cancelled trips on the first iteration at the resume index, so the
            // cursor to resume from equals the StartIndex we passed in.
            Assert.Equal(250, obj["result"]!["nextCursor"]!.Value<int>());
            Assert.NotNull(obj["result"]!["resumeHint"]);
        }

        [Fact]
        public void StartIndex_BeyondEnd_CompletesEmpty()
        {
            var index = Build10kIndex();
            var svc = new SourceSearchService(index, objectService: null);

            var json = svc.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = "Foo",
                StartIndex = 999999,
                MaxResults = 10,
                TimeoutMs = 30000
            });

            var obj = JObject.Parse(json);
            Assert.Equal(0, obj["result"]!["count"]!.Value<int>());
            Assert.Equal(0, obj["result"]!["scannedObjects"]!.Value<int>());
        }
    }
}
