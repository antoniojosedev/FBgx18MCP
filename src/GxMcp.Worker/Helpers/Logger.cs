using System;
using System.IO;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    // PERFORMANCE (W-A1): previously every Log() call took a global lock + File.AppendAllText
    // synchronously. With ~194 call sites across the Worker, that turned every hot path into a
    // disk-bound critical section. The async writer decouples the producers from disk I/O:
    //  - producers hand lines to a single AsyncLogWriter (a short gate around BlockingCollection.Add),
    //  - its background thread drains the queue in batches and appends with one open file
    //    handle per batch.
    // Logger stays the static facade: it owns the destination, the one-time rotation, GXMCP_SYNC_LOG,
    // formatting and the stderr mirror, and it delegates to exactly one writer instance (issue #605).
    // Console.Error.WriteLine is preserved for each line so the Gateway capture path is unchanged
    // and so a crash never loses the very latest log line.
    public static class Logger
    {
        // issue #40: the log MUST NOT live inside node_modules. Under an npm install the
        // exe dir is node_modules\genexus-mcp\publish\...; keeping an open file handle
        // there makes `npx genexus-mcp@latest` fail with EBUSY on Windows when it refreshes
        // the package. LogDirectory relocates to a stable per-user dir in that case (and
        // honours GXMCP_LOG_DIR); readers use it too so log-tail still finds the file.
        public static readonly string LogDirectory = ResolveLogDirectory();
        private static readonly string LogFile = Path.Combine(LogDirectory, "worker_debug.log");

        private static string ResolveLogDirectory()
        {
            try
            {
                string directory = RuntimePaths.LogsRoot;
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch
            {
                string fallback = Path.Combine(Path.GetTempPath(), "GenexusMCP", "logs");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
        private static readonly AsyncLogWriter _writer;

        // Diagnostics: with GXMCP_SYNC_LOG=1 every line is also appended synchronously
        // to the log file, so a hard worker crash (e.g. an uncatchable SDK
        // StackOverflow/AccessViolation mid-write) still leaves the final step on disk
        // instead of losing it in the async batch. Off by default — the async path is
        // the hot path; this is for reproducing crashes only.
        private static readonly bool SyncLog =
            string.Equals(Environment.GetEnvironmentVariable("GXMCP_SYNC_LOG"), "1", StringComparison.Ordinal);

        // FR#24 (v2.6.6 Stream C): per-phase log tag, propagated via AsyncLocal so
        // BuildService updates affect background tasks too. Setter is internal so
        // only the worker can mutate it; foreign callers see a read-only surface.
        private static readonly AsyncLocal<string> _currentPhase = new AsyncLocal<string>();
        public static string CurrentPhase
        {
            get { return _currentPhase.Value; }
            internal set { _currentPhase.Value = value; }
        }

        static Logger()
        {
            // Preserve the previous log rotation behaviour (rename existing file to .prev.log).
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (File.Exists(LogFile))
                    {
                        RotateExistingLog(LogFile);
                        break;
                    }
                }
                catch
                {
                    if (i == 2) break;
                    Thread.Sleep(100);
                }
            }

            // The destination is fixed for the life of the facade. If creating or starting the writer
            // fails, the exception surfaces as TypeInitializationException (no partial facade, no fallback).
            _writer = new AsyncLogWriter(LogFile);

            // Ensure pending lines flush on process exit (best-effort; daemon thread).
            try { AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown(); } catch { }
        }

        internal static void RotateExistingLog(string logFile)
        {
            if (string.IsNullOrWhiteSpace(logFile) || !File.Exists(logFile)) return;
            string directory = Path.GetDirectoryName(logFile) ?? RuntimePaths.LogsRoot;
            string previousLog = Path.Combine(directory, "worker_debug.prev.log");
            if (File.Exists(previousLog)) File.Delete(previousLog);
            File.Move(logFile, previousLog);
        }

        public static void Info(string message)  => Enqueue("INFO",  message);
        public static void Warn(string message)  => Enqueue("WARN",  message);
        public static void Error(string message) => Enqueue("ERROR", message);
        public static void Debug(string message) => Enqueue("DEBUG", message);

        // Test seam (issue #594): lets a unit test count the lines a code path emits
        // (level, message) without capturing stderr or the log file. Null in production.
        internal static Action<string, string> TestSink;

        private static void Enqueue(string level, string message)
        {
            var sink = TestSink;
            if (sink != null)
            {
                try { sink(level, message); } catch { }
            }

            // ISO-8601 with milliseconds + offset so build phase stalls diff
            // cleanly against external clocks; phase tag lets the agent grep
            // a build trace by phase. Phase-empty case avoids the "[" + phase
            // + "]" intermediate alloc; build emits thousands of lines.
            string phase = _currentPhase.Value;
            string line = string.IsNullOrEmpty(phase)
                ? string.Format("[{0:yyyy-MM-ddTHH:mm:ss.fffzzz}] [{1}] [] {2}", DateTimeOffset.Now, level, message)
                : string.Format("[{0:yyyy-MM-ddTHH:mm:ss.fffzzz}] [{1}] [{2}] {3}", DateTimeOffset.Now, level, phase, message);

            // Surface to stderr synchronously (cheap, non-blocking on the OS level) so the
            // Gateway captures the line immediately even if the file writer is behind.
            try { Console.Error.WriteLine($"[Worker Log] {line}"); } catch { }

            // Crash-forensics mode: persist synchronously so a hard process death
            // mid-write doesn't lose the last step in the async batch.
            if (SyncLog)
            {
                try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
            }

            // Writer shutting down: the line is refused and dropped silently (the stderr mirror already has it).
            _writer.Enqueue(line);
        }

        public static void Shutdown()
        {
            try { _writer.CompleteAdding(); } catch { }
            // Best effort: a false result (the thread is still draining) is ignored.
            try { _writer.Join(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
