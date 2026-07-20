using System;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GenexusServices = Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_create action=curl_procedure (P2 #9). Scaffolds a REST-consumer Procedure from
    /// a curl command — the IDE "Import from cURL" flow — over
    /// <c>ICurlGeneratorService.Generate(model, procName, description, parent, curlCommand)</c>.
    /// Creates a KB object (write). Constructs the public concrete <c>CurlGeneratorService</c>
    /// (not in the headless registry; ConstructOrResolve).
    /// </summary>
    public class CurlProcService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public CurlProcService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string procName = args?["name"]?.ToString();
            string curl = args?["curl"]?.ToString() ?? args?["curlCommand"]?.ToString() ?? args?["content"]?.ToString();
            string description = args?["description"]?.ToString() ?? procName;

            if (string.IsNullOrWhiteSpace(procName))
                return McpResponse.Err("BadArgs", "curl_procedure requires name (the new Procedure's name).", "Pass name=<ProcName>.");
            if (string.IsNullOrWhiteSpace(curl))
                return McpResponse.Err("BadArgs", "curl_procedure requires curl (the curl command).", "Pass curl=\"curl -X POST https://...\".");

            KBModel model;
            try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; }
            catch { model = null; }
            if (model == null)
                return McpResponse.Err("NoKbOpen", "No open KB / design model available.", "Open a KB first (genexus_kb action=open).");

            var svc = SdkServiceLocator.ConstructOrResolve<GenexusServices.ICurlGeneratorService>(
                () => new Artech.Packages.Genexus.BL.Services.CurlGeneratorService());
            if (svc == null)
                return McpResponse.Err("CurlGeneratorServiceUnavailable", "Could not construct the SDK's CurlGeneratorService.", "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                // parent = null → created at the KB root (folder/module placement is IDE-only).
                svc.Generate(model, procName, description, null, curl);

                KBObject created = null;
                try { created = _objects?.FindObject(procName, "Procedure"); } catch { }

                return McpResponse.Ok(
                    code: created != null ? "CurlProcedureCreated" : "CurlProcedureGenerated",
                    result: new JObject
                    {
                        ["name"] = procName,
                        ["created"] = created != null,
                        ["hint"] = created == null ? "Generate returned without error; the Procedure may need a genexus_lifecycle action=index to appear." : null,
                        ["source"] = "sdk:ICurlGeneratorService.Generate"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("CurlGenerateFailed", ex.Message, "Check the curl syntax and the worker log for the full stack trace.");
            }
        }
    }
}
