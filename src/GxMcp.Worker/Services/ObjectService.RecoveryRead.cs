using System;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public partial class ObjectService
    {
        /// <summary>
        /// Re-reads one object part straight from the SDK for a post-timeout recovery and
        /// answers with the pre-cut certificate of that read (<see cref="RecoveryReadCertificate"/>)
        /// and no content.
        /// </summary>
        /// <remarks>
        /// The read is a fresh one (SDK caches invalidated, a new instance of the same
        /// object) with no pagination and no response budget in the way, so what is
        /// certified is what the SDK holds, not what a client-facing page could carry. It
        /// performs no write, no specification and no build: it only reads and hashes.
        /// A read that cannot be performed fresh answers with an incomplete certificate
        /// (never an error the caller could mistake for "nothing to certify"), and an
        /// object that cannot be found is the ordinary not-found envelope.
        /// </remarks>
        internal string CertifyRecoveryRead(
            string target, string partName, int? offset = null, int? limit = null,
            string typeFilter = null, string guid = null, string entityKey = null, string path = null)
        {
            string resolvedTarget = ResolveTargetForIdentity(target, guid, entityKey, path);
            var observation = new RecoveryReadObservation
            {
                RequestedPart = partName,
                ResolvedPart = partName,
                RequestOffset = offset,
                RequestLimit = limit
            };

            KBObject obj;
            try { obj = FindObjectFresh(resolvedTarget, typeFilter, guid, entityKey, path); }
            catch (Exception ex)
            {
                observation.UnavailableReason = "fresh-read-failed:" + ex.GetType().Name;
                return CertificateEnvelope(resolvedTarget, observation);
            }

            if (obj == null)
            {
                var diagnostic = GetLastResolutionDiagnostic();
                if (!string.Equals(diagnostic?["code"]?.ToString(), "FreshReadUnavailable", StringComparison.OrdinalIgnoreCase))
                    return FormatReadNotFound(resolvedTarget);
                observation.UnavailableReason = "fresh-read-unavailable";
                return CertificateEnvelope(resolvedTarget, observation);
            }

            string resolvedPart = ResolvePartName(obj, partName);
            observation.Name = obj.Name;
            observation.Type = obj.TypeDescriptor?.Name;
            observation.ResolvedPart = resolvedPart;
            observation.Guid = ReadIdentityField(obj, "guid", () => obj.Guid.ToString());
            observation.EntityKey = ReadIdentityField(obj, "entityKey", () => obj.Key?.ToString());
            observation.Path = ReadIdentityField(obj, "path", () => BuildObjectIdentity(obj)["path"]?.ToString());

            string[] stampKeys = { target, resolvedTarget, obj.Name };
            observation.WriteStampBefore = WriteService.TargetWriteStamp(stampKeys);
            observation.RevisionBefore = TryComputeRevisionStamp(obj);

            try
            {
                JObject payload = JObject.Parse(ReadObjectSourceInternal(obj, resolvedPart, 0, 0, "mcp", false));
                bool failed = payload["error"] != null
                    || string.Equals(payload["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase);
                if (failed)
                {
                    observation.UnavailableReason = "part-read-failed";
                }
                else if (payload["source"]?.Type == JTokenType.String
                    && payload["isBase64"]?.Value<bool?>() != true)
                {
                    observation.Text = payload["source"].ToString();
                    observation.VersionToken = payload["versionToken"]?.ToString();
                }
            }
            catch (Exception ex)
            {
                observation.UnavailableReason = "part-read-failed:" + ex.GetType().Name;
            }

            observation.WriteStampAfter = WriteService.TargetWriteStamp(stampKeys);
            observation.RevisionAfter = TryComputeRevisionStamp(obj);
            return CertificateEnvelope(obj.Name, observation);
        }

        // The identity fields are best effort (see RecoveryReadObservation); a field the SDK cannot
        // answer is left out of the certificate and the reason is logged.
        private static string ReadIdentityField(KBObject obj, string field, Func<string> read)
        {
            try { return read(); }
            catch (Exception ex)
            {
                Logger.Debug("[RecoveryRead] " + field + " of '" + obj.Name + "' unavailable: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        private static string CertificateEnvelope(string target, RecoveryReadObservation observation)
            => McpResponse.Ok(target: target, code: "RecoveryReadCertificate", result: new JObject
            {
                ["recoveryCertificate"] = RecoveryReadCertificate.Build(observation)
            });
    }
}
