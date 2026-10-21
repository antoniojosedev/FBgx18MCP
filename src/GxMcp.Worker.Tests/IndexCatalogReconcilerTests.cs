using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The warm-start delta only re-reads objects whose LastUpdate is newer than the persisted
    // high-water-mark and Objects.GetKeys(hwm) never reports deletions. A packaged SDK run kept
    // 856 cached objects (26 deleted ones, 8+ missing live ones) after a delta that logged
    // `changed=1 deleted=0`, because the deletion sweep was gated on `Objects.Count`, a member the
    // SDK's DesignModel.Objects (IKBModelObjects: IEnumerable<KBObject> only) does not have.
    public class IndexCatalogReconcilerTests
    {
        public sealed class FakeKbObject
        {
            public FakeKbObject(string guid) { Guid = guid; }
            public string Guid { get; }
        }

        // Mirrors IKBModelObjects: enumerable, and nothing else the reconciler could count with.
        private sealed class CountlessCatalog : IEnumerable<FakeKbObject>
        {
            private readonly List<FakeKbObject> _objects;
            public CountlessCatalog(IEnumerable<FakeKbObject> objects) { _objects = objects.ToList(); }
            public IEnumerator<FakeKbObject> GetEnumerator() => _objects.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static string GuidOf(object o) => ((FakeKbObject)o).Guid;

        private static CountlessCatalog Catalog(params string[] guids) =>
            new CountlessCatalog(guids.Select(g => new FakeKbObject(g)));

        [Fact]
        public void Deleted_object_is_reported_stale_when_the_catalog_has_no_Count_member()
        {
            var result = IndexCatalogReconciler.Reconcile(
                Catalog("a", "b"), GuidOf, new[] { "a", "b", "deleted" });

            Assert.True(result.Swept);
            Assert.Equal(new[] { "deleted" }, result.StaleGuids);
        }

        [Fact]
        public void Deleted_object_is_reported_stale_even_when_nothing_changed_since_the_high_water_mark()
        {
            var result = IndexCatalogReconciler.Reconcile(
                Catalog("a"), GuidOf, new[] { "a", "deleted" });

            Assert.Equal(new[] { "deleted" }, result.StaleGuids);
        }

        [Fact]
        public void Live_object_older_than_the_high_water_mark_is_reported_missing()
        {
            var live = new FakeKbObject("restored");
            var catalog = new CountlessCatalog(new[] { new FakeKbObject("a"), live });

            var result = IndexCatalogReconciler.Reconcile(
                catalog, GuidOf, new[] { "a" });

            Assert.Same(live, Assert.Single(result.MissingObjects));
            Assert.Empty(result.StaleGuids);
        }

        [Fact]
        public void Guid_comparison_is_case_insensitive()
        {
            var result = IndexCatalogReconciler.Reconcile(
                Catalog("ABC-1"), GuidOf, new[] { "abc-1" });

            Assert.Empty(result.StaleGuids);
            Assert.Empty(result.MissingObjects);
        }

        [Fact]
        public void An_empty_catalog_never_wipes_a_populated_index()
        {
            var result = IndexCatalogReconciler.Reconcile(
                Catalog(), GuidOf, new[] { "a", "b" });

            Assert.False(result.Swept);
            Assert.Empty(result.StaleGuids);
        }

        [Fact]
        public void An_unreadable_guid_never_turns_a_live_object_into_a_deletion()
        {
            var catalog = Catalog("a", "unreadable");
            Func<object, string> guidOf = o =>
            {
                string guid = ((FakeKbObject)o).Guid;
                if (guid == "unreadable") throw new InvalidOperationException("COM read failed");
                return guid;
            };

            var result = IndexCatalogReconciler.Reconcile(
                catalog, guidOf, new[] { "a", "unreadable", "deleted" });

            Assert.False(result.Swept);
            Assert.Empty(result.StaleGuids);
        }

        [Fact]
        public void Stale_guids_are_removed_from_the_restored_index()
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Live", Type = "Procedure", Guid = "live" },
                new SearchIndex.IndexEntry { Name = "Gone", Type = "Procedure", Guid = "gone" },
            });
            cache.UpdateIndex(cache.GetIndex());

            var result = IndexCatalogReconciler.Reconcile(
                Catalog("live"), GuidOf, cache.GetIndex().GuidToKey.Keys.ToList());
            foreach (var guid in result.StaleGuids) cache.RemoveEntryByGuid(guid);

            var index = cache.GetIndex();
            Assert.True(index.Objects.ContainsKey("Procedure:Live"));
            Assert.False(index.Objects.ContainsKey("Procedure:Gone"));
        }

        // --- Applying a reconciliation: an authoritative pass that could not be completed must
        // never be certified as current, and a superseded generation must not keep deleting. ---

        [Fact]
        public void A_refused_reconciliation_names_why_and_cannot_be_applied()
        {
            var refused = IndexCatalogReconciler.Reconcile(
                Catalog(), GuidOf, new[] { "a", "b" });
            var removed = new List<string>();

            var ex = Assert.Throws<InvalidOperationException>(() => IndexCatalogReconciler.Apply(
                refused, () => true, removed.Add, o => { }));

            Assert.Contains("enumerated no objects", ex.Message);
            Assert.Empty(removed); // refusal never deletes
        }

        [Fact]
        public void An_unreadable_guid_refusal_carries_the_read_error()
        {
            Func<object, string> guidOf = o => throw new InvalidOperationException("COM read failed");

            var refused = IndexCatalogReconciler.Reconcile(Catalog("a"), guidOf, new[] { "a" });

            Assert.False(refused.Swept);
            Assert.Contains("COM read failed", refused.SkipReason);
        }

        [Fact]
        public void A_live_object_that_cannot_be_indexed_fails_the_application_instead_of_counting_as_added()
        {
            var ok = new FakeKbObject("ok");
            var broken = new FakeKbObject("broken");
            var reconciliation = IndexCatalogReconciler.Reconcile(
                new CountlessCatalog(new[] { ok, broken }), GuidOf, new[] { "gone" });
            var indexed = new List<object>();

            var ex = Assert.Throws<InvalidOperationException>(() => IndexCatalogReconciler.Apply(
                reconciliation, () => true, g => { },
                o => { if (ReferenceEquals(o, broken)) throw new InvalidOperationException("enrich failed"); indexed.Add(o); }));

            Assert.Contains("1 of 2", ex.Message);
            Assert.Contains("enrich failed", ex.Message);
            Assert.Same(ok, Assert.Single(indexed));
        }

        [Fact]
        public void A_superseded_generation_stops_deleting_stale_entries()
        {
            var reconciliation = IndexCatalogReconciler.Reconcile(
                Catalog("live"), GuidOf, new[] { "live", "gone1", "gone2", "gone3" });
            var removed = new List<string>();
            int checks = 0;

            var applied = IndexCatalogReconciler.Apply(
                reconciliation, () => ++checks <= 1, removed.Add, o => { });

            Assert.True(applied.Superseded);
            Assert.Equal(new[] { "gone1" }, removed);
        }

        [Fact]
        public void A_complete_application_reports_what_it_removed_and_added()
        {
            var reconciliation = IndexCatalogReconciler.Reconcile(
                Catalog("live", "new"), GuidOf, new[] { "live", "gone" });
            var removed = new List<string>();
            var added = new List<object>();

            var applied = IndexCatalogReconciler.Apply(reconciliation, () => true, removed.Add, added.Add);

            Assert.False(applied.Superseded);
            Assert.Equal(1, applied.Removed);
            Assert.Equal(1, applied.Added);
            Assert.Equal(new[] { "gone" }, removed);
        }

        // --- Progress: the delta thread's watchdog marks an operation Stalled after ~180 s without
        // a heartbeat. The catalog walk and the per-object enrichment of a large restore run far
        // longer than that and used to beat only in the earlier GetKeys loop. ---

        [Fact]
        public void Reading_each_catalog_guid_reports_progress()
        {
            int beats = 0;

            IndexCatalogReconciler.Reconcile(
                Catalog("a", "b", "c"), GuidOf, new[] { "a" }, progress: () => beats++);

            Assert.Equal(3, beats);
        }

        [Fact]
        public void An_unreadable_guid_is_not_reported_as_progress()
        {
            int beats = 0;
            Func<object, string> guidOf = o => throw new InvalidOperationException("COM read failed");

            var refused = IndexCatalogReconciler.Reconcile(
                Catalog("a", "b"), guidOf, new[] { "a" }, progress: () => beats++);

            Assert.False(refused.Swept);
            Assert.Equal(0, beats);
        }

        [Fact]
        public void Every_successful_enrichment_reports_progress_and_a_failed_one_does_not()
        {
            var ok1 = new FakeKbObject("ok1");
            var broken = new FakeKbObject("broken");
            var ok2 = new FakeKbObject("ok2");
            var reconciliation = IndexCatalogReconciler.Reconcile(
                new CountlessCatalog(new[] { ok1, broken, ok2 }), GuidOf, new[] { "gone" });
            int beats = 0;

            Assert.Throws<InvalidOperationException>(() => IndexCatalogReconciler.Apply(
                reconciliation, () => true, g => { },
                o => { if (ReferenceEquals(o, broken)) throw new InvalidOperationException("enrich failed"); },
                progress: () => beats++));

            Assert.Equal(2, beats);
        }

        [Fact]
        public void A_superseded_generation_reports_no_further_progress()
        {
            var reconciliation = IndexCatalogReconciler.Reconcile(
                Catalog("n1", "n2", "n3"), GuidOf, new[] { "live" });
            int checks = 0;
            int beats = 0;

            var applied = IndexCatalogReconciler.Apply(
                reconciliation, () => ++checks <= 2, g => { }, o => { }, progress: () => beats++);

            Assert.True(applied.Superseded);
            Assert.Equal(1, beats);
        }
    }
}
