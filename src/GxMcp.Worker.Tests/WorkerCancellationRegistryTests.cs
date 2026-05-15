using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (post-Task 7.2 fix) — WorkerCancellationRegistry pins the worker-side
    // half of the gateway↔worker cancel side-channel. The thread-safe dispatcher
    // registers a scoped CTS keyed by cancelToken; a parallel Control:Cancel
    // command looks it up and signals it.
    public class WorkerCancellationRegistryTests
    {
        [Fact]
        public void Register_NullToken_ReturnsUnusableCt_AndNoLeak()
        {
            WorkerCancellationRegistry.Reset();
            using (WorkerCancellationRegistry.Register(null, out var ct))
            {
                Assert.False(ct.CanBeCanceled);
                Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
            }
            Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
        }

        [Fact]
        public void Register_Token_AppearsInActiveCountUntilDisposed()
        {
            WorkerCancellationRegistry.Reset();
            using (WorkerCancellationRegistry.Register("job-1", out var ct))
            {
                Assert.True(ct.CanBeCanceled);
                Assert.False(ct.IsCancellationRequested);
                Assert.Equal(1, WorkerCancellationRegistry.ActiveCount);
            }
            Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
        }

        [Fact]
        public void Cancel_KnownToken_TripsRegisteredCt()
        {
            WorkerCancellationRegistry.Reset();
            using (WorkerCancellationRegistry.Register("job-2", out var ct))
            {
                Assert.True(WorkerCancellationRegistry.Cancel("job-2"));
                Assert.True(ct.IsCancellationRequested);
            }
        }

        [Fact]
        public void Cancel_UnknownToken_ReturnsFalse()
        {
            WorkerCancellationRegistry.Reset();
            Assert.False(WorkerCancellationRegistry.Cancel("never-registered"));
        }

        [Fact]
        public void Register_SameToken_TwiceShareSameCts()
        {
            WorkerCancellationRegistry.Reset();
            using (WorkerCancellationRegistry.Register("job-3", out var ctA))
            using (WorkerCancellationRegistry.Register("job-3", out var ctB))
            {
                WorkerCancellationRegistry.Cancel("job-3");
                Assert.True(ctA.IsCancellationRequested);
                Assert.True(ctB.IsCancellationRequested);
            }
        }
    }
}
