using System;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_module — GeneXus Module Manager (install/update modules) over the
    /// SDK's <see cref="IModuleManagerService"/>. Follows the same SDK-service-
    /// resolution idiom as <see cref="CompareService"/>/<see cref="GxServerSyncService"/>:
    /// resolve via <c>SdkServices.TryGetService</c>, guard every null/throw path,
    /// never crash the worker. If the Module Manager package isn't loaded in this
    /// session, returns a clean <c>ModuleManagerServiceUnavailable</c> error.
    ///
    /// Spike scope (feasibility gate): only the string/KBModel-based overloads are
    /// wired — Install(KBModel,string opcFile), InstallByName, InstallBuiltIn, and
    /// Update(KBModel,Module,string) (the Module KB object is resolved by name via
    /// ObjectService.FindObject, same as CompareService resolves KBObjects). The
    /// overloads requiring a constructed ModulePackage/IModuleManagerServer are NOT
    /// wired — no clean headless path to build those types.
    /// </summary>
    public class ModuleService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public ModuleService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "list";
            action = action.Trim().ToLowerInvariant();

            if (action != "install" && action != "install_builtin" && action != "update" && action != "list")
            {
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'. Expected one of: install, install_builtin, update, list.",
                    hint: "Pass action=install, action=install_builtin, action=update, or action=list.");
            }

            KnowledgeBase kb;
            try { kb = _kb?.GetKB() as KnowledgeBase; }
            catch { kb = null; }

            if (kb == null)
            {
                return McpResponse.Err(
                    code: "NoKbOpen",
                    message: "No KB is open in this worker session.",
                    hint: "Open a KB first (genexus_kb action=open).");
            }

            if (action == "list")
            {
                return ListModules(kb);
            }

            // issue #32 item 2 (generalized): resilient resolve — self-heals a lazy/late
            // SDK registration after a worker respawn before hard-failing.
            IModuleManagerService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IModuleManagerService>();

            if (svc == null)
            {
                return McpResponse.Err(
                    code: "ModuleManagerServiceUnavailable",
                    message: "The GeneXus SDK's IModuleManagerService is not registered in this worker session (self-heal retries were exhausted).",
                    hint: "The Module Manager package may not be loaded in this worker. Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            var model = kb.DesignModel;
            string opcFile = args?["opcFile"]?.ToString();
            string name = args?["name"]?.ToString();
            string version = args?["version"]?.ToString();

            try
            {
                if (action == "install")
                {
                    if (!string.IsNullOrWhiteSpace(opcFile))
                    {
                        bool ok = svc.Install(model, opcFile);
                        return InstallResult(ok, "opcFile", opcFile);
                    }
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        bool ok = svc.InstallByName(model, name, version);
                        return InstallResult(ok, "name", name);
                    }
                    return McpResponse.Err(
                        code: "BadArgs",
                        message: "action=install requires either opcFile or name.",
                        hint: "Pass opcFile=<path to a .opc file> or name=<module name> (optionally version=<x>).");
                }

                if (action == "install_builtin")
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return McpResponse.Err(
                            code: "BadArgs",
                            message: "action=install_builtin requires name.",
                            hint: "Pass name=<built-in module name>.");
                    }
                    bool ok = svc.InstallBuiltIn(model, name);
                    return InstallResult(ok, "name", name);
                }

                // action == "update"
                if (string.IsNullOrWhiteSpace(name))
                {
                    return McpResponse.Err(
                        code: "BadArgs",
                        message: "action=update requires name.",
                        hint: "Pass name=<installed module name> and version=<target version>.");
                }

                KBObject moduleObj;
                try
                {
                    moduleObj = _objects?.FindObject(name, "Module");
                }
                catch (Exception ex)
                {
                    return McpResponse.Err(code: "ModuleUpdateFailed", message: ex.Message, hint: "Check the worker log for details.");
                }

                if (!(moduleObj is Module module))
                {
                    return McpResponse.Err(
                        code: "ModuleNotFound",
                        message: "Module '" + name + "' not found in this KB.",
                        hint: "Confirm the module is installed (genexus_module action=list).");
                }

                bool updated = svc.Update(model, module, version);
                return McpResponse.Ok(
                    code: updated ? "ModuleUpdated" : "ModuleUpdateDeclined",
                    result: new JObject
                    {
                        ["success"] = updated,
                        ["name"] = name,
                        ["version"] = version,
                        ["source"] = "sdk:IModuleManagerService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleOperationFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        /// <summary>
        /// Read-only enumeration of "Module" KB objects (the GX organizational/
        /// package unit installed modules attach to), same shape as the runtime
        /// type check already used by ListService/IndexCacheService
        /// (<c>Artech.Architecture.Common.Objects.Module</c>).
        /// </summary>
        private static string ListModules(dynamic kb)
        {
            try
            {
                var modules = new JArray();
                foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                {
                    if (o == null) continue;
                    if (!string.Equals(o.TypeDescriptor?.Name, "Module", StringComparison.OrdinalIgnoreCase)) continue;
                    modules.Add(new JObject
                    {
                        ["name"] = o.Name,
                        ["description"] = SafeStr(() => o.Description)
                    });
                }
                return McpResponse.Ok(
                    code: "ModuleListRetrieved",
                    result: new JObject
                    {
                        ["count"] = modules.Count,
                        ["modules"] = modules,
                        ["source"] = "sdk:DesignModel.Objects"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleListFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private static string InstallResult(bool ok, string byField, string value)
        {
            return McpResponse.Ok(
                code: ok ? "ModuleInstalled" : "ModuleInstallDeclined",
                result: new JObject
                {
                    ["success"] = ok,
                    [byField] = value,
                    ["source"] = "sdk:IModuleManagerService"
                });
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
