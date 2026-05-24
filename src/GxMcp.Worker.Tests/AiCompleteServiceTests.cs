using System.Collections.Generic;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AiCompleteServiceTests
    {
        private static System.Func<string, string> Envless(Dictionary<string, string> map = null)
        {
            return key => map != null && map.TryGetValue(key, out var v) ? v : null;
        }

        [Fact]
        public void Complete_EnvVarsUnset_ReturnsAiEndpointNotConfigured()
        {
            var svc = new AiCompleteService(http: null, envLookup: Envless());

            var j = svc.Complete("MyObj", "Events", "explain this code", 100);

            Assert.Equal("AiEndpointNotConfigured", (string)j["code"]);
            Assert.NotNull(j["hint"]);
            // Must NOT include a "status: Success" — the call short-circuited.
            Assert.Null(j["status"]);
        }

        [Fact]
        public void Complete_EnvVarsSetButContextEmpty_ReturnsInvalidRequest()
        {
            var env = new Dictionary<string, string>
            {
                ["GXMCP_AI_COMPLETE_URL"] = "https://example.invalid/v1/chat/completions",
                ["GXMCP_AI_COMPLETE_KEY"] = "test-key"
            };
            var svc = new AiCompleteService(http: null, envLookup: Envless(env));

            var j = svc.Complete("MyObj", "Events", "", 100);
            Assert.Equal("InvalidRequest", (string)j["code"]);
        }
    }
}
