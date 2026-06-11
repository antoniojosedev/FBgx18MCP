using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Step 4 — universal dryRun guard tests.
    ///
    /// Theory 1 (SchemaHasDryRun): for each mutating tool, asserts the inputSchema
    /// contains a top-level "dryRun" property.
    ///
    /// Theory 2 (ServiceEmitsDryRunCode): for each mutating tool's associated
    /// service file in src/GxMcp.Worker/Services/, asserts the literal string
    /// "DryRun" appears — verifying the service code path emits the DryRun code.
    /// </summary>
    public class DryRunUniversalGuardTests
    {
        // Curated list of mutating tools. Alphabetical for maintainability.
        // N/A read-only tools are excluded.
        public static readonly IReadOnlyList<string> MutatingTools = new[]
        {
            "genexus_apply_pattern",
            "genexus_create",
            "genexus_delete_object",
            "genexus_edit",
            "genexus_edit_and_build",
            "genexus_edit_form",
            // genexus_github, genexus_multi_agent_lock and genexus_rename_across_kb
            // were de-advertised from tools/list in v2.10.0 (still dispatchable by
            // name) — no advertised schema to assert dryRun on.
            "genexus_lifecycle",
            "genexus_refactor",
            "genexus_run_object",
            "genexus_variable",
            "genexus_versioning"
        };

        // Map: tool name → worker service file (relative to Services/ root) that
        // should contain the literal "DryRun" string.
        private static readonly Dictionary<string, string> ToolToServiceFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["genexus_apply_pattern"]   = "PatternApplyService.cs",     // diagnose path acts as dryRun
            ["genexus_create"]          = "SaveAsService.cs",           // dryRun in SaveAs; also PopupTemplateService
            ["genexus_delete_object"]   = "ObjectService.cs",
            ["genexus_edit"]            = "WriteService.cs",
            ["genexus_edit_and_build"]  = "EditAndBuildOrchestrator.cs",
            ["genexus_edit_form"]       = "WebFormEditService.cs",
            ["genexus_lifecycle"]       = "BuildService.cs",
            ["genexus_refactor"]        = "RefactorService.cs",
            ["genexus_run_object"]      = "RunObjectService.cs",
            ["genexus_variable"]        = "WriteService.cs",
            ["genexus_versioning"]      = "HistoryService.cs"           // undo also in UndoService.cs
        };

        private static string FindToolDefinitionsPath()
        {
            // Walk up from the test assembly location to find tool_definitions.json
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                string candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                var gatewayDir = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(gatewayDir)) return gatewayDir;
                // Try sibling: bin/Debug/net48 → up 3 → repo root
                string p2 = Path.Combine(dir, "..", "..", "..", "src", "GxMcp.Gateway", "tool_definitions.json");
                p2 = Path.GetFullPath(p2);
                if (File.Exists(p2)) return p2;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            throw new FileNotFoundException("tool_definitions.json not found. Searched from: " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string FindServicesRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                string candidate = Path.Combine(dir, "src", "GxMcp.Worker", "Services");
                if (Directory.Exists(candidate)) return candidate;
                string p2 = Path.Combine(dir, "..", "..", "..", "src", "GxMcp.Worker", "Services");
                p2 = Path.GetFullPath(p2);
                if (Directory.Exists(p2)) return p2;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            throw new DirectoryNotFoundException("GxMcp.Worker/Services directory not found. Searched from: " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static JArray LoadToolDefinitions()
        {
            string path = FindToolDefinitionsPath();
            string json = File.ReadAllText(path);
            return JArray.Parse(json);
        }

        public static IEnumerable<object[]> MutatingToolNames()
        {
            foreach (string t in MutatingTools)
                yield return new object[] { t };
        }

        // Theory 1: every mutating tool's inputSchema has a "dryRun" property.
        [Theory]
        [MemberData(nameof(MutatingToolNames))]
        public void SchemaHasDryRun(string toolName)
        {
            var tools = LoadToolDefinitions();
            var tool = tools.Children<JObject>()
                .FirstOrDefault(t => string.Equals(t["name"]?.ToString(), toolName, StringComparison.OrdinalIgnoreCase));

            Assert.True(tool != null, $"Tool '{toolName}' not found in tool_definitions.json.");

            var props = tool?["inputSchema"]?["properties"] as JObject;
            Assert.True(props != null, $"Tool '{toolName}': inputSchema.properties is null.");
            Assert.True(props.ContainsKey("dryRun"),
                $"Tool '{toolName}': inputSchema.properties does not contain 'dryRun'. " +
                $"Add {{\"dryRun\":{{\"type\":\"boolean\",\"default\":false}}}} to its inputSchema.");
        }

        // Theory 2: each mutating tool's service file contains the literal "DryRun" string.
        [Theory]
        [MemberData(nameof(MutatingToolNames))]
        public void ServiceEmitsDryRunCode(string toolName)
        {
            Assert.True(ToolToServiceFile.ContainsKey(toolName),
                $"Tool '{toolName}' is not in the ToolToServiceFile map. Update the test to include it.");

            string serviceFile = ToolToServiceFile[toolName];
            string servicesRoot = FindServicesRoot();
            string filePath = Path.Combine(servicesRoot, serviceFile);

            Assert.True(File.Exists(filePath),
                $"Service file '{serviceFile}' not found at '{filePath}'.");

            string content = File.ReadAllText(filePath);
            Assert.True(content.Contains("DryRun"),
                $"Tool '{toolName}': service file '{serviceFile}' does not contain the literal string 'DryRun'. " +
                $"Ensure the service method handles dryRun=true and returns McpResponse.Ok(code:\"DryRun\", ...).");
        }
    }
}
