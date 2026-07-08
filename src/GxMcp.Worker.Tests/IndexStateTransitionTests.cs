using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IndexStateTransitionTests
    {
        private static IndexCacheService NewService()
        {
            return new IndexCacheService();
        }

        [Fact]
        public void WaitStateSignal_ReturnsTrue_WhenTransitionFiresAfterArm()
        {
            // issue #25 #1: a lifecycle status wait must wake the moment the index
            // state transitions. Arm, transition, then wait → already signalled.
            var cache = NewService();
            cache.ArmStateSignal();
            cache.MarkUltraLiteReady(10);
            Assert.True(cache.WaitStateSignal(2000));
        }

        [Fact]
        public void WaitStateSignal_TimesOut_WhenNoTransition()
        {
            var cache = NewService();
            cache.ArmStateSignal();
            Assert.False(cache.WaitStateSignal(30));
        }

        [Fact]
        public void MarkEnrichmentProgress_SetsFraction_WhileEnriching()
        {
            // issue #25 follow-up (P3): Enriching phase now carries a progress fraction.
            var cache = NewService();
            cache.MarkLitePassComplete(100);
            cache.MarkEnrichmentStarted();
            cache.MarkEnrichmentProgress(25, 100);
            var st = cache.GetState();
            Assert.Equal("Enriching", st.Status);
            Assert.NotNull(st.Progress);
            Assert.Equal(0.25, st.Progress.Value, 3);
        }

        [Fact]
        public void MarkEnrichmentProgress_Ignored_WhenNotEnriching()
        {
            var cache = NewService();
            cache.MarkIndexComplete(100); // Ready
            cache.MarkEnrichmentProgress(25, 100);
            // Must not clobber the Ready state's null progress with a stale fraction.
            Assert.Equal("Ready", cache.GetState().Status);
        }

        [Fact]
        public void MarkLitePassComplete_TransitionsToLiteReady()
        {
            var cache = NewService();
            cache.MarkReindexStarted(100);
            cache.MarkLitePassComplete(100);

            Assert.Equal("LiteReady", cache.GetState().Status);
        }

        [Fact]
        public void MarkEnrichmentStarted_TransitionsToEnriching()
        {
            var cache = NewService();
            cache.MarkLitePassComplete(100);
            cache.MarkEnrichmentStarted();

            Assert.Equal("Enriching", cache.GetState().Status);
        }

        [Fact]
        public void MarkIndexComplete_FromEnriching_TransitionsToReady()
        {
            var cache = NewService();
            cache.MarkLitePassComplete(100);
            cache.MarkEnrichmentStarted();
            cache.MarkIndexComplete(100);

            Assert.Equal("Ready", cache.GetState().Status);
        }
    }
}
