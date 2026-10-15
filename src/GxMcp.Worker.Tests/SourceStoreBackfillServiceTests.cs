using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreBackfillServiceTests
    {
        [Theory]
        [InlineData("Transaction", "Events,Rules,WebForm", "Events,Rules,WebForm")]
        [InlineData("Attribute", "", "")]
        [InlineData("Table", "", "")]
        [InlineData("Procedure", "Source,Rules,Layout", "Source,Rules,Layout")]
        [InlineData("DataSelector", "Conditions", "Conditions")]
        public void RequiredPartsOnlyIncludeSupportedSdkParts(string type, string available, string expected)
        {
            var parts = ObjectService.GetSourceStoreRequiredParts(type,
                available.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(expected.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), parts);
        }

        [Theory]
        [InlineData("Transaction", "Events,Rules,WebForm")]
        [InlineData("Attribute", "")]
        [InlineData("Table", "")]
        public void SliceCompletesWithoutRequestingUnsupportedParts(string type, string available)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry(Guid.NewGuid().ToString("N"), 1);
                entry.Type = type;
                var required = ObjectService.GetSourceStoreRequiredParts(type,
                    available.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                var service = new SourceStoreBackfillService(store);
                Assert.True(service.Start(() => new[] { entry }, e => required
                    .Select(p => new SourceStoreBackfillPart { PartName = p, Source = "", ReadSucceeded = true }).ToList(),
                    requiredPartProvider: e => required, schedule: false));
                Assert.True(service.RunOneSliceForTest(1, 1000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(1, state.ProcessedObjects);
                Assert.True(string.IsNullOrEmpty(state.LastError));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void SlicePersistsProgressAndResumesFromCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entries = new[]
                {
                    Entry("00000000-0000-0000-0000-000000000001", 20),
                    Entry("00000000-0000-0000-0000-000000000002", 10)
                };
                var service = new SourceStoreBackfillService(store);
                bool started = service.Start(
                    () => entries,
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source-" + e.Guid } },
                    maxObjectsPerSlice: 1,
                    sliceBudgetMs: 1000,
                    schedule: false);
                Assert.True(started);

                Assert.True(service.RunOneSliceForTest(1, 1000));
                var afterFirst = service.GetState();
                Assert.Equal("running", afterFirst.State);
                Assert.Equal(1, afterFirst.CursorIndex);
                Assert.Equal(1, afterFirst.StoredObjects);

                Assert.True(service.RunOneSliceForTest(1, 1000));
                var afterSecond = service.GetState();
                Assert.Equal("complete", afterSecond.State);
                Assert.Equal(2, afterSecond.StoredObjects);
                Assert.True(File.Exists(Path.Combine(root, "backfill-state.json")));

                var resumed = new SourceStoreBackfillService(store);
                Assert.Equal(2, resumed.GetState().ProcessedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void CatalogueIdentityChangeResetsTheResumableCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var first = Entry("00000000-0000-0000-0000-000000000011", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(() => new[] { first }, e => Array.Empty<SourceStoreBackfillPart>(), schedule: false);
                service.RunOneSliceForTest(1, 1000);
                Assert.Equal(1, service.GetState().CursorIndex);

                var inserted = Entry("00000000-0000-0000-0000-000000000012", 2);
                service.Start(
                    () => new[] { inserted, first },
                    e => Array.Empty<SourceStoreBackfillPart>(),
                    schedule: false);

                Assert.Equal(0, service.GetState().CursorIndex);
                Assert.Equal("queued", service.GetState().State);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void ReaderFailureDoesNotAdvanceCursorOrCertifyCompleteCoverage()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-error-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entries = new[]
                {
                    Entry("00000000-0000-0000-0000-000000000004", 1),
                    Entry("00000000-0000-0000-0000-000000000005", 2)
                };
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => entries,
                    e => throw new InvalidOperationException("SDK read failed"),
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Contains("SDK read failed", state.LastError);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void NullSourceDoesNotAdvanceCursorOrCertifyComplete()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-null-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000006", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart
                        {
                            PartName = "Source",
                            Source = null
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Equal(0, state.StoredObjects);
                Assert.Null(state.CompletedAtUtc);
                Assert.Contains("null", state.LastError, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void MissingRequiredPartEvidenceDoesNotAdvanceOrCertify()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-missing-part-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000010", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart { PartName = "Source", Source = "source" }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source", "Rules" });

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Null(state.CompletedAtUtc);
                Assert.Contains("Rules", state.LastError);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ExplicitPartReadFailureDoesNotPartiallyAdvanceTheObject()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-read-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000011", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart { PartName = "Source", Source = "source" },
                        new SourceStoreBackfillPart
                        {
                            PartName = "Rules",
                            Source = null,
                            ReadSucceeded = false,
                            ReadError = "SDK reader failed"
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source", "Rules" });

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Equal(0, store.StoredRecordCount);
                Assert.Contains("Rules", state.LastError);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void EmptyPartReadIsPersistedAsSuccessfulEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-empty-part-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000012", 1);
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart
                        {
                            PartName = "Source",
                            Source = string.Empty,
                            ReadSucceeded = true
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                Assert.True(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(1, state.StoredObjects);
                Assert.True(store.TryGet(entry.Guid, "Source", out string source));
                Assert.Equal(string.Empty, source);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void EmptyReaderResultIsValidWhenEveryRequiredPartIsAlreadyFresh()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-all-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000013", 1);
                Assert.True(store.Put(entry.Guid, "Source", string.Empty, entry.LastUpdate, "v1"));
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e => Array.Empty<SourceStoreBackfillPart>(),
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                Assert.True(service.RunOneSliceForTest(10, 1000));
                Assert.Equal("complete", service.GetState().State);
                Assert.Equal(1, service.GetState().ProcessedObjects);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void FailedSourceStorePutDoesNotAdvanceCursorOrCertifyComplete()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-put-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000007", 1);
                var service = new SourceStoreBackfillService(
                    store,
                    (guid, partName, source, lastUpdate, versionToken) => false,
                    null);
                service.Start(
                    () => new[] { entry },
                    e => new[]
                    {
                        new SourceStoreBackfillPart
                        {
                            PartName = "Source",
                            Source = "source"
                        }
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false);

                Assert.False(service.RunOneSliceForTest(10, 1000));
                var state = service.GetState();
                Assert.Equal("error", state.State);
                Assert.Equal(0, state.CursorIndex);
                Assert.Equal(0, state.ProcessedObjects);
                Assert.Equal(0, state.StoredObjects);
                Assert.Null(state.CompletedAtUtc);
                Assert.Contains("rejected", state.LastError, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void SdkEnqueueRejectionSchedulesRetryAndEventuallyRunsSlice()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-enqueue-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000008", 1);
                int enqueueAttempts = 0;
                int sliceCompleted = 0;
                var service = new SourceStoreBackfillService(
                    store,
                    null,
                    action =>
                    {
                        if (Interlocked.Increment(ref enqueueAttempts) == 1) return false;
                        action();
                        Volatile.Write(ref sliceCompleted, 1);
                        return true;
                    });

                Assert.True(service.Start(
                    () => new[] { entry },
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source" } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: true));
                Assert.True(SpinWait.SpinUntil(
                    () => Volatile.Read(ref sliceCompleted) == 1,
                    TimeSpan.FromSeconds(5)));
                Assert.Equal(2, Volatile.Read(ref enqueueAttempts));
                Assert.Equal("complete", service.GetState().State);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public async Task RepeatedSdkEnqueueRejectionsStopAtTheRetryLimit()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-enqueue-limit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000009", 1);
                int enqueueAttempts = 0;
                var service = new SourceStoreBackfillService(
                    store,
                    null,
                    action =>
                    {
                        Interlocked.Increment(ref enqueueAttempts);
                        return false;
                    });

                service.Start(
                    () => new[] { entry },
                    e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source" } },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: true);

                Assert.True(SpinWait.SpinUntil(
                    () => Volatile.Read(ref enqueueAttempts) >= 5,
                    TimeSpan.FromSeconds(5)));
                await Task.Delay(250);
                Assert.Equal(5, Volatile.Read(ref enqueueAttempts));
                Assert.Equal(5, service.GetState().RetryCount);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void FreshStoredPartIsNotReadAgain()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry = Entry("00000000-0000-0000-0000-000000000003", 1);
                int reads = 0;
                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry },
                    e =>
                    {
                        if (store.IsStoredAndFresh(e, new List<string> { "source" }))
                            return Array.Empty<SourceStoreBackfillPart>();
                        reads++;
                        return Array.Empty<SourceStoreBackfillPart>();
                    }, schedule: false);
                store.Put(entry.Guid, "source", "cached", entry.LastUpdate, "v1");
                service.RunOneSliceForTest(10, 1000);
                Assert.Equal(0, reads);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ConfirmedDeletedObjectAdvancesCursorAndRemovesFromStore()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-source-backfill-deleted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SourceStoreService();
                store.SetStoreDirectoryForTest(root);
                var entry1 = Entry("00000000-0000-0000-0000-000000000091", 5);
                var entry2 = Entry("00000000-0000-0000-0000-000000000092", 1);

                // Pre-populate entry1 in store to prove RemoveObject cleans it up
                store.Put(entry1.Guid, "Source", "old source", entry1.LastUpdate, "v0");
                Assert.True(store.StoredRecordCount > 0);

                var service = new SourceStoreBackfillService(store);
                service.Start(
                    () => new[] { entry1, entry2 },
                    e =>
                    {
                        if (e.Guid == entry1.Guid)
                        {
                            return new[] { new SourceStoreBackfillPart { IsDeleted = true } };
                        }
                        return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "fresh source" } };
                    },
                    maxObjectsPerSlice: 10,
                    sliceBudgetMs: 1000,
                    schedule: false,
                    requiredPartProvider: e => new[] { "Source" });

                bool sliceSuccess = service.RunOneSliceForTest(10, 1000);
                Assert.True(sliceSuccess);

                var state = service.GetState();
                Assert.Equal("complete", state.State);
                Assert.Equal(2, state.CursorIndex);
                Assert.Equal(2, state.ProcessedObjects);
                Assert.Equal(1, state.StoredObjects);
                Assert.Equal(1, state.RemovedObjects);
                Assert.True(string.IsNullOrEmpty(state.LastError));

                // Verify entry1 was cleaned up from the store
                Assert.False(store.RecordExistsForTest(entry1.Guid + ":Source"));
                // Verify entry2 is stored and fresh
                Assert.True(store.IsStoredAndFresh(entry2, new List<string> { "Source" }));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void TriggerStateReflectsWaitingForTriggerWhenOff()
        {
            var state = new SourceStoreBackfillState { State = "off" };
            var json = state.ToJson();
            Assert.Equal("off", json["state"]?.ToString());
            Assert.Equal("waitingForTrigger", json["triggerState"]?.ToString());

            state.TriggerState = "running";
            Assert.Equal("running", state.ToJson()["triggerState"]?.ToString());
        }

        [Fact]
        public void DurableSourceStoreRoot_IndependentOfGatewayProcessScopeId()
        {
            string originalScope = Environment.GetEnvironmentVariable("GXMCP_STATE_SCOPE_ID");
            string originalKey = Environment.GetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY");
            string originalKbId = Environment.GetEnvironmentVariable("GXMCP_KB_ID");
            string originalKbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");
            string originalGen = Environment.GetEnvironmentVariable("GXMCP_KB_GENERATION");

            try
            {
                Environment.SetEnvironmentVariable("GXMCP_KB_ID", "kb-alpha");
                Environment.SetEnvironmentVariable("GX_KB_PATH", @"C:\TestKBs\Alpha");
                Environment.SetEnvironmentVariable("GXMCP_KB_GENERATION", "1");

                // Process 1 scope
                Environment.SetEnvironmentVariable("GXMCP_STATE_SCOPE_ID", Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY", "proc-1-token");
                string root1 = RuntimePaths.DurableSourceStoreRoot;

                // Process 2 scope (simulating gateway restart with a different StateScopeId)
                Environment.SetEnvironmentVariable("GXMCP_STATE_SCOPE_ID", Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY", "proc-2-token");
                string root2 = RuntimePaths.DurableSourceStoreRoot;

                Assert.Equal(root1, root2);

                // Now bump generation: durable root MUST change
                Environment.SetEnvironmentVariable("GXMCP_KB_GENERATION", "2");
                string root3 = RuntimePaths.DurableSourceStoreRoot;
                Assert.NotEqual(root1, root3);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_STATE_SCOPE_ID", originalScope);
                Environment.SetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY", originalKey);
                Environment.SetEnvironmentVariable("GXMCP_KB_ID", originalKbId);
                Environment.SetEnvironmentVariable("GX_KB_PATH", originalKbPath);
                Environment.SetEnvironmentVariable("GXMCP_KB_GENERATION", originalGen);
            }
        }

        [Fact]
        public void TryCleanupOrphanedStores_RemovesExpiredProcessScopedStores()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "gxmcp-test-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempBase);
            try
            {
                string stateDir = Path.Combine(tempBase, "state");
                string currentStore = Path.Combine(stateDir, "current-hash", "source-store");
                string oldStore = Path.Combine(stateDir, "old-hash", "source-store");

                Directory.CreateDirectory(currentStore);
                Directory.CreateDirectory(oldStore);

                File.WriteAllText(Path.Combine(currentStore, "catalog.json.gz"), "current");
                File.WriteAllText(Path.Combine(oldStore, "catalog.json.gz"), "old");

                // Set old store last write time to 2 hours ago
                Directory.SetLastWriteTimeUtc(oldStore, DateTime.UtcNow.AddHours(-2));

                int cleaned = SourceStoreService.TryCleanupOrphanedStores(currentStore, TimeSpan.FromHours(1));
                Assert.True(cleaned >= 1);
                Assert.True(Directory.Exists(currentStore));
                Assert.False(Directory.Exists(oldStore));
            }
            finally
            {
                try { Directory.Delete(tempBase, true); } catch { }
            }
        }

        private static SearchIndex.IndexEntry Entry(string guid, int minutes)
            => new SearchIndex.IndexEntry
            {
                Guid = guid,
                StorageKey = "Type:" + guid,
                Type = "Procedure",
                Name = guid,
                LastUpdate = DateTime.UtcNow.AddMinutes(-minutes)
            };
    }
}
