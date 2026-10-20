using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// <c>genexus_connection_recover action=recovery_read_complete</c>: clears one
    /// post-timeout fence on the strength of a Worker-produced certificate of a complete,
    /// fresh SDK read, without returning the part, so a part larger than the response
    /// budget is no longer stuck behind its fence.
    /// </summary>
    /// <remarks>
    /// Nothing the caller sends is evidence. The caller names the object, part and
    /// (optionally) the fence's operation id; the evidence is the certificate the Worker
    /// builds from its own read, checked against the fence the journal holds. With
    /// <c>dryRun</c> (the default) the action reads and reports and never touches the
    /// journal. Without it, the part is certified twice and the two certificates must
    /// describe the same version, bytes and write stamp before the one fence the
    /// journal still holds for that operation is removed; a fence re-armed meanwhile, a
    /// version the caller did not inspect (<c>baseVersion</c>), or a journal that cannot be
    /// trusted all leave the fence in place. The action performs no Specify, Generate,
    /// Build, Rebuild, reorganization, execution or test, and writes no object.
    /// </remarks>
    internal static class RecoveryReadCompletion
    {
        internal const string ActionName = "recovery_read_complete";

        internal static Task<JObject> RunAsync(
            MutationRecoveryRegistry registry,
            string? kbScope,
            JObject? args,
            Func<JObject, Task<JObject?>> certifyAsync)
            => RunAsync(registry,
                string.IsNullOrWhiteSpace(kbScope) ? Array.Empty<string>() : new[] { kbScope! },
                args, certifyAsync);

        /// <param name="kbScopes">
        /// The scope the call resolved to first, then any other alias fences of the same
        /// physical KB may have been recorded under (see <c>Program.EquivalentKbScopes</c>).
        /// </param>
        internal static async Task<JObject> RunAsync(
            MutationRecoveryRegistry registry,
            IReadOnlyCollection<string> kbScopes,
            JObject? args,
            Func<JObject, Task<JObject?>> certifyAsync)
        {
            string? kbScope = kbScopes.FirstOrDefault(scope => !string.IsNullOrWhiteSpace(scope));
            bool dryRun = IsPreview(args);
            string? name = Clean(args?["name"]) ?? Clean(args?["target"]);
            string? guid = Clean(args?["guid"]);
            string? entityKey = Clean(args?["entityKey"]);
            string? path = Clean(args?["path"]);
            string? type = Clean(args?["type"]);
            string? part = Clean(args?["part"]);
            string? operationId = Clean(args?["operationId"]);
            string? baseVersion = Clean(args?["baseVersion"]);

            if (name == null && guid == null && entityKey == null && path == null)
                return Failure(dryRun, "InvalidRecoveryRead",
                    "recovery_read_complete needs the fenced object: name, guid, entityKey or path.",
                    "Pass the target and part named by the PostTimeoutReadRequired error (its nextSteps carry them).");
            if (args?["force"]?.Type == JTokenType.Boolean && args["force"]!.Value<bool>())
                return Failure(dryRun, "InvalidRecoveryRead",
                    "recovery_read_complete cannot be combined with force: it never restarts a Worker.",
                    "Repeat without force.");
            if (string.IsNullOrWhiteSpace(kbScope))
                return Failure(dryRun, "KB_CONTEXT_REQUIRED",
                    "No Knowledge Base is selected, so no fence can be looked up.",
                    "Pass kb=<alias> (the alias the fenced write ran under).");

            if (dryRun) registry.RefreshIfChanged(); else registry.Refresh();
            if (!registry.IsHealthy)
            {
                var blocked = Failure(dryRun, "MutationRecoveryJournalUnavailable",
                    "The mutation recovery journal could not be trusted, so no fence can be inspected or cleared.",
                    "Call genexus_connection_recover action=journal_status, then journal_repair with dryRun=true.");
                blocked["detail"] = registry.JournalError;
                return blocked;
            }

            var readArgs = new JObject();
            if (name != null) readArgs["name"] = name;
            if (guid != null) readArgs["guid"] = guid;
            if (entityKey != null) readArgs["entityKey"] = entityKey;
            if (path != null) readArgs["path"] = path;
            if (type != null) readArgs["type"] = type;
            List<RecoveryRequirement> Lookup() => kbScopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(scope => registry.FindForRead(scope, readArgs, part))
                .Where(fence => operationId == null
                    || string.Equals(fence.OperationId, operationId, StringComparison.Ordinal))
                .ToList();
            var matches = Lookup();

            if (matches.Count == 0) return NoFence(dryRun, registry, kbScopes, name, guid, entityKey, part, operationId);
            if (matches.Count > 1)
            {
                var ambiguous = Failure(dryRun, "RecoveryFenceAmbiguous",
                    "More than one pending fence matches; none was cleared.",
                    "Repeat with part and operationId (and type/guid when homonymous objects are fenced).");
                ambiguous["fences"] = new JArray(matches.Select(Describe));
                return ambiguous;
            }

            RecoveryRequirement fence = matches[0];
            JObject selector = BuildSelector(fence);

            var first = ExtractCertificate(await certifyAsync(selector).ConfigureAwait(false), out string firstFailure);
            if (first == null)
                return Refusal(dryRun, fence, null, "RecoveryReadNotCertified", firstFailure, null);

            var verdict = RecoveryReadCertificateValidator.Evaluate(first, fence);
            bool versionPinned = baseVersion == null
                || string.Equals(baseVersion, first["versionToken"]?.ToString(), StringComparison.Ordinal);

            if (dryRun)
            {
                var inspection = Report(dryRun: true, fence, first, fenceCleared: false, persisted: false);
                bool wouldClear = verdict.Accepted && versionPinned;
                inspection["wouldClear"] = wouldClear;
                if (!wouldClear)
                    inspection["reason"] = verdict.Accepted ? "base-version-mismatch" : verdict.Reason;
                if (verdict.Detail != null) inspection["reasonDetail"] = verdict.Detail;
                return inspection;
            }

            if (!verdict.Accepted)
                return Refusal(false, fence, first, "RecoveryReadNotCertified", verdict.Reason, verdict.Detail);
            if (!versionPinned)
                return Refusal(false, fence, first, "RecoveryReadStale", "base-version-mismatch",
                    "The part changed since the version you inspected.");

            // Concurrency protection: certify again right before clearing. Anything that
            // moved between the two reads - content, version, write stamp, identity - means
            // the first certificate describes a state that is no longer current.
            var second = ExtractCertificate(await certifyAsync(selector).ConfigureAwait(false), out string secondFailure);
            if (second == null)
                return Refusal(false, fence, first, "RecoveryReadNotCertified", "confirmation-read-failed", secondFailure);
            var secondVerdict = RecoveryReadCertificateValidator.Evaluate(second, fence);
            if (!secondVerdict.Accepted)
                return Refusal(false, fence, first, "RecoveryReadStale", "confirmation-" + secondVerdict.Reason, secondVerdict.Detail);
            if (!RecoveryReadCertificateValidator.SameObservedState(first, second, out string difference))
                return Refusal(false, fence, first, "RecoveryReadStale", "version-changed-during-recovery", difference);

            if (!registry.ConfirmRead(kbScope, fence.Target, fence.Part, fence))
            {
                if (!registry.IsHealthy)
                    return Refusal(false, fence, first, "RecoveryReadStale", "journal-unavailable", registry.JournalError);

                // The fence is gone: either a newer write timeout replaced it (a different
                // fence now holds the part) or something else cleared it first - a plain
                // read, or a second recovery call. The second is not a failure, and
                // reporting it as one would send the caller around in circles.
                registry.Refresh();
                if (Lookup().Count == 0)
                {
                    var settled = Report(dryRun: false, fence, first, fenceCleared: false, persisted: false);
                    settled["reason"] = "already-cleared";
                    return settled;
                }
                return Refusal(false, fence, first, "RecoveryReadStale", "fence-rearmed",
                    "A newer write timeout re-armed the fence while it was being certified.");
            }

            return Report(dryRun: false, fence, first, fenceCleared: true, persisted: registry.IsDurable);
        }

        internal static bool IsPreview(JObject? args)
        {
            JToken? token = args?["dryRun"];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            // Only an explicit false applies; anything unparseable stays a preview.
            return !(token.Type == JTokenType.String
                && bool.TryParse(token.ToString(), out bool parsed) && !parsed);
        }

        internal static JObject BuildSelector(RecoveryRequirement fence)
        {
            // The Worker reads what the fence recorded, not what the caller loosely
            // described: a stable identity on the fence selects that object only, and a
            // fence with no recorded identity is read by name alone. The caller's `type`
            // never reaches the Worker, or it could pick which of two homonymous objects
            // gets certified for a fence that does not say which one it was.
            var selector = new JObject
            {
                ["module"] = "Read",
                ["action"] = "CertifyRecovery",
                ["client"] = "mcp",
                ["target"] = fence.Target,
                ["part"] = fence.Part,
                ["limit"] = 0
            };
            void Set(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) selector[key] = value; }
            Set("type", fence.TargetType);
            Set("guid", fence.TargetGuid);
            Set("entityKey", fence.TargetEntityKey);
            Set("path", fence.TargetPath);
            return selector;
        }

        /// <summary>Finds the certificate in a Worker envelope, or says why there is none.</summary>
        internal static JObject? ExtractCertificate(JObject? response, out string failure)
        {
            failure = "worker-unavailable";
            if (response == null) return null;
            if (response["__timeout"]?.Value<bool?>() == true)
            {
                failure = "worker-timeout";
                return null;
            }

            JToken? level = response;
            for (int depth = 0; depth < 4 && level is JObject current; depth++)
            {
                if (current["recoveryCertificate"] is JObject certificate) return certificate;
                if (current["error"] != null
                    || string.Equals(current["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase))
                {
                    failure = "worker-error:" + (current["error"]?["code"]?.ToString() ?? current["code"]?.ToString() ?? "unknown");
                    return null;
                }
                level = current["result"];
            }
            failure = "certificate-absent";
            return null;
        }

        private static JObject NoFence(
            bool dryRun, MutationRecoveryRegistry registry, IReadOnlyCollection<string> kbScopes,
            string? name, string? guid, string? entityKey, string? part, string? operationId)
        {
            string kbScope = kbScopes.First(scope => !string.IsNullOrWhiteSpace(scope));
            bool InScope(RecoveryRequirement fence)
                => kbScopes.Any(scope => string.Equals(fence.KbAlias, scope, StringComparison.OrdinalIgnoreCase));
            // Say why nothing matched: a fence recorded under another alias, another
            // identity (type/guid) or another part is the usual cause, and a caller that
            // sees only "no fence" would conclude the object is free when it is not.
            var nearMisses = registry.Pending
                .Where(fence => !MutationRecoveryRegistry.IsKbLevelRecovery(fence))
                .Where(fence => (name != null && string.Equals(fence.Target, name, StringComparison.OrdinalIgnoreCase))
                    || (guid != null && string.Equals(fence.TargetGuid, guid, StringComparison.OrdinalIgnoreCase))
                    || (entityKey != null && string.Equals(fence.TargetEntityKey, entityKey, StringComparison.OrdinalIgnoreCase)))
                .Select(fence =>
                {
                    var item = Describe(fence);
                    item["sameKb"] = InScope(fence);
                    return item;
                })
                .ToList();

            var lookup = new JObject
            {
                ["kbAlias"] = kbScope,
                ["pendingInKb"] = registry.Pending.Count(InScope),
                ["requestedPart"] = part,
                ["requestedOperationId"] = operationId,
                ["nearMisses"] = new JArray(nearMisses)
            };

            if (nearMisses.Count == 0)
            {
                var free = Report(dryRun, fence: null, certificate: null, fenceCleared: false, persisted: false);
                free["reason"] = "no-fence-pending";
                free["lookup"] = lookup;
                return free;
            }

            lookup["hint"] = "A fence exists for this object but not under this KB alias, part, operationId or identity. "
                + "Pass kb=<alias the fenced write ran under>, the fence's part, and the type/guid/entityKey it recorded.";
            var failure = Failure(dryRun, "RecoveryFenceNotFound",
                "No pending fence matches this selector, but the journal holds fences for the same object; none was cleared.",
                (string)lookup["hint"]!);
            failure["reason"] = "no-matching-fence";
            failure["lookup"] = lookup;
            return failure;
        }

        private static JObject Report(
            bool dryRun, RecoveryRequirement? fence, JObject? certificate, bool fenceCleared, bool persisted)
        {
            var report = new JObject
            {
                ["status"] = "ok",
                ["action"] = ActionName,
                ["dryRun"] = dryRun,
                ["complete"] = certificate?["complete"]?.Value<bool?>() ?? false,
                ["totalLines"] = certificate?["totalLines"],
                ["versionToken"] = certificate?["versionToken"],
                ["hash"] = certificate?["sha256"],
                ["fenceCleared"] = fenceCleared,
                ["persisted"] = persisted,
                // The action reads and, when it clears, rewrites the recovery journal. It
                // never specifies, generates, builds, reorganizes, runs or tests, and it
                // writes no object, so there is nothing implicit to list.
                ["implicitOperations"] = new JArray()
            };
            if (certificate != null)
            {
                report["identity"] = certificate["identity"]?.DeepClone();
                report["coverage"] = certificate["coverage"];
                report["utf8Bytes"] = certificate["utf8Bytes"];
                report["targetWriteStamp"] = certificate["targetWriteStamp"];
                if (fence != null)
                {
                    // `persisted` above says the journal change is durable; it says nothing
                    // about the write that timed out. This does, as far as the fence knows.
                    report["expectedVersion"] = string.IsNullOrWhiteSpace(fence.ExpectedVersion) ? null : fence.ExpectedVersion;
                    report["timedOutWrite"] = TimedOutWriteState(fence, certificate["versionToken"]?.ToString());
                }
            }
            if (fence != null) report["fence"] = Describe(fence);
            return report;
        }

        internal const string WriteNotApplied = "notApplied";
        internal const string WriteChanged = "changed";
        internal const string WriteUnknown = "unknown";

        /// <summary>
        /// What the certified version says about the write that timed out. The fence keeps
        /// the <c>baseVersion</c> the write was made against: a part still at that version
        /// was not changed, so the write did not land; any other version means the part
        /// moved (the write landed, or someone else wrote). A write that carried no
        /// <c>baseVersion</c> leaves nothing to compare, and the caller must verify the
        /// content itself before repeating it. Clearing a fence never says the write was
        /// applied.
        /// </summary>
        internal static string TimedOutWriteState(RecoveryRequirement fence, string? currentVersion)
        {
            if (string.IsNullOrWhiteSpace(fence.ExpectedVersion) || string.IsNullOrWhiteSpace(currentVersion))
                return WriteUnknown;
            return string.Equals(fence.ExpectedVersion, currentVersion, StringComparison.Ordinal)
                ? WriteNotApplied
                : WriteChanged;
        }

        private static JObject Refusal(
            bool dryRun, RecoveryRequirement fence, JObject? certificate,
            string code, string reason, string? detail)
        {
            var refusal = Report(dryRun, fence, certificate, fenceCleared: false, persisted: false);
            refusal["status"] = "error";
            refusal["reason"] = reason;
            if (detail != null) refusal["reasonDetail"] = detail;
            refusal["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = "The fence was not cleared: " + reason + (detail == null ? string.Empty : " (" + detail + ").")
                    + " Nothing was written to the object.",
                ["hint"] = reason == "base-version-mismatch"
                    ? "The part changed after the version you inspected. Repeat recovery_read_complete with dryRun=true to get the current versionToken, then confirm with it as baseVersion."
                    : code == "RecoveryReadStale"
                        ? "The part or the fence changed while it was being certified. Repeat recovery_read_complete with dryRun=true, then without it using the new versionToken as baseVersion."
                        : "Repeat with dryRun=true to see the certificate, or read the part with genexus_read limit=0.",
                ["retryable"] = false,
                ["reconciliationRequired"] = true
            };
            return refusal;
        }

        private static JObject Failure(bool dryRun, string code, string message, string hint)
        {
            var failure = Report(dryRun, fence: null, certificate: null, fenceCleared: false, persisted: false);
            failure["status"] = "error";
            failure["error"] = new JObject
            {
                ["code"] = code, ["message"] = message, ["hint"] = hint,
                ["retryable"] = false, ["reconciliationRequired"] = true
            };
            return failure;
        }

        private static JObject Describe(RecoveryRequirement fence) => new JObject
        {
            ["kbAlias"] = fence.KbAlias,
            ["target"] = fence.Target,
            ["part"] = fence.Part,
            ["operationId"] = fence.OperationId,
            ["requiredAtUtc"] = fence.RequiredAtUtc,
            ["targetType"] = string.IsNullOrWhiteSpace(fence.TargetType) ? null : fence.TargetType,
            ["targetGuid"] = string.IsNullOrWhiteSpace(fence.TargetGuid) ? null : fence.TargetGuid,
            ["targetEntityKey"] = string.IsNullOrWhiteSpace(fence.TargetEntityKey) ? null : fence.TargetEntityKey
        };

        private static string? Clean(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            string value = token.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
