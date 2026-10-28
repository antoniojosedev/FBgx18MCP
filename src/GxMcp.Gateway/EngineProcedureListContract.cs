using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The bounded `genexus_list_objects typeFilter=Procedure` page gx-engine may answer, shaped like
    /// Worker <c>ListService.BuildPagedResponseInternal</c>. Empty pages remain on the SDK: their
    /// `typesAvailable` hint describes the whole SDK index, which this route does not read.
    /// </summary>
    internal static class EngineProcedureListContract
    {
        internal const int MaxLimit = 100;
        internal static readonly string[] SuppliedFields = { "name", "type" };

        internal static string? TryPlan(JObject args, out int offset, out int limit)
        {
            offset = 0;
            limit = 0;
            string? type = args["typeFilter"]?.Type == JTokenType.String ? args["typeFilter"]!.ToString() : null;
            if (!string.Equals(type?.Trim(), "Procedure", StringComparison.OrdinalIgnoreCase))
                return "native list_objects admits only typeFilter=Procedure";
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
            offset = (int)requestedOffset;
            limit = (int)requested;
            return null;
        }

        internal static List<string> ToEngineArguments(string kbPath, int offset, int limit) =>
            new[] { "--diagnostics=json", "kb-list-procedures", kbPath }
                .Concat(EngineIntFlag.Tokens("--offset", offset))
                .Concat(EngineIntFlag.Tokens("--limit", limit))
                .ToList();

        internal static bool TryShape(
            JObject json, int offset, int limit, DateTime utcNow, out JObject? payload, out string? refusal)
        {
            payload = null;
            refusal = null;
            if (json.Count != 4 || json["objects"] is not JArray objects
                || json["total"]?.Type != JTokenType.Integer || json["offset"]?.Value<long?>() != offset
                || json["limit"]?.Value<long?>() != limit)
            {
                refusal = "kb-list-procedures returned an unexpected page envelope";
                return false;
            }
            long total = json["total"]!.Value<long>();
            long expected = Math.Max(0, Math.Min(limit, total - offset));
            if (total <= 0 || total > int.MaxValue || objects.Count == 0 || objects.Count != expected)
            {
                // An empty page (including offset past the end) keeps the SDK's index-wide typesAvailable hint.
                refusal = "native Procedure pages must be nonempty and match the requested window";
                return false;
            }

            var results = new JArray();
            foreach (JToken token in objects)
            {
                if (!TryItem(token, out JObject? item))
                {
                    refusal = "kb-list-procedures returned a malformed Procedure row";
                    return false;
                }
                results.Add(item!);
            }
            payload = Page(results, (int)total, offset, limit, utcNow);
            // Emulate the Worker JSON hop, so dates take the same Gateway representation.
            payload = JObject.Parse(payload.ToString(Formatting.None));
            return true;
        }

        private static bool TryItem(JToken token, out JObject? item)
        {
            item = null;
            if (token is not JObject row || row.Count != 3 || row["name"]?.Type != JTokenType.String
                || !string.Equals(row["type"]?.ToString(), "Procedure", StringComparison.Ordinal))
                return false;
            string name = row["name"]!.ToString();
            if (name.Length == 0 || name.Any(char.IsControl)) return false;
            item = new JObject { ["name"] = name, ["type"] = "Procedure" };
            JToken? ticks = row["last_update_ticks"];
            if (ticks?.Type == JTokenType.Null) return true;
            if (ticks?.Type != JTokenType.Integer) return false;
            long value = ticks.Value<long>();
            if (value <= DateTime.MinValue.Ticks || value > DateTime.MaxValue.Ticks) return false;
            // Stored ObjectSaved values matched the SDK UTC display on the live fixture.
            item["lastUpdate"] = new DateTime(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
            return true;
        }

        private static JObject Page(JArray results, int total, int offset, int limit, DateTime utcNow)
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
            var meta = new JObject { ["aggregates"] = Aggregates(results, utcNow) };
            var top = (JObject)results[0];
            meta["suggested_next"] = new JObject
            {
                ["tool"] = "genexus_read",
                ["args"] = new JObject { ["name"] = top["name"]!.ToString(), ["type"] = "Procedure" },
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

        private static JObject Aggregates(JArray results, DateTime utcNow)
        {
            var aggregates = new JObject
            {
                ["total"] = results.Count,
                ["by_type"] = new JObject { ["Procedure"] = results.Count },
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
