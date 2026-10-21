using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Single-consumer asynchronous log appender extracted from <see cref="Logger"/> (issue #605).
    /// Producers hand lines over through <see cref="Enqueue(string)"/>; one background thread drains the
    /// unbounded queue in batches and appends each batch with one <see cref="File.AppendAllText(string, string)"/>.
    /// The type knows nothing about directories, rotation, formatting or stderr: the façade owns those.
    /// <para>
    /// The production path passes no receipt and allocates neither receipt nor completion sources. A receipt
    /// is an optional, test-facing observation handle (see <see cref="AppendReceipt"/>).
    /// </para>
    /// </summary>
    internal sealed class AsyncLogWriter
    {
        // Same batch predicate as the original Logger: characters, checked before each extra take.
        private const int BatchCharLimit = 64 * 1024;

        private readonly string _filePath;
        private readonly BlockingCollection<LogQueueItem> _queue = new BlockingCollection<LogQueueItem>();
        // Protects, in this order: "acceptance still open?", BlockingCollection.Add and the first-wins
        // acceptance reservation. CompleteAdding closes acceptance under the same gate, so closing never
        // interleaves with Add. Join and every TaskCompletionSource signal happen outside it.
        private readonly object _gate = new object();
        private readonly Thread _thread;
        private bool _acceptanceClosed;   // guarded by _gate

        /// <summary>
        /// Creates the writer and starts its background thread as the LAST action, after queue, gate and
        /// state exist. A failure to create or start the thread propagates to the caller.
        /// </summary>
        internal AsyncLogWriter(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A log file path is required.", nameof(filePath));
            _filePath = filePath;
            _thread = new Thread(ConsumerLoop)
            {
                IsBackground = true,
                Name = "LoggerWriter"
            };
            _thread.Start();
        }

        /// <summary>Production path: no receipt, no receipt/TCS allocation. A refused line is dropped silently.</summary>
        internal void Enqueue(string line)
        {
            Enqueue(line, null);
        }

        /// <summary>
        /// Queues a line. With a receipt, acceptance becomes Accepted only after Add returns normally;
        /// a closed gate or an InvalidOperationException from Add yields Rejected + NotEnqueued instead.
        /// </summary>
        internal void Enqueue(string line, AppendReceipt receipt)
        {
            var item = new LogQueueItem(line, receipt);
            try
            {
                lock (_gate)
                {
                    if (_acceptanceClosed)
                    {
                        Refuse(receipt, null);
                        return;
                    }

                    try
                    {
                        _queue.Add(item);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Defensive: closing happens under this same gate, so this is not an expected race.
                        Refuse(receipt, ex);
                        return;
                    }

                    receipt?.TryReserveAcceptance(AcceptanceResult.Accepted);
                }
            }
            finally
            {
                // Outside the gate. The consumer may already have published Append for an accepted item.
                receipt?.SignalAcceptance();
                receipt?.SignalAppend();
            }
        }

        /// <summary>
        /// Closes acceptance and completes the queue so the consumer drains what was already accepted
        /// and stops. Idempotent: the underlying BlockingCollection.CompleteAdding runs at most once.
        /// </summary>
        internal void CompleteAdding()
        {
            lock (_gate)
            {
                if (_acceptanceClosed) return;
                _acceptanceClosed = true;
                _queue.CompleteAdding();
            }
        }

        /// <summary>Waits for the consumer thread. Always called outside the gate.</summary>
        internal bool Join(TimeSpan timeout)
        {
            return _thread.Join(timeout);
        }

        // Caller holds the gate.
        private static void Refuse(AppendReceipt receipt, Exception cause)
        {
            if (receipt == null) return;
            receipt.TryReserveAcceptance(new AcceptanceResult(AcceptanceState.Rejected, cause));
            receipt.TryReserveAppend(new AppendResult(AppendState.NotEnqueued, cause));
        }

        private void ConsumerLoop()
        {
            var batch = new StringBuilder(8192);
            var batchItems = new List<LogQueueItem>();   // items assigned to the in-flight batch
            LogQueueItem currentItem = null;              // delivered/taken but not yet transferred to the batch
            bool appendAttemptStarted = false;            // per batch; set right before File.AppendAllText
            Exception preparationError = null;
            Exception appendError = null;
            Exception exitError = null;

            try
            {
                foreach (var delivered in _queue.GetConsumingEnumerable())
                {
                    currentItem = delivered;
                    appendAttemptStarted = false;
                    preparationError = null;
                    appendError = null;

                    // Preparation: transfer each item to the batch collection, format it and append its line.
                    // The first item is always taken; extra items are drained while the batch is below the
                    // character limit (checked before each take, so the last line may overshoot it).
                    string payload;
                    try
                    {
                        batch.Clear();
                        batchItems.Clear();
                        do
                        {
                            batchItems.Add(currentItem);
                            string line = currentItem.Line;
                            currentItem = null;
                            batch.AppendLine(line);
                        }
                        while (batch.Length < BatchCharLimit && _queue.TryTake(out currentItem));
                        payload = batch.ToString();
                    }
                    catch (Exception ex)
                    {
                        // Preparation failed: the in-flight items (including currentItem) are Failed; keep serving.
                        preparationError = ex;
                        SettleInFlight(currentItem, batchItems, new AppendResult(AppendState.Failed, ex));
                        currentItem = null;
                        batchItems.Clear();
                        continue;
                    }

                    bool appended = false;
                    try
                    {
                        appendAttemptStarted = true;
                        File.AppendAllText(_filePath, payload);
                        appended = true;
                    }
                    catch (Exception ex)
                    {
                        appendError = ex;
                    }

                    // Reserve first (nothing else may fail in between), then signal. A failed append (disk full,
                    // locked file) never ends the consumer; it moves on to the next batch. Without a receipt the
                    // failure stays silent: the facade already mirrored each line to stderr.
                    ReserveAppend(batchItems, appended
                        ? AppendResult.Written
                        : new AppendResult(AppendState.Failed, appendError));
                    SignalBatch(batchItems);
                    batchItems.Clear();
                    appendAttemptStarted = false;
                    appendError = null;
                }
            }
            catch (Exception ex)
            {
                // Ordinary exception outside batch handling: record it and end the consumer without
                // rethrowing to the thread. The finally block settles whatever is still pending.
                exitError = ex;
            }
            finally
            {
                SettleOnExit(currentItem, batchItems, appendAttemptStarted, preparationError, appendError, exitError);
            }
        }

        // Last resort when the consumer leaves the loop (normal end: nothing is pending). Must not throw:
        // an exception escaping a finally on this thread would take the whole process down.
        private void SettleOnExit(
            LogQueueItem currentItem,
            List<LogQueueItem> batchItems,
            bool appendAttemptStarted,
            Exception preparationError,
            Exception appendError,
            Exception exitError)
        {
            try
            {
                // Idempotent: closes acceptance and completes the queue under the gate (also after a normal shutdown).
                CompleteAdding();

                // In-flight items: preparation failure prevails; otherwise an unresolved started append is
                // Failed (result unknown), and an item that never reached the append is NotProcessed.
                // Results already reserved (Written/Failed) are never reclassified: reservation is first-wins.
                if (currentItem != null || batchItems.Count > 0)
                {
                    AppendResult inFlight;
                    if (preparationError != null)
                    {
                        inFlight = new AppendResult(AppendState.Failed, preparationError);
                    }
                    else if (appendAttemptStarted)
                    {
                        inFlight = new AppendResult(AppendState.Failed, appendError ?? exitError ?? new InvalidOperationException(
                            "The append result is unknown: the consumer ended after the append attempt started."));
                    }
                    else
                    {
                        inFlight = new AppendResult(AppendState.NotProcessed, exitError);
                    }

                    SettleInFlight(currentItem, batchItems, inFlight);
                }

                // Items still queued never went through preparation (nothing is left after a normal shutdown).
                AppendResult notProcessed = null;
                LogQueueItem queued;
                while (_queue.TryTake(out queued))
                {
                    if (notProcessed == null) notProcessed = new AppendResult(AppendState.NotProcessed, exitError);
                    Settle(queued, notProcessed);
                }
            }
            catch
            {
                // Best effort: never let the consumer's exit path throw.
            }
        }

        private void SettleInFlight(LogQueueItem currentItem, List<LogQueueItem> batchItems, AppendResult result)
        {
            if (currentItem != null) Settle(currentItem, result);
            for (int i = 0; i < batchItems.Count; i++)
            {
                Settle(batchItems[i], result);
            }
        }

        private void Settle(LogQueueItem item, AppendResult result)
        {
            AppendReceipt receipt = item.Receipt;
            if (receipt == null) return;
            receipt.TryReserveAppend(result);
            receipt.SignalAppend();
            SettleAcceptance(receipt);
        }

        private static void ReserveAppend(List<LogQueueItem> items, AppendResult result)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Receipt?.TryReserveAppend(result);
            }
        }

        private void SignalBatch(List<LogQueueItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                AppendReceipt receipt = items[i].Receipt;
                if (receipt == null) continue;
                receipt.SignalAppend();
                SettleAcceptance(receipt);
            }
        }

        // An item the consumer holds was necessarily added, so its acceptance is Accepted. Taking the gate
        // makes Enqueue's reservation visible (first-wins keeps whichever came first) so Acceptance never
        // outlives the consumer's own settlement; the signal itself happens outside the gate.
        private void SettleAcceptance(AppendReceipt receipt)
        {
            lock (_gate)
            {
                receipt.TryReserveAcceptance(AcceptanceResult.Accepted);
            }
            receipt.SignalAcceptance();
        }
    }
}
