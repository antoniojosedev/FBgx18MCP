using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class EditPostStateTests
    {
        [Fact]
        public void UnifiedDiff_DetectsChangedLine()
        {
            var diff = DiffBuilder.UnifiedDiff("a\nb\nc", "a\nB!\nc", 3);
            Assert.Contains("-b", diff);
            Assert.Contains("+B!", diff);
            Assert.Contains("@@", diff);
        }

        [Fact]
        public void UnifiedDiff_NoChange_Empty()
        {
            var diff = DiffBuilder.UnifiedDiff("a\nb", "a\nb", 3);
            Assert.True(string.IsNullOrEmpty(diff));
        }

        [Fact]
        public void UnifiedDiff_AddedLine_ShowsPlus()
        {
            var diff = DiffBuilder.UnifiedDiff("a\nb", "a\nb\nc", 3);
            Assert.Contains("+c", diff);
            Assert.Contains("@@", diff);
        }

        [Fact]
        public void UnifiedDiff_RemovedLine_ShowsMinus()
        {
            var diff = DiffBuilder.UnifiedDiff("a\nb\nc", "a\nc", 3);
            Assert.Contains("-b", diff);
            Assert.Contains("@@", diff);
        }

        [Fact]
        public void UnifiedDiff_ContextLines_IncludesUnchangedNeighbors()
        {
            // 10-line file; change line 5 only; context=2 should show lines 3-7
            string before = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10";
            string after  = "1\n2\n3\n4\nX\n6\n7\n8\n9\n10";
            var diff = DiffBuilder.UnifiedDiff(before, after, context: 2);
            Assert.Contains("-5", diff);
            Assert.Contains("+X", diff);
            // context lines 3,4,6,7 should appear
            Assert.Contains(" 3", diff);
            Assert.Contains(" 7", diff);
            // lines 1,2 and 8,9,10 should NOT appear (outside context)
            Assert.DoesNotContain(" 1\n", diff);
            Assert.DoesNotContain(" 10\n", diff);
        }

        [Fact]
        public void BuildPostState_DefaultHasDiffNoSlices()
        {
            var ps = JsonPatchService.BuildPostState("a", "b", verbose: false);
            Assert.NotNull(ps["diff"]);
            Assert.Null(ps["slices"]);
        }

        [Fact]
        public void BuildPostState_VerboseHasSlices()
        {
            var ps = JsonPatchService.BuildPostState("a", "b", verbose: true);
            Assert.NotNull(ps["slices"]);
            Assert.True(ps["slices"] is JArray);
        }

        [Fact]
        public void BuildPostState_NoDiff_WhenIdentical()
        {
            var ps = JsonPatchService.BuildPostState("same\ncontent", "same\ncontent", verbose: false);
            Assert.Equal("", ps["diff"]?.ToString());
        }

        [Fact]
        public void BuildPostState_DiffContainsChangedLines()
        {
            var ps = JsonPatchService.BuildPostState("hello\nworld", "hello\nearth", verbose: false);
            string diff = ps["diff"]?.ToString() ?? "";
            Assert.Contains("-world", diff);
            Assert.Contains("+earth", diff);
        }
    }
}
