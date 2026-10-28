using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The `genexus_kb_version action=list` answer gx-engine may serve, shaped like Worker
    /// <c>KbVersionService.ListVersions</c>: every non-trunk version, with the trunk named after the
    /// Knowledge Base. Provenance labels name the stored entity read, not the SDK property.
    /// </summary>
    internal static class EngineKbVersionListContract
    {
        internal const string Source = "udm:KnowledgeBaseVersions";
        internal const string LastUpdateSource = "udm:EntityVersion.EntityVersionTimestamp";
        internal const string CreatedAtSource = "unavailable:udm-KnowledgeBaseVersions";
        internal const string CreatedAtNote = "Stored KB version entities record a last-update timestamp only; no reliable creation timestamp is available.";

        private static readonly string[] EnvelopeKeys = { "activeVersion", "activeVersionId", "trunk", "versions" };
        private static readonly string[] VersionKeys =
        {
            "description", "designModelId", "id", "isFrozen", "lastUpdateTicks", "name", "parent", "parentId", "userName",
        };

        internal static List<string> ToEngineArguments(string kbPath) => new() { "--diagnostics=json", "kb-versions", kbPath };

        internal static bool TryShape(JObject json, int activeVersionId, out JObject? payload, out string? refusal)
        {
            payload = null;
            refusal = "kb-versions returned an unexpected version-tree envelope";
            if (!HasExactly(json, EnvelopeKeys) || json["versions"] is not JArray stored
                || !TryName(json["trunk"], out string trunk) || !TryName(json["activeVersion"], out string active)
                || json["activeVersionId"]?.Type != JTokenType.Integer || json["activeVersionId"]!.Value<long>() != activeVersionId)
                return false;

            var names = new Dictionary<long, string> { [0] = trunk };
            var versions = new JArray();
            foreach (JToken token in stored)
            {
                if (!TryVersion(token, activeVersionId, out long id, out JObject? version) || !names.TryAdd(id, version!["name"]!.ToString()))
                {
                    refusal = "kb-versions returned a malformed or duplicated version";
                    return false;
                }
                if (!names.TryGetValue(token["parentId"]!.Value<long>(), out string? parent) || parent != version["parent"]!.ToString())
                {
                    refusal = "kb-versions returned a parent that is not an earlier listed version or the trunk";
                    return false;
                }
                versions.Add(version);
            }
            if (!names.TryGetValue(activeVersionId, out string? activeName) || activeName != active)
            {
                refusal = "kb-versions named an active version it did not list";
                return false;
            }

            refusal = null;
            payload = new JObject
            {
                ["status"] = "ok",
                ["code"] = "KbVersionListRetrieved",
                ["result"] = new JObject
                {
                    ["versions"] = versions,
                    ["activeVersion"] = active,
                    ["source"] = Source,
                },
            };
            // Emulate the Worker JSON hop, so dates take the same Gateway representation.
            payload = JObject.Parse(payload.ToString(Formatting.None));
            return true;
        }

        private static bool TryVersion(JToken token, int activeVersionId, out long id, out JObject? version)
        {
            id = 0;
            version = null;
            if (token is not JObject row || !HasExactly(row, VersionKeys)
                || row["id"]?.Type != JTokenType.Integer || row["parentId"]?.Type != JTokenType.Integer
                || row["designModelId"]?.Type != JTokenType.Integer || row["lastUpdateTicks"]?.Type != JTokenType.Integer
                || row["isFrozen"]?.Type != JTokenType.Boolean || row["description"]?.Type != JTokenType.String
                || !TryName(row["name"], out string name) || !TryName(row["parent"], out string parent)
                || !TryName(row["userName"], out string userName))
                return false;
            id = row["id"]!.Value<long>();
            long ticks = row["lastUpdateTicks"]!.Value<long>();
            if (id <= 0 || id > int.MaxValue || row["parentId"]!.Value<long>() < 0
                || ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return false;
            bool frozen = row["isFrozen"]!.Value<bool>();
            version = new JObject
            {
                ["name"] = name,
                ["description"] = row["description"]!.ToString(),
                ["isFrozen"] = frozen,
                // Every listed version is a frozen version or a development branch; the trunk is not listed.
                ["isBranch"] = !frozen,
                ["isTrunk"] = false,
                ["isActive"] = id == activeVersionId,
                ["parent"] = parent,
                ["lastUpdate"] = new DateTime(ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
                ["lastUpdateSource"] = LastUpdateSource,
                ["userName"] = userName,
                // The SDK-visible answer carries no createdAt key (the Worker's null does not survive its hop).
                ["createdAtAvailable"] = false,
                ["createdAtSource"] = CreatedAtSource,
                ["createdAtNote"] = CreatedAtNote,
            };
            return true;
        }

        private static bool HasExactly(JObject json, string[] keys) =>
            json.Count == keys.Length && keys.All(key => json.ContainsKey(key));

        private static bool TryName(JToken? token, out string name)
        {
            name = token?.Type == JTokenType.String ? token.ToString() : string.Empty;
            return name.Trim().Length > 0 && !name.Any(char.IsControl);
        }
    }
}
