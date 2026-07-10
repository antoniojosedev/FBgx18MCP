using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #31.3: the persisted snippet must show the edited region, not just the first
    // ~10 lines. Small parts return in full; large parts window on the changed line.
    public class PersistedSnippetTests
    {
        [Fact]
        public void ExtractSnippet_CentersOnEditLine_ShowsEditedRegion()
        {
            // A 15-line SDTStructure edited at line 12: with the edit line as hint the
            // snippet must include it (the old default lineHint=0 cut it off). issue #31.3.
            var lines = new string[15];
            for (int i = 0; i < 15; i++) lines[i] = "line" + i;
            string src = string.Join("\n", lines);

            string atEdit = WriteService.ExtractSnippet(src, 12, 10);
            Assert.Contains("line12", atEdit);

            string atZero = WriteService.ExtractSnippet(src, 0, 10);
            Assert.DoesNotContain("line12", atZero); // old default missed it — why FirstDiffLine matters
        }

        [Fact]
        public void ExtractSnippet_LargeSource_WindowsAroundEditLine()
        {
            var lines = new string[200];
            for (int i = 0; i < 200; i++) lines[i] = "line" + i;
            string src = string.Join("\n", lines);

            string snip = WriteService.ExtractSnippet(src, 150, 5);

            Assert.Contains("line150", snip);
            Assert.DoesNotContain("line0\n", snip);
        }

        [Theory]
        [InlineData("a\nb\nc", "a\nb\nc", 0)]      // identical
        [InlineData("a\nb\nc", "a\nX\nc", 1)]      // middle line changed
        [InlineData("a\nb", "a\nb\nc", 2)]         // appended line
        public void FirstDiffLine_FindsChangedLine(string before, string after, int expected)
        {
            Assert.Equal(expected, WriteService.FirstDiffLine(before, after));
        }
    }
}
