using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #25 #4: while the lite walk is still running (UltraLiteReady), a
    // list_objects page describes only the objects walked so far. It must not
    // present that subset as the complete catalogue (authoritative total /
    // hasMore:false), and a filter miss must not read as "does not exist".
    public class ListPartialCatalogueTests
    {
        private static IndexCacheService PartialIndex(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries, markReady: false);
            idx.MarkUltraLiteReady(2);
            return idx;
        }

        [Fact]
        public void List_PartialIndex_MarksTotalPartialAndHasMore()
        {
            var idx = PartialIndex(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Foo", Type = "Procedure", Path = "M/Foo" },
                new SearchIndex.IndexEntry { Name = "Bar", Type = "Procedure", Path = "M/Bar" }
            });
            var svc = new ListService(idx);
            var obj = JObject.Parse(svc.List(new ListCriteria { Limit = 50 }));

            Assert.True(obj["partial"]?.ToObject<bool>());
            Assert.True(obj["totalIsPartial"]?.ToObject<bool>());
            // Even though the current page returned every walked object, more are
            // still arriving — hasMore must be true, and the canonical total null.
            Assert.True(obj["hasMore"]?.ToObject<bool>());
            Assert.Equal(JTokenType.Null, obj["pagination"]?["total"]?.Type);
            Assert.NotNull(obj["partialHint"]);
        }

        [Fact]
        public void List_PartialIndex_TypeFilterMiss_DoesNotImplyAbsent()
        {
            var idx = PartialIndex(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Foo", Type = "Procedure", Path = "M/Foo" }
            });
            var svc = new ListService(idx);
            // WebPanel not walked yet — must not be reported as absent.
            var obj = JObject.Parse(svc.List(new ListCriteria { TypeFilter = "WebPanel", Limit = 50 }));

            Assert.True(obj["partial"]?.ToObject<bool>());
            var hint = obj["_meta"]?["filterHint"]?.ToString() ?? "";
            Assert.Contains("still walking", hint);
        }
    }
}
