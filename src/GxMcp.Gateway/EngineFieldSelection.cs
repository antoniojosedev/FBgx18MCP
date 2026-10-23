using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The explicit <c>fields</c> selection of an engine-routed call. The engine
    /// supplies only a few of the fields the SDK returns, and the Gateway projects
    /// only what the caller asked for, so a call is admitted only when every
    /// requested field is one the engine supplies from storage. A call without an
    /// explicit selection gets the SDK default shape (which includes identity
    /// fields the engine cannot supply) and therefore stays on the SDK.
    /// </summary>
    internal static class EngineFieldSelection
    {
        /// <summary>
        /// The requested fields, or the reason the call is outside the engine
        /// space (then <paramref name="fields"/> is null).
        /// </summary>
        internal static string? TryRead(
            JObject args, string operation, IReadOnlyCollection<string> supplied, out HashSet<string>? fields)
        {
            fields = null;
            if (args["full"]?.Type == JTokenType.Boolean && args["full"]!.Value<bool>())
                return $"`full:true` asks for the complete SDK {operation} payload, which the engine does not supply";

            string? refusal = TryParseFieldNames(args["fields"], out HashSet<string> requested);
            if (refusal != null) return refusal;

            string suppliedList = string.Join(", ", supplied);
            if (requested.Count == 0)
                return $"without an explicit `fields` selection the SDK {operation} answer carries identity and metadata fields the engine cannot supply; pass fields limited to: {suppliedList}";

            string? unsupplied = requested.FirstOrDefault(field => !supplied.Contains(field, StringComparer.OrdinalIgnoreCase));
            if (unsupplied != null)
                return $"`fields` asks for `{unsupplied}`, which the engine does not supply (it supplies: {suppliedList})";

            fields = requested;
            return null;
        }

        /// <summary>
        /// A <c>fields</c> value as the caller meant it. A client whose schema does not declare
        /// <c>fields</c> as an array sends the array as a string (<c>["name","type"]</c>), which a
        /// comma split would cut into <c>["name"</c> and <c>"type"]</c>; a string holding a JSON
        /// array literal is therefore read as that array. Any other token is returned unchanged.
        /// </summary>
        internal static JToken? NormalizeFields(JToken? token)
        {
            if (token == null || token.Type != JTokenType.String) return token;
            string text = token.ToString().Trim();
            if (!text.StartsWith("[", StringComparison.Ordinal) || !text.EndsWith("]", StringComparison.Ordinal)) return token;
            try
            {
                return JToken.Parse(text) is JArray array ? array : token;
            }
            catch (JsonReaderException)
            {
                return token;
            }
        }

        private static string? TryParseFieldNames(JToken? token, out HashSet<string> requested)
        {
            requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            token = NormalizeFields(token);
            if (token == null || token.Type == JTokenType.Null) return null;

            IEnumerable<JToken> items;
            if (token is JArray array) items = array;
            else if (token.Type == JTokenType.String) items = token.ToString().Split(',').Select(piece => (JToken)piece);
            else return "`fields` must be an array of field names";

            foreach (JToken item in items)
            {
                if (item.Type != JTokenType.String) return "`fields` entries must be strings";
                string value = item.ToString().Trim();
                if (value.Length > 0) requested.Add(value);
            }
            return null;
        }
    }
}
