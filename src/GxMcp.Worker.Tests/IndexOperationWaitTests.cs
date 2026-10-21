using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Tests
{
    public class IndexOperationWaitTests
    {
        // Ready/current can be published before flush and FinishIndexOperation.
        [Theory]
        [InlineData("Starting")]
        [InlineData("Building")]
        [InlineData("Stalled")]
        [InlineData("WorkerExited")]
        [InlineData("Recovering")]
        public void Wait_ForCurrentFreshness_IsNotSatisfiedWhileTheIndexOperationIsActive(string operationState)
        {
            var publishedButRunning = new IndexState
            {
                Status = "Ready",
                Freshness = "current",
                OperationState = operationState,
                OperationActive = true
            };
            Assert.False(IndexWaitPolicy.IsSatisfied(publishedButRunning, null, "current"));
            Assert.False(IndexWaitPolicy.IsSatisfied(publishedButRunning, "Ready", "current"));

            var finished = new IndexState { Status = "Ready", Freshness = "current", OperationState = "Idle" };
            Assert.True(IndexWaitPolicy.IsSatisfied(finished, null, "current"));
        }

        [Fact]
        public void NotifyOperationCompleted_WakesAStatusWaiter()
        {
            var svc = new IndexCacheService();
            svc.ArmStateSignal();
            Assert.False(svc.WaitStateSignal(1));

            svc.NotifyOperationCompleted();

            Assert.True(svc.WaitStateSignal(1000));
        }

        [Fact]
        public void Wait_WithoutFreshnessTarget_IgnoresTheIndexOperation()
        {
            // Status-only and since-progress waits report availability, not completion.
            var running = new IndexState { Status = "Ready", Freshness = "current", OperationState = "Building", OperationActive = true };
            Assert.True(IndexWaitPolicy.IsSatisfied(running, null, null));
            Assert.True(IndexWaitPolicy.IsSatisfied(
                new IndexState { Status = "Enriching", OperationState = "Building", OperationActive = true }, "Ready", null));
        }
    }
}
