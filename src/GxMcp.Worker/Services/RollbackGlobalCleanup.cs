using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Parsers;

namespace GxMcp.Worker.Services
{
    /// <summary>A global the cleanup re-resolved in the live model by its recorded GUID.</summary>
    internal sealed class ResolvedGlobal
    {
        internal Guid Guid;
        internal string Name;
        internal string TypeName;
    }

    /// <summary>An object the reference graph reports as using the global.</summary>
    internal sealed class ReferencingObject
    {
        internal string Name;
        internal string TypeName;
        /// <summary>Empty when the reference did not resolve to an object.</summary>
        internal Guid Guid;
    }

    internal sealed class ReferenceProbe
    {
        internal List<ReferencingObject> References = new List<ReferencingObject>();
        /// <summary>True when the walk stopped at its bound, so more references may exist.</summary>
        internal bool Truncated;
    }

    /// <summary>
    /// SDK boundary for undoing the globals a reverted Structure write created.
    /// Every member may throw; the cleanup treats a throw as "unknown" and keeps
    /// the global.
    /// </summary>
    internal interface ICreatedGlobalStore
    {
        /// <summary>GUID of the object whose write was rolled back, when it resolves.</summary>
        Guid? TargetGuid { get; }

        /// <summary>The object the GUID resolves to, or null when nothing does.</summary>
        ResolvedGlobal Resolve(Guid guid);

        /// <summary>Objects referencing the global, from the Worker's reference graph.</summary>
        ReferenceProbe FindReferences(Guid guid);

        /// <summary>Deletes through the Worker's object-delete path and returns its envelope.</summary>
        JObject Delete(ResolvedGlobal global);
    }

    /// <summary>
    /// Removes the global Attributes a rolled-back Structure write created and
    /// reports what was removed and what was kept, with the reason (issue #592).
    ///
    /// Fail closed: a global is deleted only when the rollback was verified, its
    /// GUID still resolves to an Attribute of the recorded name, and the
    /// reference graph answers that no object other than the rolled-back one
    /// uses it. Anything unknown keeps the global and says so.
    /// </summary>
    internal static class RollbackGlobalCleanup
    {
        internal const string ReasonRollbackUnverified = "rollbackUnverified";
        internal const string ReasonIdentityUnknown = "identityUnknown";
        internal const string ReasonIdentityMismatch = "identityMismatch";
        internal const string ReasonResolveUnavailable = "resolveUnavailable";
        internal const string ReasonReferenced = "referenced";
        internal const string ReasonReferenceCheckUnavailable = "referenceCheckUnavailable";
        internal const string ReasonDeleteFailed = "deleteFailed";

        /// <summary>Bounded echo of referencing objects per retained global.</summary>
        internal const int ReferencePreviewLimit = 10;

        internal static JObject Run(IReadOnlyList<TransactionDslParser.CreatedGlobalIdentity> created, ICreatedGlobalStore store, bool rollbackVerified)
        {
            var removed = new JArray();
            var retained = new JArray();
            foreach (var global in created ?? new TransactionDslParser.CreatedGlobalIdentity[0])
            {
                if (global == null) continue;
                var entry = new JObject
                {
                    ["name"] = global.Name,
                    ["guid"] = global.Guid == Guid.Empty ? null : global.Guid.ToString()
                };
                string reason = TryRemove(global, store, rollbackVerified, entry);
                if (reason == null)
                {
                    entry["removed"] = true;
                    removed.Add(entry);
                }
                else
                {
                    entry["removed"] = false;
                    entry["reason"] = reason;
                    retained.Add(entry);
                }
            }

            var receipt = new JObject
            {
                ["removed"] = removed,
                ["retained"] = retained,
                ["complete"] = retained.Count == 0
            };
            if (retained.Count > 0) receipt["hint"] = BuildRetainedHint(retained);
            return receipt;
        }

        /// <summary>
        /// Null when the global was removed (or no longer exists); otherwise the
        /// reason it was kept, with the supporting detail written on the entry.
        /// </summary>
        private static string TryRemove(TransactionDslParser.CreatedGlobalIdentity global, ICreatedGlobalStore store, bool rollbackVerified, JObject entry)
        {
            if (!rollbackVerified) return ReasonRollbackUnverified;
            if (global.Guid == Guid.Empty) return ReasonIdentityUnknown;
            if (store == null)
            {
                entry["error"] = "No Knowledge Base access was available for the cleanup.";
                return ReasonResolveUnavailable;
            }

            ResolvedGlobal resolved;
            try { resolved = store.Resolve(global.Guid); }
            catch (Exception ex)
            {
                entry["error"] = ex.Message;
                return ReasonResolveUnavailable;
            }
            if (resolved == null)
            {
                // Nothing resolves to the GUID this write created: no orphan remains.
                entry["alreadyAbsent"] = true;
                entry["reReadConfirmed"] = true;
                return null;
            }
            if (!string.Equals(resolved.Name, global.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(resolved.TypeName, "Attribute", StringComparison.OrdinalIgnoreCase))
            {
                entry["resolvedName"] = resolved.Name;
                entry["resolvedType"] = resolved.TypeName;
                return ReasonIdentityMismatch;
            }

            ReferenceProbe probe;
            try { probe = store.FindReferences(global.Guid); }
            catch (Exception ex)
            {
                entry["error"] = ex.Message;
                return ReasonReferenceCheckUnavailable;
            }
            if (probe == null || probe.References == null) return ReasonReferenceCheckUnavailable;

            // The rolled-back object's own reference is explained by the verified
            // restore (its Structure no longer carries the attribute). Any other
            // referencer, and any reference that did not resolve, keeps the global.
            Guid? target = store.TargetGuid;
            var others = probe.References
                .Where(r => r != null && (r.Guid == Guid.Empty || target == null || r.Guid != target.Value))
                .ToList();
            if (probe.Truncated || others.Count > 0)
            {
                entry["references"] = new JArray(others.Take(ReferencePreviewLimit).Select(r => new JObject
                {
                    ["name"] = r.Name,
                    ["type"] = r.TypeName
                }));
                entry["referenceCount"] = others.Count;
                entry["referencesTruncated"] = probe.Truncated;
                return ReasonReferenced;
            }

            JObject deletion;
            try { deletion = store.Delete(resolved); }
            catch (Exception ex)
            {
                entry["error"] = ex.Message;
                return ReasonDeleteFailed;
            }
            string status = deletion?["status"]?.ToString();
            bool deleted = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
            if (!deleted)
            {
                entry["error"] = deletion?["error"]?["message"]?.ToString()
                    ?? deletion?["message"]?.ToString()
                    ?? "The delete path returned no success envelope.";
                string code = deletion?["error"]?["code"]?.ToString() ?? deletion?["code"]?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) entry["code"] = code;
                return ReasonDeleteFailed;
            }
            entry["reReadConfirmed"] = deletion["result"]?["rereadConfirmed"]?.Value<bool?>() == true
                || deletion["rereadConfirmed"]?.Value<bool?>() == true;
            return null;
        }

        private static string BuildRetainedHint(JArray retained)
        {
            var names = retained.OfType<JObject>()
                .Select(r => "'" + r["name"] + "' (" + r["reason"] + ")")
                .ToList();
            return "Global Attribute(s) created by this write remain in the Knowledge Base: "
                + string.Join(", ", names)
                + ". Inspect the listed references, then remove each one with genexus_delete_object name=<name> type=Attribute confirm=true (dryRun=true first).";
        }
    }
}
