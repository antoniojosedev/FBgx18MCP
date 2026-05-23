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
            //   v2.6.6 (Stream A FR#13): 6700 → 6800 for genexus_edit `validate` enum
            //   (strict|best-effort|only) declaration. Net ~+60 tokens.
            //   v2.6.6 (Stream F FR#19 follow-up): 6800 → 6900 for `wait`/`since` on
            //   genexus_lifecycle action=status (event-driven long-poll on worker taskId).
            //   Net ~+45 tokens.
            //   v2.6.6 (Stream H FR#25/#28): 6900 → 7200 for genexus_preview action=run
            //   (F5 launcher resolution) plus genexus_history discard/snapshot/part
            //   (IDE Discard-changes parity) declarations. Net ~+210 tokens.
            //   2026-05-22 bloat sweep: 7200 → 6500. Cumulative trim of ~1150 tokens by
            //   shortening overlong descriptions on genexus_edit.validate, apply_pattern.validate,
            //   genexus_history, genexus_lifecycle.target/compact/force/includeCallees,
            //   genexus_preview, dedupe of the kb-description boilerplate across 32 tools.
            //   New additions (wait_until_done, skipFullDeploy, edit_and_build.patch, worker_reload.force)
            //   fit comfortably under the lowered budget. Net ~-870 tokens vs prior 7200.
            //   v2.6.8: 6500 → 6700 for sort/since/modifiedBefore/cursor declarations on
            //   genexus_list_objects + genexus_query. Net ~+55 tokens; budget set with
            //   ~140 tokens of headroom for the next small batch.
            //   2026-05-22 friction items 4/9/17: 6700 → 7000 for replaceAll flag on
            //   genexus_edit + genexus_inspect runtimeIds enum addition. Net ~+155 tokens.
            //   2026-05-23 wave3 items 62/63/64: 7200 → 7400 for projection enum on
            //   genexus_inspect/list_objects plus docUrl/suggested_next_step inflation
            //   in error envelopes. Measured 7337; budget set at 7400 with ~60 headroom.
            //   2026-05-23 friction items 45/50/65: 7000 → 7200 for genexus_apply_pattern
            //   mode=diagnose + new genexus_security + genexus_orient tools. Net ~+99 tokens
            //   measured (7099); budget set at 7200 with ~100 tokens of headroom.
            //   2026-05-23 friction items 20/21/43/72: 7200 → 7400 for universal dryRun
            //   (5 tools), autoFormat on genexus_edit, reorg_preview action + notifyOnFailure
            //   on genexus_lifecycle. Net ~+110 tokens measured.
            //   2026-05-23 improvements item 41: 7400 → 7500 for genexus_db_drift
            //   (Transaction↔DB schema drift detection). Net ~+30 tokens.
            //   2026-05-23 improvements item 19: 7500 → 7700 for genexus_edit_form
            //   (semantic WebForm: add_textblock / add_button / set_visibility /
            //   remove_control / wrap_in_fieldset). Net ~+195 tokens.
            //   2026-05-23 improvements items 11/15: 7700 → 8000 for genexus_run_object
            //   (runtime URL + GAM cookies) and transactional flag on genexus_bulk_edit.
            //   Measured 7851 with both; budget set at 8000 with ~150 tokens of headroom.
            //   2026-05-23 wave3 Tier-S items 28/51: 8000 → 8600 for fastIncremental
            //   on genexus_lifecycle + warm mode on genexus_worker_reload (EXPERIMENTAL),
            //   plus genexus_kb startup actions added in parallel.
            //   2026-05-23 wave3 IDE right-click sweep: 8600 → 9000 for genexus_kb_explorer
            //   (Locate parity), genexus_navigation (View Navigation parity), genexus_blame
            //   (foundation wiring), and doc-explain tools (genexus_explain / genexus_diff_generated
            //   / genexus_kb_readme) added in parallel. Measured ~8853; budget set at 9000.
            //   2026-05-23 wave3 browser-verify: 9000 → 9300 for genexus_browser_capture,
            //   genexus_smoke_test, genexus_a11y_audit. Net ~+220 tokens.
            Assert.True(approxTokens < 9300, $"tool_definitions.json is ~{approxTokens} tokens; budget 9300.");
        }
    }
}
