using System;
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
                        type = args?["type"]?.ToString(),
                        // Domain-specific options (ignored for other types). Sent verbatim so the
                        // worker's CreateObject(options) overload can pick them up without the
                        // gateway having to know the schema of every future type.
                        dataType = args?["dataType"]?.ToString(),
                        length = args?["length"]?.ToObject<int?>(),
                        decimals = args?["decimals"]?.ToObject<int?>(),
                        signed = args?["signed"]?.ToObject<bool?>(),
                        description = args?["description"]?.ToString(),
                        basedOn = args?["basedOn"]?.ToString(),
                        enumValues = args?["enumValues"],
                        // Item 21 (friction 2026-05-22) — universal dryRun: report
                        // the planned object shape without calling Save().
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
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
                    // FR#20 (v2.6.6 Stream B): mode=soft|hard, default soft. Forwarded
                    // verbatim — the worker's CommandDispatcher negotiates default when null.
                    return new
                    {
                        module = "Object",
                        action = "WorkerReload",
                        target = "_self",
                        sourceDir = args?["sourceDir"]?.ToString(),
                        mode = args?["mode"]?.ToString(),
                        drainTimeoutMs = args?["drainTimeoutMs"]?.ToObject<int?>()
                    };

                case "genexus_logs":
                    return new
                    {
                        module = "Object",
                        action = "ReadLogs",
                        target = "_self",
                        // Item 32: new filter params. tail replaces the old lines param
                        // (both forwarded; ObjectService prefers tail when non-zero).
                        lines = args?["tail"]?.ToObject<int?>() ?? args?["lines"]?.ToObject<int?>() ?? 100,
                        filterCorrelation = args?["filterCorrelation"]?.ToString(),
                        grep = args?["grep"]?.ToString(),
                        // since: 'crash' or ISO-8601 timestamp.
                        since = args?["since"]?.ToString(),
                        // Item 32: object-name filter.
                        objectFilter = args?["target"]?.ToString()
                    };

                case "genexus_refactor":
                    return ConvertRefactorToolCall(args);

                // Item 91: genexus_rename_across_kb — thin wrapper that routes to the
                // existing RefactorService.Refactor(action=RenameObject|RenameAttribute)
                // path. The service already iterates the index's CalledBy edges and
                // patches every source-text call-site, so KB-wide rename is just the
                // RenameAttribute/RenameObject flow under a more discoverable name.
                case "genexus_rename_across_kb":
                {
                    string? from = args?["from"]?.ToString() ?? args?["oldName"]?.ToString();
                    string? to = args?["to"]?.ToString() ?? args?["newName"]?.ToString();
                    string? type = args?["type"]?.ToString();
                    // RenameAttribute path is the index-driven one (writes attribute then
                    // updates every CalledBy edge). For non-Attribute types, RenameObject
                    // currently falls into the same code path (line 64 of RefactorService).
                    string refactorAction = string.Equals(type, "Attribute", System.StringComparison.OrdinalIgnoreCase)
                        ? "RenameAttribute"
                        : "RenameObject";
                    return new
                    {
                        module = "Refactor",
                        action = refactorAction,
                        target = from,
                        payload = new JObject
                        {
                            ["oldName"] = from,
                            ["newName"] = to
                        }.ToString()
                    };
                }

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
                {
                    // Item 45: mode=diagnose routes to read-only Diagnose action; default → Apply.
                    // Item 21 (friction 2026-05-22): dryRun=true is an alias for mode=diagnose
                    // — both return the same read-only findings without mutating the KB.
                    string apPatMode = args?["mode"]?.ToString();
                    bool isDiagnose = string.Equals(apPatMode, "diagnose", System.StringComparison.OrdinalIgnoreCase);
                    bool isDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                    return new
                    {
                        module = "Pattern",
                        action = (isDiagnose || isDryRun) ? "Diagnose" : "Apply",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };
                }

                case "genexus_sdk_probe":
                    return new
                    {
                        module = "SdkProbe",
                        action = "Run",
                        target = "_self",
                        outputDir = args?["outputDir"]?.ToString()
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
                        action = RouterArgs.Str(args, "action"),
                        target = RouterArgs.Str(args, "name"),
                        versionId = RouterArgs.Int(args, "versionId"),
                        // v2.6.6 Stream H (FR#28) — IDE "Discard changes" parity.
                        part = RouterArgs.Str(args, "part"),
                        snapshot = RouterArgs.Str(args, "snapshot"),
                        discard = RouterArgs.Bool(args, "discard"),
                        // Item 21 (friction 2026-05-22): dryRun=true on restore returns diff without writing.
                        dryRun = RouterArgs.Bool(args, "dryRun")
                    };

                // Item 16 — genexus_undo last=N
                case "genexus_undo":
                    return new
                    {
                        module = "Undo",
                        action = "Undo",
                        last = args?["last"]?.ToObject<int?>() ?? 1,
                        // Item 21 (friction 2026-05-22): dryRun=true lists snapshots without writing.
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                // Item 50 — genexus_security action=audit_gam
                case "genexus_security":
                    return new
                    {
                        module = "Security",
                        action = args?["action"]?.ToString() ?? "audit_gam"
                    };

                // Item 41 (mcp-improvements-2026-05-22) — Transaction ↔ DB drift detection.
                case "genexus_db_drift":
                {
                    string driftAction = args?["action"]?.ToString();
                    bool isReport = string.Equals(driftAction, "report", StringComparison.OrdinalIgnoreCase);
                    return new
                    {
                        module = "DbDrift",
                        action = isReport ? "Report" : "Check",
                        target = args?["target"]?.ToString()
                    };
                }

                // SOTA — index advisor + hot-path analysis. Three actions: analyze
                // (KB-wide static walk of For each blocks), suggest_indexes (covering
                // index DDL for a single Transaction), report (top-10 unindexed hot
                // paths with optional markdown).
                case "genexus_db_optimize":
                {
                    string optAction = args?["action"]?.ToString();
                    string mapped;
                    if (string.Equals(optAction, "suggest_indexes", StringComparison.OrdinalIgnoreCase))
                        mapped = "SuggestIndexes";
                    else if (string.Equals(optAction, "report", StringComparison.OrdinalIgnoreCase))
                        mapped = "Report";
                    else
                        mapped = "Analyze";
                    return new
                    {
                        module = "DbOptimize",
                        action = mapped,
                        target = args?["target"]?.ToString(),
                        format = args?["format"]?.ToString()
                    };
                }

                // Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
                case "genexus_edit_form":
                {
                    string editAction = args?["action"]?.ToString();
                    string normalised = string.IsNullOrEmpty(editAction)
                        ? string.Empty
                        : editAction.Trim();
                    return new
                    {
                        module = "WebFormEdit",
                        action = normalised,
                        target = args?["name"]?.ToString(),
                        @params = args
                    };
                }

                // Item 11 — resolve runtime URL + optional GAM cookies. No browser launch.
                case "genexus_run_object":
                    return new
                    {
                        module = "RunObject",
                        action = "Resolve",
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString(),
                        args = args?["args"],
                        gamSession = args?["gamSession"]
                    };

                // Item 68 — deterministic PM-readable summary.
                case "genexus_explain":
                    return new
                    {
                        module = "Explain",
                        action = "Explain",
                        target = args?["name"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        depth = args?["depth"]?.ToString()
                    };

                // Item 12 — unified diff of generated artifacts.
                case "genexus_diff_generated":
                    return new
                    {
                        module = "GeneratedDiff",
                        action = "Diff",
                        target = args?["name"]?.ToString(),
                        against = args?["against"]?.ToString()
                    };

                // Item 90 — Markdown README generation.
                case "genexus_kb_readme":
                    return new
                    {
                        module = "KbReadme",
                        action = "Generate",
                        target = "_self",
                        outputPath = args?["outputPath"]?.ToString()
                    };

                case "genexus_ocr_screenshot":
                    return new { module = "Ocr", action = "Run", path = args?["path"]?.ToString() };

                case "genexus_pr_description":
                    return new
                    {
                        module = "PrDescription",
                        action = "Generate",
                        last = args?["last"]?.ToObject<int?>() ?? 10,
                        workingDir = args?["workingDir"]?.ToString()
                    };

                case "genexus_screenshot_publish":
                    return new { module = "ScreenshotPublish", action = "Publish", path = args?["path"]?.ToString() };

                case "genexus_friction_log":
                {
                    string fAction = args?["action"]?.ToString();
                    bool isTail = string.Equals(fAction, "tail", StringComparison.OrdinalIgnoreCase);
                    return new
                    {
                        module = "FrictionLog",
                        action = isTail ? "Tail" : "Append",
                        tool = args?["tool"]?.ToString(),
                        message = args?["message"]?.ToString(),
                        severity = args?["severity"]?.ToString(),
                        n = args?["n"]?.ToObject<int?>() ?? 20
                    };
                }

                // Item 76 — friction-journal aggregation.
                case "genexus_learning":
                    return new
                    {
                        module = "Learning",
                        action = "Report",
                        since = args?["since"]?.ToString(),
                        until = args?["until"]?.ToString()
                    };

                // Item 78 — SDPanel parity proxy.
                case "genexus_sd_panel":
                    return new
                    {
                        module = "SdPanel",
                        action = args?["action"]?.ToString() ?? "inspect",
                        target = args?["name"]?.ToString() ?? args?["target"]?.ToString(),
                        @params = args
                    };

                // Item 84 — multi-agent file lock.
                case "genexus_multi_agent_lock":
                    return new
                    {
                        module = "MultiAgentLock",
                        action = args?["action"]?.ToString() ?? "status",
                        target = args?["target"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        ownerId = args?["ownerId"]?.ToString(),
                        ttlSec = args?["ttlSec"]?.ToObject<int?>() ?? 300
                    };

                // Item 86 — typed-change impact simulator (no mutation).
                case "genexus_what_if":
                    return new
                    {
                        module = "WhatIf",
                        action = "Simulate",
                        change = args?["change"]
                    };

                // genexus_doctor — health-check triage envelope. No args.
                case "genexus_doctor":
                    return new
                    {
                        module = "Doctor",
                        action = "Diagnose",
                        target = "_self"
                    };

                // Item 66 — static step-by-step onboarding walkthrough.
                case "genexus_tutorial":
                    return new { module = "Tutorial", action = "Step", step = args?["step"]?.ToObject<int?>() ?? 1 };

                // Read-only surface for GxServer sync state. No SDK calls; worker probes
                // <kbPath>/Repository/Repository.gxs and similar metadata files.
                case "genexus_gxserver":
                    return new
                    {
                        module = "GxServer",
                        action = "Run",
                        @params = args
                    };

                // Item 71 — gh CLI passthrough.
                case "genexus_github":
                    return new
                    {
                        module = "Github",
                        action = "CreatePr",
                        title = args?["title"]?.ToString(),
                        body = args?["body"]?.ToString(),
                        @base = args?["base"]?.ToString(),
                        workingDir = args?["workingDir"]?.ToString()
                    };

                // Item 81 — OpenAI-compatible LLM endpoint forward.
                case "genexus_ai_complete":
                    return new
                    {
                        module = "AiComplete",
                        action = "Complete",
                        name = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        context = args?["context"]?.ToString(),
                        maxTokens = args?["maxTokens"]?.ToObject<int?>() ?? 200
                    };

                // Item 82 — git-based time travel for a KB object.
                case "genexus_time_travel":
                    return new
                    {
                        module = "TimeTravel",
                        action = "Recover",
                        target = args?["name"]?.ToString(),
                        at = args?["at"]?.ToString()
                    };

                // Item 83 — voice transcript → intent mapping.
                case "genexus_voice":
                    return new { module = "Voice", action = "Intent", transcript = args?["transcript"]?.ToString() };

                // Item 95 — generate GXtest stubs from production JSONL log.
                case "genexus_auto_test":
                    return new { module = "AutoTest", action = "Generate", path = args?["path"]?.ToString() };

                // Item 96 — surface structural commonalities across N objects.
                case "genexus_reverse_pattern":
                    return new { module = "ReversePattern", action = "Infer", source = args?["source"] };

                // Item 98 — parallel multi-browser render.
                case "genexus_cross_browser":
                    return new
                    {
                        module = "CrossBrowser",
                        action = "Run",
                        target = args?["target"]?.ToString(),
                        browsers = args?["browsers"],
                        capture = args?["capture"]
                    };

                case "genexus_wcag_check":
                    return new
                    {
                        module = "WcagCheck",
                        action = "Check",
                        target = args?["target"]?.ToString() ?? args?["name"]?.ToString()
                    };

                // IDE Save-As parity.
                case "genexus_save_as":
                    return new
                    {
                        module = "Object",
                        action = "SaveAs",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };

                // Item 65 — genexus_orient welcome card
                case "genexus_orient":
                    return new
                    {
                        module = "Orient",
                        action = "Welcome"
                    };

                case "genexus_structure":
                    return ConvertStructureToolCall(args);
                case "genexus_layout":
                    return ConvertLayoutToolCall(args);

                case "genexus_create_popup":
                    return new
                    {
                        module = "Popup",
                        action = "Create",
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString(),
                        spec = args?["spec"],
                        // Item 21 (friction 2026-05-22): dryRun=true validates + renders layout XML, no save.
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                case "genexus_preview":
                    return new
                    {
                        module = "Preview",
                        // v2.6.6 Stream H (FR#25) — action=run picks the KB launcher
                        // object when name is omitted; default 'render' preserves prior behaviour.
                        action = string.Equals(args?["action"]?.ToString(), "run", StringComparison.OrdinalIgnoreCase)
                            ? "Run"
                            : "Render",
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString(),
                        parms = args?["parms"],
                        launcher = args?["launcher"]?.ToString() ?? "auto",
                        buildFirst = args?["buildFirst"]?.ToObject<bool?>() ?? false,
                        waitMs = args?["waitMs"]?.ToObject<int?>() ?? 3000,
                        capture = args?["capture"],
                        diffBaseline = args?["diffBaseline"]?.ToObject<bool?>() ?? false,
                        updateBaseline = args?["updateBaseline"]?.ToObject<bool?>() ?? false,
                        // Stream G (v2.6.6): GX-aware fill / click and GAM auth.
                        fill = args?["fill"],
                        click = args?["click"]?.ToString(),
                        auth = args?["auth"],
                        // Items 39/97: device emulation + network-throttle pass-through.
                        emulate = args?["emulate"]?.ToString(),
                        network = args?["network"]?.ToString()
                    };

                case "genexus_browser_capture":
                    return new
                    {
                        module = "browser_capture",
                        action = "Capture",
                        target = args?["target"]?.ToString() ?? args?["name"]?.ToString(),
                        name = args?["target"]?.ToString() ?? args?["name"]?.ToString(),
                        capture = args?["capture"]
                    };

                case "genexus_smoke_test":
                    return new
                    {
                        module = "smoke_test",
                        action = "Run",
                        target = args?["target"]?.ToString() ?? args?["name"]?.ToString(),
                        name = args?["target"]?.ToString() ?? args?["name"]?.ToString()
                    };

                case "genexus_a11y_audit":
                    return new
                    {
                        module = "a11y_audit",
                        action = "Audit",
                        target = args?["target"]?.ToString() ?? args?["name"]?.ToString(),
                        name = args?["target"]?.ToString() ?? args?["name"]?.ToString()
                    };

                // genexus_api — REST endpoint introspection + breaking-change diff.
                // Single dispatcher arm; the worker's ApiIntrospectService.Run switches
                // on args.action (list|describe|diff_baseline|snapshot).
                case "genexus_api":
                    return new
                    {
                        module = "Api",
                        action = args?["action"]?.ToString() ?? "list",
                        target = args?["target"]?.ToString(),
                        @params = args
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
