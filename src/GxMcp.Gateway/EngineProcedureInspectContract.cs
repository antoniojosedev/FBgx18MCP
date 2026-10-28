using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The `genexus_inspect type=Procedure projection=minimal` answer gx-engine may serve, shaped like
    /// Worker <c>AnalyzeService.GetConversionContext</c> for the minimal projection.
    /// </summary>
    internal static class EngineProcedureInspectContract
    {
        /// <summary>The SDK type descriptor GUID of Procedure objects (<c>KBObject.Key.Type</c>).</summary>
        internal const string ProcedureTypeGuid = "84a12160-f59b-4ad7-a683-ea4481ac23e9";
        internal const string Hint = "Minimal projection. Pass projection=standard for variables+signature+source heads, or projection=verbose for full detail.";

        private static readonly string[] EngineKeys =
        {
            "availableParts", "description", "entityId", "guid", "lastUpdateTicks", "name", "path", "userName",
        };

        internal static string? TryPlan(JObject args, string kbPath, out string name)
        {
            name = args["name"]?.Type == JTokenType.String ? args["name"]!.ToString() : string.Empty;
            if (name.Length == 0 || name.Any(c => c > 0x7f || char.IsControl(c)))
                return "native inspect requires a nonempty ASCII `name`";
            if (args["type"]?.Type != JTokenType.String || !string.Equals(args["type"]!.ToString(), "Procedure", StringComparison.OrdinalIgnoreCase))
                return "native inspect admits only an explicit type=Procedure";
            if (args["projection"]?.Type != JTokenType.String || !string.Equals(args["projection"]!.ToString().Trim(), "minimal", StringComparison.OrdinalIgnoreCase))
                return "native inspect admits only projection=minimal";
            // The Worker attaches per-KB memories to inspections; that store is not reproduced.
            if (File.Exists(Path.Combine(kbPath, ".gx", "memory", "memory.jsonl")))
                return "the KB has a genexus_memory store whose notes the SDK inspection would attach";
            return null;
        }

        internal static List<string> ToEngineArguments(string kbPath, string name) => new()
        {
            "--diagnostics=json", "kb-inspect", kbPath, name,
        };

        internal static bool TryShape(JObject json, string requestedName, out JObject? payload, out string? refusal)
        {
            payload = null;
            refusal = "kb-inspect returned an unexpected inspection";
            if (json.Count != EngineKeys.Length || EngineKeys.Any(key => !json.ContainsKey(key))
                || json["entityId"]?.Type != JTokenType.Integer || json["lastUpdateTicks"]?.Type != JTokenType.Integer
                || json["description"]?.Type != JTokenType.String || json["availableParts"] is not JArray parts
                || !TryText(json["name"], out string name) || !TryText(json["guid"], out string guid)
                || !TryText(json["path"], out string path) || !TryText(json["userName"], out string userName))
                return false;
            if (!string.Equals(name, requestedName, StringComparison.Ordinal))
            {
                // The SDK's handling of case variants of a name is not established.
                refusal = "kb-inspect resolved a differently cased name";
                return false;
            }
            long entityId = json["entityId"]!.Value<long>();
            long ticks = json["lastUpdateTicks"]!.Value<long>();
            if (entityId <= 0 || entityId > int.MaxValue || ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(guid, "D", out _) || !path.EndsWith("/" + name, StringComparison.Ordinal)
                || parts.Any(part => part.Type != JTokenType.String || string.IsNullOrWhiteSpace(part.ToString()))
                || parts.Select(part => part.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Count)
                return false;

            refusal = null;
            payload = new JObject
            {
                ["name"] = name,
                ["type"] = "Procedure",
                ["identity"] = new JObject
                {
                    ["guid"] = guid,
                    ["entityKey"] = ProcedureTypeGuid + "-" + entityId.ToString(CultureInfo.InvariantCulture),
                    ["entityTypeGuid"] = ProcedureTypeGuid,
                    ["entityId"] = entityId,
                    ["path"] = path,
                },
                ["description"] = json["description"]!.ToString(),
                ["lifecycle"] = new JObject
                {
                    ["lastUpdate"] = new DateTime(ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
                    ["lastModifiedBy"] = userName,
                },
                ["projection"] = "minimal",
                ["availableParts"] = new JArray(parts.Select(part => part.ToString())),
                ["hint"] = Hint,
            };
            // Emulate the Worker JSON hop, so dates take the same Gateway representation.
            payload = JObject.Parse(payload.ToString(Formatting.None));
            return true;
        }

        private static bool TryText(JToken? token, out string text)
        {
            text = token?.Type == JTokenType.String ? token.ToString() : string.Empty;
            return text.Trim().Length > 0 && !text.Any(char.IsControl);
        }
    }
}
