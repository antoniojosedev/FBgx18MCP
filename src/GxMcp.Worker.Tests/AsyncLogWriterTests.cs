using System;
using System.IO;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #605 (follow-up of #548): exercises <see cref="AsyncLogWriter"/> in isolation, with its own
    /// temporary file and writer instance, so queue acceptance and the real file append become observable
    /// through <see cref="AppendReceipt"/>. The static <see cref="Logger"/> facade is not involved.
    /// Every wait is signal-driven (a receipt task or <see cref="AsyncLogWriter.Join"/>) with a 10 s ceiling.
    /// The file-lock scenario needs Win32 sharing semantics, so it runs on Windows like the rest of this
    /// net48 project; it never skips.
    /// </summary>
    public class AsyncLogWriterTests
    {
        private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public void Enqueue_WithReceiptAndSuccessfulAppend_WritesExactlyOnce()
        {
            RunWithWriter((writer, logFile) =>
            {
                string marker = NewMarker();
                var receipt = new AppendReceipt();

                writer.Enqueue(marker, receipt);

                Assert.Equal(AcceptanceState.Accepted, Await(receipt.Acceptance, "acceptance").State);

                // The item may only be processed while the writer shuts down; it must still be written.
                writer.CompleteAdding();
                AssertJoined(writer);

                Assert.Equal(AppendState.Written, Await(receipt.Append, "append").State);
                Assert.Equal(1, CountOccurrences(File.ReadAllText(logFile), marker));
            });
        }

        [Fact]
        public void Enqueue_WhenFileLocked_FailsAndContinuesAfterUnlock()
        {
            RunWithWriter((writer, logFile) =>
            {
                string first = NewMarker();
                string second = NewMarker();
                string third = NewMarker();

                // Exclusive owner: any other open of the file fails with a sharing violation.
                using (var owner = new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Precondition: the exact open used by File.AppendAllText must be refused with Win32 error 32.
                    Exception probe = Record.Exception(() =>
                    {
                        using (new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                        }
                    });
                    var probeFailure = Assert.IsAssignableFrom<IOException>(probe);
                    Assert.Equal(32, probeFailure.HResult & 0xFFFF);

                    var firstReceipt = new AppendReceipt();
                    writer.Enqueue(first, firstReceipt);
                    AssertAcceptedThenLockedFailure(firstReceipt, "first item");

                    // Only after the first item reached a terminal result, so both can never share a batch.
                    var secondReceipt = new AppendReceipt();
                    writer.Enqueue(second, secondReceipt);
                    AssertAcceptedThenLockedFailure(secondReceipt, "second item");

                    // The open failed before any write, so nothing reached the owner's file.
                    Assert.Equal(0L, owner.Length);
                }

                // The owner handle is released: the consumer must still be alive and now succeed.
                var thirdReceipt = new AppendReceipt();
                writer.Enqueue(third, thirdReceipt);
                Assert.Equal(AcceptanceState.Accepted, Await(thirdReceipt.Acceptance, "third item acceptance").State);
                Assert.Equal(AppendState.Written, Await(thirdReceipt.Append, "third item append").State);

                writer.CompleteAdding();
                AssertJoined(writer);

                string content = File.ReadAllText(logFile);
                Assert.Equal(0, CountOccurrences(content, first));
                Assert.Equal(0, CountOccurrences(content, second));
                Assert.Equal(1, CountOccurrences(content, third));
            });
        }

        [Fact]
        public void Enqueue_AfterCompleteAdding_ReportsRejectedWithoutEnqueue()
        {
            RunWithWriter((writer, logFile) =>
            {
                string marker = NewMarker();

                writer.CompleteAdding();
                AssertJoined(writer);

                var receipt = new AppendReceipt();
                writer.Enqueue(marker, receipt);

                AcceptanceResult acceptance = Await(receipt.Acceptance, "acceptance");
                AppendResult append = Await(receipt.Append, "append");
                Assert.Equal(AcceptanceState.Rejected, acceptance.State);
                Assert.Equal(AppendState.NotEnqueued, append.State);
                Assert.Empty(File.ReadAllText(logFile));
            });
        }

        [Fact]
        public void Enqueue_WithoutReceipt_WritesLine()
        {
            RunWithWriter((writer, logFile) =>
            {
                string marker = NewMarker();

                // The production overload: no receipt, no task to wait on; Join plus the file prove the write.
                writer.Enqueue(marker);

                writer.CompleteAdding();
                AssertJoined(writer);

                Assert.Equal(1, CountOccurrences(File.ReadAllText(logFile), marker));
            });
        }

        private static void AssertAcceptedThenLockedFailure(AppendReceipt receipt, string what)
        {
            Assert.Equal(AcceptanceState.Accepted, Await(receipt.Acceptance, what + " acceptance").State);

            AppendResult append = Await(receipt.Append, what + " append");
            Assert.Equal(AppendState.Failed, append.State);
            var failure = Assert.IsAssignableFrom<IOException>(append.Exception);
            Assert.Equal(32, failure.HResult & 0xFFFF);
            Assert.Equal(failure.HResult, append.HResult);
        }

        /// <summary>
        /// Creates a unique temporary directory with an empty log file and a fresh writer, runs the body, and
        /// always closes and joins the writer. The directory is deleted only after the writer thread stopped;
        /// otherwise it is preserved and its path is reported in the failure.
        /// </summary>
        private static void RunWithWriter(Action<AsyncLogWriter, string> body)
        {
            string directory = Path.Combine(Path.GetTempPath(), "GxMcpAsyncLogWriterTests-" + Guid.NewGuid().ToString("N"));
            string logFile = Path.Combine(directory, "async-log-writer.log");
            Directory.CreateDirectory(directory);
            File.Create(logFile).Dispose();

            AsyncLogWriter writer = null;
            Exception bodyFailure = null;
            try
            {
                writer = new AsyncLogWriter(logFile);
                body(writer, logFile);
            }
            catch (Exception ex)
            {
                bodyFailure = ex;
                throw;
            }
            finally
            {
                bool joined = true;
                if (writer != null)
                {
                    writer.CompleteAdding();
                    joined = writer.Join(SignalTimeout);
                }

                if (joined)
                {
                    TryDeleteDirectory(directory);
                }
                else
                {
                    string message = "The log writer thread did not stop within " + SignalTimeout.TotalSeconds
                        + " s; the temporary log directory was preserved for manual cleanup: " + directory;
                    Assert.Fail(bodyFailure == null ? message : message + Environment.NewLine + bodyFailure);
                }
            }
        }

        private static void AssertJoined(AsyncLogWriter writer)
        {
            Assert.True(writer.Join(SignalTimeout), "The log writer thread did not stop within " + SignalTimeout.TotalSeconds + " s.");
        }

        private static T Await<T>(Task<T> task, string what)
        {
            Assert.True(task.Wait(SignalTimeout), "The " + what + " result was not signaled within " + SignalTimeout.TotalSeconds + " s.");
            return task.Result;
        }

        private static string NewMarker()
        {
            return "async-log-writer-test " + Guid.NewGuid().ToString("N");
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort: a leftover temp directory must not fail an otherwise green test.
            }
        }
    }
}
