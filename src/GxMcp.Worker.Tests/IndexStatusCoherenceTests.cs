using System.Reflection;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // `lifecycle action=status` used to assemble one answer from several reads taken at different
    // moments: the wait decision and `indexStatus` from the cache state, `operationState` and
    // `isIndexing` from the coordinator and a static flag. The index thread flips those a few
    // statements apart, so a packaged reply could say Ready/current/waitSatisfied together with
    // isIndexing=true, operationState=Building. The caller's snapshot is now the one source.
    [Collection("SdkOperationState")]
    public class IndexStatusCoherenceTests
    {
        private static readonly FieldInfo IsIndexingField =
            typeof(KbService).GetField("_isIndexing", BindingFlags.NonPublic | BindingFlags.Static);

        private static JObject StatusFor(bool staticIndexingFlag, IndexState observed)
        {
            bool previous = (bool)IsIndexingField.GetValue(null);
            IsIndexingField.SetValue(null, staticIndexingFlag);
            try { return JObject.Parse(new KbService(new IndexCacheService()).GetIndexStatus(observed)); }
            finally { IsIndexingField.SetValue(null, previous); }
        }

        [Fact]
        public void A_finished_operation_is_reported_idle_and_not_indexing_from_the_same_snapshot()
        {
            // The static flag is cleared a statement after the coordinator completes; a snapshot
            // that already saw the operation Idle must not be contradicted by it.
            var observed = new IndexState
            {
                Status = "Ready", Freshness = "current",
                OperationId = "idx-done", OperationState = "Idle", OperationActive = false
            };

            var status = StatusFor(staticIndexingFlag: true, observed);

            Assert.Equal("idx-done", (string)status["operationId"]);
            Assert.Equal("Idle", (string)status["operationState"]);
            Assert.False((bool)status["isIndexing"]);
            Assert.Equal("current", (string)status["freshness"]);
        }

        [Fact]
        public void An_active_operation_is_reported_from_the_supplied_snapshot()
        {
            var observed = new IndexState
            {
                Status = "Ready", Freshness = "current",
                OperationId = "idx-running", OperationState = "Building", OperationActive = true, WorkerAlive = true
            };

            var status = StatusFor(staticIndexingFlag: true, observed);

            Assert.Equal("idx-running", (string)status["operationId"]);
            Assert.Equal("Building", (string)status["operationState"]);
            Assert.True((bool)status["isIndexing"]);
            Assert.True((bool)status["workerAlive"]);
        }
    }
}
