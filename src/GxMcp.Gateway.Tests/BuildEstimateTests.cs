using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Issue #27 item 2: the async-build path reported a flat estimated_seconds=60/120.
    // BackgroundJobRegistry now records observed build wall-clocks and returns a median
    // so the reported estimate tracks reality. Routing is unaffected (covered by the
    // caller-estimate-only sync gate in Program), so these focus on the estimator.
    public class BuildEstimateTests
    {
        [Fact]
        public void NoHistory_ReturnsNull()
        {
            var registry = new BackgroundJobRegistry();
            Assert.Null(registry.EstimateBuildSeconds("lifecycle/build"));
        }

        [Fact]
        public void Median_OfRecordedDurations()
        {
            var registry = new BackgroundJobRegistry();
            foreach (var s in new[] { 100, 200, 300 })
                registry.RecordBuildDuration("lifecycle/build", s);
            Assert.Equal(200, registry.EstimateBuildSeconds("lifecycle/build"));
        }

        [Fact]
        public void History_IsPerKind()
        {
            var registry = new BackgroundJobRegistry();
            registry.RecordBuildDuration("lifecycle/build", 40);
            registry.RecordBuildDuration("lifecycle/rebuild", 400);
            Assert.Equal(40, registry.EstimateBuildSeconds("lifecycle/build"));
            Assert.Equal(400, registry.EstimateBuildSeconds("lifecycle/rebuild"));
        }

        [Fact]
        public void Estimate_IsClamped()
        {
            var registry = new BackgroundJobRegistry();
            registry.RecordBuildDuration("lifecycle/build", 999999);
            Assert.Equal(1800, registry.EstimateBuildSeconds("lifecycle/build"));
        }

        [Fact]
        public void InvalidSamples_Ignored()
        {
            var registry = new BackgroundJobRegistry();
            registry.RecordBuildDuration("lifecycle/build", 0);
            registry.RecordBuildDuration("lifecycle/build", -5);
            Assert.Null(registry.EstimateBuildSeconds("lifecycle/build"));
        }

        [Fact]
        public void Complete_RecordsSuccessfulBuildDuration()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("sess", "lifecycle/build", 60);
            // Backdate so the recorded duration is a stable, positive value.
            job.StartedAt = job.StartedAt.AddSeconds(-90);
            registry.Complete(job.Id, success: true, summary: "ok", result: null);

            var est = registry.EstimateBuildSeconds("lifecycle/build");
            Assert.NotNull(est);
            Assert.InRange(est!.Value, 80, 100);
        }

        [Fact]
        public void Complete_DoesNotRecordFailedBuild()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("sess", "lifecycle/build", 60);
            job.StartedAt = job.StartedAt.AddSeconds(-90);
            registry.Complete(job.Id, success: false, summary: "failed", result: null);
            Assert.Null(registry.EstimateBuildSeconds("lifecycle/build"));
        }

        [Fact]
        public void Complete_DoesNotRecordNonBuildKind()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("sess", "lifecycle/edit", 30);
            job.StartedAt = job.StartedAt.AddSeconds(-30);
            registry.Complete(job.Id, success: true, summary: "ok", result: null);
            Assert.Null(registry.EstimateBuildSeconds("lifecycle/edit"));
        }
    }
}
