using System;
using System.Text;
using System.Text.RegularExpressions;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Bounded, credential-masked projection of gx-engine process output for logs and payload
    /// fields. The engine reads a KB through SQL Server, so a failing run can echo a login,
    /// password or connection string; none of that may reach the Gateway log or an MCP result.
    /// </summary>
    internal static class EngineFailureText
    {
        internal const string RedactedMarker = "[redacted]";

        // One quoted value shape for `Password=...`, `pwd: ...`, `SQLCMDPASSWORD=...` and
        // sqlcmd's `-P <password>`. A JSON diagnostic escapes an inner quote as `\"`; the
        // leading and trailing backslash are therefore optional. The lookahead recognises a
        // closing quote only when it is followed by a delimiter rather than the rest of the
        // secret, so a password containing an escaped quote stays inside one match. The greedy
        // alternatives are a bounded fallback for malformed text: masking too much is safer
        // than leaking. After the separator only spaces/tabs are skipped, so a prompt such as
        // "Password:" that ends its line does not swallow the next line.
        private const string QuotedCredentialValuePattern =
            @"\\?""(?:\\.|[^""\\\r\n])*?\\?""(?=[\s\p{P}\p{S}]|$)"
            + @"|'(?:\\.|[^'\\\r\n])*?'(?=[\s\p{P}\p{S}]|$)"
            + @"|\\?""(?:\\.|[^""\\\r\n])*\\?"""
            + @"|'(?:\\.|[^'\\\r\n])*'";

        private const string CredentialValuePattern =
            QuotedCredentialValuePattern + @"|(?:\\.|[^\s;,&""'])+";

        // sqlcmd's -P value is either quoted or a single non-whitespace token. Keep the broad
        // unquoted fallback after the quoted alternatives so an embedded quote is still masked.
        private const string SqlcmdValuePattern = QuotedCredentialValuePattern + @"|\S+";

        private static readonly Regex SecretAssignment = new Regex(
            @"(?<name>\b\w*(?:password|passwd|pwd|senha|secret|token)\b[ \t]*[=:][ \t]*)(?<value>"
            + CredentialValuePattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // sqlcmd's `-P <password>` switch.
        private static readonly Regex SqlcmdPasswordSwitch = new Regex(
            @"(?<name>(?:^|\s)-P[ \t]*)(?<value>" + SqlcmdValuePattern + @")",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string masked = SecretAssignment.Replace(text, match => match.Groups["name"].Value + RedactedMarker);
            return SqlcmdPasswordSwitch.Replace(masked, match => match.Groups["name"].Value + RedactedMarker);
        }

        /// <summary>Masked text cut to <paramref name="maxLength"/> characters (never inside a surrogate pair), with an ellipsis when cut.</summary>
        internal static string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength) return text;
            int end = maxLength;
            if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
            return text.Substring(0, end) + "…";
        }

        /// <summary>A masked, single-line, length-bounded excerpt for one log line.</summary>
        internal static string ForLog(string? text, int maxLength = 300)
        {
            string masked = Redact(text);
            var builder = new StringBuilder(Math.Min(masked.Length, maxLength + 8));
            bool pendingSpace = false;
            foreach (char c in masked)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }
                if (pendingSpace) builder.Append(' ');
                pendingSpace = false;
                builder.Append(c);
                if (builder.Length > maxLength + 1) break;
            }
            return Truncate(builder.ToString(), maxLength);
        }
    }
}
