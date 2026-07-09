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
            //
            //   v2.6.9 (2026-05-22 → 2026-05-24): cumulative bump 6700 → 13000 across the
            //   friction-wishlist sweep. Net ~+6300 tokens spread over ~75 new tools/fields
            //   and a 23-item DEFERRED-stub sweep (FutureItemRouter → FutureItemStub).
            //   Per-batch detail is captured in CHANGELOG.md v2.6.9 — the prior per-bump
            //   comment trail churned in parallel from many worktree branches and ended up
            //   non-chronological / contradictory, so it was collapsed into this single
            //   line. Future bumps should add ONE entry below this one with rationale.
            //   v2.6.9 (2026-05-24): 13000 → 13100 for genexus_doctor (~31 tokens). New
            //   triage tool that consolidates health checks the user previously had to
            //   chain across genexus_whoami + genexus_logs + manual psutil.
            //   v2.6.9 (2026-05-24): 13100 → 13200 for "examples" arrays on three
            //   high-traffic schemas (genexus_voice, genexus_recipe, genexus_edit_form).
            //   LLM clients render schema examples in the tool picker; the +27 tokens
            //   measured pay for themselves the first time the agent guesses a malformed
            //   call. Capped at three tools to avoid death-by-thousand-fields.
            //   v2.6.9 (2026-05-24, description trim sweep): 13200 → 12400. Tightened
            //   ~25 top-level tool descriptions and shrank repeated kb-param description
            //   (64 instances of "KB alias (multi-KB only)." → "KB alias.") plus the
            //   visualVerify/notifyOnFailure/skipFullDeploy/fastIncremental over-prose
            //   on genexus_lifecycle and genexus_edit{,_and_build}. Net ~-860 tokens.
            //   Golden tools-list fixture regenerated. No tool-help resource changes
            //   (ToolHelpCatalog.cs already carries the long-form for the trimmed tools).
            //   v2.6.9 (2026-05-24, SOTA db_optimize): 12400 → 12800 for genexus_db_optimize
            //   (~80 tokens — static index advisor + hot-path analysis) plus other
            //   parallel-wave additions that landed in the same window.
            //   v2.6.9 (2026-05-24, SOTA wave consolidation): 12800 → 13050 to cover
            //   the full parallel-wave landing: genexus_api, genexus_db_optimize,
            //   genexus_gxserver, genexus_types, plus genexus_analyze.cross_platform_impact
            //   enum value and genexus_recipe action surface expansion (suggest_macro /
            //   crystallize). Measured ~12943; ~107 tokens headroom.
            //   v2.6.9 (2026-05-24, profile bridge): 13050 → 13150 for genexus_profile
            //   (~75 tokens — runtime profiler XML ingest). Measured 13081.
            //   v2.6.12 (2026-05-26, deferred-load playbook): 13150 → 13300 for
            //   genexus_playbook (~110 tokens — topic enum + deferred-load skill packs;
            //   the markdown bodies live as embedded resources in the Worker assembly,
            //   not in the tool schema, so the budget impact is the schema entry only).
            //   v2.7.0 (2026-05-26, consolidation sweep): 13300 → 9500. 92 tools collapsed
            //   to 42 (~54% reduction) by introducing 8 umbrella tools
            //   (genexus_browser, _db, _versioning, _io, _variable, _telemetry, _create)
            //   that absorb 38 legacy tools via action= dispatch, plus 14 niche tools
            //   removed from advertisement (kb_diff, kb_import, kb_explorer, sandbox,
            //   inject_context, what_if, reverse_pattern, explain, kb_readme,
            //   pr_description, tutorial, build_plan, auto_test, voice). Legacy names
            //   still dispatch via McpRouter.LegacyToolAliases (set
            //   GXMCP_LEGACY_TOOL_ALIASES=0 to opt out). Net: ~13.2k → ~8.8k tokens.
            //   Folded duplicates: orient (use whoami), validate_payload (use edit
            //   validate=only), bulk_edit (use edit targets[]).
            //   v2.8.0 (2026-05-28, examples annotations): 9500 → 10500. Added 1-2
            //   call-shape examples to every tool's inputSchema.examples array (40 tools
            //   updated; genexus_recipe and genexus_edit_form already had them). Measured
            //   ~10034 tokens; ~466 tokens headroom.
            //   v2.8.0 (2026-05-28, MCP-spec annotations): 10500 → 12000. Added
            //   annotations block (readOnlyHint, destructiveHint, idempotentHint,
            //   openWorldHint) to all 42 tools. Measured ~11588 tokens; ~412 headroom.
            //   v2.9.2 (2026-06-11, schema trim): 12000 → 11400. De-advertised 5 tools
            //   (genexus_ai_complete, genexus_github, genexus_multi_agent_lock,
            //   genexus_rename_across_kb, genexus_worker_pool); they remain callable via
            //   their existing direct dispatch but are no longer in the tool list.
            //   Added index-readiness preconditions to genexus_query/search_source/analyze,
            //   cross-reference hints to genexus_structure/properties, removed redundant
            //   alias params (target) from genexus_layout/db/browser, fixed terse param
            //   descriptions in genexus_versioning/io/telemetry, set genexus_edit
            //   destructiveHint=true, resolved genexus_versioning hint conflict,
            //   removed EXPERIMENTAL mode=warm from genexus_worker_reload.
            //   Measured ~10925 tokens; ~475 headroom.
            //   v2.13.2 (2026-07-09, issue #27 item 4): 11400 → 11550 for the
            //   genexus_search_source scope surface (objectName + startIndex +
            //   timeoutMs params — object-scoped, resumable search). Measured
            //   ~11423 tokens; ~127 headroom.
            //   v2.13.3+ (2026-07-09, genexus_kb_version spike): 11550 → 12050 for
            //   the new genexus_kb_version tool (KBVersionHelper-backed Create
            //   Version/Branch/Activate/Revert). Measured ~11873 tokens; ~177
            //   headroom.
            Assert.True(approxTokens < 12050, $"tool_definitions.json is ~{approxTokens} tokens; budget 12050.");
        }
    }
}
