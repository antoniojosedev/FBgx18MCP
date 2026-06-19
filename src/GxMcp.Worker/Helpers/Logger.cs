using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    // PERFORMANCE (W-A1): previously every Log() call took a global lock + File.AppendAllText
    // synchronously. With ~194 call sites across the Worker, that turned every hot path into a
    // disk-bound critical section. The async writer below decouples the producers from disk I/O:
    //  - producers push lines into a BlockingCollection (lock-free under contention),
    //  - a single background writer drains the queue in batches and appends with one open file
    //    handle per batch.
    // Console.Error.WriteLine is preserved for each line so the Gateway capture path is unchanged
    // and so a crash never loses the very latest log line.
    public static class Logger
    {
        private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker_debug.log");
        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        private static readonly Thread _writer;

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
                        string prevLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker_debug.prev.log");
                        if (File.Exists(prevLog)) File.Delete(prevLog);
                        File.Move(LogFile, prevLog);
                        break;
                    }
                }
                catch
                {
                    if (i == 2) break;
                    Thread.Sleep(100);
                }
            }

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LoggerWriter"
            };
            _writer.Start();

            // Ensure pending lines flush on process exit (best-effort; daemon thread).
            try { AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown(); } catch { }
        }

        public static void Info(string message)  => Enqueue("INFO",  message);
        public static void Warn(string message)  => Enqueue("WARN",  message);
        public static void Error(string message) => Enqueue("ERROR", message);
        public static void Debug(string message) => Enqueue("DEBUG", message);

        private static void Enqueue(string level, string message)
        {
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

            if (_queue.IsAddingCompleted) return;
            try { _queue.Add(line); }
            catch (InvalidOperationException) { /* writer shutting down — drop silently */ }
        }

        private static void WriterLoop()
        {
            var batch = new StringBuilder(8192);
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.AppendLine(first);

                // Drain anything else that's already queued so we issue one I/O per batch.
                while (batch.Length < 64 * 1024 && _queue.TryTake(out var next))
                {
                    batch.AppendLine(next);
                }

                try
                {
                    File.AppendAllText(LogFile, batch.ToString());
                }
                catch
                {
                    // Silent fallback (disk full / locked): lines already went to stderr.
                }
            }
        }

        public static void Shutdown()
        {
            try { _queue.CompleteAdding(); } catch { }
            try { _writer.Join(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
