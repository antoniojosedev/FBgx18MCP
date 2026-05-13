using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Tests for the McpRouter.PiggybackJobs helper that injects _meta.background_jobs
    /// into every tools/call response when the session has active or unseen-completed jobs.
    /// The wiring from ProcessMcpRequest to PiggybackJobs is not tested end-to-end here
    /// because it requires a live HTTP stack + worker process. The BackgroundJobRegistry
    /// contract tests (BackgroundJobRegistryTests) provide complementary coverage.
    /// </summary>
    public class PiggybackTests
    {
        [Fact]
        public void BackgroundJobs_AppendedToMeta_WhenSnapshotHasEntries()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("s1", "build", 30);
            registry.Complete(job.Id, true, "done");

            var result = new JObject();
            McpRouter.PiggybackJobs(result, "s1", registry);

            var jobs = (JArray?)result["_meta"]?["background_jobs"];
            Assert.NotNull(jobs);
            Assert.Single(jobs);
            Assert.Equal(job.Id, jobs![0]["id"]?.ToString());
            Assert.Equal("succeeded", jobs[0]["status"]?.ToString());
            Assert.Equal("done", jobs[0]["summary"]?.ToString());
        }

        [Fact]
        public void BackgroundJobs_AbsentWhenNoJobs()
        {
            var registry = new BackgroundJobRegistry(600);
            var result = new JObject();

            McpRouter.PiggybackJobs(result, "s1", registry);

            Assert.False(result.ContainsKey("_meta"));
        }

        [Fact]
        public void MarkSeen_PreventsSecondAppearanceOfCompleted()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("s1", "build", 30);
            registry.Complete(job.Id, true, "ok");

            // First call: completed job appears
            var first = new JObject();
            McpRouter.PiggybackJobs(first, "s1", registry);
            var firstJobs = (JArray?)first["_meta"]?["background_jobs"];
            Assert.NotNull(firstJobs);
            Assert.Single(firstJobs);

            // Second call: completed job was marked seen, must not appear again
            var second = new JObject();
            McpRouter.PiggybackJobs(second, "s1", registry);
            Assert.False(second.ContainsKey("_meta"));
        }

        [Fact]
        public void RunningJob_AppearsRepeatedly_UntilCompleted()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("s1", "build", 30);

            // Running job appears on every snapshot call regardless of MarkSeen
            var first = new JObject();
            McpRouter.PiggybackJobs(first, "s1", registry);
            Assert.Equal("running", ((JArray)first["_meta"]!["background_jobs"]!)[0]["status"]?.ToString());

            // Still running after second call
            var second = new JObject();
            McpRouter.PiggybackJobs(second, "s1", registry);
            Assert.NotNull(second["_meta"]?["background_jobs"]);
        }

        [Fact]
        public void ExistingMeta_IsPreserved_WhenJobsAttached()
        {
            var registry = new BackgroundJobRegistry(600);
            registry.Start("s1", "build", 30);

            var result = new JObject
            {
                ["_meta"] = new JObject { ["other_field"] = "keep_me" }
            };

            McpRouter.PiggybackJobs(result, "s1", registry);

            Assert.Equal("keep_me", result["_meta"]?["other_field"]?.ToString());
            Assert.NotNull(result["_meta"]?["background_jobs"]);
        }
    }
}
