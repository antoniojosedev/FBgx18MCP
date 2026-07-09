using System;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_gam — GeneXus GAM / integrated-security provisioning surface over the
    /// SDK's <see cref="IIntegratedSecurityService"/>. Same service-resolution idiom
    /// as <see cref="GxServerSyncService"/>: resolve via <c>SdkServices.TryGetService</c>,
    /// guard every null/throw path, never crash the worker.
    ///
    /// Distinct from the existing <c>genexus_security</c> tool (env-scan / regex secrets
    /// audit) — this is the SDK-native provisioning surface that actually talks to
    /// Define API / Deploy security / GAM table creation.
    ///
    /// action=status is read-only (IsEnabledIntegratedSecurity + the
    /// ReorganizeGamDatabase property) and safe to call anytime.
    ///
    /// action=define_api / action=deploy are DESTRUCTIVE: they call the SDK's
    /// DefineAPI/Deploy, which can create or alter the GAM security tables in the
    /// KB's configured datastore. There is no default action that reaches them —
    /// the caller must pass action=define_api or action=deploy explicitly.
    ///
    /// define_api additionally needs a KBEnvironment (not just KBModel). Resolved off
    /// KBModel.Environment the same way DatabaseInfoService / KbStartupService already
    /// do it in this codebase (dynamic — the concrete return type isn't safely nameable
    /// across SDK builds); if that resolves to null, define_api reports
    /// DefineApiEnvironmentUnavailable instead of guessing.
    /// </summary>
    public class GamService
    {
        private readonly KbService _kb;

        public GamService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "status";
            action = action.Trim().ToLowerInvariant();

            if (action != "status" && action != "define_api" && action != "deploy")
            {
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'. Expected one of: status, define_api, deploy.",
                    hint: "Pass action=status (read-only), action=define_api, or action=deploy.",
                    nextSteps: new JArray
                    {
                        McpResponse.NextStep("genexus_gam", new JObject { ["action"] = "status" },
                            "Check whether integrated security is enabled for this KB.")
                    });
            }

            // IIntegratedSecurityService itself doesn't implement IGxService, so it fails
            // the `where TSrv : IGxService` constraint on Services.TryGetService<TSrv>().
            // The concrete implementation (Artech.Packages.GAM.IntegratedSecurityService)
            // does implement IGxService — resolve that and cast to the interface.
            IIntegratedSecurityService svc;
            try
            {
                var concrete = SdkServices.TryGetService<Artech.Packages.GAM.IntegratedSecurityService>();
                svc = concrete as IIntegratedSecurityService;
            }
            catch { svc = null; }

            if (svc == null)
            {
                return McpResponse.Err(
                    code: "IntegratedSecurityServiceUnavailable",
                    message: "The GeneXus SDK's IIntegratedSecurityService is not registered in this worker session.",
                    hint: "The GAM package may not be loaded in this worker. Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            KnowledgeBase kb;
            KBModel model;
            try
            {
                kb = _kb?.GetKB() as KnowledgeBase;
                model = kb?.DesignModel;
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GamFailed", message: ex.Message, hint: "Check the worker log for details.");
            }

            if (model == null)
            {
                return McpResponse.Err(
                    code: "KbNotOpen",
                    message: "No open KB / design model available.",
                    hint: "Open a KB first (genexus_kb action=open).");
            }

            if (action == "status") return StatusEnvelope(svc, model);
            if (action == "define_api") return DefineApi(svc, model, args);
            return Deploy(svc, model, args);
        }

        private static string StatusEnvelope(IIntegratedSecurityService svc, KBModel model)
        {
            try
            {
                bool enabled = svc.IsEnabledIntegratedSecurity(model);
                bool reorganize = false;
                try { reorganize = svc.GetReorganizeGamDatabasePropertyValue(model); } catch { /* optional flag */ }

                return McpResponse.Ok(
                    code: "GamStatusRetrieved",
                    result: new JObject
                    {
                        ["integratedSecurityEnabled"] = enabled,
                        ["reorganizeGamDatabase"] = reorganize,
                        ["source"] = "sdk:IIntegratedSecurityService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GamFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private static string DefineApi(IIntegratedSecurityService svc, KBModel model, JObject args)
        {
            bool force = args?["force"]?.ToObject<bool?>() ?? false;

            dynamic environment;
            try { environment = model.Environment; }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GamFailed", message: ex.Message, hint: "Check the worker log for details.");
            }

            if (environment == null)
            {
                return McpResponse.Err(
                    code: "DefineApiEnvironmentUnavailable",
                    message: "Could not resolve a KBEnvironment for this KB's design model.",
                    hint: "define_api requires an active Environment. Confirm the KB has an Environment configured in the IDE, then retry.");
            }

            try
            {
                bool ok = svc.DefineAPI(environment, force);
                return McpResponse.Ok(
                    code: "GamDefineApiCompleted",
                    result: new JObject
                    {
                        ["success"] = ok,
                        ["force"] = force,
                        ["source"] = "sdk:IIntegratedSecurityService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GamFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private static string Deploy(IIntegratedSecurityService svc, KBModel model, JObject args)
        {
            bool? forceTableCreation = args?["forceTableCreation"]?.ToObject<bool?>();
            bool? rebuild = args?["rebuild"]?.ToObject<bool?>();

            try
            {
                bool ok = forceTableCreation.HasValue || rebuild.HasValue
                    ? svc.Deploy(model, forceTableCreation ?? false, rebuild ?? false)
                    : svc.Deploy(model);

                return McpResponse.Ok(
                    code: "GamDeployCompleted",
                    result: new JObject
                    {
                        ["success"] = ok,
                        ["forceTableCreation"] = forceTableCreation ?? false,
                        ["rebuild"] = rebuild ?? false,
                        ["source"] = "sdk:IIntegratedSecurityService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GamFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }
    }
}
