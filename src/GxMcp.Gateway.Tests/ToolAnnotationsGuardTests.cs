using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolAnnotationsGuardTests
    {
        private static JArray LoadToolDefinitions()
        {
            // Preferred: alongside the test output (propagated via Gateway's <Content> item).
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return JArray.Parse(File.ReadAllText(beside));

            // Fallback: walk up from base dir to repo src (for IDE test runs from src tree).
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return JArray.Parse(File.ReadAllText(candidate));
                candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return JArray.Parse(File.ReadAllText(candidate));
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate tool_definitions.json from test base " + AppContext.BaseDirectory);
        }

        [Fact]
        public void AllTools_HaveAnnotationsBlock()
        {
            var tools = LoadToolDefinitions();
            foreach (var tool in tools)
            {
                var name = tool["name"]?.ToString() ?? "(unknown)";
                var annotations = tool["annotations"];
                Assert.True(annotations != null, $"Tool '{name}' is missing 'annotations'.");
            }
        }

        [Fact]
        public void AllTools_AnnotationsHaveAllFourBoolHints()
        {
            var tools = LoadToolDefinitions();
            foreach (var tool in tools)
            {
                var name = tool["name"]?.ToString() ?? "(unknown)";
                var annotations = tool["annotations"] as JObject;
                Assert.NotNull(annotations);

                var readOnly = annotations!["readOnlyHint"];
                Assert.True(readOnly != null && readOnly.Type == JTokenType.Boolean,
                    $"Tool '{name}': 'readOnlyHint' must be a non-null boolean.");

                var destructive = annotations["destructiveHint"];
                Assert.True(destructive != null && destructive.Type == JTokenType.Boolean,
                    $"Tool '{name}': 'destructiveHint' must be a non-null boolean.");

                var idempotent = annotations["idempotentHint"];
                Assert.True(idempotent != null && idempotent.Type == JTokenType.Boolean,
                    $"Tool '{name}': 'idempotentHint' must be a non-null boolean.");

                var openWorld = annotations["openWorldHint"];
                Assert.True(openWorld != null && openWorld.Type == JTokenType.Boolean,
                    $"Tool '{name}': 'openWorldHint' must be a non-null boolean.");
            }
        }

        // ── spot-checks ──────────────────────────────────────────────────────

        [Fact]
        public void Whoami_IsReadOnly()
        {
            var tools = LoadToolDefinitions();
            var tool = FindTool(tools, "genexus_whoami");
            Assert.True((bool)tool["annotations"]!["readOnlyHint"]!,
                "genexus_whoami.readOnlyHint should be true.");
        }

        [Fact]
        public void DeleteObject_IsDestructive()
        {
            var tools = LoadToolDefinitions();
            var tool = FindTool(tools, "genexus_delete_object");
            Assert.True((bool)tool["annotations"]!["destructiveHint"]!,
                "genexus_delete_object.destructiveHint should be true.");
        }

        [Fact]
        public void Browser_Preview_IsOpenWorld()
        {
            // genexus_github was de-advertised in v2.9.2; use genexus_browser as the
            // representative open-world tool (shells out to headless Chrome/Playwright).
            var tools = LoadToolDefinitions();
            var tool = FindTool(tools, "genexus_browser");
            Assert.True((bool)tool["annotations"]!["openWorldHint"]!,
                "genexus_browser.openWorldHint should be true.");
        }

        [Fact]
        public void Browser_IsOpenWorld()
        {
            var tools = LoadToolDefinitions();
            var tool = FindTool(tools, "genexus_browser");
            Assert.True((bool)tool["annotations"]!["openWorldHint"]!,
                "genexus_browser.openWorldHint should be true.");
        }

        // ── helper ───────────────────────────────────────────────────────────

        private static JToken FindTool(JArray tools, string name)
        {
            foreach (var tool in tools)
            {
                if (tool["name"]?.ToString() == name)
                    return tool;
            }
            throw new Exception($"Tool '{name}' not found in tool_definitions.json.");
        }
    }
}
