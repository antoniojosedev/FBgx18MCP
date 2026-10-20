using System;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #608: when Specification and Generation succeed but the compilation launcher cannot be
    /// started, the only trace is on the <c>&gt;R</c> output channel. The build used to end with the
    /// generic "compiler diagnostics were not captured" error; it now carries a bounded excerpt of
    /// those lines. Hermetic: the lines are fed to the real line parser, no build or KB runs.
    /// </summary>
    public class BuildChannelFailureExcerptTests
    {
        private static readonly MethodInfo HandleLine = typeof(BuildService).GetMethod(
            "HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);

        // The shape of the report, with synthetic model and object names.
        private const string LauncherCommand =
            "gxexec \"C:\\GX\\Model\\web\\bldobja.cs\" -r:GxBaseBuilder.dll -arg:csc=\"C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe\"";

        private static BuildService.BuildTaskStatus Feed(params string[] lines)
        {
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus();
            foreach (string line in lines)
                HandleLine.Invoke(service, new object[] { status, line, false });
            return status;
        }

        private static string[] LauncherFailure(string firstR, string secondR) => new[]
        {
            ">SObjA Compilation:-:ObjA Compilation for Default (.NET Framework)",
            ">L" + LauncherCommand,
            ">R" + firstR,
            ">R\t" + secondR,
            ">E0ObjA Compilation:-:ObjA Compilation for Default (.NET Framework)",
        };

        // ---- Through the real line parser and FailedNativeBuild.

        [Fact]
        public void TheLauncherFailureOnTheOutputChannelReplacesTheGenericNativeBuildError()
        {
            var status = Feed(LauncherFailure(
                "Could not execute command '" + LauncherCommand + "'. Could not find a part of the path.",
                "The system cannot find the file specified"));

            Assert.Equal(0, status.ErrorCount); // nothing was itemized: the case behind the generic text

            Assert.Equal(InProcessBuildOutcome.FailedWithDiagnostics, BuildService.FailedNativeBuild(status));

            Assert.Equal("Failed", status.Status);
            Assert.Equal(1, status.ErrorCount);
            string error = Assert.Single(status.Errors);
            Assert.StartsWith("[NativeBuildFailed]", error, StringComparison.Ordinal);
            Assert.Contains("Could not execute command 'gxexec", error, StringComparison.Ordinal);
            Assert.Contains("The system cannot find the file specified", error, StringComparison.Ordinal);
            Assert.Contains("gxnet", error, StringComparison.Ordinal);
            Assert.DoesNotContain("not captured", error, StringComparison.Ordinal);
            Assert.Equal("NativeBuildFailed", Assert.Single(status.ErrorsDetailed).code);
            Assert.Contains("gxexec", status.ErrorsDetailed[0].raw, StringComparison.Ordinal);
        }

        [Fact]
        public void DetectionDependsOnTheChannelPrefixAndNotOnTheLocaleOfTheMessage()
        {
            var status = Feed(LauncherFailure(
                "N\u00e3o foi poss\u00edvel executar o comando '" + LauncherCommand + "'. N\u00e3o foi poss\u00edvel encontrar uma parte do caminho.",
                "O sistema n\u00e3o pode encontrar o arquivo especificado"));

            BuildService.FailedNativeBuild(status);

            string error = Assert.Single(status.Errors);
            Assert.Contains("N\u00e3o foi poss\u00edvel executar o comando 'gxexec", error, StringComparison.Ordinal);
            Assert.Contains("O sistema n\u00e3o pode encontrar o arquivo especificado", error, StringComparison.Ordinal);
            Assert.Contains("gxnet", error, StringComparison.Ordinal);
        }

        [Fact]
        public void WithoutAnyOutputChannelLineTheGenericMessageIsKept()
        {
            var status = Feed(
                ">SObjA Compilation:-:ObjA Compilation",
                ">L" + LauncherCommand,
                "[BUILD-INPROCESS] a status line that mentions >R in the middle",
                ">E0ObjA Compilation:-:ObjA Compilation");

            BuildService.FailedNativeBuild(status);

            string error = Assert.Single(status.Errors);
            Assert.Contains("compiler diagnostics were not captured on this route", error, StringComparison.Ordinal);
            Assert.DoesNotContain("gxnet", error, StringComparison.Ordinal);
        }

        [Fact]
        public void BlankOutputChannelLinesDoNotCountAsAnExplanation()
        {
            var status = Feed(">R", ">R   ", ">R\t");

            BuildService.FailedNativeBuild(status);

            Assert.Contains("compiler diagnostics were not captured", Assert.Single(status.Errors), StringComparison.Ordinal);
        }

        [Fact]
        public void ItemizedDiagnosticsStillWinOverTheChannelExcerpt()
        {
            var status = Feed(
                ">O1spc0010: Type mismatch in assignment|<SourcePosition><Line>3</Line><FullName>Procedure 'SampleProc'</FullName></SourcePosition>",
                ">RCould not execute command '" + LauncherCommand + "'.");

            BuildService.FailedNativeBuild(status);

            Assert.Equal(2, status.ErrorCount);
            Assert.Contains("see the diagnostics above", status.Errors[1], StringComparison.Ordinal);
            Assert.DoesNotContain("gxexec", status.Errors[1], StringComparison.Ordinal);
        }

        [Fact]
        public void TheExcerptKeepsTheFirstFiveDistinctLinesAndCountsTheRest()
        {
            var status = Feed(Enumerable.Range(1, 8).Select(i => ">RStep failure " + i).ToArray());

            BuildService.FailedNativeBuild(status);

            string error = Assert.Single(status.Errors);
            for (int i = 1; i <= 5; i++)
                Assert.Contains("Step failure " + i, error, StringComparison.Ordinal);
            Assert.DoesNotContain("Step failure 6", error, StringComparison.Ordinal);
            Assert.DoesNotContain("Step failure 8", error, StringComparison.Ordinal);
            Assert.Contains("(+3 more in the full build log)", error, StringComparison.Ordinal);
        }

        [Fact]
        public void RepeatedOutputChannelLinesAreReportedOnce()
        {
            // One failure per object in a batch produces the same line over and over.
            var status = Feed(
                ">RThe system cannot find the file specified",
                ">RThe system cannot find the file specified",
                ">R  the system cannot find the FILE specified  ",
                ">RSecond cause",
                ">RThe system cannot find the file specified");

            BuildService.FailedNativeBuild(status);

            string error = Assert.Single(status.Errors);
            Assert.Equal(1, Occurrences(error, "cannot find the file specified", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Second cause", error, StringComparison.Ordinal);
            Assert.DoesNotContain("more in the full build log", error, StringComparison.Ordinal);
        }

        [Fact]
        public void AnOutputChannelLineKeepsTheStepMarkerShapeTheLatePhaseParserKnows()
        {
            var status = Feed(">RO WebAppConfig");

            BuildService.FailedNativeBuild(status);

            Assert.Contains(">RO WebAppConfig", Assert.Single(status.Errors), StringComparison.Ordinal);
        }

        // ---- The pure rule.

        [Fact]
        public void EachLineIsCutToThreeHundredCharactersWithAnEllipsis()
        {
            string output = ">R" + new string('x', 1000) + "\n";

            string excerpt = BuildChannelFailureExcerpt.Describe(output, out string hint);

            Assert.NotNull(excerpt);
            Assert.Equal(BuildChannelFailureExcerpt.MaxLineChars, excerpt.Length);
            Assert.EndsWith("...", excerpt, StringComparison.Ordinal);
            Assert.Null(hint);
        }

        [Fact]
        public void ALineAtTheLimitIsNotCut()
        {
            string line = new string('y', BuildChannelFailureExcerpt.MaxLineChars);

            string excerpt = BuildChannelFailureExcerpt.Describe(">R" + line, out _);

            Assert.Equal(line + ".", excerpt);
        }

        [Fact]
        public void TheGxExecHintAppliesOnlyWhenTheBareLauncherNameFailed()
        {
            BuildChannelFailureExcerpt.Describe(">RCould not execute command 'gxexec \"a.cs\"'.", out string bare);
            BuildChannelFailureExcerpt.Describe(">RCould not execute command 'GXEXEC.EXE \"a.cs\"'.", out string bareExe);
            BuildChannelFailureExcerpt.Describe(">RCould not execute command 'C:\\GX\\Install\\gxnet\\GXExec.exe \"a.cs\"'.", out string absolute);
            BuildChannelFailureExcerpt.Describe(">Rcsc.exe could not be started.", out string other);

            Assert.Equal(BuildChannelFailureExcerpt.GxExecHint, bare);
            Assert.Equal(BuildChannelFailureExcerpt.GxExecHint, bareExe);
            Assert.Contains("gxnet", BuildChannelFailureExcerpt.GxExecHint, StringComparison.Ordinal);
            Assert.Contains("PATH", BuildChannelFailureExcerpt.GxExecHint, StringComparison.Ordinal);
            Assert.Null(absolute); // an absolute path that fails is not a PATH problem
            Assert.Null(other);
        }

        [Fact]
        public void TheHintLooksPastTheLinesThatFitInTheExcerpt()
        {
            string output = string.Join("\n",
                Enumerable.Range(1, 6).Select(i => ">RDetail " + i)
                    .Concat(new[] { ">RCould not execute command 'gxexec a.cs'." }));

            string excerpt = BuildChannelFailureExcerpt.Describe(output, out string hint);

            Assert.DoesNotContain("gxexec", excerpt, StringComparison.Ordinal);
            Assert.Equal(BuildChannelFailureExcerpt.GxExecHint, hint);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n  \n")]
        [InlineData(">SSection\n>Lplain log\n>E1Section\n")]
        [InlineData("text >R in the middle\n")]
        public void OutputWithoutAnOutputChannelLineDescribesNothing(string output)
        {
            Assert.Null(BuildChannelFailureExcerpt.Describe(output, out string hint));
            Assert.Null(hint);
        }

        [Fact]
        public void LineEndingsOfTheCapturedOutputDoNotMatter()
        {
            string excerpt = BuildChannelFailureExcerpt.Describe(">RFirst\r\n>RSecond\r>RThird\n", out _);

            Assert.Equal("First | Second | Third.", excerpt);
        }

        // ---- The itemless failure of the per-object route reuses the same description.

        [Fact]
        public void AppendChannelDiagnosticsExtendsTheHintAndLeavesItAloneWithoutChannelLines()
        {
            const string baseHint = "Build failed in-process with no itemized error list.";

            string extended = BuildService.AppendChannelDiagnostics(baseHint,
                ">RCould not execute command 'gxexec a.cs'.\n");
            string untouched = BuildService.AppendChannelDiagnostics(baseHint, ">SSection\n>E0Section\n");

            Assert.StartsWith(baseHint, extended, StringComparison.Ordinal);
            Assert.Contains("Could not execute command 'gxexec a.cs'.", extended, StringComparison.Ordinal);
            Assert.Contains("gxnet", extended, StringComparison.Ordinal);
            Assert.Same(baseHint, untouched);
        }

        // ---- Wiring: the two failure routes consume the description.

        private static string Source() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Worker", "Services", "BuildService.cs").Replace("\r\n", "\n");

        [Fact]
        public void BothInProcessFailureRoutesConsumeTheChannelDescription()
        {
            string source = Source();

            string failed = GxMcp.TestSupport.SourceAssert.MethodBody(source,
                "internal static InProcessBuildOutcome FailedNativeBuild(BuildTaskStatus status)");
            Assert.Contains("BuildChannelFailureExcerpt.Describe(", failed, StringComparison.Ordinal);
            Assert.Contains("compiler diagnostics were not captured on this route", failed, StringComparison.Ordinal);

            string phase = GxMcp.TestSupport.SourceAssert.MethodBody(source,
                "internal bool RunInProcessBuildPhase(BuildTaskStatus status, string action, List<string> targets)");
            Assert.Equal(1, GxMcp.TestSupport.SourceAssert.Count(phase, "status.Hint = AppendChannelDiagnostics(status.Hint, fullText);"));
        }

        private static int Occurrences(string text, string needle, StringComparison comparison)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(needle, index, comparison)) >= 0) { count++; index += needle.Length; }
            return count;
        }
    }
}
