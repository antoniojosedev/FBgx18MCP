using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PrDescriptionServiceTests
    {
        [Fact]
        public void ParseCommits_HandlesTabSeparatedAndRecordSeparator()
        {
            // hash<tab>subject<tab>body<RS>; use  (RS) — \x1e is variable-length hex
            // and would greedily eat subsequent hex digits.
            string raw = "abc1234\tfeat: add foo\tWhy: neededaaa5678\tfix(mcp): bar\t";
            var list = PrDescriptionService.ParseCommits(raw);

            Assert.Equal(2, list.Count);
            Assert.Equal("abc1234", list[0].hash);
            Assert.Equal("feat: add foo", list[0].subject);
            Assert.Equal("Why: needed", list[0].body);
            Assert.Equal("fix(mcp): bar", list[1].subject);
        }

        [Fact]
        public void BuildEnvelope_ProducesTitleSummaryWhyChanges()
        {
            var commits = new List<(string, string, string)>
            {
                ("hash0001", "feat(mcp): add ocr stub", "Wires the heavyweight integration as a stub."),
                ("hash0002", "feat(mcp): pr description", ""),
                ("hash0003", "test(mcp): bump budget", "")
            };
            var env = PrDescriptionService.BuildEnvelope(commits);

            Assert.NotNull(env["title"]);
            Assert.Contains("feat", (string)env["title"]!);
            var changes = (JArray)env["changes"]!;
            Assert.Equal(3, changes.Count);
            Assert.Equal("feat", (string)((JObject)changes[0])["type"]!);
            Assert.Equal("test", (string)((JObject)changes[2])["type"]!);
            Assert.Equal("Wires the heavyweight integration as a stub.", (string)env["why"]!);
            Assert.True(((JArray)env["summary"]!).Count >= 3);
        }

        [Fact]
        public void BuildEnvelope_EmptyCommitList_GracefullyDegrades()
        {
            var env = PrDescriptionService.BuildEnvelope(new List<(string, string, string)>());
            Assert.Equal("(no commits)", (string)env["title"]!);
            Assert.Empty((JArray)env["changes"]!);
        }
    }
}
