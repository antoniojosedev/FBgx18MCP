using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class EditAndBuildOrchestratorTests
    {
        [Fact]
        public void Orchestrate_ReturnsCompositeEnvelope_WhenAllPhasesSucceed()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{
                ""status"": ""Ok"",
                ""diff"": ""@@ -1 +1 @@\n-old\n+new""
            }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{
                ""status"": ""Ready"",
                ""target"": ""InvoiceProc"",
                ""callers"": [""WebInvoice"", ""ReportInvoice""],
                ""callersTruncated"": false,
                ""riskLevel"": ""Low""
            }"));
            var fakeBuild = new FakeBuildService(JObject.Parse(@"{
                ""status"": ""Accepted"",
                ""taskId"": ""b1c2d3e4""
            }"));

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject
            {
                ["name"] = "InvoiceProc",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["content"] = "@@ -1 +1 @@\n-old\n+new",
                ["buildIncludeCallees"] = "direct"
            });

            var env = JObject.Parse(raw);
            Assert.Equal("Ok", env["status"]?.ToString());
            Assert.NotNull(env["edit"]);
            Assert.NotNull(env["impact"]);
            Assert.NotNull(env["build"]);
            Assert.Equal("b1c2d3e4", env["build"]?["taskId"]?.ToString());
            Assert.Equal(2, ((JArray)env["impact"]!["callers"]!).Count);
        }

        [Fact]
        public void Orchestrate_ShortCircuits_WhenEditFails()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{
                ""status"": ""Error"",
                ""error"": ""Ambiguous object name"",
                ""alternatives"": [
                    { ""name"": ""InvoiceProc"", ""type"": ""Procedure"" },
                    { ""name"": ""InvoiceProc"", ""type"": ""WebPanel"" }
                ]
            }"));
            var fakeAnalyze = new FakeAnalyzeService(null);
            var fakeBuild = new FakeBuildService(null);

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject { ["name"] = "InvoiceProc" });

            var env = JObject.Parse(raw);
            Assert.Equal("Error", env["status"]?.ToString());
            Assert.NotNull(env["alternatives"]);
            Assert.Null(env["impact"]);
            Assert.Null(env["build"]);
            Assert.False(fakeAnalyze.WasCalled);
            Assert.False(fakeBuild.WasCalled);
        }

        [Fact]
        public void Orchestrate_SkipsBuild_WhenImpactReportsNoCallers()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Ok"" }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{
                ""status"": ""Ready"",
                ""callers"": []
            }"));
            var fakeBuild = new FakeBuildService(null);

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject { ["name"] = "OrphanProc" });
            var env = JObject.Parse(raw);

            Assert.Equal("Ok", env["status"]?.ToString());
            Assert.NotNull(env["impact"]);
            Assert.NotNull(env["build"]);
            Assert.Equal("Skipped", env["build"]?["status"]?.ToString());
            Assert.False(fakeBuild.WasCalled);
        }
    }

    internal class FakeWriteService : IWriteServiceFacade
    {
        private readonly JObject _result;
        public bool WasCalled { get; private set; }
        public FakeWriteService(JObject result) { _result = result; }
        public string WriteObject(string target, JObject args)
        {
            WasCalled = true;
            return _result.ToString();
        }
    }
    internal class FakeAnalyzeService : IAnalyzeServiceFacade
    {
        private readonly JObject _result;
        public bool WasCalled { get; private set; }
        public FakeAnalyzeService(JObject result) { _result = result; }
        public string ImpactAnalysis(string target, bool waitForIndex, int waitTimeoutMs)
        {
            WasCalled = true;
            return _result == null ? "{}" : _result.ToString();
        }
    }
    internal class FakeBuildService : IBuildServiceFacade
    {
        private readonly JObject _result;
        public bool WasCalled { get; private set; }
        public FakeBuildService(JObject result) { _result = result; }
        public string Build(string action, string target, string includeCallees, int buildPlanCap)
        {
            WasCalled = true;
            return _result == null ? "{}" : _result.ToString();
        }
    }
}
