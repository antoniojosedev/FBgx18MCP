using System;
using System.Linq;
using System.Text.Json;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Reads the engine's one-line diagnostic contract. A diagnostic authorizes replaying the
    /// call on the SDK, so only the exact strict JSON the engine emits is accepted: one object
    /// with exactly <c>category</c>, <c>message</c> and <c>location</c>, a nonblank message and
    /// a null location. Newtonsoft's parser is deliberately not used here: it also accepts
    /// comments, single quotes and trailing commas that the engine never emits.
    /// </summary>
    internal static class EngineDiagnostic
    {
        internal static bool TryReadSingleLine(
            string? stderr, string expectedCategory, out string message)
        {
            message = string.Empty;
            string text = (stderr ?? string.Empty).Trim();
            if (text.Length == 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0) return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement diagnostic = document.RootElement;
                if (diagnostic.ValueKind != JsonValueKind.Object
                    || diagnostic.EnumerateObject().Count() != 3) return false;
                // Exactly these three distinct fields also excludes duplicate keys.
                if (!diagnostic.TryGetProperty("category", out JsonElement category)
                    || category.ValueKind != JsonValueKind.String
                    || !string.Equals(category.GetString(), expectedCategory, StringComparison.Ordinal)) return false;
                if (!diagnostic.TryGetProperty("message", out JsonElement messageElement)
                    || messageElement.ValueKind != JsonValueKind.String) return false;
                string? value = messageElement.GetString();
                if (string.IsNullOrWhiteSpace(value)) return false;
                if (!diagnostic.TryGetProperty("location", out JsonElement location)
                    || location.ValueKind != JsonValueKind.Null) return false;
                message = value!;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
