using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #608: the .NET Framework generator starts <c>gxexec</c> by bare name, and
    /// <c>&lt;GeneXus install&gt;\gxnet</c> was not on the Worker process PATH, so Compilation failed
    /// after Specification and Generation had succeeded. These tests are hermetic: the PATH value is a
    /// string or a throwaway variable, existence is injected, and no Knowledge Base is touched.
    /// </summary>
    public class GxBuildPathEnvironmentTests
    {
        private const string Install = @"C:\GX\Install";
        private const string GxNet = @"C:\GX\Install\gxnet";

        private static Func<string, bool> Existing(params string[] directories)
        {
            var set = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
            return directory => set.Contains(directory);
        }

        private static readonly Func<string, bool> AllExist = _ => true;

        [Fact]
        public void PrependsGxNetThenInstallInThatOrderAndKeepsTheRestOfThePath()
        {
            string result = GxBuildPathEnvironment.Compose(Install, @"C:\Windows;C:\Tools", AllExist, out var added);

            Assert.Equal(GxNet + ";" + Install + @";C:\Windows;C:\Tools", result);
            Assert.Equal(new[] { GxNet, Install }, added);
        }

        [Fact]
        public void OnlyDirectoriesThatExistAreAdded()
        {
            string onlyGxNet = GxBuildPathEnvironment.Compose(Install, @"C:\Windows", Existing(GxNet), out var addedGxNet);
            Assert.Equal(GxNet + @";C:\Windows", onlyGxNet);
            Assert.Equal(new[] { GxNet }, addedGxNet);

            string onlyInstall = GxBuildPathEnvironment.Compose(Install, @"C:\Windows", Existing(Install), out var addedInstall);
            Assert.Equal(Install + @";C:\Windows", onlyInstall);
            Assert.Equal(new[] { Install }, addedInstall);

            string none = GxBuildPathEnvironment.Compose(Install, @"C:\Windows", Existing(), out var addedNone);
            Assert.Equal(@"C:\Windows", none);
            Assert.Empty(addedNone);
        }

        [Theory]
        [InlineData(@"c:\gx\install\GXNET")]
        [InlineData(@"C:\GX\Install\gxnet\")]
        [InlineData("\"C:\\GX\\Install\\gxnet\"")]
        [InlineData("C:/GX/Install/gxnet")]
        [InlineData(@"  C:\GX\Install\gxnet  ")]
        public void AnEntryAlreadyPresentIsNotDuplicatedWhateverItsCaseSlashOrQuoting(string existingEntry)
        {
            string current = @"C:\Windows;" + existingEntry + @";C:\Tools";

            string result = GxBuildPathEnvironment.Compose(Install, current, AllExist, out var added);

            Assert.Equal(new[] { Install }, added);
            Assert.Equal(Install + ";" + current, result);
            Assert.Equal(1, CountEntries(result, GxNet));
        }

        [Fact]
        public void NothingToAddReturnsThePathUnchangedAndDoesNotMoveExistingEntries()
        {
            string current = @"C:\Windows;" + Install + @"\;C:\Tools;" + GxNet.ToUpperInvariant();

            string result = GxBuildPathEnvironment.Compose(Install, current, AllExist, out var added);

            Assert.Same(current, result);
            Assert.Empty(added);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"GX\Install")]
        [InlineData(@".\GX\Install")]
        [InlineData(@"..\GX\Install")]
        [InlineData(@"\GX\Install")]
        [InlineData("/GX/Install")]
        [InlineData("C:GX")]
        [InlineData("C:")]
        [InlineData(@"\\?\C:\GX\Install")]
        [InlineData(@"C:\GX\Install;C:\Evil")]
        [InlineData(@"C:\GX\Install;Evil")] // a ';' would split into a second PATH entry, and GetFullPath accepts it
        [InlineData("C:\\GX\\\"Install")]
        public void NullEmptyRelativeOrMalformedInstallationPathLeavesThePathUntouched(string installationPath)
        {
            const string current = @"C:\Windows;C:\Tools";

            string result = GxBuildPathEnvironment.Compose(installationPath, current, AllExist, out var added);

            Assert.Same(current, result);
            Assert.Empty(added);
        }

        [Fact]
        public void ANonexistentInstallationLeavesThePathUntouched()
        {
            const string current = @"C:\Windows";

            string result = GxBuildPathEnvironment.Compose(Install, current, Existing(), out var added);

            Assert.Same(current, result);
            Assert.Empty(added);
        }

        [Theory]
        [InlineData(@"C:\GX\Install\")]
        [InlineData("C:/GX/Install/")]
        [InlineData(@"  C:\GX\Install  ")]
        [InlineData(@"C:\GX\.\Other\..\Install")]
        public void TheInstallationPathIsNormalizedBeforeUse(string installationPath)
        {
            string result = GxBuildPathEnvironment.Compose(installationPath, @"C:\Windows", AllExist, out var added);

            Assert.Equal(GxNet + ";" + Install + @";C:\Windows", result);
            Assert.Equal(new[] { GxNet, Install }, added);
        }

        [Fact]
        public void AUncInstallationPathIsAccepted()
        {
            string result = GxBuildPathEnvironment.Compose(@"\\fileserver\gx\Install", @"C:\Windows", AllExist, out var added);

            Assert.Equal(@"\\fileserver\gx\Install\gxnet;\\fileserver\gx\Install;C:\Windows", result);
            Assert.Equal(2, added.Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AMissingPathBecomesJustTheAddedDirectoriesWithoutATrailingSeparator(string current)
        {
            string result = GxBuildPathEnvironment.Compose(Install, current, AllExist, out var added);

            Assert.Equal(GxNet + ";" + Install, result);
            Assert.Equal(2, added.Count);
        }

        [Fact]
        public void LongAndEmptySegmentsOfTheExistingPathArePreservedVerbatim()
        {
            string longSegment = @"C:\" + new string('a', 4000);
            string current = ";;" + longSegment + @";;C:\Windows;; ;";

            string result = GxBuildPathEnvironment.Compose(Install, current, AllExist, out var added);

            Assert.Equal(GxNet + ";" + Install + ";" + current, result);
            Assert.EndsWith(current, result, StringComparison.Ordinal);
            Assert.Equal(2, added.Count);
        }

        [Fact]
        public void APathThatWouldExceedTheWindowsLimitIsLeftAloneInsteadOfFailing()
        {
            string current = new string('x', 32760);

            string result = GxBuildPathEnvironment.Compose(Install, current, AllExist, out var added);

            Assert.Same(current, result);
            Assert.Empty(added);
        }

        [Fact]
        public void ComposeIsIdempotent()
        {
            string first = GxBuildPathEnvironment.Compose(Install, @"C:\Windows", AllExist, out var firstAdded);
            string second = GxBuildPathEnvironment.Compose(Install, first, AllExist, out var secondAdded);

            Assert.Equal(2, firstAdded.Count);
            Assert.Empty(secondAdded);
            Assert.Same(first, second);
        }

        [Fact]
        public void AThrowingExistenceProbeIsTreatedAsMissing()
        {
            string result = GxBuildPathEnvironment.Compose(Install, @"C:\Windows",
                directory => { throw new UnauthorizedAccessException(); }, out var added);

            Assert.Equal(@"C:\Windows", result);
            Assert.Empty(added);
        }

        // ---- Apply: the getter/setter seam and the [BUILD-PATH] log contract.

        private sealed class FakeProcessPath
        {
            public string Value;
            public int Writes;
            public readonly List<string> Info = new List<string>();
            public readonly List<string> Warn = new List<string>();

            public IReadOnlyList<string> Apply(string installationPath, Func<string, bool> exists)
            {
                return GxBuildPathEnvironment.Apply(installationPath, () => Value,
                    v => { Value = v; Writes++; }, exists, Info.Add, Warn.Add);
            }
        }

        [Fact]
        public void ApplyWritesTheComposedPathOnceAndLogsOneBuildPathLine()
        {
            var path = new FakeProcessPath { Value = @"C:\Windows" };

            var added = path.Apply(Install, AllExist);

            Assert.Equal(2, added.Count);
            Assert.Equal(GxNet + ";" + Install + @";C:\Windows", path.Value);
            Assert.Equal(1, path.Writes);
            string line = Assert.Single(path.Info);
            Assert.StartsWith("[BUILD-PATH]", line, StringComparison.Ordinal);
            Assert.Contains(GxNet, line, StringComparison.Ordinal);
            Assert.Contains(Install, line, StringComparison.Ordinal);
            Assert.Empty(path.Warn);
        }

        [Fact]
        public void ApplyTwiceChangesAndLogsNothingTheSecondTime()
        {
            var path = new FakeProcessPath { Value = @"C:\Windows" };
            path.Apply(Install, AllExist);
            string afterFirst = path.Value;

            var second = path.Apply(Install, AllExist);

            Assert.Empty(second);
            Assert.Equal(afterFirst, path.Value);
            Assert.Equal(1, path.Writes);
            Assert.Single(path.Info);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(@"GX\Install")]
        public void ApplyDoesNotWriteOrLogWhenThereIsNothingToAdd(string installationPath)
        {
            var path = new FakeProcessPath { Value = @"C:\Windows" };

            var added = path.Apply(installationPath, AllExist);

            Assert.Empty(added);
            Assert.Equal(@"C:\Windows", path.Value);
            Assert.Equal(0, path.Writes);
            Assert.Empty(path.Info);
            Assert.Empty(path.Warn);
        }

        [Fact]
        public void ApplyNeverThrowsWhenTheEnvironmentRefusesTheWrite()
        {
            var warnings = new List<string>();
            var infos = new List<string>();

            var added = GxBuildPathEnvironment.Apply(Install, () => @"C:\Windows",
                v => { throw new ArgumentException("value too long"); }, AllExist, infos.Add, warnings.Add);

            Assert.Empty(added);
            Assert.Empty(infos);
            string warning = Assert.Single(warnings);
            Assert.StartsWith("[BUILD-PATH]", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void ApplyNeverThrowsWhenThePathCannotBeRead()
        {
            var warnings = new List<string>();

            var added = GxBuildPathEnvironment.Apply(Install, () => { throw new InvalidOperationException("denied"); },
                v => { }, AllExist, s => { }, warnings.Add);

            Assert.Empty(added);
            Assert.Single(warnings);
        }

        // ---- The real Environment calls, on a throwaway variable so the host's PATH is never touched.

        [Fact]
        public void EnsureOnProcessVariableWritesTheProcessLevelValueOnlyAndIsIdempotent()
        {
            string variable = "GXMCP_TEST_PATH_" + Guid.NewGuid().ToString("N");
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-path-" + Guid.NewGuid().ToString("N"));
            string gxnet = Path.Combine(root, "gxnet");
            Directory.CreateDirectory(gxnet);
            try
            {
                Environment.SetEnvironmentVariable(variable, @"C:\Windows;C:\Tools", EnvironmentVariableTarget.Process);

                var added = GxBuildPathEnvironment.EnsureOnProcessVariable(variable, root);

                Assert.Equal(new[] { gxnet, root }, added);
                string expected = gxnet + ";" + root + @";C:\Windows;C:\Tools";
                Assert.Equal(expected, Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process));
                Assert.Null(Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User));
                Assert.Null(Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine));

                Assert.Empty(GxBuildPathEnvironment.EnsureOnProcessVariable(variable, root));
                Assert.Equal(expected, Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null, EnvironmentVariableTarget.Process);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void EnsureOnProcessVariableSkipsADirectoryThatDoesNotExistOnDisk()
        {
            string variable = "GXMCP_TEST_PATH_" + Guid.NewGuid().ToString("N");
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable(variable, @"C:\Windows", EnvironmentVariableTarget.Process);

                var added = GxBuildPathEnvironment.EnsureOnProcessVariable(variable, root);

                Assert.Equal(new[] { root }, added);
                Assert.Equal(root + @";C:\Windows", Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null, EnvironmentVariableTarget.Process);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        // ---- Source guards: scope of the write and the place it is wired in.

        private static string Source(params string[] segments) =>
            GxMcp.TestSupport.RepoSource.WithoutComments(segments).Replace("\r\n", "\n");

        [Fact]
        public void TheHelperOnlyEverWritesTheProcessLevelEnvironment()
        {
            string source = Source("src", "GxMcp.Worker", "Helpers", "GxBuildPathEnvironment.cs");

            Assert.DoesNotContain("EnvironmentVariableTarget.Machine", source, StringComparison.Ordinal);
            Assert.DoesNotContain("EnvironmentVariableTarget.User", source, StringComparison.Ordinal);

            // One write in the whole helper, and it names the process target explicitly: the
            // two-argument overload would also be process-level, but a later edit to a wider
            // target must not go unnoticed.
            Assert.Equal(1, GxMcp.TestSupport.SourceAssert.Count(source, "Environment.SetEnvironmentVariable("));
            Assert.Equal(1, GxMcp.TestSupport.SourceAssert.Count(source,
                "Environment.SetEnvironmentVariable(variableName, value, EnvironmentVariableTarget.Process)"));
            Assert.Equal(1, GxMcp.TestSupport.SourceAssert.Count(source,
                "Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process)"));
        }

        [Fact]
        public void TheInstallationDirectoryComesFromTheWorkersOwnResolverNotFromAHardcodedPath()
        {
            string source = Source("src", "GxMcp.Worker", "Helpers", "GxBuildPathEnvironment.cs");

            Assert.Contains("GeneXusInstallPath.Resolve()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Program Files", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GeneXus18", source, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheInProcessBuildPhasePrependsThePathOnceBeforeStartingTheNativeBuild()
        {
            string source = Source("src", "GxMcp.Worker", "Services", "BuildService.cs");
            string body = GxMcp.TestSupport.SourceAssert.MethodBody(source,
                "internal bool RunInProcessBuildPhase(BuildTaskStatus status, string action, List<string> targets)");

            const string apply = "GxBuildPathEnvironment.EnsureOnProcessPath()";
            int applyAt = body.IndexOf(apply, StringComparison.Ordinal);
            int guardAt = body.IndexOf("if (!useInProcess) return false;", StringComparison.Ordinal);
            int runAt = body.IndexOf("InProcessBuildRunner.Run(", StringComparison.Ordinal);

            Assert.True(applyAt >= 0, "RunInProcessBuildPhase must call " + apply);
            Assert.Equal(1, GxMcp.TestSupport.SourceAssert.Count(body, apply));
            Assert.True(guardAt >= 0 && guardAt < applyAt, "workers that do not build in-process must not touch PATH");
            Assert.True(runAt > applyAt, "PATH must be extended before the orchestrator starts");
        }

        private static int CountEntries(string path, string entry) =>
            path.Split(';').Count(e => string.Equals(e.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\'), entry, StringComparison.OrdinalIgnoreCase));
    }
}
