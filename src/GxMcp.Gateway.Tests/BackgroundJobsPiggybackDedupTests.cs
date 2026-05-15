using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.3.8 (Task 6.3) — friction report 2026-05-15 #14: a completed
    // background job was surfacing in _meta.background_jobs on consecutive
    // tool calls, doubling the apparent count. The registry already tracks
    // per-session seen IDs (SnapshotForSession + MarkSeen); this test pins
    // the end-to-end Piggyback path so the dedup contract doesn't regress.
    public class BackgroundJobsPiggybackDedupTests
    {
        private static JObject MakeToolResult()
        {
            return new JObject
            {
                ["isError"] = false,
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "{}" } }
            };
        }

        private static JArray? ReadBgJobs(JObject toolResult)
        {
            var content = toolResult["content"] as JArray;
            var first = content?[0] as JObject;
            var text = first?["text"]?.ToString();
            if (string.IsNullOrEmpty(text)) return null;
            var inner = JObject.Parse(text!);
            return inner["_meta"]?["background_jobs"] as JArray;
        }

        [Fact]
        public void CompletedJob_AppearsOnlyOnce_AcrossTwoToolCalls()
        {
            var reg = new BackgroundJobRegistry(600);
            var j = reg.Start("session-A", "edit/genexus_edit", 30);
            reg.Complete(j.Id, true, "edit applied");

            var first = MakeToolResult();
            McpRouter.PiggybackJobs(first, "session-A", reg);
            var second = MakeToolResult();
            McpRouter.PiggybackJobs(second, "session-A", reg);

            var firstJobs = ReadBgJobs(first);
            var secondJobs = ReadBgJobs(second);
            Assert.NotNull(firstJobs);
            Assert.Single(firstJobs!);
            Assert.Null(secondJobs); // second call: no _meta.background_jobs at all
        }

        [Fact]
        public void RunningJob_AppearsOnEveryCall_UntilCompleted()
        {
            var reg = new BackgroundJobRegistry(600);
            reg.Start("session-B", "build", 60);

            var first = MakeToolResult();
            McpRouter.PiggybackJobs(first, "session-B", reg);
            var second = MakeToolResult();
            McpRouter.PiggybackJobs(second, "session-B", reg);

            Assert.Single(ReadBgJobs(first)!);
            Assert.Single(ReadBgJobs(second)!);
        }

        [Fact]
        public void Sessions_DoNotCrossContaminate()
        {
            var reg = new BackgroundJobRegistry(600);
            var jA = reg.Start("session-A", "edit", 10);
            reg.Complete(jA.Id, true, "ok");

            var b = MakeToolResult();
            McpRouter.PiggybackJobs(b, "session-B", reg);
            Assert.Null(ReadBgJobs(b)); // session B should not see session A's job
        }
    }
}
