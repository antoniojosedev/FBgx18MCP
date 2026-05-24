using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AutoTestServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public AutoTestServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-autotest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void Generate_MissingFile_ReturnsErrorEnvelope()
        {
            var svc = new AutoTestService();
            var bogus = Path.Combine(_tempDir, "does-not-exist.jsonl");
            var j = JObject.Parse(svc.Generate(bogus));
            Assert.Equal("Error", (string)j["status"]);
            Assert.Contains("does not exist", (string)j["message"]);
        }

        [Fact]
        public void Generate_EmptyPath_ReturnsErrorEnvelope()
        {
            var svc = new AutoTestService();
            var j = JObject.Parse(svc.Generate(""));
            Assert.Equal("Error", (string)j["status"]);
            Assert.Contains("path is required", (string)j["message"]);
        }

        [Fact]
        public void Generate_DeduplicatesByToolAndTarget()
        {
            var path = Path.Combine(_tempDir, "log.jsonl");
            File.WriteAllLines(path, new[]
            {
                "{\"tool\":\"genexus_query\",\"target\":\"X\",\"params\":{\"a\":1}}",
                "{\"tool\":\"genexus_query\",\"target\":\"X\",\"params\":{\"a\":2}}",
                "{\"tool\":\"genexus_query\",\"target\":\"Y\"}"
            });

            var svc = new AutoTestService();
            var j = JObject.Parse(svc.Generate(path));
            Assert.Equal("Success", (string)j["status"]);
            Assert.Equal(3, (int)j["linesRead"]);
            var stubs = (JArray)j["stubsGenerated"];
            Assert.Equal(2, stubs.Count);
            Assert.Empty((JArray)j["skipped"]);
        }

        [Fact]
        public void Generate_MalformedLineInMiddle_IsSkippedNotFatal()
        {
            var path = Path.Combine(_tempDir, "log.jsonl");
            File.WriteAllLines(path, new[]
            {
                "{\"tool\":\"genexus_query\",\"target\":\"A\"}",
                "this is not json {",
                "{\"tool\":\"genexus_query\",\"target\":\"B\"}"
            });

            var svc = new AutoTestService();
            var j = JObject.Parse(svc.Generate(path));
            Assert.Equal("Success", (string)j["status"]);

            var stubs = (JArray)j["stubsGenerated"];
            Assert.Equal(2, stubs.Count);

            var skipped = (JArray)j["skipped"];
            Assert.Single(skipped);
            Assert.Equal(2, (int)skipped[0]["line"]);
            Assert.Equal("malformed-json", (string)skipped[0]["reason"]);
        }
    }
}
