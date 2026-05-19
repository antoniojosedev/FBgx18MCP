using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    public class OperationsRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_create_object":
                    return new
                    {
                        module = "Object",
                        action = "Create",
                        target = args?["name"]?.ToString(),
                        type = args?["type"]?.ToString()
                    };

                case "genexus_delete_object":
                    return new
                    {
                        module = "Object",
                        action = "Delete",
                        target = args?["name"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        confirm = args?["confirm"]?.ToObject<bool?>() ?? false
                    };

                case "genexus_worker_reload":
                    return new
                    {
                        module = "Object",
                        action = "WorkerReload",
                        target = "_self",
                        sourceDir = args?["sourceDir"]?.ToString()
                    };

                case "genexus_logs":
                    return new
                    {
                        module = "Object",
                        action = "ReadLogs",
                        target = "_self",
                        lines = args?["lines"]?.ToObject<int?>() ?? 50,
                        filterCorrelation = args?["filterCorrelation"]?.ToString(),
                        grep = args?["grep"]?.ToString()
                    };

                case "genexus_refactor":
                    return ConvertRefactorToolCall(args);

                case "genexus_add_variable":
                    return new
                    {
                        module = "Write",
                        action = "AddVariable",
                        target = args?["name"]?.ToString(),
                        varName = args?["varName"]?.ToString(),
                        typeName = args?["typeName"]?.ToString()
                    };

                case "genexus_delete_variable":
                    return new
                    {
                        module = "Write",
                        action = "DeleteVariable",
                        target = args?["name"]?.ToString(),
                        varName = args?["varName"]?.ToString()
                    };

                case "genexus_modify_variable":
                    return new
                    {
                        module = "Write",
                        action = "ModifyVariable",
                        target = args?["name"]?.ToString(),
                        varName = args?["varName"]?.ToString(),
                        typeName = args?["typeName"]?.ToString(),
                        basedOn = args?["basedOn"]?.ToString()
                    };

                case "genexus_validate_payload":
                    return new
                    {
                        module = "Write",
                        action = "ValidatePayload",
                        target = args?["name"]?.ToString(),
                        payload = args?["content"]?.ToString(),
                        @params = new JObject { ["part"] = args?["part"]?.ToString() }
                    };

                case "genexus_bulk_edit":
                    return new
                    {
                        module = "Write",
                        action = "Bulk",
                        @params = args
                    };

                case "genexus_apply_template":
                    return new
                    {
                        module = "Write",
                        action = "ApplyTemplate",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };

                case "genexus_apply_pattern":
                    return new
                    {
                        module = "Pattern",
                        action = "Apply",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };

                case "genexus_diff":
                    return new
                    {
                        module = "Diff",
                        action = args?["mode"]?.ToString() ?? "textVsText",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };

                case "genexus_export_unified":
                    return new
                    {
                        module = "Export",
                        action = "Unified",
                        target = args?["name"]?.ToString(),
                        @params = new JObject { ["type"] = args?["type"]?.ToString() }
                    };

                case "genexus_format":
                    return new
                    {
                        module = "Formatting",
                        action = "Format",
                        payload = args?["code"]?.ToString()
                    };

                case "genexus_properties":
                    return ConvertPropertiesToolCall(args);

                case "genexus_asset":
                    return ConvertAssetToolCall(args);

                case "genexus_history":
                    return new
                    {
                        module = "History",
                        action = args?["action"]?.ToString(),
                        target = args?["name"]?.ToString(),
                        versionId = args?["versionId"]?.ToObject<int?>()
                    };

                case "genexus_structure":
                    return ConvertStructureToolCall(args);
                case "genexus_layout":
                    return ConvertLayoutToolCall(args);

                case "genexus_preview":
                    return new
                    {
                        module = "Preview",
                        action = "Render",
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString(),
                        parms = args?["parms"],
                        launcher = args?["launcher"]?.ToString() ?? "auto",
                        buildFirst = args?["buildFirst"]?.ToObject<bool?>() ?? false,
                        waitMs = args?["waitMs"]?.ToObject<int?>() ?? 3000,
                        capture = args?["capture"],
                        diffBaseline = args?["diffBaseline"]?.ToObject<bool?>() ?? false,
                        updateBaseline = args?["updateBaseline"]?.ToObject<bool?>() ?? false
                    };

                default:
                    return null;
            }
        }

        private static object? ConvertRefactorToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            if (action == "ExtractProcedure")
            {
                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["objectName"]?.ToString(),
                    payload = new JObject
                    {
                        ["code"] = args?["code"]?.ToString(),
                        ["procedureName"] = args?["procedureName"]?.ToString()
                    }.ToString()
                };
            }

            if (action == "WWPSetCondition")
            {
                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString(),
                    payload = new JObject
                    {
                        ["controlAttribute"] = args?["controlAttribute"]?.ToString(),
                        ["value"] = args?["value"]?.ToString(),
                        ["typeFilter"] = args?["type"]?.ToString()
                    }.ToString()
                };
            }

            string? target = args?["target"]?.ToString();
            if (action == "RenameVariable")
            {
                target = args?["objectName"]?.ToString();
            }

            return new
            {
                module = "Refactor",
                action,
                target,
                payload = new JObject
                {
                    ["oldName"] = args?["target"]?.ToString(),
                    ["newName"] = args?["newName"]?.ToString()
                }.ToString()
            };
        }

        private static object? ConvertPropertiesToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            if (action.Equals("set", System.StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    module = "Property",
                    action = "Set",
                    target = args?["name"]?.ToString(),
                    propertyName = args?["propertyName"]?.ToString(),
                    value = args?["value"]?.ToString(),
                    control = args?["control"]?.ToString(),
                    type = args?["type"]?.ToString()
                };
            }

            return new
            {
                module = "Property",
                action = "Get",
                target = args?["name"]?.ToString(),
                control = args?["control"]?.ToString(),
                type = args?["type"]?.ToString()
            };
        }

        private static object? ConvertAssetToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            return new
            {
                module = "Asset",
                action = char.ToUpperInvariant(action[0]) + action.Substring(1).ToLowerInvariant(),
                target = args?["path"]?.ToString(),
                pattern = args?["pattern"]?.ToString(),
                relativeRoot = args?["relativeRoot"]?.ToString(),
                limit = args?["limit"]?.ToObject<int?>(),
                includeContent = args?["includeContent"]?.ToObject<bool?>(),
                maxBytes = args?["maxBytes"]?.ToObject<int?>(),
                contentBase64 = args?["contentBase64"]?.ToString()
            };
        }

        private static object? ConvertStructureToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? mappedAction = action switch
            {
                "get_visual" => "GetVisualStructure",
                "update_visual" => "UpdateVisualStructure",
                "get_indexes" => "GetVisualIndexes",
                "get_logic" => "GetLogicStructure",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Structure",
                action = mappedAction,
                target = args?["name"]?.ToString(),
                payload = args?["payload"]?.Type == JTokenType.Object || args?["payload"]?.Type == JTokenType.Array
                    ? args?["payload"]?.ToString()
                    : args?["payload"]?.ToString()
            };
        }

        private static object? ConvertLayoutToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;
            string? objectName = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(objectName))
            {
                objectName = args?["target"]?.ToString();
            }

            string? mappedAction = action switch
            {
                "get_tree" => "GetTree",
                "set_property" => "SetProperty",
                "find_controls" => "FindControls",
                "set_properties" => "SetProperties",
                "inspect_surface" => "InspectSurface",
                "get_preview" => "GetVisualPreview",
                "scan_mutators" => "ScanMutators",
                "rename_printblock" => "RenamePrintBlock",
                "add_printblock" => "AddPrintBlock",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Layout",
                action = mappedAction,
                target = objectName,
                control = args?["control"]?.ToString(),
                propertyName = args?["propertyName"]?.ToString(),
                value = args?["value"]?.ToString(),
                query = args?["query"]?.ToString(),
                changes = args?["changes"],
                limit = args?["limit"]?.ToObject<int?>(),
                currentName = args?["currentName"]?.ToString(),
                newName = args?["newName"]?.ToString(),
                printBlockName = args?["printBlockName"]?.ToString(),
                height = args?["height"]?.ToObject<int?>()
            };
        }
    }
}
