using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>One engine `kb-query` invocation derived from a `genexus_query` call.</summary>
    internal sealed class EngineQueryPlan
    {
        internal string? Type { get; init; }

        /// <summary>Requested page size; 0 asks for the SDK default (50).</summary>
        internal int Limit { get; init; }
    }

    /// <summary>
    /// What gx-engine may answer for `genexus_query`, and the SDK-shaped response
    /// it must produce when it does. The SDK ranks fuzzy text with a vector
    /// index the engine does not have, and its exact-name branch has its own
    /// envelope (no match_quality, ignores `limit`), so the only admitted query is
    /// a bare `type:X` filter, which the SDK answers as a score-then-name browse
    /// the engine reproduces. The engine sees only the object kinds it serves, so
    /// an empty result, any object name and `usedby:` (whose SDK answer unions
    /// same-named targets and SDK-only edges) are left to the SDK. Rows carry only
    /// name and type, and the SDK gateway projects `fields` over query rows, so a
    /// call must select exactly those fields for the two answers to match.
    /// </summary>
    internal static class EngineQueryContract
    {
        internal const int SdkDefaultLimit = 50;
        internal const int EngineMaxLimit = 200;

        /// <summary>The row fields the engine supplies from storage.</summary>
        internal static readonly string[] SuppliedFields = { "name", "type" };

        // SDK `type:` matching is alias-aware and substring-based
        // (IndexEntryFilterBuilder.IsTypeMatchAliasAware). Only the spellings
        // whose SDK match set is exactly one engine object kind are admitted; the
        // engine's own looser spellings (StructuredDataType, PROC, WEB_PANEL, ...)
        // would return objects the SDK filter does not. WebPanel/wp also defer:
        // the live UDM WebPanel set includes objects absent from the SDK's panel set.
        private static readonly Dictionary<string, string> EngineTypeByToken =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["procedure"] = "Procedure", ["proc"] = "Procedure", ["prc"] = "Procedure",
                ["transaction"] = "Transaction", ["trn"] = "Transaction",
                ["table"] = "Table", ["tab"] = "Table",
                ["dataprovider"] = "DataProvider", ["dp"] = "DataProvider",
                ["sdt"] = "SDT",
                ["attribute"] = "Attribute", ["attr"] = "Attribute",
                ["domain"] = "Domain",
                ["dataselector"] = "DataSelector",
                ["module"] = "Module",
                ["designsystem"] = "DesignSystem",
                ["dataview"] = "DataView",
            };

        /// <summary>The engine spelling of an SDK type token, or null when there is none.</summary>
        internal static string? EngineTypeFor(string token) =>
            EngineTypeByToken.TryGetValue(token.Trim(), out string? engineType) ? engineType : null;

        internal static string SupportedTypeTokens =>
            string.Join(", ", EngineTypeByToken.Keys.OrderBy(key => key, StringComparer.Ordinal));

        /// <summary>
        /// Plans the engine invocation, or returns the reason the call is outside
        /// the engine query space (then <paramref name="plan"/> is null).
        /// </summary>
        internal static string? TryPlan(JObject args, out EngineQueryPlan? plan)
        {
            plan = null;
            string? refusal = EngineFieldSelection.TryRead(args, "query", SuppliedFields, out _);
            if (refusal != null) return refusal;
            refusal = TryParseTypeFilter(EngineRoutedOperations.EngineArgString(args, "query") ?? "", out string? type);
            if (refusal != null) return refusal;

            string? typeFilter = EngineRoutedOperations.EngineArgString(args, "typeFilter");
            if (!string.IsNullOrEmpty(typeFilter))
            {
                // The SDK preserves an external filter verbatim, unlike parsed query terms.
                if (!string.Equals(typeFilter, typeFilter.Trim(), StringComparison.Ordinal))
                    return "leading or trailing whitespace in `typeFilter` requires SDK matching unchanged";
                if (type != null)
                    return "`typeFilter` combined with a `type:` query term is ambiguous in the engine query space";
                type = typeFilter;
            }

            refusal = TryAdmitType(type, out string? engineType);
            if (refusal != null) return refusal;
            refusal = TryReadLimit(args["limit"], out int limit);
            if (refusal != null) return refusal;

            plan = new EngineQueryPlan { Type = engineType, Limit = limit };
            return null;
        }

        /// <summary>Reads the query as exactly one `type:` filter; any other term is outside the space.</summary>
        private static string? TryParseTypeFilter(string query, out string? type)
        {
            type = null;
            foreach (string token in query.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                string? refusal = colon <= 0
                    ? $"the query term `{token}` is an object name or text search: the SDK ranks it (fuzzy) or answers it from its exact-name branch (different envelope and `limit` handling), which the engine does not reproduce"
                    : TryTakeFilter(token.Substring(0, colon).ToLowerInvariant(), token.Substring(colon + 1).Trim(), ref type);
                if (refusal != null) return refusal;
            }
            return null;
        }

        private static string? TryTakeFilter(string prefix, string value, ref string? type)
        {
            if (value.Length == 0) return $"`{prefix}:` requires a value in the engine query space";
            if (prefix == "usedby") return UsedByRefusal;
            if (prefix != "type")
                return $"query prefix `{prefix}:` is outside the engine query space yet (supported: `type:`)";
            if (type != null) return "multiple `type:` filters are outside the engine query space yet";
            type = value;
            return null;
        }

        private static readonly char[] WhitespaceSeparators = { ' ', (char)9, (char)13, (char)10 };

        private const string UsedByRefusal =
            "`usedby:` is outside the engine query space: the SDK unions the referrers of every same-named target and its SDK-only call/table edges, which the engine's reference table does not reproduce";

        /// <summary>The engine spelling of the `type:` filter, or the reason there is none.</summary>
        private static string? TryAdmitType(string? type, out string? engineType)
        {
            engineType = null;
            if (type == null)
                return "an empty query is outside the engine query space yet; pass `type:X`";
            engineType = EngineTypeFor(type);
            return engineType == null
                ? $"type `{type}` has no engine spelling whose result set equals the SDK's alias-aware type filter (supported: {SupportedTypeTokens})"
                : null;
        }

        private static string? TryReadLimit(JToken? token, out int limit)
        {
            limit = 0;
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer)
                return "`limit` must be an integer in the engine query space";
            long requested = token.Value<long>();
            if (requested < 1 || requested > EngineMaxLimit)
                return $"`limit` {requested} is outside 1..{EngineMaxLimit}: the SDK returns every match for limit<=0 and the engine pages at most {EngineMaxLimit}";
            limit = (int)requested;
            return null;
        }

        /// <summary>Engine `kb-query` arguments for a plan.</summary>
        internal static List<string> ToEngineArguments(string kbPath, EngineQueryPlan plan)
        {
            var engineArgs = new List<string> { "--diagnostics=json", "kb-query", kbPath };
            if (plan.Type != null) { engineArgs.Add("--type"); engineArgs.Add(plan.Type); }
            if (plan.Limit > 0)
            {
                engineArgs.Add("--limit");
                engineArgs.Add(plan.Limit.ToString(CultureInfo.InvariantCulture));
            }
            return engineArgs;
        }

        /// <summary>
        /// The SDK query envelope for an engine answer (an empty answer is
        /// refused): `results`, counts, `pagination` (with `nextOffset`) and
        /// `_meta.match_quality`, like <c>SearchService.Search</c>.
        /// </summary>
        internal static bool TryShape(
            EngineQueryPlan plan, JObject engineJson, JObject engineInfo,
            out JObject? payload, out string? refusal)
        {
            payload = null;
            refusal = null;
            var results = new JArray();
            if (engineJson["objects"] is JArray objects)
            {
                foreach (JObject entry in objects.OfType<JObject>())
                {
                    results.Add(new JObject
                    {
                        ["name"] = entry["name"]?.ToString(),
                        ["type"] = entry["type"]?.ToString(),
                    });
                }
            }
            int total = engineJson["total"]?.ToObject<int?>() ?? results.Count;
            bool truncated = engineJson["truncated"]?.ToObject<bool?>() ?? false;

            // The engine catalog is incomplete (it serves a subset of the SDK's object kinds),
            // so an empty answer is not proof that the SDK index has no match.
            if (results.Count == 0)
            {
                refusal = "the engine found no match, and an empty answer is not authoritative: its catalog covers only the object kinds it serves";
                return false;
            }

            payload = new JObject
            {
                ["results"] = results,
                ["count"] = results.Count,
                ["total"] = total,
                ["hasMore"] = truncated,
                ["pagination"] = new JObject
                {
                    ["offset"] = 0,
                    ["limit"] = plan.Limit > 0 ? plan.Limit : SdkDefaultLimit,
                    ["returned"] = results.Count,
                    ["total"] = total,
                    ["hasMore"] = truncated,
                    ["nextOffset"] = truncated ? (JToken)results.Count : JValue.CreateNull(),
                },
                ["_meta"] = BuildMeta((JObject)results[0]),
                ["engine"] = engineInfo,
            };
            // Worker wire serialization omits null properties; a terminal page has no nextOffset.
            if (!truncated) ((JObject)payload["pagination"]!).Remove("nextOffset");
            return true;
        }

        /// <summary>
        /// Mirrors the match_quality rule of the SDK search for a query without
        /// free-text terms: the SDK's browsing scores rank Module rows (1000) above
        /// the rest (1), so a Module on top is "substring" (score >= 500) and
        /// anything else is "vector". A term-less top row is never an exact or
        /// prefix match, so no `suggested_next` is produced.
        /// </summary>
        internal static JObject BuildMeta(JObject top) => new JObject
        {
            ["match_quality"] = string.Equals(top["type"]?.ToString(), "Module", StringComparison.OrdinalIgnoreCase)
                ? "substring"
                : "vector",
        };
    }
}
