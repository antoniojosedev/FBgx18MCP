using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Plan 017: every WorkerStopReason teardown (idle/heap/wedged reap from the
    // health loop, and gateway-shutdown via StopWithReason) must cancel _cts so
    // the writer + health-check background loops exit instead of leaking.
    public class WorkerReapCancellationTests
    {
        private static WorkerProcess NewWorker() =>
            new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake"));

        [Theory]
        [InlineData(WorkerStopReason.IdleTimeout)]
        [InlineData(WorkerStopReason.HeapRecycle)]
        [InlineData(WorkerStopReason.Wedged)]
        public void StopProcess_Reap_CancelsCts(WorkerStopReason reason)
        {
            var worker = NewWorker();
            Assert.False(worker.CancellationRequestedForTest);

            worker.StopProcessForTest(reason);

            Assert.True(worker.CancellationRequestedForTest);
        }

        [Fact]
        public void StopWithReason_Shutdown_CancelsCts()
        {
            var worker = NewWorker();

            worker.StopWithReason(WorkerStopReason.GatewayShutdown);

            Assert.True(worker.CancellationRequestedForTest);
        }
    }
}
