using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TerseErrorTests
    {
        [Fact]
        public void TrimErrorEnvelope_DefaultDropsStackAndKeepsMessage()
        {
            var input = JObject.Parse(@"{
                ""code"":""internal"",""message"":""boom\nstack frame 1\nstack frame 2"",
                ""stack"":""..."",""details"":""..."",""hint"":""try X""
            }");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("boom", (string)trimmed["message"]!);
            Assert.Equal("internal", (string)trimmed["code"]!);
            Assert.Equal("try X", (string)trimmed["hint"]!);
            Assert.False(trimmed.ContainsKey("stack"));
            Assert.False(trimmed.ContainsKey("details"));
        }

        [Fact]
        public void TrimErrorEnvelope_VerbosePassesThrough()
        {
            var input = JObject.Parse(@"{""message"":""x"",""stack"":""trace""}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: true);
            Assert.Equal("trace", (string)trimmed["stack"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_HandlesMissingFieldsGracefully()
        {
            var input = JObject.Parse(@"{}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("Unknown error", (string)trimmed["message"]!);
            Assert.False(trimmed.ContainsKey("code"));
        }
    }
}
