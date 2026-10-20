using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The engine runner must survive the two failure modes the gxwire runner
    /// documents from issue #498: a child that fills the stderr pipe while the
    /// parent blocks on stdout, and a child that outlives the budget. Both are
    /// exercised against cmd.exe so the guard does not depend on a built engine.
    /// </summary>
    public class GxEngineProcessRunnerTests
    {
        private static Process StartCmd(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var proc = new Process { StartInfo = startInfo };
            Assert.True(proc.Start());
            return proc;
        }

        [Fact]
        public async Task StopAndWait_ObservesTheOwnedProcessExit()
        {
            using var proc = StartCmd("/c ping -n 30 127.0.0.1 >nul");
            try
            {
                Assert.True(await GxEngineProcessRunner.StopAndWaitAsync(proc));
                Assert.True(proc.HasExited);
            }
            finally
            {
                GxEngineProcessRunner.TryKillProcessTree(proc);
            }
        }

        [Fact]
        public async Task StopAndWait_DoesNotClaimAnUnstartedProcessExited()
        {
            using var proc = new Process();
            Assert.False(await GxEngineProcessRunner.StopAndWaitAsync(proc));
        }

        [Fact]
        public async Task Run_CapturesStdoutAndExitCode()
        {
            using var proc = StartCmd("/c echo hello-engine");

            var run = await GxEngineProcessRunner.RunAsync(proc, 15);

            Assert.False(run.TimedOut);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("hello-engine", run.StandardOutput);
        }

        [Fact]
        public async Task Run_CapturesNonZeroExitCodeAndStderr()
        {
            using var proc = StartCmd("/c echo boom 1>&2 & exit /b 7");

            var run = await GxEngineProcessRunner.RunAsync(proc, 15);

            Assert.False(run.TimedOut);
            Assert.Equal(7, run.ExitCode);
            Assert.Contains("boom", run.StandardError);
        }

        [Fact]
        public async Task Run_DrainsBothPipesFully_WithoutDeadlock()
        {
            // ~250 KB on each pipe: far beyond the pipe buffer, so a runner that
            // reads one stream to completion before touching the other deadlocks.
            using var proc = StartCmd("/c for /L %i in (1,1,10000) do @(echo out-%i & echo err-%i 1>&2)");

            var run = await GxEngineProcessRunner.RunAsync(proc, 60);

            Assert.False(run.TimedOut);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("out-10000", run.StandardOutput);
            Assert.Contains("err-10000", run.StandardError);
        }

        [Fact]
        public async Task Run_KillsTheProcessTree_WhenTheBudgetExpires()
        {
            // ping -n 6 takes about five seconds when left alone.
            using var proc = StartCmd("/c ping -n 6 127.0.0.1 >nul");

            var stopwatch = Stopwatch.StartNew();
            var run = await GxEngineProcessRunner.RunAsync(proc, 1);
            stopwatch.Stop();

            Assert.True(run.TimedOut);
            Assert.Null(run.ExitCode);
            Assert.Equal(1, run.TimeoutSeconds);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"the budget did not end the run: {stopwatch.Elapsed}");
            Assert.True(proc.HasExited, "the timed-out child must be killed, not left running");
        }
    }
}
