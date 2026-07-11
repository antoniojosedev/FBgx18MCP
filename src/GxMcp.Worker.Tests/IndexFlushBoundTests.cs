using System;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 001 — regression test that pins how many times IndexCacheService actually
    // re-serializes the on-disk index. IndexCacheService.cs:885-887 documents a past
    // regression (261 unthrottled full serializations, 12MB→45MB) whose only guard is
    // ScheduleThrottledFlush's runtime time-check. This test observes real write counts
    // (via the test-only FlushWriteCountForTest counter) under a burst of dirty updates,
    // so Plans 003/004 can't silently reintroduce the "full re-serialize per tick" cost.
    public class IndexFlushBoundTests
    {
        private static string UniqueKbPath() =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp-flushtest-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void FlushCount_StaysBounded_UnderBurstOfDirtyUpdates()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                // Large throttle window so the immediate-flush branch in
                // ScheduleThrottledFlush is never taken — everything routes through the
                // trailing-edge timer, which is the coalescing path under test.
                cache.SetFlushThrottleForTest(3600);
                cache.ResetFlushWriteCountForTest();

                for (int i = 0; i < 200; i++)
                {
                    cache.MarkDirty();
                    cache.ScheduleThrottledFlush();
                }

                // The throttle must coalesce a burst of 200 dirty ticks into at most a
                // couple of real writes, not one write per call.
                Assert.True(cache.FlushWriteCountForTest <= 2,
                    $"Expected flush writes to be coalesced (<=2), got {cache.FlushWriteCountForTest}");
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void FlushNow_Certifies_AllDirtyState()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "Foo", Type = "Procedure", Guid = Guid.NewGuid().ToString() }
                });

                cache.SetFlushThrottleForTest(0);
                cache.FlushNow();

                Assert.True(cache.IsFullyFlushed);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }
    }
}
