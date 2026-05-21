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
            // Budget bumped from 3500 → 4000 in v2.3.0 to accommodate the `kb`
            // parameter added to 28 tools for multi-KB parallel support. Bumped to 4600
            // in v2.3.7 to fit 6 new tools (validate_payload, bulk_edit, apply_template,
            // diff, export_unified, delete_variable). Bumped to 4800 in v2.3.8 (Task 2.2)
            // to fit nameFilter/descriptionFilter/pathPrefix on genexus_list_objects.
            // Bumped to 5000 in v2.3.8 (Task 5.2) for includeCallees/buildPlanCap on
            // genexus_lifecycle.
            // Bumped from 5000 → 5200 in SP1.T2 (2026-05-15-mcp-perf-1) to make room for the
            // axiCompact schema declaration on genexus_query / genexus_list_objects. SP2
            // (tool description trim) will reclaim space and lower this back to 4900.
            //   v2.4.0 (SP2.T3): 5200 → 5000 (description trim; long-form moved to genexus://kb/tool-help/{name};
            //   actual ~4956 — budget set to 5000, tighten further once remaining descriptions are trimmed)
            //   v2.4.0 (SP4.T5): 5000 → 5300 to accommodate genexus_edit_and_build composite tool (~240 tokens).
            //   v2.5.2 (W2+W4 from IDE-parity roadmap): 5300 → 6000 to accommodate genexus_preview
            //   (~410 tokens for full a11y / capture / baseline schema) and genexus_apply_pattern
            //   (~150 tokens for pattern key / settings tree).
            //   v2.5.3 (W3 from IDE-parity roadmap): 6000 → 6300 to accommodate genexus_create_popup
            //   (~290 tokens for the popup spec sub-schema: inputs[options], buttons, inParms/outParms).
            //   v2.6.4 (LLM-UX pass): 6300 → 6700 for new genexus_recipe tool, apply_pattern `validate`
            //   flag + parent-type routing hint, create_object/edit/whoami description front-loading.
            //   Net ~+230 tokens; budget set conservatively at 6700.
            Assert.True(approxTokens < 6700, $"tool_definitions.json is ~{approxTokens} tokens; budget 6700.");
        }
    }
}
