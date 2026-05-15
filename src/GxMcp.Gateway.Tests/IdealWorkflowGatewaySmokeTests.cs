using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.3.8 (Task 8.1 — gateway half). The worker-side smoke lives in
    // IdealWorkflowSmokeTest under GxMcp.Worker.Tests; this exercises the
    // Gateway-side composition (compact shaper + Piggyback dedup + cancel)
    // that crosses the net8 boundary.
    public class IdealWorkflowGatewaySmokeTests
    {
        [Fact]
        public void EndToEnd_GatewayPieces_Compose()
        {
            // ── 1. Compact shaper applied to a verbose BuildTaskStatus ──────
            var verboseStatus = new JObject
            {
                ["Status"] = "Failed",
                ["ErrorCount"] = 30,
                ["WarningCount"] = 6,
                ["Errors"] = new JArray(Enumerable.Range(0, 30).Select(i => (JToken)new JObject { ["message"] = "err" + i })),
                ["Warnings"] = new JArray(Enumerable.Range(0, 6).Select(_ => (JToken)new JObject { ["message"] = "GAM" })),
                ["Output"] = new string('x', 8000),
                ["TaskId"] = "t"
            }.ToString();
            var compact = JObject.Parse(LifecycleResponseShaper.Compact(verboseStatus, compact: true));
            Assert.Null(compact["Output"]);
            Assert.Equal(30, compact["errorCount"]!.Value<int>());
            Assert.Single((JArray)compact["warnings"]!); // 6 duplicates collapsed
            Assert.Equal(6, compact["warnings"]![0]!["count"]!.Value<int>());

            // ── 2. JobRegistry cancel + CTS signalling ──────────────────────
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("sess", "build", 30);
            var ct = registry.RegisterCancellation(job.Id);
            Assert.True(registry.Cancel(job.Id, "smoke"));
            Assert.True(ct.IsCancellationRequested);
            Assert.Equal("cancelled", registry.Get(job.Id)!.Status);

            // ── 3. Piggyback dedup across two consecutive tool calls ────────
            var jobB = registry.Start("sessB", "edit", 30);
            registry.Complete(jobB.Id, success: true, summary: "edit applied");
            var first = new JObject
            {
                ["isError"] = false,
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "{}" } }
            };
            McpRouter.PiggybackJobs(first, "sessB", registry);
            var second = new JObject
            {
                ["isError"] = false,
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "{}" } }
            };
            McpRouter.PiggybackJobs(second, "sessB", registry);
            var firstInner = JObject.Parse(((JArray)first["content"]!)[0]!["text"]!.ToString());
            var secondInner = JObject.Parse(((JArray)second["content"]!)[0]!["text"]!.ToString());
            Assert.NotNull(firstInner["_meta"]?["background_jobs"]);
            Assert.Null(secondInner["_meta"]); // dedup'd on second call
        }
    }
}
