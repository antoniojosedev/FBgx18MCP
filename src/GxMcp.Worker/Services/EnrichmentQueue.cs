using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// FIFO enrichment queue. Synchronous by design: the previous implementation used
    /// <c>await SemaphoreSlim.WaitAsync(...).ConfigureAwait(false)</c>, so contended
    /// continuations resumed on MTA threadpool threads and then called straight into
    /// the (thread-unsafe) GeneXus SDK. Nothing here needs async — a plain lock keeps
    /// the caller's thread (STA drain thread or gated dispatcher thread) for the whole
    /// SDK call. The methods keep their Task-returning signatures for source
    /// compatibility with existing callers (AnalyzeService awaits PromoteAsync).
    /// </summary>
    public class EnrichmentQueue
    {
        private readonly IndexEntryEnricher _enricher;
        private readonly ConcurrentQueue<SearchIndex.IndexEntry> _queue = new ConcurrentQueue<SearchIndex.IndexEntry>();
        private readonly object _enrichGate = new object();
        private int _pendingCount;

        public EnrichmentQueue(IndexEntryEnricher enricher)
        {
            _enricher = enricher;
        }

        public int PendingCount { get { return Volatile.Read(ref _pendingCount); } }

        public void Enqueue(SearchIndex.IndexEntry entry)
        {
            if (entry == null || entry.IsEnriched) return;
            _queue.Enqueue(entry);
            Interlocked.Increment(ref _pendingCount);
        }

        public Task DrainAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            SearchIndex.IndexEntry entry;
            while (_queue.TryDequeue(out entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_enrichGate)
                {
                    _enricher.Enrich(entry);
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
            return CompletedTask;
        }

        public Task PromoteAsync(SearchIndex.IndexEntry entry, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (entry == null || entry.IsEnriched) return CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_enrichGate)
            {
                _enricher.Enrich(entry);
            }
            return CompletedTask;
        }

        private static readonly Task CompletedTask = Task.FromResult(0);
    }
}
