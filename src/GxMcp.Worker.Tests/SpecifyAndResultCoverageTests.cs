using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// #654: <c>specify</c> validates the environment's copy of an object, which only a build
    /// refreshes, and its response never said so. The #656 error-count fields of action=result
    /// are the Gateway's (totalErrors/shownErrors/omittedErrors, LifecycleResultTests).
    /// </summary>
    public class SpecifyAndResultCoverageTests
    {
        private const string StaleCopyLine =
            "warning : changed since the last build: SyntheticProc. GeneXus specifies the environment's copy of an object, which only a build refreshes, so this result reflects the source as of the last build.";

        private static void HandleLine(BuildService.BuildTaskStatus status, string line)
        {
            var method = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(new BuildService(), new object[] { status, line, false });
        }

        [Fact]
        public void Specify_StaleCopyWarning_IsFlaggedOnTheStatus()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "specify-stale", SpecifyOnly = true, ValidatedSource = "environment-copy" };

            HandleLine(status, StaleCopyLine);

            Assert.True(status.StaleEnvironmentCopy);
            Assert.Contains("action=build", status.Hint);
            Assert.Single(status.Warnings);

            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Equal("environment-copy", (string)json["validatedSource"]);
            Assert.True((bool)json["staleEnvironmentCopy"]);
        }

        [Fact]
        public void Specify_OtherWarning_DoesNotFlagTheStatus()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "specify-clean", SpecifyOnly = true, ValidatedSource = "environment-copy" };

            HandleLine(status, "warning : spc0001 something unrelated");

            Assert.Null(status.StaleEnvironmentCopy);
            Assert.Null(status.Hint);
            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Null(json["staleEnvironmentCopy"]);
        }

        [Fact]
        public void Build_WithTheSameLine_IsNotAffected()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "build" };

            HandleLine(status, StaleCopyLine);

            Assert.Null(status.StaleEnvironmentCopy);
            Assert.Null(status.ValidatedSource);
            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Null(json["validatedSource"]);
        }

        [Fact]
        public void OnlyASpecifyTaskIsMarkedAsValidatingTheEnvironmentCopy()
        {
            string build = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "BuildService.cs"));

            Assert.Equal(1, SourceAssert.Count(build, "if (status.SpecifyOnly) status.ValidatedSource = \"environment-copy\";"));
        }
    }
}
