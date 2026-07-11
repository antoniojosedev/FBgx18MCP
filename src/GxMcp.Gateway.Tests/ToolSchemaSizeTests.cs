using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolSchemaSizeTests
    {
        private static string FindToolDefinitionsJson()
        {
            // Preferred: alongside the test output (propagated via Gateway's <Content> item).
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return beside;

            // Fallback: walk up from base dir to repo src (for IDE test runs from src tree).
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
            // This budget guards the combined size of every tool schema in
            // tool_definitions.json (approximated as content.Length / 4 tokens). MCP
            // clients pay this cost on every session's tool list, so growth here is
            // deliberate — bump the constant only alongside a schema change that needs
            // the extra room, and check headroom before adding a new field to an
            // existing tool. Full bump-by-bump history lives in CHANGELOG.md; only the
            // last few entries are kept here for quick context:
            //   2026-07-09 (genexus_merge spike): 11750 → 12200 for the new
            //   genexus_merge tool (IMergeService object-merge, WRITE +
            //   destructiveHint=true). Measured ~12084 tokens; ~116 headroom.
            //   2026-07-09 (SDK-coverage batch integration): 12200 → 13300 for
            //   genexus_kb_version, genexus_module, genexus_gam, plus genexus_gxserver
            //   write actions landed together. Measured ~13150 tokens; ~150 headroom.
            //   2026-07-10 (issue #28 authoring papercuts): 13300 → 13600 for
            //   genexus_variable length/decimals/collection params + genexus_create
            //   firstItem/firstItemType SDT-seed params. Measured ~13378 tokens;
            //   ~222 headroom.
            Assert.True(approxTokens < 13600, $"tool_definitions.json is ~{approxTokens} tokens; budget 13600.");
        }
    }
}
