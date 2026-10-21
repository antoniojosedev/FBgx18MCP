using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Result of one gxwire invocation.
    /// </summary>
    internal readonly struct GxwireProcessRun
    {
        internal GxwireProcessRun(
            string standardOutput,
            string standardError,
            bool timedOut,
            int timeoutSeconds,
            int? exitCode = null,
            long elapsedMs = 0)
        {
            StandardOutput = standardOutput;
            StandardError = standardError;
            TimedOut = timedOut;
            TimeoutSeconds = timeoutSeconds;
            ExitCode = exitCode;
            ElapsedMs = elapsedMs;
        }

        internal string StandardOutput { get; }
        internal string StandardError { get; }

        /// <summary>True when the budget expired and the child was terminated.</summary>
        internal bool TimedOut { get; }

        internal int TimeoutSeconds { get; }

        /// <summary>
        /// The child's exit code, or <c>null</c> when it was terminated by the
        /// budget and never reported one of its own (#593).
        /// </summary>
        internal int? ExitCode { get; }

        /// <summary>Wall-clock time the run took, including a timed-out run.</summary>
        internal long ElapsedMs { get; }
    }

    /// <summary>
    /// Runs gxwire to completion under a wall-clock budget.
    ///
    /// Two properties matter and were both absent before issue #498:
    ///
    /// 1. Both pipes are drained concurrently. Reading stdout to end and only
    ///    then stderr deadlocks as soon as the child fills the stderr pipe
    ///    buffer, because the child blocks writing stderr while the gateway
    ///    blocks reading stdout. gxwire is chatty on stderr, so that is the
    ///    normal case, not an edge case.
    /// 2. On expiry the process tree is killed. Previously the gateway returned
    ///    `GxwireException` and left a running, ownerless gxwire.exe behind.
    /// </summary>
    internal static class GxwireProcessRunner
    {
        /// <summary>Budget for ordinary query actions.</summary>
        internal const int DefaultQueryTimeoutSeconds = 30;

        /// <summary>
        /// Budget for `index`, which is the only action that parses the whole
        /// Knowledge Base. A 15k-object export takes minutes, so the old shared
        /// 30s budget reported failure for work that then completed unobserved.
        /// </summary>
        internal const int DefaultIndexTimeoutSeconds = 900;

        internal const string TimeoutEnvVar = "GXMCP_GXWIRE_TIMEOUT_SECONDS";

        internal static int ResolveTimeoutSeconds(string action)
        {
            int fallback = string.Equals(action, "index", StringComparison.OrdinalIgnoreCase)
                ? DefaultIndexTimeoutSeconds
                : DefaultQueryTimeoutSeconds;

            string? configured = Environment.GetEnvironmentVariable(TimeoutEnvVar);
            if (string.IsNullOrWhiteSpace(configured)) return fallback;
            if (!int.TryParse(configured.Trim(), out int seconds) || seconds <= 0) return fallback;
            return seconds;
        }

        internal static async Task<GxwireProcessRun> RunAsync(Process proc, int timeoutSeconds)
        {
            if (proc == null) throw new ArgumentNullException(nameof(proc));
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var stopwatch = Stopwatch.StartNew();

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                Task exitTask = proc.WaitForExitAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Kill first, then report: the caller must never learn that the
                // budget expired while the child is still indexing.
                TryKillProcessTree(proc);
                return new GxwireProcessRun(
                    standardOutput: string.Empty,
                    standardError: string.Empty,
                    timedOut: true,
                    timeoutSeconds: timeoutSeconds,
                    exitCode: null,
                    elapsedMs: stopwatch.ElapsedMilliseconds);
            }

            return new GxwireProcessRun(
                standardOutput: stdoutTask.Result,
                standardError: stderrTask.Result,
                timedOut: false,
                timeoutSeconds: timeoutSeconds,
                exitCode: TryReadExitCode(proc),
                elapsedMs: stopwatch.ElapsedMilliseconds);
        }

        /// <summary>
        /// The exit code is read here, while the handle is known to be valid, so
        /// the caller never has to touch the <see cref="Process"/> after the run
        /// and turn a reporting step into an <see cref="InvalidOperationException"/>.
        /// </summary>
        private static int? TryReadExitCode(Process proc)
        {
            try
            {
                return proc.HasExited ? proc.ExitCode : (int?)null;
            }
            catch (InvalidOperationException)
            {
                return null;
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
