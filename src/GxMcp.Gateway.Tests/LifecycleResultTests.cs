using System;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Item #18 (v2.6.4): lifecycle action=result target=op:<id> consults the
    // JobRegistry first. v2.6.3 fixed the symmetric path for status/cancel but
    // left result forwarding to the worker, which returned "Task ID not found"
    // for completed background jobs. BuildJobResultEnvelope is the extracted
    // helper that powers the route; covered here in isolation.
    public class LifecycleResultTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EditResult_PreservesSaveEvidenceEvenWhenComparisonFails(bool matches)
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "edit/Source", 30);
            var stored = JObject.Parse("{sdkSaveCompleted:true,saved:true,persistedStateKnown:true,versionToken:'35',postSaveVerification:{representation:'genexus_read'},implicitLifecycleActions:[]}");
            stored["status"] = matches ? "ok" : "error";
            stored["code"] = matches ? "WriteApplied" : "WriteNotPersisted";
            stored["persisted"] = matches;
            stored["postSaveVerification"]!["matches"] = matches;
            registry.Complete(job.Id, success: matches, summary: "Source verification", result: stored);
            var (envelope, isError) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);
            Assert.Equal(!matches, isError);
            Assert.True(JToken.DeepEquals(stored, envelope["result"]));
        }

        // Issue #656: a terminal result must say which job it answers, and a payload that
        // names another Worker task must never be served as this job's output.
        [Fact]
        public void TerminalResult_EchoesTheJobItIsBoundTo()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            registry.SetWorkerTaskId(job.Id, "aaaa1111");
            registry.Complete(job.Id, success: true, summary: "Build OK",
                result: new JObject { ["Status"] = "Succeeded", ["TaskId"] = "aaaa1111", ["operationId"] = "aaaa1111" });

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.False(isErr);
            Assert.Equal(job.Id, env["jobId"]!.ToString());
            Assert.Equal("aaaa1111", env["workerTaskId"]!.ToString());
            Assert.Equal("Succeeded", env["result"]!["Status"]!.ToString());
        }

        [Fact]
        public void ResultNamingAnotherWorkerTask_IsRefusedInsteadOfServed()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            registry.SetWorkerTaskId(job.Id, "aaaa1111");
            registry.Complete(job.Id, success: false, summary: "Build Failed",
                result: new JObject { ["Status"] = "Failed", ["TaskId"] = "bbbb2222", ["Errors"] = new JArray("belongs to job B") });

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.True(isErr);
            Assert.Equal("ResultJobMismatch", env["code"]!.ToString());
            Assert.Equal(job.Id, env["jobId"]!.ToString());
            Assert.Null(env["result"]);
            Assert.DoesNotContain("belongs to job B", env.ToString());
        }

        [Theory]
        [InlineData("aaaa1111", "AAAA1111", true)]
        [InlineData("bbbb2222", "aaaa1111", false)]
        [InlineData(null, "aaaa1111", true)]
        public void ResultBelongsToWorkerTask_ComparesEveryIdentityTheWorkerStamps(string? stamped, string expected, bool belongs)
        {
            var payload = new JObject();
            if (stamped != null)
            {
                payload["TaskId"] = stamped;
                payload["operationId"] = stamped;
            }

            Assert.Equal(belongs, McpRouter.ResultBelongsToWorkerTask(payload, "job-gateway-id", expected));
        }

        [Fact]
        public void TruncatedErrorList_ReportsTotalShownAndOmitted()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            var shown = new JArray();
            for (int i = 0; i < 50; i++) shown.Add("error " + i);
            registry.Complete(job.Id, success: false, summary: "Build Failed: 80 errors",
                result: new JObject { ["Status"] = "Failed", ["ErrorCount"] = 80, ["Errors"] = shown, ["FullLogPath"] = "C:/logs/full.log" });

            var (env, _) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.Equal(80, env["totalErrors"]!.Value<int>());
            Assert.Equal(50, env["shownErrors"]!.Value<int>());
            Assert.Equal(30, env["omittedErrors"]!.Value<int>());
            Assert.True(env["truncated"]!.Value<bool>());
            Assert.Equal("C:/logs/full.log", env["fullLogPath"]!.ToString());
        }

        [Fact]
        public void CompleteErrorList_IsNotMarkedTruncated()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            registry.Complete(job.Id, success: false, summary: "Build Failed: 2 errors",
                result: new JObject { ["Status"] = "Failed", ["ErrorCount"] = 2, ["Errors"] = new JArray("e1", "e2") });

            var (env, _) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.Equal(2, env["totalErrors"]!.Value<int>());
            Assert.Equal(0, env["omittedErrors"]!.Value<int>());
            Assert.False(env["truncated"]!.Value<bool>());
        }

        [Fact]
        public void Running_ReturnsPendingEnvelope_WithoutIsError()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", estimatedSeconds: 60);

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(job);

            Assert.False(isErr, "running job must not be marked as error");
            Assert.Equal("Pending", env["status"]!.ToString());
            Assert.Equal(job.Id, env["operationId"]!.ToString());
            Assert.NotNull(env["message"]);
            Assert.NotNull(env["startedAt"]);
        }

        [Fact]
        public void Succeeded_SurfacesStoredResultAndIsErrorFalse()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            var stored = new JObject { ["Status"] = "OK", ["ErrorCount"] = 0 };
            registry.Complete(job.Id, success: true, summary: "Build OK", result: stored);

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.False(isErr);
            Assert.Equal("succeeded", env["status"]!.ToString());
            Assert.Equal(job.Id, env["operationId"]!.ToString());
            Assert.Equal("lifecycle/build", env["kind"]!.ToString());
            Assert.Equal("Build OK", env["summary"]!.ToString());
            Assert.NotNull(env["completedAt"]);
            Assert.NotNull(env["result"]);
            Assert.Equal(0, env["result"]!["ErrorCount"]!.ToObject<int>());
        }

        [Fact]
        public void Failed_MarksIsErrorTrue_AndSurfacesErrors()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            var stored = new JObject
            {
                ["Status"] = "Failed",
                ["ErrorCount"] = 6,
                ["Errors"] = new JArray("err1", "err2")
            };
            registry.Complete(job.Id, success: false, summary: "Build Failed: 6 errors", result: stored);

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.True(isErr, "failed job must propagate isError=true");
            Assert.Equal("failed", env["status"]!.ToString());
            Assert.Equal(6, env["result"]!["ErrorCount"]!.ToObject<int>());
            Assert.Contains("6 errors", env["summary"]!.ToString());
        }

        [Fact]
        public void Cancelled_MarksIsErrorTrue()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            registry.Cancel(job.Id, "Cancelled by client");

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.True(isErr);
            Assert.Equal("cancelled", env["status"]!.ToString());
            Assert.Equal("Cancelled by client", env["summary"]!.ToString());
        }

        [Fact]
        public void Completed_NullResult_StillReturnsTerminalEnvelopeWithoutResultKey()
        {
            // Some completion paths flag status without a structured result body.
            // The envelope must still be terminal and not crash on null.
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/edit", 30);
            registry.Complete(job.Id, success: true, summary: "ok", result: null);

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);

            Assert.False(isErr);
            Assert.Equal("succeeded", env["status"]!.ToString());
            // result is omitted when null — caller branches on status, not on result presence.
            Assert.True(env["result"] == null || env["result"]!.Type == JTokenType.Null);
        }

        [Fact]
        public void NullJob_Throws_GuardsAgainstCallerError()
        {
            Assert.Throws<ArgumentNullException>(() => McpRouter.BuildJobResultEnvelope(null!));
        }
    }
}
