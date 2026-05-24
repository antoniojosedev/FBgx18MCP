using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    // Wave-3 doc-flagged "Long-term / strategic XL" + "Skip / wait for user
    // feedback" items (18 tools). Schema is declared in tool_definitions.json
    // and each tool routes through here to FutureItemStub.Deferred in the
    // worker, which returns a typed { status:"Future", code:"ItemDeferred",
    // hint, docRef } envelope. The map below is the single source of truth
    // for {tool → (itemNumber, hint)}.
    public class FutureItemRouter : IMcpModuleRouter
    {
        public string ModuleName => "Future";

        private static readonly Dictionary<string, (int Number, string Hint)> _items =
            new Dictionary<string, (int, string)>
            {
                ["genexus_watch_event"]            = (35, "Set breakpoints in Events; capture variable state when triggered."),
                ["genexus_worker_pool"]            = (53, "Maintain N warm workers for instant spawn."),
                ["genexus_sandbox"]                = (54, "Clone the KB for sandboxed edits; merge-back manual."),
                ["genexus_kb_diff"]                = (55, "Object-level diff between two KBs."),
                ["genexus_kb_import"]              = (56, "Import an object plus deps from another KB."),
                ["genexus_tutorial"]               = (66, "Interactive onboarding tutorial."),
                ["genexus_github"]                 = (71, "Auto-create PR from current branch."),
                ["genexus_learning"]               = (76, "Cross-session friction-learning loop."),
                ["genexus_sd_panel"]               = (78, "SDPanel parity for mobile. Most existing tools assume WebPanel."),
                ["genexus_ai_complete"]            = (81, "AI-prompted code completion in Events Source. Stub for upstream LLM integration."),
                ["genexus_time_travel"]            = (82, "Restore object state from a past timestamp without losing intermediate edits."),
                ["genexus_voice"]                  = (83, "Voice-driven edits via NL command."),
                ["genexus_multi_agent_lock"]       = (84, "Granular lock for parallel multi-agent edits."),
                ["genexus_what_if"]                = (86, "Simulate a type/schema change; report breakage without persisting."),
                ["genexus_rename_across_kb"]       = (91, "Rename across every call site / attribute / index in the KB."),
                ["genexus_auto_test"]              = (95, "Generate tests from real production invocation patterns."),
                ["genexus_reverse_pattern"]        = (96, "Infer a pattern (WWP-like) from existing similar objects."),
                ["genexus_cross_browser"]          = (98, "Render in parallel browsers; diff screenshots.")
            };

        public static IReadOnlyDictionary<string, (int Number, string Hint)> Items => _items;

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            if (toolName == null || !_items.TryGetValue(toolName, out var entry)) return null;
            return new
            {
                module = "Future",
                action = "Deferred",
                itemNumber = entry.Number,
                hint = entry.Hint
            };
        }
    }
}
