using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Item #11 (v2.6.4): whoami response carries inline playbooks for the most
    // common flows so the LLM gets routing without exploration. Asserts the
    // shape and presence of the canonical entries.
    public class WhoamiPlaybooksTests
    {
        [Fact]
        public void BuildWhoamiPayload_IncludesPlaybooksBlock()
        {
            JObject whoami = Program.BuildWhoamiPayload();
            Assert.True(whoami.ContainsKey("playbooks"), "whoami envelope must include playbooks block");
            Assert.IsType<JObject>(whoami["playbooks"]);
        }

        [Theory]
        [InlineData("wwp_on_transaction")]
        [InlineData("wwp_on_webpanel")]
        [InlineData("edit_wwp_layout")]
        [InlineData("create_popup")]
        [InlineData("read_object_structure")]
        [InlineData("recipes_index")]
        public void Playbooks_ContainsCanonicalRoutes(string key)
        {
            JObject playbooks = (JObject)Program.BuildWhoamiPayload()["playbooks"]!;
            Assert.True(playbooks.ContainsKey(key), $"playbooks must include '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(playbooks[key]!.ToString()),
                $"playbooks['{key}'] must be a non-empty route hint");
        }

        [Fact]
        public void Playbooks_WwpOnWebpanel_EmphasizesParentTypeCheck()
        {
            // The original bug was apply WWP on WebPanel binding as Transaction.
            // The playbook MUST steer the LLM to inspect parentType first.
            JObject playbooks = (JObject)Program.BuildWhoamiPayload()["playbooks"]!;
            string route = playbooks["wwp_on_webpanel"]!.ToString();
            Assert.Contains("PARENT TYPE", route, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("inspect", route, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Playbooks_RecipesIndex_PointsToGenexusRecipeTool()
        {
            JObject playbooks = (JObject)Program.BuildWhoamiPayload()["playbooks"]!;
            string route = playbooks["recipes_index"]!.ToString();
            Assert.Contains("genexus_recipe", route);
        }

        [Fact]
        public void WhoamiCache_AfterUpdate_BuildIndexBlockReflectsLatest()
        {
            // Item #6 — whoami caches index state to avoid round-tripping the
            // worker on every call. UpdateLastKnownIndexState mutates the
            // snapshot and BuildWhoamiPayload's index block must echo it.
            Program.UpdateLastKnownIndexState("Ready", 1234, System.DateTime.UtcNow, 1.0, 0);
            JObject whoami = Program.BuildWhoamiPayload();
            JObject index = (JObject)whoami["index"]!;
            Assert.Equal("Ready", index["status"]!.ToString());
            Assert.Equal(1234, index["totalObjects"]!.ToObject<int>());
        }
    }
}
