using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Item #13 (v2.6.4): tool descriptions front-load WWP routing hints so the
    // LLM picks the right entry point from `tools/list` without exploration.
    // Item #1: analyze.mode dropped 'explain'. Item #12: genexus_recipe registered.
    public class ToolDefinitionsRedirectsTests
    {
        private static JArray LoadToolDefinitions()
        {
            string path = Path.Combine(System.AppContext.BaseDirectory, "tool_definitions.json");
            string text = File.ReadAllText(path);
            return JArray.Parse(text);
        }

        private static JObject? FindTool(string name)
            => LoadToolDefinitions().OfType<JObject>().FirstOrDefault(t => t["name"]?.ToString() == name);

        [Fact]
        public void GenexusApplyPattern_DescriptionMentionsInspectFirst()
        {
            var t = FindTool("genexus_apply_pattern");
            Assert.NotNull(t);
            string desc = t!["description"]!.ToString();
            // v2.6.9 description trim: the "inspect first / parentType-routing"
            // long-form moved into the genexus://kb/tool-help/genexus_apply_pattern
            // resource. The terse description still names the two binding modes
            // which is the routing signal the test was guarding for.
            Assert.Contains("Transaction", desc, System.StringComparison.Ordinal);
            Assert.Contains("WebPanel", desc, System.StringComparison.Ordinal);
            Assert.Contains("diagnose", desc, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GenexusCreateObject_DescriptionRedirectsWwpToApplyPattern()
        {
            var t = FindTool("genexus_create_object");
            Assert.NotNull(t);
            string desc = t!["description"]!.ToString();
            Assert.Contains("apply_pattern", desc);
            Assert.Contains("WorkWithPlus", desc);
        }

        [Fact]
        public void GenexusEdit_DescriptionWarnsAboutPatternInstanceVsWebForm()
        {
            var t = FindTool("genexus_edit");
            Assert.NotNull(t);
            string desc = t!["description"]!.ToString();
            Assert.Contains("PatternInstance", desc);
        }

        [Fact]
        public void GenexusWhoami_DescriptionPointsToPlaybooks()
        {
            var t = FindTool("genexus_whoami");
            Assert.NotNull(t);
            string desc = t!["description"]!.ToString();
            Assert.Contains("playbooks", desc, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GenexusRecipe_ToolIsRegistered()
        {
            var t = FindTool("genexus_recipe");
            Assert.NotNull(t);
            // v2.6.10 (auto-macro recording) — `name` is no longer universally
            // required since the new suggest_macro/crystallize actions don't take
            // it. Test now just guards registration + action surface presence.
            var actions = (JArray)t!["inputSchema"]!["properties"]!["action"]!["enum"]!;
            var actionNames = actions.Select(x => x.ToString()).ToList();
            Assert.Contains("list", actionNames);
            Assert.Contains("run", actionNames);
        }

        [Fact]
        public void GenexusAnalyze_ModeEnumDoesNotIncludeExplain()
        {
            // Item #1: analyze mode 'explain' was a stub returning hardcoded
            // "Code analysis simulation". Removed from the schema; legacy
            // dispatchers respond NotImplemented.
            var t = FindTool("genexus_analyze");
            Assert.NotNull(t);
            var modeEnum = (JArray)t!["inputSchema"]!["properties"]!["mode"]!["enum"]!;
            var names = modeEnum.Select(m => m.ToString()).ToList();
            Assert.DoesNotContain("explain", names);
            // Sanity: the canonical modes are still there.
            Assert.Contains("summary", names);
            Assert.Contains("linter", names);
            Assert.Contains("navigation", names);
            Assert.Contains("pattern_metadata", names);
        }

        [Fact]
        public void GenexusApplyPattern_HasValidateBoolean()
        {
            // Item #17: apply_pattern { validate: true } triggers post-apply build.
            var t = FindTool("genexus_apply_pattern");
            Assert.NotNull(t);
            var props = (JObject)t!["inputSchema"]!["properties"]!;
            Assert.True(props.ContainsKey("validate"), "apply_pattern must declare 'validate' boolean");
            Assert.Equal("boolean", props["validate"]!["type"]!.ToString());
        }
    }
}
