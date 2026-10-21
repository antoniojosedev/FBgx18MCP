using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>The outcome of comparing a restored index with the objects the open KB currently holds.</summary>
    internal sealed class IndexCatalogReconciliation
    {
        internal static IndexCatalogReconciliation Refused(string reason) =>
            new IndexCatalogReconciliation(false, new string[0], new object[0]) { SkipReason = reason };

        internal IndexCatalogReconciliation(bool swept, IReadOnlyList<string> staleGuids, IReadOnlyList<object> missingObjects)
        {
            Swept = swept;
            StaleGuids = staleGuids;
            MissingObjects = missingObjects;
        }

        /// <summary>Why an unsafe enumeration was not trusted; null when <see cref="Swept"/>.</summary>
        internal string SkipReason { get; private set; }

        /// <summary>True when the current catalog was enumerated and compared.</summary>
        internal bool Swept { get; }

        /// <summary>Guids the restored index holds that the KB no longer has.</summary>
        internal IReadOnlyList<string> StaleGuids { get; }

        /// <summary>KB objects whose guid the restored index does not hold.</summary>
        internal IReadOnlyList<object> MissingObjects { get; }
    }

    /// <summary>
    /// Reconciles a restored warm index with the KB catalog after the high-water-mark delta.
    /// <c>Objects.GetKeys(hwm)</c> reports neither a deleted object nor an object that
    /// appeared with a last-update older than the mark (a restored backup, an XPZ import),
    /// so only a comparison of the guid sets can correct those.
    /// </summary>
    internal static class IndexCatalogReconciler
    {
        /// <summary>
        /// Compares <paramref name="storedGuids"/> with one enumeration of <paramref name="catalog"/>.
        /// The catalog is only enumerated: the SDK's <c>DesignModel.Objects</c> exposes no
        /// <c>Count</c>, so a count-gated sweep silently never ran. An enumeration that is empty
        /// while the index is not, or in which any guid could not be read, is not trusted to
        /// delete anything and returns a refusal (<see cref="IndexCatalogReconciliation.SkipReason"/>).
        /// </summary>
        internal static IndexCatalogReconciliation Reconcile(
            object catalog, Func<object, string> guidOf, ICollection<string> storedGuids, Action progress = null)
        {
            var stored = new HashSet<string>(storedGuids, StringComparer.OrdinalIgnoreCase);
            var currentGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<object>();
            foreach (object catalogObject in (IEnumerable)catalog)
            {
                string guid;
                try { guid = guidOf(catalogObject); }
                catch (Exception ex) { return IndexCatalogReconciliation.Refused("a catalog guid could not be read: " + ex.Message); }
                if (string.IsNullOrEmpty(guid)) return IndexCatalogReconciliation.Refused("a catalog object reported an empty guid");
                progress?.Invoke();
                if (currentGuids.Add(guid) && !stored.Contains(guid)) missing.Add(catalogObject);
            }
            if (currentGuids.Count == 0 && stored.Count > 0)
                return IndexCatalogReconciliation.Refused("the catalog enumerated no objects while the restored index holds " + stored.Count);

            var stale = storedGuids.Where(guid => !currentGuids.Contains(guid)).ToList();
            return new IndexCatalogReconciliation(true, stale, missing);
        }

        internal sealed class Applied
        {
            internal int Removed;
            internal int Added;
            internal bool Superseded;
        }

        /// <summary>
        /// Applies a reconciliation to the index. A refused reconciliation, or one in which a live
        /// object could not be indexed, throws: the index is then not known to match the KB and the
        /// caller must not publish it as current. <paramref name="isCurrent"/> fences every
        /// mutation so a superseded operation generation stops at once (<see cref="Applied.Superseded"/>).
        /// </summary>
        internal static Applied Apply(
            IndexCatalogReconciliation reconciliation, Func<bool> isCurrent, Action<string> removeStale, Action<object> addMissing, Action progress = null)
        {
            if (!reconciliation.Swept)
                throw new InvalidOperationException("catalog reconciliation refused: " + reconciliation.SkipReason);

            var applied = new Applied();
            foreach (var guid in reconciliation.StaleGuids)
            {
                if (!isCurrent()) { applied.Superseded = true; return applied; }
                removeStale(guid); applied.Removed++;
            }
            int failures = 0;
            string firstFailure = null;
            foreach (var missing in reconciliation.MissingObjects)
            {
                if (!isCurrent()) { applied.Superseded = true; return applied; }
                try { addMissing(missing); }
                catch (Exception ex) { failures++; if (firstFailure == null) firstFailure = ex.Message; continue; }
                applied.Added++;
                progress?.Invoke(); // outside the try: a heartbeat fault is not an indexing failure
            }
            if (failures > 0)
                throw new InvalidOperationException(
                    "catalog reconciliation incomplete: " + failures + " of " + reconciliation.MissingObjects.Count
                    + " live objects could not be indexed (first: " + firstFailure + ")");
            return applied;
        }
    }
}
