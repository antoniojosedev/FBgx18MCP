using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #638 (minor point): add_grid_variable and move_grid_column required a baseVersion even for a dryRun, which is the call
    // that returns the token. A preview changes nothing, so it does not pin a version; a write still must.
    //
    // The decision is a pure function and is tested as one; the router needs a live SDK to run, so only its wiring (where the guard
    // sits, what it answers) is pinned in its source, the way the stale-object envelope is.
    public class WwpGridColumnVersionContractTests
    {
        private static string Router()
            => SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "WwpActionService.cs")));

        [Theory]
        [InlineData("add_grid_variable", null, null, true)]
        [InlineData("add_grid_variable", "", false, true)]
        [InlineData("move_grid_column", "  ", null, true)]
        [InlineData("add_grid_variable", null, true, false)]
        [InlineData("move_grid_column", null, true, false)]
        [InlineData("add_grid_variable", "639286164400000000:a:b", null, false)]
        [InlineData("add_grid", null, null, false)]
        public void AGridColumnWriteNeedsAVersionButAPreviewDoesNot(string operation, string version, bool? dryRun, bool lacksVersion)
        {
            var args = new JObject();
            if (dryRun.HasValue) args["dryRun"] = dryRun.Value;

            Assert.Equal(lacksVersion, WwpActionService.GridColumnWriteLacksVersion(operation, version, args));
        }

        [Fact]
        public void TheRouterRefusesAWriteWithoutAVersionBeforeAnyWork()
        {
            string router = Router();
            int guard = router.IndexOf("if (GridColumnWriteLacksVersion(operation, expectedVersion, args))", StringComparison.Ordinal);
            Assert.True(guard >= 0, "the grid-column version guard moved");

            string block = router.Substring(guard, 360);
            Assert.Contains("code: \"ExpectedVersionRequired\"", block);
            Assert.Contains("a dryRun preview returns the versionToken", block);
            Assert.Equal(1, SourceAssert.Count(router, "code: \"ExpectedVersionRequired\""));
        }

        [Fact]
        public void AddGridVariableIsRoutedToItsOwnWriterAfterTheGuard()
        {
            string router = Router();
            int guard = router.IndexOf("if (GridColumnWriteLacksVersion(operation, expectedVersion, args))", StringComparison.Ordinal);
            int route = router.IndexOf("return RunAddGridVariableOperation(target, requestedObject, instance, xml, versionToken, args);", StringComparison.Ordinal);
            int stale = router.IndexOf("if (!IsExpectedVersion(expectedVersion, versionToken))", StringComparison.Ordinal);

            Assert.True(guard >= 0 && route > guard, "add_grid_variable must be routed after the version guard");
            // The writer compares the version itself (before the preview and under the lock), so the generic compare comes after.
            Assert.True(stale > route, "the generic stale compare must not run before the add_grid_variable writer");
        }
    }
}
