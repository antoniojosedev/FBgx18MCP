using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // PERF-02 regression: HasPendingEnrichment must stay correct while its result is
    // cached against the dirty-generation. Any mutation invalidates the cache, so the
    // answer tracks stubs appearing and draining; a stable index returns the same answer
    // without rescanning.
    public class EnrichmentPendingCacheTests
    {
        private static SearchIndex.IndexEntry Stub(string name) =>
            new SearchIndex.IndexEntry { Name = name, Type = "Procedure", ParentPath = "M", Guid = "g-" + name };

        private static SearchIndex.IndexEntry Enriched(string name)
        {
            var e = Stub(name);
            e.Embedding = new float[128];
            e.IsEnriched = true;
            return e;
        }

        [Fact]
        public void HasPendingEnrichment_TracksStubsAcrossMutations_AndIsStableWhenUnchanged()
        {
            var svc = new IndexCacheService();

            // All enriched → nothing pending; second call hits the generation cache.
            svc.AddOrUpdateBatch(new[] { Enriched("A") });
            Assert.False(svc.HasPendingEnrichment());
            Assert.False(svc.HasPendingEnrichment());

            // A new stub mutates the index → cache invalidated → pending flips true.
            svc.AddOrUpdateBatch(new[] { Stub("B") });
            Assert.True(svc.HasPendingEnrichment());
            Assert.True(svc.HasPendingEnrichment());

            // Enriching the last stub drains it → pending flips back to false.
            svc.AddOrUpdateBatch(new[] { Enriched("B") });
            Assert.False(svc.HasPendingEnrichment());
        }
    }
}
