using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class SystemRouter : IMcpModuleRouter
    {
        public string ModuleName => "System";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? target = args?["target"]?.ToString();

            switch (toolName)
            {
                case "genexus_lifecycle":
                    switch (action)
                    {
                        case "build": return new {
                            module = "Build",
                            action = "Build",
                            target = target,
                            includeCallees = args?["includeCallees"]?.ToString(),
                            buildPlanCap = args?["buildPlanCap"]?.ToObject<int?>(),
                            // Item 72 (friction 2026-05-22) — Slack/Discord webhook on terminal Failed state.
                            notifyOnFailure = args?["notifyOnFailure"]?.ToString(),
                            skipFullDeploy = args?["skipFullDeploy"]?.ToObject<bool?>() ?? false,
                            // Item 28 (Tier-S, EXPERIMENTAL) — fastIncremental opt-in.
                            fastIncremental = args?["fastIncremental"]?.ToObject<bool?>() ?? false
                        };
                        case "cancel": return new { module = "Build", action = "Cancel", target = target };
                        case "rebuild": return new { module = "Build", action = "RebuildAll", target = target };
                        case "reorg": return new { module = "Build", action = "Reorg", target = target };
                        // Item 43 (friction 2026-05-22) — DDL diff/preview pre-reorg.
                        case "reorg_preview": return new { module = "Build", action = "ReorgPreview", target = target };
                        case "validate": return new { module = "Validation", action = "Check", target = target, payload = args?["code"]?.ToString() };
                        case "validate-kb": return new { module = "KB", action = "ValidateConditions", limit = args?["limit"]?.ToObject<int?>() };
                        case "snapshots-list": return new { module = "KB", action = "ListPatternSnapshots", target = target };
                        case "snapshots-restore": return new { module = "KB", action = "RestorePatternSnapshot", target = target, snapshotPath = args?["snapshotPath"]?.ToString() };
                        case "sync": return new { module = "Build", action = "Sync", target = target };
                        case "index": return new {
                            module = "KB",
                            action = "BulkIndex",
                            force = args?["force"]?.ToObject<bool?>() ?? false
                        };
                        case "status":
                            if (!string.IsNullOrEmpty(target))
                            {
                                int? page = args?["page"]?.ToObject<int?>();
                                int? pageSize = args?["pageSize"]?.ToObject<int?>() ?? args?["page_size"]?.ToObject<int?>();
                                // v2.6.6 Stream F: event-driven long-poll on worker taskId.
                                // `wait` blocks the worker until the baseline changes (Phase /
                                // counts / TargetsDone / terminal Status) or the timeout fires.
                                // `since` is the snapshot string returned under _meta.snapshot
                                // by the previous status response — pass it back for chaining.
                                int wait = args?["wait"]?.ToObject<int?>() ?? 0;
                                if (wait < 0) wait = 0;
                                if (wait > 300) wait = 300;
                                return new {
                                    module = "Build",
                                    action = "Status",
                                    target = target,
                                    page = page ?? 1,
                                    pageSize = pageSize ?? 50,
                                    wait = wait,
                                    since = args?["since"]?.ToString()
                                };
                            }
                            return new { module = "KB", action = "GetIndexStatus" };
                        case "result":
                            if (!string.IsNullOrEmpty(target))
                            {
                                int? page = args?["page"]?.ToObject<int?>();
                                int? pageSize = args?["pageSize"]?.ToObject<int?>() ?? args?["page_size"]?.ToObject<int?>();
                                return new { module = "Build", action = "Result", target = target, page = page ?? 1, pageSize = pageSize ?? 50 };
                            }
                            return null;
                        default: return null;
                    }

                case "genexus_forge":
                    switch (action)
                    {
                        case "scaffold":
                            return new {
                                module = "Forge",
                                action = "Scaffold",
                                type = args?["type"]?.ToString(),
                                name = args?["name"]?.ToString(),
                                code = args?["content"]?.ToString(),
                                description = args?["description"]?.ToString()
                            };
                        case "translate":
                            return new {
                                module = "Conversion",
                                action = "TranslateTo",
                                target = args?["name"]?.ToString(),
                                language = args?["content"]?.ToString()
                            };
                        case "sample": return new { module = "Pattern", action = "GetSample", target = args?["type"]?.ToString() };
                        default: return null;
                    }

                case "genexus_doc":
                    switch (action)
                    {
                        case "wiki": return new { module = "Wiki", action = "Generate", target = target };
                        case "visualize": return new { module = "Visualizer", action = "Generate", target = target };
                        case "health": return new { module = "Health", action = "GetReport" };
                        default: return null;
                    }

                case "genexus_test":
                    return new { module = "Test", action = "Run", target = args?["name"]?.ToString() };

                // genexus_kb set_startup / get_startup — SDK-bound startup-object
                // management. IDE "Set As Startup Object" parity. The other actions
                // (list/open/close/set_default) are handled directly in Program.cs
                // and never reach a router. Schema (action enum) is declared on
                // genexus_kb in tool_definitions.json.
                case "genexus_kb":
                {
                    string kbAction = args?["action"]?.ToString();
                    if (string.Equals(kbAction, "set_startup", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "SetStartupObject",
                            target = args?["name"]?.ToString(),
                            name = args?["name"]?.ToString()
                        };
                    }
                    if (string.Equals(kbAction, "get_startup", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "GetStartupObject"
                        };
                    }
                    return null;
                }

                // genexus_kb_explorer action=locate — "Locate in KB Explorer" parity.
                case "genexus_kb_explorer":
                {
                    string kxAction = args?["action"]?.ToString() ?? "locate";
                    return new
                    {
                        module = "KbExplorer",
                        action = string.Equals(kxAction, "locate", System.StringComparison.OrdinalIgnoreCase)
                            ? "Locate"
                            : kxAction,
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString()
                    };
                }

                // genexus_navigation action=view — "View Navigation / View Last Navigation" parity.
                case "genexus_navigation":
                {
                    bool latest = args?["latest"]?.ToObject<bool?>() ?? false;
                    return new
                    {
                        module = "Navigation",
                        action = "View",
                        target = args?["name"]?.ToString(),
                        latest = latest
                    };
                }

                // genexus_blame — git blame for an object part.
                case "genexus_blame":
                {
                    return new
                    {
                        module = "Blame",
                        action = "Get",
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        line = args?["line"]?.ToObject<int?>(),
                        filePath = args?["filePath"]?.ToString(),
                        context = args?["context"]?.ToObject<int?>()
                    };
                }
                
                // Legados
                case "genexus_validate":
                    return new { module = "Validation", action = "Check", target = target, payload = args?["code"]?.ToString() };
                case "genexus_build":
                    return new { module = "Build", action = args?["action"]?.ToString(), target = target };
                // genexus_history routing intentionally NOT handled here — the
                // canonical handler lives in OperationsRouter.cs:177 which forwards
                // the v2.6.6 Stream H fields (discard / part / snapshot). The old
                // duplicate handler dropped those flags silently because router
                // pipeline order made SystemRouter win before OperationsRouter.
                //
                default:
                    return null;
            }
        }
    }
}
