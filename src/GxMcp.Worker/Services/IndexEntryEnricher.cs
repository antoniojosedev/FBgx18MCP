using System;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class IndexEntryEnricher
    {
        private readonly Func<SearchIndex.IndexEntry, bool> _enrichDelegate;

        /// <summary>
        /// Primary form: the delegate returns true only when enrichment actually
        /// succeeded. IsEnriched is flipped ONLY on success — a delegate that
        /// swallowed an exception (e.g. the SDK lookup failed) must return false so
        /// PromoteAsync / the background drain can retry the entry later instead of
        /// leaving it permanently false-enriched for the session.
        /// </summary>
        public IndexEntryEnricher(Func<SearchIndex.IndexEntry, bool> enrichDelegate)
        {
            if (enrichDelegate == null) throw new ArgumentNullException("enrichDelegate");
            _enrichDelegate = enrichDelegate;
        }

        /// <summary>
        /// Convenience overload for delegates with no failure mode (tests, pure
        /// in-memory enrichers): completing without throwing counts as success.
        /// </summary>
        public IndexEntryEnricher(Action<SearchIndex.IndexEntry> enrichDelegate)
        {
            if (enrichDelegate == null) throw new ArgumentNullException("enrichDelegate");
            _enrichDelegate = e => { enrichDelegate(e); return true; };
        }

        public void Enrich(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return;
            if (entry.IsEnriched) return;

            bool succeeded = _enrichDelegate(entry);
            if (succeeded) entry.IsEnriched = true;
        }
    }
}
