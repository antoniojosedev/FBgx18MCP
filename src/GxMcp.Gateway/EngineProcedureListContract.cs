using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal readonly record struct EngineListPageWindow(string ExpectedType, int Offset, int Limit);

    /// <summary>
    /// The bounded `genexus_list_objects` page gx-engine may answer, shaped like
    /// Worker <c>ListService.BuildPagedResponseInternal</c>. Empty pages remain on the SDK: their
    /// `typesAvailable` hint describes the whole SDK index, which this route does not read.
    /// </summary>
    internal static class EngineProcedureListContract
    {
        internal const int MaxLimit = 100;
        internal static readonly string[] SuppliedFields = { "name", "type" };

        private static readonly Dictionary<string, string> AdmittedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Procedure"] = "Procedure",
            ["Proc"] = "Procedure",
            ["Transaction"] = "Transaction",
            ["Trn"] = "Transaction",
            ["WebPanel"] = "WebPanel",
            ["WP"] = "WebPanel",
            ["Table"] = "Table",
            ["DataProvider"] = "DataProvider",
        };

        internal static string? TryPlan(JObject args, out EngineListPageWindow window)
        {
            window = default;
            string? type = args["typeFilter"]?.Type == JTokenType.String ? args["typeFilter"]!.ToString() : null;
            if (string.IsNullOrWhiteSpace(type) || !AdmittedTypes.TryGetValue(type.Trim(), out string? canonicalType))
                return "native list_objects admits only typeFilter in (Procedure, Transaction, WebPanel, Table, DataProvider)";
            if (args["limit"]?.Type != JTokenType.Integer)
                return "native list_objects requires an explicit integer `limit`";
            long requested = args["limit"]!.Value<long>();
            if (requested < 1 || requested > MaxLimit)
                return $"native list_objects requires `limit` 1..{MaxLimit}";
            JToken? offsetToken = args["offset"];
            long requestedOffset = 0;
            if (offsetToken != null && offsetToken.Type != JTokenType.Null)
            {
                if (offsetToken.Type != JTokenType.Integer) return "`offset` must be an integer";
                requestedOffset = offsetToken.Value<long>();
            }
            if (requestedOffset < 0 || requestedOffset > int.MaxValue)
                return "native list_objects requires an Int32 nonnegative `offset`";
            window = new EngineListPageWindow(canonicalType!, (int)requestedOffset, (int)requested);
            return null;
        }

        internal static List<string> ToEngineArguments(string kbPath, EngineListPageWindow window)
        {
            if (string.Equals(window.ExpectedType, "Procedure", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "--diagnostics=json", "kb-list-procedures", kbPath }
                    .Concat(EngineIntFlag.Tokens("--offset", window.Offset))
                    .Concat(EngineIntFlag.Tokens("--limit", window.Limit))
                    .ToList();
            }
            return new[] { "--diagnostics=json", "kb-list-objects", kbPath, "--type", window.ExpectedType }
                .Concat(EngineIntFlag.Tokens("--offset", window.Offset))
                .Concat(EngineIntFlag.Tokens("--limit", window.Limit))
                .ToList();
        }

        internal static bool TryShapeListResponse(
            JObject json, EngineListPageWindow window, DateTime utcNow, out JObject? payload, out string? refusal)
        {
            payload = null;
            refusal = null;
            if (json.Count != 4 || json["objects"] is not JArray objects
                || json["total"]?.Type != JTokenType.Integer || json["offset"]?.Value<long?>() != window.Offset
                || json["limit"]?.Value<long?>() != window.Limit)
            {
                refusal = "kb-list-objects returned an unexpected page envelope";
                return false;
            }
            long total = json["total"]!.Value<long>();
            long expected = Math.Max(0, Math.Min(window.Limit, total - window.Offset));
            if (total <= 0 || total > int.MaxValue || objects.Count == 0 || objects.Count != expected)
            {
                // An empty page (including offset past the end) keeps the SDK's index-wide typesAvailable hint.
                refusal = $"native {window.ExpectedType} pages must be nonempty and match the requested window";
                return false;
            }

            var results = new JArray();
            foreach (JToken token in objects)
            {
                if (!TryItem(token, window.ExpectedType, out JObject? item))
                {
                    refusal = $"kb-list-objects returned a malformed {window.ExpectedType} row";
                    return false;
                }
                results.Add(item!);
            }
            payload = Page(results, window.ExpectedType, (int)total, window.Offset, window.Limit, utcNow);
            // Emulate the Worker JSON hop, so dates take the same Gateway representation.
            payload = JObject.Parse(payload.ToString(Formatting.None));
            return true;
        }

        private static bool TryItem(JToken token, string expectedType, out JObject? item)
        {
            item = null;
            if (token is not JObject row || row.Count != 3 || row["name"]?.Type != JTokenType.String
                || !string.Equals(row["type"]?.ToString(), expectedType, StringComparison.OrdinalIgnoreCase))
                return false;
            string name = row["name"]!.ToString();
            if (name.Length == 0 || name.Any(char.IsControl)) return false;
            item = new JObject { ["name"] = name, ["type"] = expectedType };
            JToken? ticks = row["last_update_ticks"];
            if (ticks?.Type == JTokenType.Null) return true;
            if (ticks?.Type != JTokenType.Integer) return false;
            long value = ticks.Value<long>();
            if (value <= DateTime.MinValue.Ticks || value > DateTime.MaxValue.Ticks) return false;
            // Stored ObjectSaved values matched the SDK UTC display on the live fixture.
            item["lastUpdate"] = new DateTime(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
            return true;
        }

        private static JObject Page(JArray results, string expectedType, int total, int offset, int limit, DateTime utcNow)
        {
            int consumed = offset + results.Count;
            bool hasMore = consumed < total;
            var response = new JObject
            {
                ["count"] = results.Count,
                ["total"] = total,
                ["offset"] = offset,
                ["hasMore"] = hasMore,
            };
            if (hasMore) response["nextOffset"] = consumed;
            response["results"] = results;
            response["pagination"] = new JObject
            {
                ["offset"] = offset, ["limit"] = limit, ["returned"] = results.Count, ["total"] = total,
                ["hasMore"] = hasMore, ["nextOffset"] = hasMore ? consumed : JValue.CreateNull(),
            };
            var meta = new JObject { ["aggregates"] = Aggregates(results, expectedType, utcNow) };
            var top = (JObject)results[0];
            meta["suggested_next"] = new JObject
            {
                ["tool"] = "genexus_read",
                ["args"] = new JObject { ["name"] = top["name"]!.ToString(), ["type"] = expectedType },
            };
            if (results.Any(row => row["lastUpdate"] != null))
            {
                meta["alternative_views"] = new JObject
                {
                    ["recently_changed"] = new JObject
                    {
                        ["tool"] = "genexus_list_objects",
                        ["args"] = new JObject { ["sort"] = "lastUpdate", ["limit"] = 20 },
                    },
                };
            }
            response["_meta"] = meta;
            return response;
        }

        private static JObject Aggregates(JArray results, string expectedType, DateTime utcNow)
        {
            var aggregates = new JObject
            {
                ["total"] = results.Count,
                ["by_type"] = new JObject { [expectedType] = results.Count },
            };
            DateTime[] updates = results
                .Select(row => row["lastUpdate"]?.ToString())
                .Where(value => value != null)
                .Select(value => DateTime.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                .ToArray();
            if (updates.Length == 0) return aggregates;
            aggregates["lastUpdate"] = new JObject
            {
                ["min"] = updates.Min().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["max"] = updates.Max().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            };
            DateTime cutoff = utcNow.AddDays(-7);
            aggregates["modified_last_7d"] = updates.Count(update => update >= cutoff);
            return aggregates;
        }
    }
}
