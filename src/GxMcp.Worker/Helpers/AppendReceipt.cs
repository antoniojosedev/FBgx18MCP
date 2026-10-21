using System;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Outcome of handing a line to <see cref="AsyncLogWriter"/>. <see cref="Pending"/> only describes
    /// a receipt whose task has not completed yet; a completed task always carries a terminal state.
    /// </summary>
    internal enum AcceptanceState
    {
        Pending,
        Accepted,
        Rejected
    }

    /// <summary>
    /// Outcome of appending an accepted line to the log file. <see cref="Written"/> only means that
    /// <c>File.AppendAllText</c> returned normally; it is not a durability promise.
    /// </summary>
    internal enum AppendState
    {
        Pending,
        Written,
        Failed,
        NotProcessed,
        NotEnqueued
    }

    internal sealed class AcceptanceResult
    {
        internal static readonly AcceptanceResult Accepted = new AcceptanceResult(AcceptanceState.Accepted, null);

        internal AcceptanceResult(AcceptanceState state, Exception exception)
        {
            State = state;
            Exception = exception;
            HResult = exception?.HResult;
        }

        public AcceptanceState State { get; }
        public Exception Exception { get; }
        public int? HResult { get; }
    }

    internal sealed class AppendResult
    {
        internal static readonly AppendResult Written = new AppendResult(AppendState.Written, null);

        internal AppendResult(AppendState state, Exception exception)
        {
            State = state;
            Exception = exception;
            HResult = exception?.HResult;
        }

        public AppendState State { get; }
        public Exception Exception { get; }
        public int? HResult { get; }
    }

    /// <summary>
    /// Observation handle for one line. Both tasks always complete with a result (never faulted or
    /// canceled), so Exception/HResult are data of the result, not task failures. The two tasks are
    /// independent: the consumer may publish <see cref="Append"/> before <see cref="Acceptance"/>.
    /// Each terminal result is reserved first-wins and then signaled; the writer signals only after
    /// leaving its gate, and continuations never run inline (RunContinuationsAsynchronously).
    /// </summary>
    internal sealed class AppendReceipt
    {
        private readonly TaskCompletionSource<AcceptanceResult> _acceptance =
            new TaskCompletionSource<AcceptanceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AppendResult> _append =
            new TaskCompletionSource<AppendResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Written only while the writer holds its gate (first-wins); read lock-free by the signal step.
        private AcceptanceResult _acceptanceReserved;
        // Claimed by compare-and-exchange (first-wins).
        private AppendResult _appendReserved;

        public Task<AcceptanceResult> Acceptance => _acceptance.Task;
        public Task<AppendResult> Append => _append.Task;

        /// <summary>Caller must hold the writer gate.</summary>
        internal bool TryReserveAcceptance(AcceptanceResult result)
        {
            if (_acceptanceReserved != null) return false;
            _acceptanceReserved = result;
            return true;
        }

        internal bool TryReserveAppend(AppendResult result)
        {
            return Interlocked.CompareExchange(ref _appendReserved, result, null) == null;
        }

        /// <summary>Completes the acceptance task with the reserved result, if any. Never call under the gate.</summary>
        internal void SignalAcceptance()
        {
            AcceptanceResult reserved = Volatile.Read(ref _acceptanceReserved);
            if (reserved != null) _acceptance.TrySetResult(reserved);
        }

        /// <summary>Completes the append task with the reserved result, if any. Never call under the gate.</summary>
        internal void SignalAppend()
        {
            AppendResult reserved = Volatile.Read(ref _appendReserved);
            if (reserved != null) _append.TrySetResult(reserved);
        }
    }

    /// <summary>Immutable queue entry: the line plus an optional receipt (null on the production path).</summary>
    internal sealed class LogQueueItem
    {
        internal LogQueueItem(string line, AppendReceipt receipt)
        {
            Line = line;
            Receipt = receipt;
        }

        public string Line { get; }
        public AppendReceipt Receipt { get; }
    }
}
