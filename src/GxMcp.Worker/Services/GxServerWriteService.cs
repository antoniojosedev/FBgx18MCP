using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Architecture.Common.Services.TeamDevData.Server;
using Artech.Udm.Framework;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// GxServer (Team Development) WRITE surface for genexus_gxserver — commit,
    /// update, lock, resolve. Sibling to <see cref="GxServerSyncService"/> (the
    /// read-only status/pending/conflicts/history path); shares the same
    /// SDK-service-resolution idiom (SdkServices.TryGetService, guard every
    /// null/throw path, never crash the worker).
    ///
    /// Resolves <see cref="IGXserverService"/> for Commit/GetUpdateFile/Lock,
    /// and <see cref="ITeamDevClientService"/> (same service the read path
    /// already resolves) for MarkAsResolved.
    ///
    /// Feasibility note (spike, v2.14): the SDK's write-path data-transfer
    /// objects (<see cref="ServerCommitData"/>/<see cref="ServerUpdateData"/>/
    /// <see cref="ServerLockData"/>) are plain settable POCOs — confirmed via
    /// reflection against Artech.Architecture.Common.dll, no hidden IDE/session
    /// context required to *construct* them (Model + KBAlias + the action's own
    /// fields is enough). What remains unverified is whether
    /// <see cref="IGXserverService"/> itself resolves in a headless worker and
    /// whether Commit/GetUpdateFile/Lock succeed without the authenticated
    /// Team-Dev session the IDE's login flow normally establishes — no
    /// GXserver-linked KB was available locally to exercise this live. Every
    /// call is defensively wrapped; failures surface as clean error envelopes
    /// rather than crashing the worker.
    /// </summary>
    public class GxServerWriteService
    {
        private readonly KbService _kb;

        public GxServerWriteService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(string action, JObject args)
        {
            KnowledgeBase kb;
            try
            {
                kb = _kb?.GetKB() as KnowledgeBase;
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbUnavailable", message: ex.Message, hint: "Open a KB first (genexus_kb action=open).");
            }
            if (kb == null)
            {
                return McpResponse.Err(code: "KbUnavailable", message: "No KB is open in this worker.", hint: "Open a KB first (genexus_kb action=open).");
            }

            ITeamDevClientService tdSvc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<ITeamDevClientService>();
            if (tdSvc == null)
            {
                return McpResponse.Err(
                    code: "GxServerServiceUnavailable",
                    message: "The GeneXus SDK's ITeamDevClientService is not registered in this worker session (self-heal retries were exhausted).",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            bool linked;
            try { linked = tdSvc.IsLinkedKB(kb); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GxServerCheckFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
            if (!linked)
            {
                return McpResponse.Err(
                    code: "NotLinked",
                    message: "This KB is not linked to a GeneXus Server instance.",
                    hint: "Link the KB to a GeneXus Server first (IDE: Team Development > Connect), then retry.");
            }

            KBModel model = kb.DesignModel;
            string kbPath = _kb?.GetKbPath();
            string kbAlias = Environment.GetEnvironmentVariable("GX_KB_ALIAS")
                             ?? (string.IsNullOrEmpty(kbPath) ? string.Empty : Path.GetFileName(kbPath.TrimEnd('\\', '/')));

            switch (action)
            {
                case "commit": return DoCommit(tdSvc, model, kbAlias, args);
                case "update": return DoUpdate(model, kbAlias, args);
                case "lock": return DoLock(model, kbAlias, args);
                case "resolve": return DoResolve(tdSvc, model, args);
                default:
                    return McpResponse.Err(code: "BadAction", message: "Unknown write action '" + action + "'.");
            }
        }

        private string DoCommit(ITeamDevClientService tdSvc, KBModel model, string kbAlias, JObject args)
        {
            string message = args?["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "message is required for commit.",
                    hint: "Pass action=commit, message=\"<commit comment>\".");
            }

            IGXserverService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IGXserverService>();
            if (svc == null) return ServerServiceUnavailable();

            // issue #32 item 6: optional partial commit. IGXserverService.Commit has no
            // per-object selection — it commits the whole non-ignored changelist. The IDE's
            // "commit these objects" works by marking every OTHER pending object
            // IgnoreForCommit, committing, then restoring the flags. We mirror that. When
            // `targets` is omitted this is the previous whole-changelist behavior.
            var targetsToken = args?["targets"] as JArray;
            HashSet<string> wanted = null;
            if (targetsToken != null && targetsToken.Count > 0)
            {
                wanted = new HashSet<string>(
                    targetsToken.Select(t => t?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)),
                    StringComparer.OrdinalIgnoreCase);
                if (wanted.Count == 0)
                    return McpResponse.Err(code: "BadArgs", message: "targets must contain at least one non-empty object name.");
            }

            var ignored = new List<KBObjectHistory>();
            try
            {
                if (wanted != null)
                {
                    // Partial commit requires enumerating + toggling the local changelist,
                    // which is the ITeamDevClientService surface (resolved by the caller).
                    List<KBObjectHistory> pending;
                    try
                    {
                        pending = (tdSvc.GetLocalChanges(model) ?? Enumerable.Empty<KBObjectHistory>())
                            .Where(h => h != null).ToList();
                    }
                    catch (Exception ex)
                    {
                        return McpResponse.Err(code: "CommitFailed", message: "Could not read the pending changelist for partial commit: " + ex.Message, hint: "Retry without targets to commit the whole changelist, or check the worker log.");
                    }

                    var pendingNames = new HashSet<string>(
                        pending.Select(h => SafeStr(() => h.ObjectName)).Where(n => !string.IsNullOrEmpty(n)),
                        StringComparer.OrdinalIgnoreCase);

                    var unknown = wanted.Where(w => !pendingNames.Contains(w)).ToList();
                    if (unknown.Count > 0)
                    {
                        // Refuse rather than commit everything — the caller asked for a subset.
                        return McpResponse.Err(
                            code: "NoMatchingPending",
                            message: "These targets are not in the pending changelist: " + string.Join(", ", unknown) + ". Nothing was committed.",
                            hint: "Check action=pending for the exact committable object names, then retry.");
                    }

                    // Exclude every pending object the caller did NOT ask for; ensure the
                    // wanted ones are enabled. Track excluded keys so we can restore them.
                    foreach (var h in pending)
                    {
                        string name = SafeStr(() => h.ObjectName);
                        if (name != null && !wanted.Contains(name))
                        {
                            try { tdSvc.IgnoreForCommit(model, h.Key); ignored.Add(h); } catch { /* best-effort */ }
                        }
                        else
                        {
                            try { tdSvc.EnableForCommit(model, h.Key); } catch { /* best-effort */ }
                        }
                    }
                }

                var data = new ServerCommitData
                {
                    Model = model,
                    KBAlias = kbAlias,
                    Comments = message,
                    CommitDate = DateTime.Now,
                    ForceCommit = args?["force"]?.ToObject<bool?>() ?? false
                };

                string errorMsg;
                bool ok = svc.Commit(data, null, out errorMsg);
                if (!ok)
                {
                    return McpResponse.Err(
                        code: "CommitFailed",
                        message: string.IsNullOrEmpty(errorMsg) ? "Commit returned false." : errorMsg,
                        hint: "Check for pending conflicts (action=conflicts) before retrying.");
                }

                var result = new JObject
                {
                    ["committed"] = true,
                    ["message"] = message,
                    ["source"] = "sdk:IGXserverService"
                };
                if (wanted != null)
                {
                    result["partial"] = true;
                    result["committedTargets"] = new JArray(wanted);
                    result["excludedCount"] = ignored.Count;
                }
                return McpResponse.Ok(code: "GxServerCommitCompleted", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CommitFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
            finally
            {
                // Restore ignore flags on the objects we excluded so a later whole-changelist
                // commit doesn't silently skip them. Best-effort — a failure here only means
                // the excluded objects stay ignored until re-enabled manually.
                foreach (var h in ignored)
                {
                    try { tdSvc.EnableForCommit(model, h.Key); } catch { /* best-effort */ }
                }
            }
        }

        private string DoUpdate(KBModel model, string kbAlias, JObject args)
        {
            IGXserverService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IGXserverService>();
            if (svc == null) return ServerServiceUnavailable();

            try
            {
                var data = new ServerUpdateData
                {
                    Model = model,
                    KBAlias = kbAlias,
                    UpdateStatistics = false,
                    ExportAll = args?["all"]?.ToObject<bool?>() ?? false
                };

                string updateFile = svc.GetUpdateFile(data);
                return McpResponse.Ok(
                    code: "GxServerUpdateFileRetrieved",
                    result: new JObject
                    {
                        ["updateFile"] = updateFile ?? string.Empty,
                        ["note"] = "Downloaded the pending-changes package from the server. Applying it into local KB objects (ITeamDevClientUpdate.Update) is a separate, not-yet-wired step — use the IDE's Team Development > Update to apply.",
                        ["source"] = "sdk:IGXserverService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "UpdateFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string DoLock(KBModel model, string kbAlias, JObject args)
        {
            string target = args?["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(target))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "target is required for lock.",
                    hint: "Pass action=lock, target=<object name>.");
            }

            IGXserverService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IGXserverService>();
            if (svc == null) return ServerServiceUnavailable();

            try
            {
                var data = new ServerLockData
                {
                    Model = model,
                    KBAlias = kbAlias,
                    FilePath = target
                };

                ServerLockData result = svc.Lock(data);
                return McpResponse.Ok(
                    code: "GxServerLockAcquired",
                    result: new JObject
                    {
                        ["target"] = target,
                        ["locked"] = result != null,
                        ["note"] = "ServerLockData.FilePath expects the KB-relative export path, not necessarily the bare object name — unverified against a live GXserver.",
                        ["source"] = "sdk:IGXserverService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "LockFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string DoResolve(ITeamDevClientService tdSvc, KBModel model, JObject args)
        {
            var targetsToken = args?["targets"] as JArray;
            if (targetsToken == null || targetsToken.Count == 0)
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "targets (non-empty array of object names) is required for resolve.",
                    hint: "Pass action=resolve, targets=[\"Customer\", ...].");
            }

            var wanted = new HashSet<string>(
                targetsToken.Select(t => t?.ToString()).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
            {
                return McpResponse.Err(code: "BadArgs", message: "targets must contain at least one non-empty object name.");
            }

            try
            {
                var keys = new List<EntityKey>();
                var matched = new List<string>();
                foreach (var ct in new[] { UpdateConflict.YesMustOverwrite, UpdateConflict.YesWithAutoMerge })
                {
                    IEnumerable<Entity> raw = tdSvc.GetConflictEntities(model, ct);
                    if (raw == null) continue;
                    foreach (Entity e in raw)
                    {
                        string name = SafeStr(() => e.Name);
                        if (name != null && wanted.Contains(name))
                        {
                            keys.Add(e.Key);
                            matched.Add(name);
                        }
                    }
                }

                if (keys.Count == 0)
                {
                    return McpResponse.Err(
                        code: "NoMatchingConflicts",
                        message: "None of the requested targets have an active conflict.",
                        hint: "Check action=conflicts for current conflict object names.");
                }

                bool ok = tdSvc.MarkAsResolved(model, keys);
                if (!ok)
                {
                    return McpResponse.Err(code: "ResolveFailed", message: "MarkAsResolved returned false.", hint: "Check the worker log for details.");
                }

                return McpResponse.Ok(
                    code: "GxServerConflictsResolved",
                    result: new JObject
                    {
                        ["resolved"] = true,
                        ["targets"] = new JArray(matched),
                        ["count"] = matched.Count,
                        ["source"] = "sdk:ITeamDevClientService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ResolveFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private static string ServerServiceUnavailable()
        {
            return McpResponse.Err(
                code: "GxServerServiceUnavailable",
                message: "The GeneXus SDK's IGXserverService is not registered in this worker session (self-heal retries were exhausted).",
                hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
        }


        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
