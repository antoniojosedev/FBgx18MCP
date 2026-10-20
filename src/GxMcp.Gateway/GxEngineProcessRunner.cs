using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    /// <summary>Result of one gx-engine invocation.</summary>
    internal readonly struct GxEngineProcessRun
    {
        internal GxEngineProcessRun(string standardOutput, string standardError, int? exitCode, bool timedOut, int timeoutSeconds)
        {
            StandardOutput = standardOutput;
            StandardError = standardError;
            ExitCode = exitCode;
            TimedOut = timedOut;
            TimeoutSeconds = timeoutSeconds;
        }

        internal string StandardOutput { get; }
        internal string StandardError { get; }

        /// <summary>Exit code, or null when the budget expired and the child was killed.</summary>
        internal int? ExitCode { get; }

        /// <summary>True when the budget expired and the child was terminated.</summary>
        internal bool TimedOut { get; }

        internal int TimeoutSeconds { get; }
    }

    /// <summary>
    /// Runs gx-engine to completion under a wall-clock budget. Carries the same
    /// two hardened properties GxwireProcessRunner documents from issue #498,
    /// because a deadlocked pipe is a property of "child process with two
    /// redirected pipes", not of gxwire:
    ///
    /// 1. Both pipes are drained concurrently. Reading stdout to end and only
    ///    then stderr deadlocks as soon as the child fills the stderr buffer.
    /// 2. On expiry the process tree is killed. The caller must never learn that
    ///    the budget expired while a compiler child is still running.
    /// </summary>
    internal static class GxEngineProcessRunner
    {
        internal static async Task<GxEngineProcessRun> RunAsync(Process proc, int timeoutSeconds)
        {
            if (proc == null) throw new ArgumentNullException(nameof(proc));
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                Task exitTask = proc.WaitForExitAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(proc);
                return new GxEngineProcessRun(string.Empty, string.Empty, null, timedOut: true, timeoutSeconds: timeoutSeconds);
            }

            return new GxEngineProcessRun(
                standardOutput: stdoutTask.Result,
                standardError: stderrTask.Result,
                exitCode: proc.ExitCode,
                timedOut: false,
                timeoutSeconds: timeoutSeconds);
        }

        internal static async Task<bool> StopAndWaitAsync(Process proc)
        {
            TryKillProcessTree(proc);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is InvalidOperationException
                || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Best-effort kill of the child and anything it spawned. Never throws:
        /// a process that already exited, or one the caller cannot kill, must not
        /// turn a timeout into an unhandled exception.
        /// </summary>
        internal static void TryKillProcessTree(Process? proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited or never started.
            }
            catch (NotSupportedException)
            {
                // Process tree termination unavailable on this host.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access denied or the handle is gone.
            }
        }
    }
}
