using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Item #11 (v2.6.4): whoami response carries inline playbooks for the most
    // common flows so the LLM gets routing without exploration. Asserts the
    // shape and presence of the canonical entries.
    [Collection("IndexStateMirror")]
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
        [InlineData("html_form_inline_js")]
        [InlineData("popup_call_async")]
        [InlineData("verify_in_browser")]
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

        // v2.8.1 regression — v2.8.0 wrapped the worker's GetIndexState reply in the canonical
        // McpResponse.Ok envelope { status:"ok", code:"IndexState", result:{ indexStatus, totalObjects } },
        // nesting the payload one level below env["result"]. The gateway read the envelope's
        // status:"ok"/totalObjects:null (→0), so every SDK-bound read/query/list fast-failed with
        // IndexNotReady while the worker's index was in fact Ready. ApplyIndexStateFromWorkerResult
        // must descend into the nested result.
        [Fact]
        public void ApplyIndexState_CanonicalEnvelope_UnwrapsNestedResult()
        {
            try
            {
                var workerResult = new JObject
                {
                    ["status"] = "ok",
                    ["code"] = "IndexState",
                    ["result"] = new JObject
                    {
                        ["indexStatus"] = "Ready",
                        ["totalObjects"] = 2090,
                        ["lastIndexedAt"] = null,
                        ["progress"] = null,
                        ["etaMs"] = null
                    }
                };

                bool applied = Program.ApplyIndexStateFromWorkerResult(workerResult);
                Assert.True(applied);

                JObject index = (JObject)Program.BuildWhoamiPayload()["index"]!;
                Assert.Equal("Ready", index["status"]!.ToString());
                Assert.Equal(2090, index["totalObjects"]!.ToObject<int>());
            }
            finally
            {
                Program.UpdateLastKnownIndexState("Cold", 0, null, null, null);
            }
        }

        [Fact]
        public void ApplyIndexState_FlatLegacyShape_StillParses()
        {
            // Pre-2.8.0 worker returned the index payload flat at env["result"] level.
            try
            {
                var flat = new JObject
                {
                    ["indexStatus"] = "LiteReady",
                    ["totalObjects"] = 42
                };

                Assert.True(Program.ApplyIndexStateFromWorkerResult(flat));

                JObject index = (JObject)Program.BuildWhoamiPayload()["index"]!;
                Assert.Equal("LiteReady", index["status"]!.ToString());
                Assert.Equal(42, index["totalObjects"]!.ToObject<int>());
            }
            finally
            {
                Program.UpdateLastKnownIndexState("Cold", 0, null, null, null);
            }
        }

        [Fact]
        public void ApplyIndexState_StringDataPayload_StillParses()
        {
            // Legacy string-wrapped payload: the real state arrives as a JSON string under "data".
            try
            {
                var wrapped = new JObject
                {
                    ["data"] = "{\"indexStatus\":\"Enriching\",\"totalObjects\":7}"
                };

                Assert.True(Program.ApplyIndexStateFromWorkerResult(wrapped));

                JObject index = (JObject)Program.BuildWhoamiPayload()["index"]!;
                Assert.Equal("Enriching", index["status"]!.ToString());
                Assert.Equal(7, index["totalObjects"]!.ToObject<int>());
            }
            finally
            {
                Program.UpdateLastKnownIndexState("Cold", 0, null, null, null);
            }
        }

        // v2.8.2 regression — same v2.8.0 canonical-envelope nesting as the index state, but for
        // GetDatabaseInfo. Without descending, whoami.database showed the envelope wrapper and the
        // SQL-dialect hint went silent. `env` is the JSON-RPC response; its `result` is the worker
        // payload (here the McpResponse.Ok envelope { status, code, result:{ default, … } }).
        [Fact]
        public void ExtractDatabaseInfo_CanonicalEnvelope_UnwrapsNestedResult()
        {
            var env = new JObject
            {
                ["result"] = new JObject
                {
                    ["status"] = "ok",
                    ["code"] = "DatabaseInfoCollected",
                    ["result"] = new JObject
                    {
                        ["default"] = new JObject { ["type"] = "PostgreSQL", ["dialect"] = "postgresql" }
                    }
                }
            };

            JObject? info = Program.ExtractDatabaseInfoFromWorkerResult(env);
            Assert.NotNull(info);
            Assert.Equal("PostgreSQL", info!["default"]?["type"]?.ToString());
            Assert.Equal("postgresql", info["default"]?["dialect"]?.ToString());
        }

        [Fact]
        public void ExtractDatabaseInfo_FlatLegacyShape_PassesThrough()
        {
            // Pre-2.8.0: store fields sit beside status at the top of env["result"], no nested result.
            var env = new JObject
            {
                ["result"] = new JObject
                {
                    ["status"] = "ok",
                    ["default"] = new JObject { ["type"] = "Oracle" }
                }
            };

            JObject? info = Program.ExtractDatabaseInfoFromWorkerResult(env);
            Assert.NotNull(info);
            Assert.Equal("Oracle", info!["default"]?["type"]?.ToString());
        }

        [Fact]
        public void ExtractDatabaseInfo_NotOk_ReturnsNull()
        {
            var env = new JObject
            {
                ["result"] = new JObject { ["status"] = "error" }
            };
            Assert.Null(Program.ExtractDatabaseInfoFromWorkerResult(env));
            Assert.Null(Program.ExtractDatabaseInfoFromWorkerResult(new JObject { ["__timeout"] = true }));
            Assert.Null(Program.ExtractDatabaseInfoFromWorkerResult(null));
        }
    }
}
