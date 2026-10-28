using System;

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

            return EngineDiagnostic.TryReadSingleLine(invocation.StandardError, Category, out string message)
                ? EngineFailureText.Truncate(EngineFailureText.Redact(message).Trim(), 400)
                : null;
        }
    }
}
