using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    public class CompactJsonTests
    {
        [Fact]
        public void StripNulls_RemovesNullProperties()
        {
            var j = JObject.Parse(@"{""ok"":true,""err"":null,""data"":""x""}");
            McpRouter.StripNulls(j);
            Assert.False(j.ContainsKey("err"));
            Assert.True(j.ContainsKey("ok"));
            Assert.True(j.ContainsKey("data"));
        }

        [Fact]
        public void StripNulls_KeepsEmptyArraysAndZeros()
        {
            var j = JObject.Parse(@"{""items"":[],""count"":0,""flag"":false,""s"":""""}");
            McpRouter.StripNulls(j);
            Assert.True(j.ContainsKey("items"));
            Assert.True(j.ContainsKey("count"));
            Assert.True(j.ContainsKey("flag"));
            Assert.True(j.ContainsKey("s"));
        }

        [Fact]
        public void StripNulls_RecursesIntoNestedObjects()
        {
            var j = JObject.Parse(@"{""nested"":{""a"":null,""b"":1}}");
            McpRouter.StripNulls(j);
            var nested = (JObject)j["nested"]!;
            Assert.False(nested.ContainsKey("a"));
            Assert.True(nested.ContainsKey("b"));
        }
    }
}
