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

        // `Password=...`, `pwd: ...`, `Senha=...`, `SQLCMDPASSWORD=...`, quoted or bare values.
        // After the separator only spaces/tabs are skipped, so a prompt such as "Password:" that ends
        // its line does not swallow the next line.
        private static readonly Regex SecretAssignment = new Regex(
            @"(?<name>\b\w*(?:password|passwd|pwd|senha|secret|token)\b[ \t]*[=:][ \t]*)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^\s;,&""']+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // sqlcmd's `-P <password>` switch.
        private static readonly Regex SqlcmdPasswordSwitch = new Regex(
            @"(?<name>(?:^|\s)-P[ \t]*)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|\S+)",
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
