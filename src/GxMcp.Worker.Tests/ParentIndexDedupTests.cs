using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // PERF-01 regression: the incremental parent-index insert path
    // (AddOrUpdateEntryInParentIndex) must dedup in O(1) via the ChildKeysByParent
    // companion set, keeping the per-parent list free of duplicates without the old
    // O(n) List.Any scan. These tests pin the observable contract: no duplicates, the
    // key-set stays in lockstep with the list across add / re-add / remove.
    public class ParentIndexDedupTests
    {
        private const string Parent = "Root Module/M";

        private static List<SearchIndex.IndexEntry> Siblings(int count)
        {
            var list = new List<SearchIndex.IndexEntry>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new SearchIndex.IndexEntry
                {
                    Name = "Proc" + i,
                    Type = "Procedure",
                    ParentPath = Parent,
                    Guid = "guid-" + i
                });
            }
            return list;
        }

        [Fact]
        public void AddOrUpdateBatch_LargeSiblingGroup_NoDuplicates_AndKeySetTracksList()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(Siblings(500));

            var idx = svc.TryGetLoadedIndex();
            Assert.NotNull(idx);
            var list = idx.ChildrenByParent[Parent];
            var keys = idx.ChildKeysByParent[Parent];

            Assert.Equal(500, list.Count);
            Assert.Equal(500, keys.Count);                       // companion set mirrors the list
            Assert.Equal(500, list.Select(e => e.Name).Distinct().Count()); // no duplicates
        }

        [Fact]
        public void AddOrUpdateBatch_ReAddingSameEntries_DoesNotDuplicate()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(Siblings(100));
            svc.AddOrUpdateBatch(Siblings(100)); // same storage keys again

            var idx = svc.TryGetLoadedIndex();
            Assert.Equal(100, idx.ChildrenByParent[Parent].Count);
            Assert.Equal(100, idx.ChildKeysByParent[Parent].Count);
        }

        [Fact]
        public void RemoveEntryByGuid_DropsFromBothListAndKeySet()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(Siblings(10));

            svc.RemoveEntryByGuid("guid-3");

            var idx = svc.TryGetLoadedIndex();
            var list = idx.ChildrenByParent[Parent];
            var keys = idx.ChildKeysByParent[Parent];

            Assert.Equal(9, list.Count);
            Assert.Equal(9, keys.Count);
            Assert.DoesNotContain(list, e => e.Name == "Proc3");
        }
    }
}
