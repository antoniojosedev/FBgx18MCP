using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Tests for the McpRouter.PiggybackJobs helper that injects _meta.background_jobs
    /// into the inner content[0].text JSON of every tools/call response when the session
    /// has active or unseen-completed jobs.
    ///
    /// The toolResult shape mirrors the real MCP wrapper:
    ///   { isError: bool, content: [{ type: "text", text: "<serialized-json>" }] }
    /// PiggybackJobs must parse content[0].text, inject _meta.background_jobs, and
    /// re-serialize — so the LLM (which reads the inner text) actually sees the payload.
    /// </summary>
    public class PiggybackTests
    {
        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a minimal tool-result wrapper with a JSON payload as content[0].text.
        /// </summary>
        private static JObject MakeWrapper(JObject innerPayload)
        {
            return new JObject
            {
                ["isError"] = false,
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = innerPayload.ToString(Newtonsoft.Json.Formatting.None)
                    }
                }
            };
        }

        /// <summary>
        /// Parses and returns the inner JSON from content[0].text of a wrapper.
        /// </summary>
        private static JObject ParseInner(JObject wrapper)
        {
            var text = wrapper["content"]?[0]?["text"]?.ToString() ?? "{}";
            return JObject.Parse(text);
        }

        // ── tests ─────────────────────────────────────────────────────────────────

        [Fact]
        public void BackgroundJobs_AppendedToInnerMeta_WhenSnapshotHasEntries()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("s1", "build", 30);
            registry.Complete(job.Id, true, "done");

            var wrapper = MakeWrapper(new JObject { ["result"] = "ok" });
            McpRouter.PiggybackJobs(wrapper, "s1", registry);

            var inner = ParseInner(wrapper);
            var jobs = (JArray?)inner["_meta"]?["background_jobs"];
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
            var wrapper = MakeWrapper(new JObject { ["result"] = "ok" });

            McpRouter.PiggybackJobs(wrapper, "s1", registry);

            // Wrapper-level _meta must not be set
            Assert.False(wrapper.ContainsKey("_meta"));
            // Inner text must also not have _meta (it was never touched)
            var inner = ParseInner(wrapper);
            Assert.False(inner.ContainsKey("_meta"));
        }

        [Fact]
        public void MarkSeen_PreventsSecondAppearanceOfCompleted()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("s1", "build", 30);
            registry.Complete(job.Id, true, "ok");

            // First call: completed job appears in inner text
            var first = MakeWrapper(new JObject { ["result"] = "a" });
            McpRouter.PiggybackJobs(first, "s1", registry);
            var firstInner = ParseInner(first);
            var firstJobs = (JArray?)firstInner["_meta"]?["background_jobs"];
            Assert.NotNull(firstJobs);
            Assert.Single(firstJobs);

            // Second call: completed job was marked seen, must not appear again
            var second = MakeWrapper(new JObject { ["result"] = "b" });
            McpRouter.PiggybackJobs(second, "s1", registry);
            var secondInner = ParseInner(second);
            Assert.False(secondInner.ContainsKey("_meta"));
        }

        [Fact]
        public void RunningJob_AppearsRepeatedly_UntilCompleted()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("s1", "build", 30);

            // Running job appears on every snapshot call regardless of MarkSeen
            var first = MakeWrapper(new JObject { ["result"] = "x" });
            McpRouter.PiggybackJobs(first, "s1", registry);
            var firstInner = ParseInner(first);
            Assert.Equal("running", ((JArray)firstInner["_meta"]!["background_jobs"]!)[0]["status"]?.ToString());

            // Still running after second call
            var second = MakeWrapper(new JObject { ["result"] = "y" });
            McpRouter.PiggybackJobs(second, "s1", registry);
            var secondInner = ParseInner(second);
            Assert.NotNull(secondInner["_meta"]?["background_jobs"]);
        }

        [Fact]
        public void ExistingMeta_IsPreserved_WhenJobsAttached()
        {
            var registry = new BackgroundJobRegistry(600);
            registry.Start("s1", "build", 30);

            var inner = new JObject
            {
                ["result"] = "value",
                ["_meta"] = new JObject { ["other_field"] = "keep_me" }
            };
            var wrapper = MakeWrapper(inner);

            McpRouter.PiggybackJobs(wrapper, "s1", registry);

            var resultInner = ParseInner(wrapper);
            Assert.Equal("keep_me", resultInner["_meta"]?["other_field"]?.ToString());
            Assert.NotNull(resultInner["_meta"]?["background_jobs"]);
        }

        [Fact]
        public void NonJsonContent_IsSkippedGracefully()
        {
            // If content[0].text is not valid JSON, PiggybackJobs must not throw.
            var registry = new BackgroundJobRegistry(600);
            registry.Start("s1", "build", 30);

            var wrapper = new JObject
            {
                ["isError"] = false,
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "plain text, not JSON"
                    }
                }
            };

            // Must not throw
            McpRouter.PiggybackJobs(wrapper, "s1", registry);

            // Text must be unchanged
            Assert.Equal("plain text, not JSON", wrapper["content"]?[0]?["text"]?.ToString());
        }
    }
}
