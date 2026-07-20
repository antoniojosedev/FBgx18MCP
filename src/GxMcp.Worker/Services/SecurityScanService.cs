using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using Scanner = GeneXus.SecurityScanner.Common;
using ScannerServices = GeneXus.SecurityScanner.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_security action=scan_native — the native GeneXus Security Scanner (P0 #2).
    /// Read-only. Distinct from action=audit_gam (env-prop scan) and action=scan_secrets
    /// (regex over Source): this drives the SDK's own <c>ISecurityScannerService.Scan</c>,
    /// the same engine the IDE's Security Scanner runs.
    ///
    /// Flow: <c>SecurityScanPlan.GetForModel(model)</c> gives the plan (whitelist +
    /// authorization procedure); a <c>KBObjectQuery</c> is built from it and passed with a
    /// worker-side <see cref="Collector"/> implementing <c>IScannerOuput</c> that accumulates
    /// findings. <c>ISecurityScannerService</c> is non-<c>IGxService</c> → resolved via
    /// <see cref="SdkServiceLocator"/> (interface-GUID locator).
    /// </summary>
    public class SecurityScanService
    {
        private readonly KbService _kb;

        public SecurityScanService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            KBModel model;
            try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; }
            catch { model = null; }

            if (model == null)
                return McpResponse.Err(
                    code: "NoKbOpen",
                    message: "No open KB / design model available.",
                    hint: "Open a KB first (genexus_kb action=open).");

            var scanner = SdkServiceLocator.TryResolve<ScannerServices.ISecurityScannerService>();
            if (scanner == null)
                return McpResponse.Err(
                    code: "SecurityScannerServiceUnavailable",
                    message: "The GeneXus SDK's ISecurityScannerService is not registered in this worker session.",
                    hint: "The Security Scanner package may not be loaded. Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                var plan = ScannerServices.SecurityScanPlan.GetForModel(model);
                if (plan == null)
                    return McpResponse.Err(
                        code: "SecurityScanPlanUnavailable",
                        message: "No SecurityScanPlan is defined for this KB model.",
                        hint: "Configure the Security Scanner in the GeneXus IDE first (it seeds the default plan).");

                var query = new ScannerServices.KBObjectQuery
                {
                    Model = model,
                    WhiteList = plan.GetWhiteList(),
                    AuthorizationProcedure = plan.GetAuthorizationProcedure()
                };

                var output = new Collector();
                scanner.Scan(query, plan, output);

                return McpResponse.Ok(
                    code: "SecurityScanCompleted",
                    result: new JObject
                    {
                        ["planName"] = SafeStr(() => plan.Name),
                        ["errorCount"] = output.Errors.Count,
                        ["warningCount"] = output.Warnings.Count,
                        ["findings"] = output.ToJson(),
                        ["source"] = "sdk:ISecurityScannerService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "SecurityScanFailed", message: ex.Message, hint: "Check the worker log for the full stack trace.");
            }
        }

        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }

        /// <summary>
        /// Worker-side <c>IScannerOuput</c> that accumulates scan findings instead of driving
        /// the IDE's report window. Every method is defensive — the scanner drives the whole
        /// interface and a throw here would abort the scan.
        /// </summary>
        private sealed class Collector : Scanner.IScannerOuput
        {
            public readonly List<JObject> Errors = new List<JObject>();
            public readonly List<JObject> Warnings = new List<JObject>();
            private readonly List<string> _infos = new List<string>();

            public void SetError(Scanner.ISecurityCommand command, string description, string name, string message, KBObject m_Object)
                => Errors.Add(Row("error", message ?? description, m_Object, null, name));

            public void SetError(string message, KBObject m_Object, string link)
                => Errors.Add(Row("error", message, m_Object, link, null));

            public void SetWarning(string message, KBObject m_Object, string link)
                => Warnings.Add(Row("warning", message, m_Object, link, null));

            public void SetModelError(Scanner.ISecurityCommand command, string description, string message)
                => Errors.Add(Row("modelError", message ?? description, null, null, null));

            public void SetInfo(string message) { if (!string.IsNullOrEmpty(message)) _infos.Add(message); }
            public void SectionStart() { }
            public void SectionEnd(bool success) { }
            public void Show() { }
            public void Clean() { Errors.Clear(); Warnings.Clear(); _infos.Clear(); }

            public JArray ToJson()
            {
                var arr = new JArray();
                foreach (var e in Errors) arr.Add(e);
                foreach (var w in Warnings) arr.Add(w);
                return arr;
            }

            private static JObject Row(string severity, string message, KBObject obj, string link, string name)
            {
                string objName = null;
                try { objName = obj?.Name; } catch { }
                return new JObject
                {
                    ["severity"] = severity,
                    ["message"] = message,
                    ["object"] = objName ?? name,
                    ["link"] = link
                };
            }
        }
    }
}
