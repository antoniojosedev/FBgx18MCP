using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Proactive recycle must fire ONLY for an idle worker whose heap is over the ceiling,
    // and never interrupt a worker with work in flight. Measured baseline is ~130-160MB, so
    // the 1500MB default only trips after sustained heavy work.
    public class WorkerHeapRecycleTests
    {
        private static WorkerProcess Make(int recycleMB)
        {
            var config = new Configuration { Server = new ServerConfig { WorkerHeapRecycleMB = recycleMB } };
            return new WorkerProcess(config, new KbHandle("test", "C:\\fake\\path"));
        }

        private const long MB = 1024 * 1024;

        [Fact]
        public void IdleWorker_OverThreshold_Recycles()
        {
            var w = Make(1500);
            w.SetHeapProbeForTest(1600 * MB, DateTime.UtcNow.AddMinutes(-1)); // idle past grace, over ceiling
            Assert.True(w.ShouldRecycleForHeap(out var ws));
            Assert.Equal(1600 * MB, ws);
        }

        [Fact]
        public void IdleWorker_UnderThreshold_DoesNotRecycle()
        {
            var w = Make(1500);
            w.SetHeapProbeForTest(160 * MB, DateTime.UtcNow.AddMinutes(-1)); // realistic baseline
            Assert.False(w.ShouldRecycleForHeap(out _));
        }

        [Fact]
        public void OverThreshold_ButRecentlyActive_DoesNotRecycle()
        {
            var w = Make(1500);
            w.SetHeapProbeForTest(1600 * MB, DateTime.UtcNow); // within idle grace — a gap, not idle
            Assert.False(w.ShouldRecycleForHeap(out _));
        }

        [Fact]
        public void Disabled_WhenThresholdZero()
        {
            var w = Make(0);
            w.SetHeapProbeForTest(4000 * MB, DateTime.UtcNow.AddHours(-1));
            Assert.False(w.ShouldRecycleForHeap(out _));
        }

        [Fact]
        public void HeapRecycle_IsPlanned_NotCountedAsCrash()
        {
            Assert.False(CrashLedger.IsUnexpected(WorkerStopReason.HeapRecycle, 0));
        }
    }
}
