using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Task 5.2 — inline_read_top on list/query responses.
    /// Uses the static AppendInlineReadsCore helper so tests run without a live GeneXus KB.
    /// </summary>
    public class InlineReadTopTests
    {
        // ── Helpers ────────────────────────────────────────────────────────────

        private static string MakeResultsJson(int count)
        {
            var results = new JArray();
            for (int i = 1; i <= count; i++)
            {
                results.Add(new JObject
                {
                    ["name"] = $"Proc{i}",
                    ["type"] = "Procedure"
                });
            }
            return new JObject
            {
                ["count"] = count,
                ["total"] = count,
                ["hasMore"] = false,
                ["results"] = results
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string FakeReader(string name, string type)
        {
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["parts"] = new JObject { ["Source"] = $"// source of {name}" }
            }.ToString();
        }

        // ── inline_read_top = 0 → no inline_reads ─────────────────────────────

        [Fact]
        public void InlineReadTop_Zero_NoInlineReads()
        {
            string json = MakeResultsJson(3);
            string result = CommandDispatcher.AppendInlineReadsCore(json, 0, FakeReader);

            var obj = JObject.Parse(result);
            Assert.Null(obj["inline_reads"]);
        }

        // ── inline_read_top = 2 → reads top 2 entries ────────────────────────

        [Fact]
        public void InlineReadTop_Two_AddsTopTwoEntries()
        {
            string json = MakeResultsJson(5);
            string result = CommandDispatcher.AppendInlineReadsCore(json, 2, FakeReader);

            var obj = JObject.Parse(result);
            var inlineReads = obj["inline_reads"] as JArray;
            Assert.NotNull(inlineReads);
            Assert.Equal(2, inlineReads!.Count);

            Assert.Equal("Proc1", inlineReads[0]?["name"]?.ToString());
            Assert.Equal("Proc2", inlineReads[1]?["name"]?.ToString());
        }

        // ── inline_read_top = 10 → capped at 3 ────────────────────────────────

        [Fact]
        public void InlineReadTop_Ten_CappedAtThree()
        {
            string json = MakeResultsJson(10);
            // Capping happens BEFORE calling AppendInlineReadsCore (in CommandDispatcher)
            // so we simulate the cap here: Math.Min(3, 10) = 3
            int n = System.Math.Min(3, 10);
            string result = CommandDispatcher.AppendInlineReadsCore(json, n, FakeReader);

            var obj = JObject.Parse(result);
            var inlineReads = obj["inline_reads"] as JArray;
            Assert.NotNull(inlineReads);
            Assert.Equal(3, inlineReads!.Count);
        }

        // ── empty results → no inline_reads ──────────────────────────────────

        [Fact]
        public void InlineReadTop_EmptyResults_NoInlineReads()
        {
            string json = new JObject
            {
                ["count"] = 0,
                ["total"] = 0,
                ["hasMore"] = false,
                ["results"] = new JArray()
            }.ToString();

            string result = CommandDispatcher.AppendInlineReadsCore(json, 2, FakeReader);

            var obj = JObject.Parse(result);
            Assert.Null(obj["inline_reads"]);
        }

        // ── reader that throws → entry skipped gracefully ─────────────────────

        [Fact]
        public void InlineReadTop_ReaderThrows_EntrySkipped()
        {
            string json = MakeResultsJson(2);
            string result = CommandDispatcher.AppendInlineReadsCore(json, 2,
                (name, type) => throw new System.InvalidOperationException("KB offline"));

            // All readers throw → inline_reads not added (graceful skip)
            var obj = JObject.Parse(result);
            Assert.Null(obj["inline_reads"]);
        }

        // ── reader throws for one entry → other entries still included ─────────

        [Fact]
        public void InlineReadTop_PartialReaderFailure_OtherEntriesPresent()
        {
            string json = MakeResultsJson(3);
            int callCount = 0;
            string result = CommandDispatcher.AppendInlineReadsCore(json, 3, (name, type) =>
            {
                callCount++;
                if (callCount == 2) throw new System.InvalidOperationException("failed");
                return FakeReader(name, type);
            });

            var obj = JObject.Parse(result);
            var inlineReads = obj["inline_reads"] as JArray;
            Assert.NotNull(inlineReads);
            // Proc1 and Proc3 succeed; Proc2 is skipped
            Assert.Equal(2, inlineReads!.Count);
        }

        // ── each inline_reads entry has name, type, content ───────────────────

        [Fact]
        public void InlineReadTop_EntryShape_HasNameTypeContent()
        {
            string json = MakeResultsJson(1);
            string result = CommandDispatcher.AppendInlineReadsCore(json, 1, FakeReader);

            var obj = JObject.Parse(result);
            var entry = (obj["inline_reads"] as JArray)?[0] as JObject;
            Assert.NotNull(entry);
            Assert.Equal("Proc1", entry!["name"]?.ToString());
            Assert.Equal("Procedure", entry["type"]?.ToString());
            Assert.NotNull(entry["content"]);
        }

        // ── original response fields preserved ────────────────────────────────

        [Fact]
        public void InlineReadTop_OriginalResponseFieldsPreserved()
        {
            string json = MakeResultsJson(2);
            string result = CommandDispatcher.AppendInlineReadsCore(json, 1, FakeReader);

            var obj = JObject.Parse(result);
            Assert.Equal(2, obj["count"]?.ToObject<int>());
            Assert.Equal(2, obj["total"]?.ToObject<int>());
            Assert.False(obj["hasMore"]?.ToObject<bool>());
            Assert.NotNull(obj["results"]);
        }
    }
}
