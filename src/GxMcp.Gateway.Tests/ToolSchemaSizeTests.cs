using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolSchemaSizeTests
    {
        private static string FindToolDefinitionsJson()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate tool_definitions.json from test base " + AppContext.BaseDirectory);
        }

        [Fact]
        public void TotalToolSchemaSizeIsUnderBudget()
        {
            var path = FindToolDefinitionsJson();
            Assert.True(File.Exists(path), $"tool_definitions.json not found at {path}");
            var content = File.ReadAllText(path);
            var approxTokens = content.Length / 4;
            Assert.True(approxTokens < 3500, $"tool_definitions.json is ~{approxTokens} tokens; budget 3500.");
        }
    }
}
