using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The MCP tool surface is defined in two files that must stay in sync by hand:
    //   - src/GxMcp.Gateway/tool_definitions.json (source of truth for schemas)
    //   - src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json
    //     (golden discovery envelope, must be alphabetically sorted by name)
    // Previously nothing failed loudly when the two drifted — a schema edit that
    // forgot the fixture regen only surfaced as a confusing DeepEquals diff, or not
    // at all if the changed field wasn't exercised. This guard diffs the tool-name
    // sets directly and enforces the fixture's sort order.
    public class ToolDefinitionsFixtureParityTests
    {
        private static string FindUp(params string[] relativeSegments)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(new[] { dir }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException(
                "Could not locate " + string.Join("/", relativeSegments) + " from " + AppContext.BaseDirectory);
        }

        private static string[] ToolDefinitionNames()
        {
            var path = FindUp("src", "GxMcp.Gateway", "tool_definitions.json");
            var arr = JArray.Parse(File.ReadAllText(path));
            return arr.Select(t => (string?)t["name"]).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray();
        }

        private static string[] GoldenFixtureNames()
        {
            var path = FindUp("src", "GxMcp.Gateway.Tests", "Fixtures", "Contract", "Discovery", "tools-list.response.json");
            var obj = JObject.Parse(File.ReadAllText(path));
            var tools = (JArray?)obj["tools"];
            Assert.NotNull(tools);
            return tools.Select(t => (string?)t["name"]).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray();
        }

        [Fact]
        public void ToolNameSetsAreIdentical()
        {
            var defs = ToolDefinitionNames();
            var golden = GoldenFixtureNames();

            var onlyInDefs = defs.Except(golden).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var onlyInGolden = golden.Except(defs).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Assert.True(
                onlyInDefs.Length == 0 && onlyInGolden.Length == 0,
                "tool_definitions.json and the golden tools-list fixture disagree.\n" +
                "  Only in tool_definitions.json: " + (onlyInDefs.Length == 0 ? "(none)" : string.Join(", ", onlyInDefs)) + "\n" +
                "  Only in golden fixture:        " + (onlyInGolden.Length == 0 ? "(none)" : string.Join(", ", onlyInGolden)) + "\n" +
                "Regenerate the fixture (GXMCP_UPDATE_GOLDEN=1) or fix the schema.");
        }

        // Issue #559: genexus_workwith's schema example used `description` while the
        // property was not declared, so schema-driven clients could not offer it. The
        // runtime only flags undeclared keys within edit distance 2 of a declared one, so
        // nothing failed. Every example key must be a declared property or a cross-cutting
        // argument the gateway honours on every tool.
        [Fact]
        public void EveryExampleKeyIsDeclaredInProperties()
        {
            var path = FindUp("src", "GxMcp.Gateway", "tool_definitions.json");
            var tools = JArray.Parse(File.ReadAllText(path));
            var undeclared = new System.Collections.Generic.List<string>();

            foreach (var tool in tools.OfType<JObject>())
            {
                var schema = tool["inputSchema"] as JObject;
                var properties = schema?["properties"] as JObject;
                var examples = schema?["examples"] as JArray;
                if (properties == null || examples == null) continue;

                for (int i = 0; i < examples.Count; i++)
                {
                    if (examples[i] is not JObject example) continue;
                    foreach (var key in example.Properties().Select(p => p.Name))
                    {
                        if (properties.ContainsKey(key) || GatewayArgsValidator.CrossCuttingArgs.Contains(key)) continue;
                        undeclared.Add($"{tool["name"]}.examples[{i}].{key}");
                    }
                }
            }

            Assert.True(undeclared.Count == 0,
                "example keys not declared in inputSchema.properties:\n  " + string.Join("\n  ", undeclared));
        }

        [Fact]
        public void GoldenFixtureIsAlphabeticallySorted()
        {
            var golden = GoldenFixtureNames();
            var sorted = golden.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.True(
                golden.SequenceEqual(sorted, StringComparer.Ordinal),
                "tools-list.response.json must be alphabetically sorted by tool name (ordinal). Actual order:\n  " +
                string.Join("\n  ", golden));
        }
    }
}
