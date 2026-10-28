using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GxMcp.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class KbSelectionRouteTests
    {
        [Fact]
        public async Task StdioSelect_IsInMemory_AndSubsequentListResolvesTheSessionSelection()
        {
            using var fixture = RouteFixture.Create();
            string session = "stdio-route-" + Guid.NewGuid().ToString("N");

            var select = await fixture.CallAsync(session, "select", "orders");
            Assert.Equal("orders", fixture.Payload(select)["selectedKb"]?.ToString());
            Assert.False(fixture.Payload(select)["persisted"]!.Value<bool>());
            Assert.Equal("customer", JObject.Parse(File.ReadAllText(fixture.ConfigPath))["Environment"]?["DefaultKb"]?.ToString());

            var list = await fixture.CallAsync(session, "list", null);
            var listPayload = fixture.Payload(list);
            Assert.Equal("orders", listPayload["selectedKb"]?.ToString());
        }

        [Fact]
        public async Task SessionHttpToolsCall_UsesTheMcpSessionAndKeepsSelectionsIndependent()
        {
            using var fixture = RouteFixture.Create();
            string sessionA = fixture.CreateHttpSession();
            string sessionB = fixture.CreateHttpSession();

            await fixture.HttpCallAsync(sessionA, "select", "orders");
            await fixture.HttpCallAsync(sessionB, "select", "customer");

            var listA = fixture.Payload(await fixture.HttpCallAsync(sessionA, "list", null));
            var listB = fixture.Payload(await fixture.HttpCallAsync(sessionB, "list", null));
            Assert.Equal("orders", listA["selectedKb"]?.ToString());
            Assert.Equal("customer", listB["selectedKb"]?.ToString());
            Assert.NotEqual(listA["selectedKb"]?.ToString(), listB["selectedKb"]?.ToString());
        }

        [Fact]
        public async Task PersistentDefault_ReturnsPersistedTo_AndReadsBackDiskState()
        {
            using var fixture = RouteFixture.Create();

            var response = await fixture.CallAsync("stdio", "set_persistent_default", "orders");
            var payload = fixture.Payload(response);

            Assert.Equal("orders", payload["defaultKb"]?.ToString());
            Assert.Equal("orders", payload["selectedKb"]?.ToString());
            Assert.Equal(fixture.ConfigPath, payload["persistedTo"]?.ToString());
            var persisted = JObject.Parse(File.ReadAllText(fixture.ConfigPath));
            Assert.Equal("orders", persisted["Environment"]?["DefaultKb"]?.ToString());
            Assert.Equal("orders", persisted["Environment"]?["ActiveKb"]?.ToString());
        }

        [Fact]
        public async Task InvalidAlias_ReturnsKbNotFoundWithoutChangingSelection()
        {
            using var fixture = RouteFixture.Create();
            string session = "invalid-route-" + Guid.NewGuid().ToString("N");
            await fixture.CallAsync(session, "select", "orders");

            var response = await fixture.CallAsync(session, "select", "missing");
            Assert.True(response["result"]?["isError"]?.Value<bool>());
            var errorPayload = fixture.Payload(response);
            Assert.Equal("KB_NOT_FOUND", errorPayload["code"]?.ToString());

            var list = fixture.Payload(await fixture.CallAsync(session, "list", null));
            Assert.Equal("orders", list["selectedKb"]?.ToString());
        }

        [Fact]
        public async Task LegacySetDefaultPersistFalse_RemainsSessionOnly()
        {
            using var fixture = RouteFixture.Create();
            string session = "legacy-route-" + Guid.NewGuid().ToString("N");

            var response = await fixture.CallAsync(session, "set_default", "orders", persist: false);
            var payload = fixture.Payload(response);
            Assert.Equal("orders", payload["selectedKb"]?.ToString());
            Assert.False(payload["persisted"]!.Value<bool>());
            Assert.Equal("customer", JObject.Parse(File.ReadAllText(fixture.ConfigPath))["Environment"]?["DefaultKb"]?.ToString());
        }

        // #683, #689: pool actions take `kb` as the alias, as the schema and help say.
        [Theory]
        [InlineData("close", "alias")]
        [InlineData("select", "selectedKb")]
        [InlineData("set_session_default", "selectedKb")]
        [InlineData("set_persistent_default", "defaultKb")]
        [InlineData("set_default", "defaultKb")]
        public async Task PoolAction_AcceptsKbAsTheAlias(string action, string aliasField)
        {
            using var fixture = RouteFixture.Create();
            string session = "pool-route-" + Guid.NewGuid().ToString("N");

            var response = await fixture.CallAsync(session, action, alias: null, kb: "orders");

            var payload = fixture.Payload(response);
            Assert.True(response["result"]?["isError"]?.Value<bool>() != true, payload.ToString());
            Assert.Equal("orders", payload[aliasField]?.ToString());
        }

        [Theory]
        [InlineData("close")]
        [InlineData("set_persistent_default")]
        [InlineData("set_default")]
        public async Task AliasActions_WithoutAliasOrKb_NameBothParameters(string action)
        {
            using var fixture = RouteFixture.Create();
            var response = await fixture.CallAsync("stdio", action, null);
            Assert.Equal($"Missing 'alias' (or 'kb') for action={action}.", fixture.Payload(response)["error"]?.ToString());
        }

        [Theory]
        [InlineData("select")]
        [InlineData("set_session_default")]
        public async Task SelectActions_WithoutAliasKbOrPath_NameEveryAcceptedParameter(string action)
        {
            using var fixture = RouteFixture.Create();
            string session = "select-missing-" + Guid.NewGuid().ToString("N");

            var response = await fixture.CallAsync(session, action, null);

            Assert.Equal($"The 'alias' (or 'kb') or 'path' parameter is required for '{action}'.",
                fixture.Payload(response)["error"]?.ToString());
        }

        // #689: the schema is what the caller reads to build the call, so it names every pool
        // action that takes `kb` in place of `alias` (the theory above exercises the same list).
        [Theory]
        [InlineData("kb")]
        [InlineData("alias")]
        public void GenexusKbSchema_NamesEveryPoolActionThatTakesKbInPlaceOfAlias(string field)
        {
            var tool = JArray.Parse(RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"))
                .OfType<JObject>().Single(t => (string?)t["name"] == "genexus_kb");
            string description = (string?)tool["inputSchema"]?["properties"]?[field]?["description"] ?? "";

            foreach (string action in new[] { "close", "select", "set_session_default", "set_persistent_default", "set_default" })
                Assert.True(description.Contains(action, StringComparison.Ordinal),
                    $"genexus_kb.{field} must name '{action}': {description}");
        }

        private sealed class RouteFixture : IDisposable
        {
            private readonly IDisposable _state;
            private readonly string _directory;
            internal string ConfigPath { get; }

            private RouteFixture(string directory, string configPath, IDisposable state)
            {
                _directory = directory;
                ConfigPath = configPath;
                _state = state;
            }

            internal static RouteFixture Create()
            {
                string directory = Path.Combine(Path.GetTempPath(), "gxmcp-route-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string configPath = Path.Combine(directory, "config.json");
                File.WriteAllText(configPath, "{\"Environment\":{\"ResolutionPolicy\":\"strict\",\"DefaultKb\":\"customer\",\"ActiveKb\":\"customer\",\"KBs\":[{\"Alias\":\"customer\",\"Path\":\"C:/KB/Customer\"},{\"Alias\":\"orders\",\"Path\":\"C:/KB/Orders\"}]}}");
                var config = new Configuration
                {
                    Environment = new EnvironmentConfig
                    {
                        ResolutionPolicy = "strict",
                        DefaultKb = "customer",
                        ActiveKb = "customer",
                        KBs =
                        {
                            new KbEntry { Alias = "customer", Path = "C:/KB/Customer" },
                            new KbEntry { Alias = "orders", Path = "C:/KB/Orders" }
                        }
                    }
                };
                return new RouteFixture(directory, configPath, Program.ConfigureRouteStateForTest(config, configPath));
            }

            internal async Task<JObject> CallAsync(string session, string action, string? alias, bool? persist = null, string? kb = null)
            {
                var arguments = new JObject { ["action"] = action };
                if (alias != null) arguments["alias"] = alias;
                if (kb != null) arguments["kb"] = kb;
                if (persist.HasValue) arguments["persist"] = persist.Value;
                return (await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = "genexus_kb", ["arguments"] = arguments }
                }, session))!;
            }

            internal string CreateHttpSession() => Program.CreateHttpSessionForTest();

            internal async Task<JObject> HttpCallAsync(string session, string action, string? alias)
            {
                var body = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_kb",
                        ["arguments"] = new JObject { ["action"] = action }
                    }
                };
                if (alias != null) body["params"]!["arguments"]!["alias"] = alias;
                var context = NewHttpContext(body.ToString(Newtonsoft.Json.Formatting.None));
                context.Request.Headers["MCP-Session-Id"] = session;
                var result = await Program.HandleJsonRpcHttpRequest(context.Request);
                await result.ExecuteAsync(context);
                context.Response.Body.Position = 0;
                return JObject.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
            }

            private static DefaultHttpContext NewHttpContext(string body)
            {
                var context = new DefaultHttpContext();
                context.Request.ContentType = "application/json";
                context.Request.Headers["Accept"] = "application/json, text/event-stream";
                context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
                context.Response.Body = new MemoryStream();
                return context;
            }

            internal JObject Payload(JObject response) => JObject.Parse(response["result"]!["content"]![0]!["text"]!.ToString());

            public void Dispose()
            {
                _state.Dispose();
                try { Directory.Delete(_directory, true); } catch { }
            }
        }
    }

    [CollectionDefinition("Gateway route state", DisableParallelization = true)]
    public sealed class GatewayRouteStateCollectionDefinition { }
}
