using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The KB version the Worker's SDK session has active and the id of its Design model, as the Worker's
    /// <c>kbversion ActiveModel</c> snapshot reports them. gx-engine reads storage directly and must be
    /// told which version's Design model the SDK serves: without it, the engine serves only a Knowledge
    /// Base with a single Design model, and refuses (exit code 4) any other before reading it.
    /// </summary>
    internal sealed class EngineActiveModel
    {
        /// <summary>gx-engine exit code for a version-scope refusal: nothing was read or written.</summary>
        internal const int VersionScopeExitCode = 4;

        internal EngineActiveModel(string kbPath, int designModelId, int kbVersionId, string? kbVersionName)
        {
            KbPath = kbPath;
            DesignModelId = designModelId;
            KbVersionId = kbVersionId;
            KbVersionName = kbVersionName;
        }

        internal string KbPath { get; }
        internal int DesignModelId { get; }
        internal int KbVersionId { get; }
        internal string? KbVersionName { get; }

        /// <summary>
        /// Parses the Worker reply (the JSON-RPC envelope, whose <c>result</c> is the McpResponse).
        /// Null for an error, an unknown snapshot or any missing field.
        /// </summary>
        internal static EngineActiveModel? FromWorkerReply(JObject? envelope)
        {
            if (envelope == null || envelope["__timeout"] != null) return null;
            JObject? response = envelope["result"] as JObject ?? envelope;
            if (!string.Equals(response["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(response["code"]?.ToString(), "KbActiveModel", StringComparison.Ordinal)) return null;
            if (response["result"] is not JObject result) return null;
            string? kbPath = result["kbPath"]?.ToString();
            if (string.IsNullOrWhiteSpace(kbPath)) return null;
            if (!TryInt(result["designModelId"], out int designModelId) || !TryInt(result["kbVersionId"], out int kbVersionId))
                return null;
            return new EngineActiveModel(kbPath!, designModelId, kbVersionId, result["kbVersionName"]?.ToString());
        }

        private static bool TryInt(JToken? token, out int value)
        {
            value = 0;
            return token != null && token.Type == JTokenType.Integer
                && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Whether this snapshot describes the Knowledge Base at <paramref name="kbPath"/>.</summary>
        internal bool Describes(string kbPath) =>
            string.Equals(Normalize(KbPath), Normalize(kbPath), StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string path)
        {
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch { full = path.Trim(); }
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>The engine arguments naming this active version.</summary>
        internal IEnumerable<string> EngineArguments() => new[]
        {
            "--design-model", DesignModelId.ToString(CultureInfo.InvariantCulture),
            "--kb-version", KbVersionId.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// The engine's version-scope refusal message (category <c>version_scope</c> with exit code 4), or
        /// null when <paramref name="invocation"/> is anything else. Only the exact strict diagnostic with
        /// empty stdout authorizes replaying the call on the SDK; a malformed exit 4 is an ordinary failure.
        /// </summary>
        internal static string? VersionScopeRefusal(GxEngineInvocation? invocation)
        {
            if (invocation == null || invocation.Ok || invocation.TimedOut) return null;
            if (invocation.ExitCode != VersionScopeExitCode) return null;
            if (!string.Equals(invocation.ErrorCode, "EngineCommandFailed", StringComparison.Ordinal)) return null;
            if (invocation.StandardOutput.Length != 0) return null;
            return EngineDiagnostic.TryReadSingleLine(invocation.StandardError, "version_scope", out string message)
                ? message
                : null;
        }
    }
}
