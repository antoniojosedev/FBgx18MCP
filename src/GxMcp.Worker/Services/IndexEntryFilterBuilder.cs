using System;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Plan 006: shared predicate factory for the filter chains that SearchService
    // and ListService independently re-implemented over IEnumerable<SearchIndex.IndexEntry>.
    //
    // Scope note (see plans/006-shared-filter-builder.md): only predicates whose
    // logic was byte-for-byte identical in both services were centralized here
    // (description substring, date range). Everything else stayed put:
    //   - Type matching is a DELIBERATE divergence, not an oversight. SearchService
    //     uses the alias/synonym-aware IsTypeMatchAliasAware below ("prc" contains-matches
    //     "Procedure"); ListService matches an exact, caller-supplied type-name set
    //     (`filterTypes.Contains(e.Type)`, still inline in ListService.ListObjects).
    //     Unifying on alias-aware matching would make List's typeFilter over-match
    //     (e.g. "WebForm" would start matching "WebFormAttribute"), which existing
    //     List tests pin against — so both behaviors are preserved, and the
    //     alias-aware algorithm is centralized here (single source of truth) so it
    //     doesn't drift out from under SearchService's four call sites.
    //   - Domain filtering only exists in SearchService (ListService has no
    //     domainFilter parameter at all) — nothing to share, left inline.
    //   - Parent/parentPath equality-as-safety-net only exists in SearchService
    //     (ListService relies solely on the ChildrenByParent index lookup, with no
    //     follow-up equality Where) — nothing to share, left inline.
    //   - The ListService runtime-SDK fallback path (no index available) operates
    //     over RuntimeListEntry/KBObject, not SearchIndex.IndexEntry — out of scope.
    internal static class IndexEntryFilterBuilder
    {
        /// <summary>
        /// Alias/synonym-aware type match — moved verbatim from SearchService.IsTypeMatch.
        /// "prc"/"proc" match any type containing "procedure", "tab"/"table" requires an
        /// exact "table" type, etc.; falls back to case-insensitive substring containment.
        /// </summary>
        public static bool IsTypeMatchAliasAware(string type, string query)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(query)) return false;
            string t = type.ToLower(); string q = query.ToLower();
            if (q == "prc" || q == "procedure" || q == "proc") return t.Contains("procedure");
            if (q == "trn" || q == "transaction") return t.Contains("transaction");
            if (q == "tab" || q == "table") return t == "table";
            if (q == "wp" || q == "webpanel") return t.Contains("webpanel");
            if (q == "dp" || q == "dataprovider") return t.Contains("dataprovider");
            if (q == "sdt") return t.Contains("sdt");
            if (q == "attr" || q == "attribute") return t.Contains("attribute");
            return t.Contains(q);
        }

        /// <summary>Case-insensitive substring match on Description. Empty/null filter matches everything.</summary>
        public static Func<SearchIndex.IndexEntry, bool> DescriptionContains(string descriptionFilter)
        {
            if (string.IsNullOrEmpty(descriptionFilter)) return _ => true;
            return e => (e.Description ?? string.Empty).IndexOf(descriptionFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Since is inclusive: LastUpdate &gt;= since.</summary>
        public static Func<SearchIndex.IndexEntry, bool> SinceInclusive(DateTime since)
        {
            return e => e.LastUpdate >= since;
        }

        /// <summary>ModifiedBefore is exclusive and requires a known LastUpdate: DateTime.MinValue never matches.</summary>
        public static Func<SearchIndex.IndexEntry, bool> ModifiedBeforeExclusive(DateTime modifiedBefore)
        {
            return e => e.LastUpdate > DateTime.MinValue && e.LastUpdate < modifiedBefore;
        }
    }
}
