using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #596: the source-store backfill start guard in ObjectService is one-shot
    /// per instance.  An index pass that publishes a changed catalogue (object created
    /// or deleted) must re-open it exactly once; a source-only edit must not.
    /// Hermetic: the backfill service is injected with a swallowed SDK queue and the
    /// walk is completed with a fake reader, so no Knowledge Base is touched.
    /// </summary>
    public class SourceStoreBackfillStartGateTests
    {
        [Fact]
        public void IndexReady_WithUnchangedCatalog_DoesNotRestartACompletedWalk()
        {
            using (var fixture = new GateFixture())
            {
                var first = fixture.Entry("00000000-0000-0000-0000-000000000001", "FirstObject");
                var second = fixture.Entry("00000000-0000-0000-0000-000000000002", "SecondObject");
                fixture.PublishCatalog(first, second);
                Assert.Equal(1, fixture.Backfill.WalkGenerationForTest);
                string signature = fixture.Backfill.GetState().CatalogSignature;
                Assert.False(string.IsNullOrWhiteSpace(signature));

                fixture.CompleteWalk();
                Assert.Equal("complete", fixture.Backfill.GetState().State);
                Assert.Equal(2, fixture.Backfill.GetState().ProcessedObjects);

                // A source edit changes LastUpdate/FullSource but not the identity set.
                // Start would restart the exhausted cursor, so an untouched state proves
                // the guard stayed closed.
                first.LastUpdate = DateTime.UtcNow;
                first.FullSource = "parm(&Edited);";
                fixture.Index.MarkIndexComplete(2);
                fixture.Service.StartSourceStoreBackfillIfReady();
                fixture.Service.GetLoadedIndexOrNull();

                var state = fixture.Backfill.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(2, state.ProcessedObjects);
                Assert.Equal(2, state.CursorIndex);
                Assert.Equal(signature, state.CatalogSignature);
                Assert.Equal(2, fixture.Backfill.WalkGenerationForTest);
            }
        }

        [Fact]
        public void IndexReady_AfterObjectCreated_StartsExactlyOneWalkWithTheNewSignature()
        {
            using (var fixture = new GateFixture())
            {
                var first = fixture.Entry("00000000-0000-0000-0000-000000000001", "FirstObject");
                var second = fixture.Entry("00000000-0000-0000-0000-000000000002", "SecondObject");
                fixture.PublishCatalog(first, second);
                string originalSignature = fixture.Backfill.GetState().CatalogSignature;
                fixture.CompleteWalk();
                Assert.Equal(2, fixture.Backfill.WalkGenerationForTest);

                var created = fixture.Entry("00000000-0000-0000-0000-000000000003", "CreatedObject");
                fixture.PublishCatalog(first, second, created);

                var restarted = fixture.Backfill.GetState();
                Assert.Equal("queued", restarted.State);
                Assert.Equal(0, restarted.CursorIndex);
                Assert.True(string.IsNullOrEmpty(restarted.Cursor));
                Assert.Equal(0, restarted.ProcessedObjects);
                Assert.Equal(0, restarted.StoredObjects);
                Assert.Equal(0, restarted.RemovedObjects);
                Assert.Equal(3, restarted.TotalObjects);
                Assert.NotEqual(originalSignature, restarted.CatalogSignature);
                Assert.Equal(3, fixture.Backfill.WalkGenerationForTest);

                // The guard is closed again for this catalogue: neither a repeated
                // index pass nor the hot-path callers start a second walk.
                fixture.Index.MarkIndexComplete(3);
                fixture.Service.StartSourceStoreBackfillIfReady();
                fixture.Service.GetLoadedIndexOrNull();
                Assert.Equal(3, fixture.Backfill.WalkGenerationForTest);
                Assert.Equal("queued", fixture.Backfill.GetState().State);
            }
        }

        [Fact]
        public void IndexReady_AfterObjectDeleted_StartsExactlyOneWalkWithTheNewSignature()
        {
            using (var fixture = new GateFixture())
            {
                var first = fixture.Entry("00000000-0000-0000-0000-000000000001", "FirstObject");
                var second = fixture.Entry("00000000-0000-0000-0000-000000000002", "SecondObject");
                var doomed = fixture.Entry("00000000-0000-0000-0000-000000000003", "DoomedObject");
                fixture.PublishCatalog(first, second, doomed);
                string originalSignature = fixture.Backfill.GetState().CatalogSignature;
                fixture.CompleteWalk();
                Assert.Equal("complete", fixture.Backfill.GetState().State);
                Assert.Equal(3, fixture.Backfill.GetState().ProcessedObjects);
                Assert.Equal(2, fixture.Backfill.WalkGenerationForTest);

                fixture.PublishCatalog(first, second);

                var restarted = fixture.Backfill.GetState();
                Assert.Equal("queued", restarted.State);
                Assert.Equal(0, restarted.CursorIndex);
                Assert.Equal(0, restarted.ProcessedObjects);
                Assert.Equal(2, restarted.TotalObjects);
                Assert.NotEqual(originalSignature, restarted.CatalogSignature);
                Assert.Equal(3, fixture.Backfill.WalkGenerationForTest);

                fixture.Service.StartSourceStoreBackfillIfReady();
                fixture.Index.MarkIndexComplete(2);
                Assert.Equal(3, fixture.Backfill.WalkGenerationForTest);
            }
        }

        [Fact]
        public void RearmCheck_IgnoresAnUnarmedGuardAndAnUnchangedSignature()
        {
            using (var fixture = new GateFixture())
            {
                var first = fixture.Entry("00000000-0000-0000-0000-000000000001", "FirstObject");
                var index = new SearchIndex();
                index.Objects[first.StorageKey] = first;

                // Nothing armed yet: there is nothing to re-open.
                Assert.False(fixture.Service.RearmSourceStoreBackfillIfCatalogChanged(index));

                fixture.PublishCatalog(first);
                Assert.Equal(1, fixture.Backfill.WalkGenerationForTest);
                Assert.False(fixture.Service.RearmSourceStoreBackfillIfCatalogChanged(fixture.Index.TryGetLoadedIndex()));

                var other = fixture.Entry("00000000-0000-0000-0000-000000000002", "OtherObject");
                var changed = new SearchIndex();
                changed.Objects[first.StorageKey] = first;
                changed.Objects[other.StorageKey] = other;
                Assert.True(fixture.Service.RearmSourceStoreBackfillIfCatalogChanged(changed));
                // Re-armed once: a second check against the same changed index is a no-op
                // until a start arms the guard again.
                Assert.False(fixture.Service.RearmSourceStoreBackfillIfCatalogChanged(changed));
            }
        }

        private sealed class GateFixture : IDisposable
        {
            private readonly string _root;

            public SourceStoreBackfillService Backfill { get; }
            public IndexCacheService Index { get; }
            public ObjectService Service { get; }

            public GateFixture()
            {
                _root = Path.Combine(Path.GetTempPath(), "gxmcp-backfill-gate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(_root);
                // The SDK queue is swallowed: production schedules the first slice
                // through it, while these tests drive slices explicitly.
                Backfill = new SourceStoreBackfillService(store, null, _ => true);
                Index = new IndexCacheService();
                Service = new ObjectService(new KbService(Index), null);
                Service.SetSourceStoreBackfillServiceForTest(Backfill);
            }

            public SearchIndex.IndexEntry Entry(string guid, string name)
                => new SearchIndex.IndexEntry
                {
                    Guid = guid,
                    StorageKey = "Procedure:" + name,
                    Type = "Procedure",
                    Name = name,
                    LastUpdate = DateTime.UtcNow.AddMinutes(-1)
                };

            /// <summary>Publishes a catalogue the way an index pass does: replace, then MarkIndexComplete.</summary>
            public void PublishCatalog(params SearchIndex.IndexEntry[] entries)
            {
                Index.LoadFromEntries(entries);
            }

            /// <summary>
            /// Finishes the current walk with a fake reader so the test never needs the
            /// SDK.  The direct Start keeps the identities ObjectService armed the guard
            /// for, so it resumes the same catalogue instead of resetting it.
            /// </summary>
            public void CompleteWalk()
            {
                var entries = Index.TryGetLoadedIndex().Objects.Values.ToList();
                Assert.True(Backfill.Start(
                    () => entries,
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "code-" + e.Guid } },
                    maxObjectsPerSlice: 50,
                    sliceBudgetMs: 5000,
                    schedule: false));
                Assert.True(Backfill.RunOneSliceForTest(50, 5000));
                Assert.True(Backfill.GetState().IsComplete);
            }

            public void Dispose()
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
            }
        }
    }
}
