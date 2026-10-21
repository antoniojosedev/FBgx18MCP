using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #498: the gateway used one fixed 30 s budget for every gxwire
    // action, read stdout to end before stderr, and never killed the child on
    // timeout. Consequences observed in production: ownerless gxwire.exe kept
    // indexing after the gateway reported failure, and `action=index` on a
    // 15,170-file export always returned an error even though the orphan
    // finished the index about two minutes later.
    public class GxwireProcessRunnerTests
    {
        [Fact]
        public void QueryActions_KeepTheShortBudget()
        {
            Assert.Equal(30, GxwireProcessRunner.ResolveTimeoutSeconds("status"));
            Assert.Equal(30, GxwireProcessRunner.ResolveTimeoutSeconds("callers"));
            Assert.Equal(30, GxwireProcessRunner.ResolveTimeoutSeconds("impact"));
        }

        [Fact]
        public void IndexGetsALongerBudget()
        {
            // Index is the only action that parses the whole Knowledge Base. A
            // 15k-object export takes minutes, so sharing the 30 s query budget
            // reported failure for work that then completed unobserved.
            Assert.Equal(900, GxwireProcessRunner.ResolveTimeoutSeconds("index"));
        }

        [Fact]
        public void ActionNameIsCaseInsensitive()
        {
            Assert.Equal(900, GxwireProcessRunner.ResolveTimeoutSeconds("INDEX"));
        }

        [Fact]
        public void EnvironmentOverrideWins()
        {
            string? previous = Environment.GetEnvironmentVariable(GxwireProcessRunner.TimeoutEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(GxwireProcessRunner.TimeoutEnvVar, "45");
                Assert.Equal(45, GxwireProcessRunner.ResolveTimeoutSeconds("status"));
                Assert.Equal(45, GxwireProcessRunner.ResolveTimeoutSeconds("index"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(GxwireProcessRunner.TimeoutEnvVar, previous);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-number")]
        [InlineData("0")]
        [InlineData("-5")]
        public void UnusableOverridesFallBackInsteadOfFailing(string configured)
        {
            string? previous = Environment.GetEnvironmentVariable(GxwireProcessRunner.TimeoutEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(GxwireProcessRunner.TimeoutEnvVar, configured);
                Assert.Equal(30, GxwireProcessRunner.ResolveTimeoutSeconds("status"));
                Assert.Equal(900, GxwireProcessRunner.ResolveTimeoutSeconds("index"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(GxwireProcessRunner.TimeoutEnvVar, previous);
            }
        }

        [Fact]
        public async Task ChildThatFloodsStderr_DoesNotDeadlock()
        {
            // The original code awaited stdout to end before touching stderr. A
            // child that fills the stderr pipe buffer blocks writing, while the
            // gateway blocks reading stdout: both wait for each other until the
            // timeout. This child writes far more than any pipe buffer and then
            // exits, so concurrent draining must let it finish well inside the
            // budget.
            using var proc = StartChild(
                "-c",
                "import sys;sys.stderr.write('E'*400000);sys.stderr.flush();print('done')");

            var run = await GxwireProcessRunner.RunAsync(proc, 30);

            Assert.False(run.TimedOut, "a chatty child must not trip the budget");
            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("done", run.StandardOutput);
            Assert.True(run.StandardError.Length > 300000, "stderr should have been drained, not truncated");
        }

        [Fact]
        public async Task TimeoutKillsTheChild_InsteadOfLeavingItOrphaned()
        {
            using var proc = StartChild("-c", "import time;time.sleep(120)");
            Assert.False(proc.HasExited);

            var run = await GxwireProcessRunner.RunAsync(proc, 1);

            Assert.True(run.TimedOut);
            Assert.Equal(1, run.TimeoutSeconds);

            // The production symptom was a running, ownerless gxwire.exe after
            // the gateway had already reported failure.
            Assert.True(
                WaitForExit(proc, TimeSpan.FromSeconds(10)),
                "the child must be terminated when the budget expires");
        }

        [Fact]
        public async Task FastChild_ReturnsOutputAndNoTimeout()
        {
            using var proc = StartChild("-c", "print('hello');import sys;sys.stderr.write('warn')");

            var run = await GxwireProcessRunner.RunAsync(proc, 30);

            Assert.False(run.TimedOut);
            Assert.Equal("hello", run.StandardOutput.Trim());
            Assert.Equal("warn", run.StandardError.Trim());
            Assert.Equal(0, run.ExitCode);
            Assert.True(run.ElapsedMs >= 0);
        }

        [Fact]
        public async Task FailingChild_ReportsItsExitCodeInTheRun()
        {
            // Issue #593: the caller used to read proc.ExitCode after the run and
            // report nothing else; the run itself now carries the code so the
            // envelope can be built without touching the Process again.
            using var proc = StartChild("-c", "import sys;sys.stderr.write('bad');sys.exit(3)");

            var run = await GxwireProcessRunner.RunAsync(proc, 30);

            Assert.False(run.TimedOut);
            Assert.Equal(3, run.ExitCode);
            Assert.Equal("bad", run.StandardError.Trim());
        }

        [Fact]
        public async Task TimedOutChild_HasNoExitCodeButAnElapsedTime()
        {
            using var proc = StartChild("-c", "import time;time.sleep(120)");

            var run = await GxwireProcessRunner.RunAsync(proc, 1);

            Assert.True(run.TimedOut);
            Assert.Null(run.ExitCode);
            Assert.True(run.ElapsedMs >= 900, $"elapsed should cover the budget, got {run.ElapsedMs} ms");
        }

        [Fact]
        public void KillingAnAlreadyExitedProcess_DoesNotThrow()
        {
            using var proc = StartChild("-c", "pass");
            proc.WaitForExit(10_000);

            // The caller's `finally` always kills; a process that finished on its
            // own must not turn a successful run into an exception.
            GxwireProcessRunner.TryKillProcessTree(proc);
            GxwireProcessRunner.TryKillProcessTree(proc);
        }

        [Fact]
        public void KillingNull_DoesNotThrow()
        {
            GxwireProcessRunner.TryKillProcessTree(null);
        }

        private static bool WaitForExit(Process proc, TimeSpan timeout)
        {
            try
            {
                proc.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return proc.HasExited;
        }

        private static Process StartChild(params string[] pythonArgs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in pythonArgs) psi.ArgumentList.Add(arg);

            var proc = Process.Start(psi);
            Assert.NotNull(proc);
            return proc;
        }
    }
}
