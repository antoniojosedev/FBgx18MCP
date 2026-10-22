using System;
using System.Linq;
using System.Text.Json;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Validates rebuild-all's dedicated pre-effect credential refusal. No other
    /// exit-5 output proves that it is safe to continue on the SDK.
    /// </summary>
    internal static class EngineSqlCredentialsPreflight
    {
        internal const int ExitCode = 5;
        internal const string Category = "sql_credentials_unavailable";
        internal const string ErrorCode = "EngineSqlCredentialsUnavailable";

        internal static string? UnavailableMessage(GxEngineInvocation? invocation)
        {
            if (invocation == null || invocation.Ok || invocation.TimedOut) return null;
            if (invocation.ExitCode != ExitCode) return null;
            if (!string.Equals(invocation.ErrorCode, "EngineCommandFailed", StringComparison.Ordinal)) return null;
            if (invocation.StandardOutput.Length != 0) return null;

            string stderr = invocation.StandardError.Trim();
            if (stderr.Length == 0 || stderr.IndexOf('\n') >= 0 || stderr.IndexOf('\r') >= 0) return null;
            try
            {
                // Newtonsoft accepts comments, single quotes and trailing commas;
                // a replay authorization must accept only the engine's strict JSON.
                using JsonDocument document = JsonDocument.Parse(stderr);
                return ReadDiagnosticMessage(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadDiagnosticMessage(JsonElement diagnostic)
        {
            if (diagnostic.ValueKind != JsonValueKind.Object
                || diagnostic.EnumerateObject().Count() != 3) return null;
            // Exactly these three distinct fields also excludes duplicate keys.
            if (!diagnostic.TryGetProperty("category", out JsonElement category)
                || category.ValueKind != JsonValueKind.String || category.GetString() != Category) return null;
            if (!diagnostic.TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString())) return null;
            if (!diagnostic.TryGetProperty("location", out JsonElement location)
                || location.ValueKind != JsonValueKind.Null) return null;
            return EngineFailureText.Truncate(EngineFailureText.Redact(message.GetString()).Trim(), 400);
        }
    }
}
