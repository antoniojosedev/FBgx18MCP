using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // How long a certification read may wait for the Worker. The Worker may still be
        // finishing the very write that armed the fence, so this is a bounded wait that
        // reports "worker-timeout" and leaves the fence alone, never a reason to clear it.
        private const int RecoveryCertificationTimeoutMs = 60000;

        /// <summary>
        /// Marks a routed read as a recovery read. Both flags go on the routed command - the
        /// object the Worker receives as its arguments - under the exact names the Worker
        /// reads: <c>requireAuthoritativeRead</c> (a conditional token must not turn the body
        /// into "not modified") and <c>certifyRecovery</c> (answer with the pre-cut
        /// certificate). They are internal transport metadata, never client arguments.
        /// </summary>
        internal static void ApplyRecoveryReadFlags(JObject workerCommand)
        {
            workerCommand["requireAuthoritativeRead"] = true;
            workerCommand["certifyRecovery"] = true;
        }

        /// <summary>
        /// Takes the certificate out of the Worker's payload before any wire guard can cut or
        /// replace it. Absent when the Worker could not produce one; that means "not proven".
        /// The certificate is removed from the payload: the caller gets a compact summary
        /// (<see cref="DescribeRecoveryFences"/>), and the extra bytes cannot push a part
        /// that fits the response budget over it.
        /// </summary>
        internal static JObject? ExtractRecoveryCertificate(JToken? workerPayload)
        {
            if (workerPayload is not JObject payload) return null;
            var certificate = payload["recoveryCertificate"] as JObject;
            if (certificate != null) payload.Remove("recoveryCertificate");
            return certificate;
        }

        /// <summary>
        /// The scopes a fence of the KB addressed by this call may have been recorded under:
        /// its own alias, and the folder-name alias that earlier builds minted when the same
        /// KB was addressed by <c>kb=&lt;absolute path&gt;</c>. A fence recorded under the second
        /// would otherwise stop blocking writes (and become impossible to clear) the moment
        /// the path starts resolving to the first. The folder alias is only taken when no
        /// other known KB owns it.
        /// </summary>
        internal static IReadOnlyList<string> EquivalentKbScopes(
            string? kbScope, string? kbPath, IEnumerable<(string Alias, string? Path)> knownKbs)
        {
            var scopes = new List<string>();
            if (!string.IsNullOrWhiteSpace(kbScope)) scopes.Add(kbScope!);
            if (string.IsNullOrWhiteSpace(kbPath)) return scopes;

            string folderAlias = Path.GetFileName(kbPath!.TrimEnd('\\', '/')).ToLowerInvariant();
            if (string.IsNullOrEmpty(folderAlias)
                || scopes.Contains(folderAlias, StringComparer.OrdinalIgnoreCase)) return scopes;

            string normalizedPath = NormalizeKbPath(kbPath);
            bool ownedByAnotherKb = knownKbs.Any(known =>
                string.Equals(known.Alias?.Trim(), folderAlias, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(NormalizeKbPath(known.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (!ownedByAnotherKb) scopes.Add(folderAlias);
            return scopes;
        }

        private static string NormalizeKbPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path!).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return path!.Trim().TrimEnd('\\', '/');
            }
        }

        /// <summary>The scopes of the KB the current call resolved to (see <see cref="EquivalentKbScopes"/>).</summary>
        private static IReadOnlyList<string> CurrentKbScopes()
        {
            var current = _currentKb.Value;
            if (current == null) return Array.Empty<string>();
            var known = new List<(string Alias, string? Path)>();
            try
            {
                if (_workerPool != null)
                {
                    known.AddRange(_workerPool.ListOpen().Select(k => (k.Alias, (string?)k.Path)));
                    known.AddRange(_workerPool.ListKnown().Select(k => (k.Alias, (string?)k.Path)));
                }
                var declared = _activeConfig?.Environment?.KBs;
                if (declared != null) known.AddRange(declared.Select(k => (k.Alias, (string?)k.Path)));
            }
            catch (Exception) { /* the scopes degrade to the handle's own alias */ }
            return EquivalentKbScopes(current.NormalizedAlias, current.Path, known);
        }

        /// <summary>
        /// Clears the fences a read observed from the Worker's pre-cut certificate, for the
        /// part the Gateway had to cut or replace and so cannot call complete. Each fence is
        /// judged on its own identity and part; one certificate never clears a fence of
        /// another object, part, type or guid. Returns how many fences were cleared.
        /// </summary>
        internal static int ConfirmCertifiedRecoveryRead(
            MutationRecoveryRegistry registry,
            string? kbScope,
            JObject? certificate,
            IReadOnlyList<(string Target, string Part, RecoveryRequirement? Observed)> recoveryReads)
        {
            if (certificate == null) return 0;
            int cleared = 0;
            foreach (var recoveryTarget in recoveryReads)
            {
                if (recoveryTarget.Observed == null) continue;
                if (!RecoveryReadCertificateValidator.Evaluate(certificate, recoveryTarget.Observed).Accepted) continue;
                if (registry.ConfirmRead(kbScope, recoveryTarget.Target, recoveryTarget.Part, recoveryTarget.Observed))
                    cleared++;
            }
            return cleared;
        }

        /// <summary>
        /// The decision a covered <c>genexus_read</c> takes after the wire guards: the page the
        /// caller received clears what it covers (<see cref="ConfirmMutationRecoveryRead"/>),
        /// and the Worker's certificate clears what the Gateway cut. A certificate that
        /// reports the part moving while it was read, or that certifies another version than
        /// the page the caller received (a page served from a cache the fresh certification
        /// read has outdated), vetoes both: that read is not evidence. A response the size
        /// guard replaced by a sentinel gives the caller nothing to act on, so only
        /// <c>recovery_read_complete</c> (which never returns the part) clears in that case.
        /// </summary>
        internal static int ConfirmRecoveryReadWithCertificate(
            MutationRecoveryRegistry registry,
            string? kbScope,
            JToken? finalResult,
            JObject? certificate,
            IReadOnlyList<(string Target, string Part, RecoveryRequirement? Observed)> recoveryReads)
        {
            if (recoveryReads.Count == 0) return 0;
            if (RecoveryReadCertificateValidator.ReportsConcurrentChange(certificate)) return 0;
            if (CertificateDisagreesWithPage(finalResult, certificate)) return 0;
            int cleared = ConfirmMutationRecoveryRead(registry, kbScope, finalResult, recoveryReads);
            if (!IsSizeGuardSentinel(finalResult) && PageIsTheCertifiedVersion(finalResult, certificate))
                cleared += ConfirmCertifiedRecoveryRead(registry, kbScope, certificate, recoveryReads);
            return cleared;
        }

        internal static bool IsSizeGuardSentinel(JToken? result)
            => result is JObject payload
                && string.Equals(payload["_meta"]?["truncated"]?["reason"]?.ToString(),
                    "response_exceeded_cap", StringComparison.Ordinal);

        /// <summary>
        /// The caller will edit from the <c>versionToken</c> of the page it received. The
        /// certificate is only evidence for that edit if it certifies the same version.
        /// </summary>
        private static bool PageIsTheCertifiedVersion(JToken? finalResult, JObject? certificate)
        {
            string? pageToken = finalResult is JObject page ? RecoveryVersionToken(page) : null;
            return !string.IsNullOrWhiteSpace(pageToken)
                && string.Equals(pageToken, certificate?["versionToken"]?.ToString(), StringComparison.Ordinal);
        }

        /// <summary>Both sides state a version and the two differ.</summary>
        private static bool CertificateDisagreesWithPage(JToken? finalResult, JObject? certificate)
        {
            string? certified = certificate?["versionToken"]?.ToString();
            string? pageToken = finalResult is JObject page ? RecoveryVersionToken(page) : null;
            return !string.IsNullOrWhiteSpace(certified)
                && !string.IsNullOrWhiteSpace(pageToken)
                && !string.Equals(pageToken, certified, StringComparison.Ordinal);
        }

        /// <summary>
        /// What a covered read tells its caller about each fence it covered: whether it is
        /// cleared, whether the certificate was complete, and what the version says about the
        /// write that timed out. Clearing a fence never says that write landed.
        /// </summary>
        internal static JArray DescribeRecoveryFences(
            MutationRecoveryRegistry registry,
            JToken? finalResult,
            JObject? certificate,
            IReadOnlyList<(string Target, string Part, RecoveryRequirement? Observed)> recoveryReads)
        {
            // The fresh certified version when there is one; the page's own otherwise.
            string? version = certificate?["versionToken"]?.ToString();
            if (string.IsNullOrWhiteSpace(version) && finalResult is JObject page) version = RecoveryVersionToken(page);
            var fences = new JArray();
            foreach (var recoveryTarget in recoveryReads)
            {
                var fence = recoveryTarget.Observed;
                if (fence == null) continue;
                var item = new JObject
                {
                    ["operationId"] = fence.OperationId,
                    ["part"] = fence.Part,
                    ["cleared"] = !registry.IsStillFenced(fence),
                    ["expectedVersion"] = string.IsNullOrWhiteSpace(fence.ExpectedVersion) ? null : fence.ExpectedVersion,
                    ["timedOutWrite"] = RecoveryReadCompletion.TimedOutWriteState(fence, version)
                };
                if (certificate != null)
                {
                    item["certificateComplete"] = certificate["complete"]?.Value<bool?>() ?? false;
                    if (certificate["reason"] != null) item["certificateReason"] = certificate["reason"];
                    item["totalLines"] = certificate["totalLines"];
                    item["hash"] = certificate["sha256"];
                }
                fences.Add(item);
            }
            return fences;
        }

        private static async Task<JObject> HandleRecoveryReadCompleteAsync(
            JToken? idToken, string toolName, JObject? args)
        {
            JObject payload = await RecoveryReadCompletion.RunAsync(
                _mutationRecovery,
                CurrentKbScopes(),
                args,
                CertifyRecoveryViaWorkerAsync).ConfigureAwait(false);
            bool isError = string.Equals(payload["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase);
            return BuildToolTextResponse(idToken, payload, isError, toolName, args, payloadOwned: true);
        }

        private static Task<JObject?> CertifyRecoveryViaWorkerAsync(JObject selector)
            => SendWorkerCommandAsync(
                selector,
                RecoveryCertificationTimeoutMs,
                "recovery-certificate timeout",
                workerResponse => workerResponse,
                (_, correlationId) => new JObject { ["__timeout"] = true },
                toolName: "connection_recover_certify",
                trackOperation: false);
    }
}
