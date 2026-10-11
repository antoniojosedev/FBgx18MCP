using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Service for typed authoring of standard WorkWith (WorkWith for Web) pattern instances.
    /// Strictly isolated from WorkWithPlus (which is handled by WwpActionService).
    /// </summary>
    public sealed class WorkWithService
    {
        private readonly ObjectService _objects;
        private readonly PatternAnalysisService _patterns;
        private readonly WriteService _write;

        public WorkWithService(ObjectService objects, PatternAnalysisService patterns, WriteService write)
        {
            _objects = objects;
            _patterns = patterns;
            _write = write;
        }

        public string Run(string target, JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "list_actions";

            string name = !string.IsNullOrWhiteSpace(target) ? target : args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "MissingTarget", message: "Target object name is required.", target: target);

            KBObject targetObj = _objects.FindObject(name);
            if (targetObj == null)
                return McpResponse.Err(code: "ObjectNotFound", message: $"Target object '{name}' was not found in the Knowledge Base.", target: name);

            var matches = _patterns.FindPatternInstances(targetObj);
            var wwMatch = matches.FirstOrDefault(m => m.Pattern != null && m.Pattern.IsWorkWith);

            if (wwMatch == null)
            {
                var wwpMatch = matches.FirstOrDefault(m => m.Pattern != null && m.Pattern.IsWorkWithPlus);
                if (wwpMatch != null)
                {
                    return McpResponse.Err(code: "WorkWithPatternMismatch",
                        message: $"'{targetObj.Name}' has a WorkWithPlus instance '{wwpMatch.Candidate.Name}', not a standard WorkWith instance.",
                        hint: "Use genexus_wwp for WorkWithPlus instances; genexus_workwith is dedicated to standard WorkWith.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_wwp",
                            new JObject { ["action"] = "list", ["name"] = wwpMatch.Candidate.Name },
                            "genexus_wwp manages WorkWithPlus actions.")),
                        target: name);
                }

                return McpResponse.Err(code: "WorkWithInstanceNotFound",
                    message: $"No standard WorkWith instance was resolved for '{targetObj.Name}'.",
                    hint: "Ensure the Transaction has the standard WorkWith pattern applied (WorkWithWeb{0}).",
                    target: name);
            }

            KBObject instanceObj = wwMatch.Candidate.Source as KBObject ?? _objects.FindObject(wwMatch.Candidate.Name);
            if (instanceObj == null)
                return McpResponse.Err(code: "WorkWithInstanceObjectNotFound",
                    message: $"WorkWith instance '{wwMatch.Candidate.Name}' could not be loaded.", target: name);

            switch (action.ToLowerInvariant())
            {
                case "list_actions":
                case "list":
                    return ExecuteListActions(name, instanceObj);

                case "add_action":
                    return ExecuteAddAction(name, instanceObj, args);

                default:
                    return McpResponse.Err(code: "UnknownAction",
                        message: $"Action '{action}' is not supported by genexus_workwith. Supported actions: list_actions, add_action.",
                        target: name);
            }
        }

        private string ExecuteListActions(string target, KBObject instanceObj)
        {
            if (!WorkWithSdkAdapter.ListActions(instanceObj, _patterns, out var tabs, out var error))
            {
                return McpResponse.Err(code: error ?? "ListActionsFailed",
                    message: $"Failed to list actions for '{instanceObj.Name}': {error}",
                    target: target);
            }

            return McpResponse.Ok(target: target, code: "WorkWithActionsRead", result: new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tabs"] = tabs
            });
        }

        private string ExecuteAddAction(string target, KBObject instanceObj, JObject args)
        {
            string tabCode = args?["tab"]?.ToString();
            if (string.IsNullOrWhiteSpace(tabCode)) tabCode = "General";

            string actionName = args?["actionName"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(actionName))
                return McpResponse.Err(code: "MissingActionName", message: "actionName is required for add_action.", target: target);

            if (!Regex.IsMatch(actionName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                return McpResponse.Err(code: "InvalidActionName", message: $"Action name '{actionName}' is not a valid identifier.", target: target);

            string procedure = args?["procedure"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(procedure))
                return McpResponse.Err(code: "MissingProcedure", message: "procedure is required for add_action.", target: target);

            KBObject procObj = _objects.FindObject(procedure);
            if (procObj == null)
                return McpResponse.Err(code: "ProcedureNotFound", message: $"Target Procedure '{procedure}' was not found in the Knowledge Base.", target: target);

            if (!string.Equals(procObj.TypeDescriptor?.Name, "Procedure", StringComparison.OrdinalIgnoreCase))
                return McpResponse.Err(code: "InvalidProcedureType",
                    message: $"Target object '{procedure}' is of type '{procObj.TypeDescriptor?.Name}', but must be a Procedure.",
                    target: target);

            string caption = args?["caption"]?.ToString();
            string condition = args?["condition"]?.ToString();
            var parametersToken = args?["parameters"] as JArray;
            var parameters = parametersToken != null
                ? parametersToken.Select(t => t.ToString()).ToList()
                : new List<string>();

            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;

            if (dryRun)
            {
                if (!WorkWithSdkAdapter.AddAction(instanceObj, _patterns, _write, tabCode, actionName, caption, procObj, condition, parameters, dryRun: true, out var plan, out var errCode, out var errMsg))
                {
                    return McpResponse.Err(code: errCode ?? "AddActionPlanFailed", message: errMsg ?? "Failed to create add_action plan.", target: target);
                }

                return McpResponse.Ok(target: target, code: "WorkWithActionPlan", result: new JObject
                {
                    ["plan"] = plan,
                    ["dryRun"] = true
                });
            }

            lock (WriteService.AcquirePerTargetLock(instanceObj.Name))
            {
                KBObject freshInstance = _objects.FindObject(instanceObj.Name) ?? instanceObj;
                if (!WorkWithSdkAdapter.AddAction(freshInstance, _patterns, _write, tabCode, actionName, caption, procObj, condition, parameters, dryRun: false, out var receipt, out var errCode, out var errMsg))
                {
                    return McpResponse.Err(code: errCode ?? "AddActionFailed", message: errMsg ?? "Failed to add action.", target: target);
                }

                return McpResponse.Ok(target: target, code: "WorkWithActionAdded", result: receipt);
            }
        }
    }
}
