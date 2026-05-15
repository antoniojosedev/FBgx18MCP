using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 Task 3.3 — verify the {find, replace} JSON patch shape resolves
    // multi-line context across CRLF/LF mismatch by routing through
    // WriteService.TryMatch (the Task 3.1 helper). Pure-string tests; the
    // ApplyPatch live path still owns persistence + verification.
    public class PatchShapesTests
    {
        [Fact]
        public void FindReplace_Multiline_Crlf_Succeeds()
        {
            var src = "foo\r\nWhere x = 1\r\nWhere y = 2\r\nbar\r\n";
            var patch = new JObject
            {
                ["find"] = "Where x = 1\nWhere y = 2",
                ["replace"] = "Where x = 1 and y = 2"
            };
            var (ok, result, _) = PatchService.ApplyFindReplace(src, patch);
            Assert.True(ok);
            Assert.Contains("Where x = 1 and y = 2", result);
            Assert.DoesNotContain("Where x = 1\r\nWhere y = 2", result);
        }

        [Fact]
        public void FindReplace_SingleLine_LfSource_Succeeds()
        {
            var src = "alpha\nbeta\ngamma\n";
            var patch = new JObject { ["find"] = "beta", ["replace"] = "BETA" };
            var (ok, result, _) = PatchService.ApplyFindReplace(src, patch);
            Assert.True(ok);
            Assert.Equal("alpha\nBETA\ngamma\n", result);
        }

        [Fact]
        public void FindReplace_TrailingWhitespaceDifference_StillMatches()
        {
            var src = "alpha   \r\nbeta\r\n";
            var patch = new JObject { ["find"] = "alpha\nbeta", ["replace"] = "X" };
            var (ok, result, _) = PatchService.ApplyFindReplace(src, patch);
            Assert.True(ok);
            Assert.Contains("X", result);
            Assert.DoesNotContain("alpha", result);
        }

        [Fact]
        public void FindReplace_NoMatch_ReturnsFalseAndUnchangedSource()
        {
            var src = "alpha\nbeta\n";
            var patch = new JObject { ["find"] = "zeta", ["replace"] = "X" };
            var (ok, result, reason) = PatchService.ApplyFindReplace(src, patch);
            Assert.False(ok);
            Assert.Equal(src, result);
            Assert.Equal("NoMatch", reason);
        }

        [Fact]
        public void FindReplace_MissingFind_ReturnsFalse_NoCrash()
        {
            var src = "alpha";
            var patch = new JObject { ["replace"] = "X" };
            var (ok, result, _) = PatchService.ApplyFindReplace(src, patch);
            Assert.False(ok);
            Assert.Equal(src, result);
        }

        [Fact]
        public void FindReplace_NullPatch_ReturnsFalse_NoCrash()
        {
            var src = "alpha";
            var (ok, result, _) = PatchService.ApplyFindReplace(src, null);
            Assert.False(ok);
            Assert.Equal(src, result);
        }
    }
}
