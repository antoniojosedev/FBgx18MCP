using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Disposable live regression for issue #238. It uses only the authorized
    // scratch KB and deletes the probe object even when the move assertion fails.
    [Trait("Category", "LiveIssue")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class Issue238LiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;

        public Issue238LiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync() => _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task FolderMove_PersistsAndVerifiesContent()
        {
            var foldersResponse = await _harness.CallToolAsync(
                "genexus_list_objects",
                new JObject { ["typeFilter"] = "Folder", ["limit"] = 10 },
                120_000);
            Assert.False(LiveGatewayHarness.IsToolError(foldersResponse),
                "Folder inventory failed: " + foldersResponse.ToString(Newtonsoft.Json.Formatting.None));
            var foldersPayload = LiveGatewayHarness.ParseToolPayload(foldersResponse);
            var folders = foldersPayload?["result"]?["objects"] as JArray
                ?? foldersPayload?["result"]?["results"] as JArray
                ?? foldersPayload?["objects"] as JArray
                ?? foldersPayload?["results"] as JArray;
            string? folder = folders?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (string.IsNullOrWhiteSpace(folder))
                return; // Fixture has no Folder; the deterministic router/unit regression still runs.

            string probe = "McpIssue238" + DateTime.UtcNow.Ticks.ToString("X").Substring(8);
            try
            {
                // Created in the Root Module so the move below really moves it (an object already in
                // the destination answers ObjectAlreadyInDestination and writes nothing).
                var create = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "Procedure",
                    ["name"] = probe
                }, 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(create),
                    "create failed: " + create.ToString(Newtonsoft.Json.Formatting.None));

                // A real move needs the object's version token; the preview hands it out.
                var preview = await _harness.CallToolAsync("genexus_properties", new JObject
                {
                    ["action"] = "move",
                    ["name"] = probe,
                    ["type"] = "Procedure",
                    ["folder"] = folder,
                    ["dryRun"] = true
                }, 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(preview),
                    "folder move preview failed: " + preview.ToString(Newtonsoft.Json.Formatting.None));
                string? token = LiveGatewayHarness.ParseToolPayload(preview)?["result"]?["versionToken"]?.ToString();
                Assert.False(string.IsNullOrWhiteSpace(token), "the move preview returned no versionToken");

                var move = await _harness.CallToolAsync("genexus_properties", new JObject
                {
                    ["action"] = "move",
                    ["name"] = probe,
                    ["type"] = "Procedure",
                    ["folder"] = folder,
                    ["baseVersion"] = token,
                    ["rollbackOnFailure"] = true
                }, 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(move),
                    "folder move failed: " + move.ToString(Newtonsoft.Json.Formatting.None));

                var payload = LiveGatewayHarness.ParseToolPayload(move);
                Assert.Equal("ObjectMovedAndVerified", payload?["code"]?.ToString());
                Assert.True(payload?["result"]?["persisted"]?.Value<bool>() == true);
                Assert.True(payload?["result"]?["verified"]?.Value<bool>() == true);
                Assert.Equal(folder, payload?["result"]?["to"]?.ToString(), ignoreCase: true);
            }
            finally
            {
                await _harness.CallToolAsync("genexus_delete_object", new JObject
                {
                    ["name"] = probe,
                    ["confirm"] = true
                }, 120_000);
            }
        }
    }
}
