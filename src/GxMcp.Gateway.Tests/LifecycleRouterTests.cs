using System;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleRouterTests
    {
        [Fact]
        public void Lifecycle_Rebuild_Without_Target_Routes_To_RebuildAll()
        {
            var router = new SystemRouter();
            var routed = router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "rebuild"
            });

            Assert.NotNull(routed);
            var jobj = JObject.FromObject(routed);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("RebuildAll", jobj["action"]?.ToString());
        }

        [Fact]
        public async Task Lifecycle_Rebuild_Intercept_With_Target_Rejects_TargetNotSupported()
        {
            var args = new JObject
            {
                ["action"] = "rebuild",
                ["target"] = "Customer"
            };

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_lifecycle",
                    ["arguments"] = args
                }
            };

            var respObj = await Program.HandleLifecycleGatewayInterceptsAsync(
                1,
                "genexus_lifecycle",
                args,
                request,
                "test-session",
                CancellationToken.None);

            Assert.NotNull(respObj);
            Assert.True(respObj["isError"]?.Value<bool>() == true || respObj["result"]?["isError"]?.Value<bool>() == true);

            string text = respObj["result"]?["content"]?[0]?["text"]?.ToString() ?? respObj.ToString();
            var payload = JObject.Parse(text);

            Assert.Equal("TargetNotSupported", payload["code"]?.ToString());
            Assert.Contains("action=rebuild does not accept a target", payload["message"]?.ToString());
            Assert.Contains("Use action=build with target=<object>", payload["hint"]?.ToString());
            Assert.Equal("Customer", payload["target"]?.ToString());
        }
    }
}
