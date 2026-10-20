using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>Whether a recovery certificate may clear one fence, and why not.</summary>
    internal readonly struct RecoveryCertificateVerdict
    {
        internal RecoveryCertificateVerdict(bool accepted, string reason, string? detail = null)
        {
            Accepted = accepted;
            Reason = reason;
            Detail = detail;
        }

        internal bool Accepted { get; }
        /// <summary>Stable code: <c>accepted</c> or the first guard that refused.</summary>
        internal string Reason { get; }
        /// <summary>The Worker's own explanation when it declared the read incomplete.</summary>
        internal string? Detail { get; }

        internal static RecoveryCertificateVerdict Accept() => new(true, "accepted");
        internal static RecoveryCertificateVerdict Refuse(string reason, string? detail = null) => new(false, reason, detail);
    }

    /// <summary>
    /// Decides whether the pre-cut certificate a Worker produced for a recovery read
    /// (<c>gx-recovery-read/1</c>) is evidence for one specific fence.
    /// </summary>
    /// <remarks>
    /// The certificate is read from the Worker's response, never from a tool argument, and
    /// every clause here refuses by default. What it can clear is deliberately narrower
    /// than what a read of the same object could: the fence's own part, and every stable
    /// identity the fence recorded (guid, entity key, type, path) must match what the
    /// Worker says it read. A KB-level fence and a property fence are never cleared by a
    /// text certificate: neither is a statement about one text part.
    /// </remarks>
    internal static class RecoveryReadCertificateValidator
    {
        internal const string Schema = "gx-recovery-read/1";
        internal const string CoverageComplete = "sdk-complete";
        internal const string ChangedDuringRead = "changed-during-read";

        internal static RecoveryCertificateVerdict Evaluate(JToken? certificate, RecoveryRequirement fence)
        {
            if (certificate is not JObject cert) return RecoveryCertificateVerdict.Refuse("certificate-missing");
            if (!string.Equals(cert["schema"]?.ToString(), Schema, StringComparison.Ordinal))
                return RecoveryCertificateVerdict.Refuse("schema-unsupported");
            if (MutationRecoveryRegistry.IsKbLevelRecovery(fence))
                return RecoveryCertificateVerdict.Refuse("kb-level-fence");
            if (string.Equals(fence.Part, "Properties", StringComparison.OrdinalIgnoreCase))
                return RecoveryCertificateVerdict.Refuse("properties-fence");

            if (cert["complete"]?.Type != JTokenType.Boolean || !cert["complete"]!.Value<bool>())
                return RecoveryCertificateVerdict.Refuse("certificate-incomplete", cert["reason"]?.ToString());
            if (!string.Equals(cert["coverage"]?.ToString(), CoverageComplete, StringComparison.Ordinal))
                return RecoveryCertificateVerdict.Refuse("coverage-not-sdk-complete", cert["coverage"]?.ToString());
            if (cert["authoritative"]?.Type != JTokenType.Boolean || !cert["authoritative"]!.Value<bool>())
                return RecoveryCertificateVerdict.Refuse("not-authoritative");
            if (ReportsConcurrentChange(cert))
                return RecoveryCertificateVerdict.Refuse("changed-during-read");
            // The Worker states stability explicitly; silence is not stability.
            if (cert["stable"]?.Type != JTokenType.Boolean || !cert["stable"]!.Value<bool>())
                return RecoveryCertificateVerdict.Refuse("stability-not-certified");

            string? version = cert["versionToken"]?.ToString();
            string? sha256 = cert["sha256"]?.ToString();
            if (string.IsNullOrWhiteSpace(version)) return RecoveryCertificateVerdict.Refuse("version-token-missing");
            if (!IsLowerHexSha256(sha256)) return RecoveryCertificateVerdict.Refuse("hash-invalid");
            if (!version!.EndsWith(":" + sha256, StringComparison.Ordinal))
                return RecoveryCertificateVerdict.Refuse("version-token-hash-mismatch");
            if (cert["totalLines"]?.Type != JTokenType.Integer || cert["totalLines"]!.Value<long>() < 0)
                return RecoveryCertificateVerdict.Refuse("total-lines-invalid");

            if (cert["identity"] is not JObject identity) return RecoveryCertificateVerdict.Refuse("identity-missing");
            string? certGuid = identity["guid"]?.ToString();
            if (string.IsNullOrWhiteSpace(certGuid)) return RecoveryCertificateVerdict.Refuse("identity-guid-missing");

            string? resolvedPart = identity["part"]?.ToString();
            string? requestedPart = identity["requestedPart"]?.ToString();
            if (!SameText(fence.Part, resolvedPart) && !SameText(fence.Part, requestedPart))
                return RecoveryCertificateVerdict.Refuse("part-mismatch");

            bool guidProven = false;
            if (!string.IsNullOrWhiteSpace(fence.TargetGuid))
            {
                if (!SameGuid(fence.TargetGuid, certGuid)) return RecoveryCertificateVerdict.Refuse("guid-mismatch");
                guidProven = true;
            }
            bool entityKeyProven = false;
            if (!string.IsNullOrWhiteSpace(fence.TargetEntityKey))
            {
                if (!SameText(fence.TargetEntityKey, identity["entityKey"]?.ToString()))
                    return RecoveryCertificateVerdict.Refuse("entity-key-mismatch");
                entityKeyProven = true;
            }
            if (!string.IsNullOrWhiteSpace(fence.TargetType)
                && !SameText(fence.TargetType, identity["type"]?.ToString()))
                return RecoveryCertificateVerdict.Refuse("type-mismatch");

            // Name and path only identify an object when no stable identity was recorded:
            // a renamed or moved object is still the fenced one if its guid matches.
            if (!guidProven && !entityKeyProven)
            {
                if (!string.IsNullOrWhiteSpace(fence.TargetPath)
                    && !SameText(fence.TargetPath, identity["path"]?.ToString()))
                    return RecoveryCertificateVerdict.Refuse("path-mismatch");
                if (!SameText(fence.Target, identity["name"]?.ToString()))
                    return RecoveryCertificateVerdict.Refuse("name-mismatch");
            }

            return RecoveryCertificateVerdict.Accept();
        }

        /// <summary>
        /// True when the Worker itself saw the object or its write stamp move while it read
        /// the part. A read that raced a write is not evidence even if the page it returned
        /// looks complete, so it must not clear a fence by any route.
        /// </summary>
        internal static bool ReportsConcurrentChange(JToken? certificate)
            => certificate is JObject cert
                && (string.Equals(cert["reason"]?.ToString(), ChangedDuringRead, StringComparison.Ordinal)
                    // `stable` is set independently of the refusal reason, so an earlier
                    // refusal (window, token, identity) cannot hide a race.
                    || (cert["stable"]?.Type == JTokenType.Boolean && !cert["stable"]!.Value<bool>()));

        /// <summary>
        /// True when two certificates describe the same object, version, bytes and write
        /// stamp. Used to confirm that the state certified first is still the one the
        /// fence is about to be cleared against.
        /// </summary>
        internal static bool SameObservedState(JToken? first, JToken? second, out string difference)
        {
            difference = string.Empty;
            if (first is not JObject a || second is not JObject b)
            {
                difference = "certificate-missing";
                return false;
            }

            foreach (string field in new[] { "versionToken", "sha256", "targetWriteStamp" })
            {
                if (!string.Equals(a[field]?.ToString(), b[field]?.ToString(), StringComparison.Ordinal))
                {
                    difference = field;
                    return false;
                }
            }
            if (a["totalLines"]?.ToString() != b["totalLines"]?.ToString())
            {
                difference = "totalLines";
                return false;
            }
            if (!SameGuid(a["identity"]?["guid"]?.ToString(), b["identity"]?["guid"]?.ToString()))
            {
                difference = "identity.guid";
                return false;
            }
            return true;
        }

        private static bool SameText(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left!.Trim(), right!.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool SameGuid(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid)
                ? leftGuid == rightGuid
                : SameText(left, right);
        }

        private static bool IsLowerHexSha256(string? value)
            => value != null && value.Length == 64 && value.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
    }
}
