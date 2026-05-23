using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class InjectMetaTokensTests
    {
        private static JObject MakeToolResult(string innerJson)
        {
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = innerJson } }
            };
        }

        [Fact]
        public void Injects_Tokens_Block_On_Json_Payload()
        {
            var tr = MakeToolResult("{\"status\":\"Success\"}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.NotNull(inner["_meta"]?["tokens"]);
            Assert.Equal(McpRouter.MetaTokenLimit, (int)inner["_meta"]!["tokens"]!["limit"]!);
            Assert.True((int)inner["_meta"]!["tokens"]!["used"]! > 0);
        }

        [Fact]
        public void Hint_Null_Below_Half_Limit()
        {
            var tr = MakeToolResult("{\"status\":\"Success\"}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.Equal(JTokenType.Null, inner["_meta"]!["tokens"]!["hint"]!.Type);
        }

        [Fact]
        public void Hint_Set_When_Over_Half_Limit()
        {
            // Build a payload big enough to cross 50% of MetaTokenLimit (25000 → 12500 tokens ≈ 50000 chars).
            string padding = new string('x', 60000);
            var tr = MakeToolResult("{\"status\":\"Success\",\"payload\":\"" + padding + "\"}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            var hint = inner["_meta"]!["tokens"]!["hint"];
            Assert.NotEqual(JTokenType.Null, hint!.Type);
            Assert.Contains("token", hint.ToString().ToLower());
        }

        [Fact]
        public void Non_Json_Text_Left_Untouched()
        {
            var tr = MakeToolResult("plain text not json");
            McpRouter.InjectMetaTokens(tr);
            Assert.Equal("plain text not json", (string)tr["content"]![0]!["text"]!);
        }

        [Fact]
        public void Existing_Tokens_Block_Not_Overwritten()
        {
            var tr = MakeToolResult("{\"_meta\":{\"tokens\":{\"used\":42,\"limit\":99,\"hint\":\"sticky\"}}}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.Equal(42, (int)inner["_meta"]!["tokens"]!["used"]!);
            Assert.Equal(99, (int)inner["_meta"]!["tokens"]!["limit"]!);
            Assert.Equal("sticky", (string)inner["_meta"]!["tokens"]!["hint"]!);
        }
    }
}
