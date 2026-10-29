using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Disposable live regression for creating and moving objects inside a Module and a nested Folder
    // (genexus_create object_atomic parentPath, genexus_properties move). It builds its own Module/Folder
    // tree in the scratch KB, never reads a business object, and removes everything it made even when an
    // assertion fails. Nothing here specifies, generates, builds, reorganizes or runs anything.
    [Trait("Category", "LiveIssue")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class PlacementLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;

        public PlacementLiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync() => _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        private async Task<JObject> CallAsync(string tool, JObject args)
        {
            var response = await _harness.CallToolAsync(tool, args, 180_000);
            return LiveGatewayHarness.ParseToolPayload(response) ?? new JObject { ["unparsed"] = response.ToString(Newtonsoft.Json.Formatting.None) };
        }

        private static string Code(JObject payload)
            => payload["error"]?["code"]?.ToString() ?? payload["code"]?.ToString() ?? string.Empty;

        private async Task DeleteQuietlyAsync(string name, string type)
        {
            try
            {
                await CallAsync("genexus_delete_object", new JObject { ["name"] = name, ["type"] = type, ["confirm"] = true });
            }
            catch { /* best-effort cleanup of scratch objects */ }
        }

        [LiveKbFact]
        public async Task ObjectAtomic_InModuleAndNestedFolder_IsPersistedThere_AndMoveHonoursTheSamePath()
        {
            string suffix = DateTime.UtcNow.Ticks.ToString("X").Substring(8);
            string module = "McpPlcMod" + suffix;
            string folder = "McpPlcFld" + suffix;
            string created = "McpPlcNew" + suffix;
            string moved = "McpPlcMov" + suffix;
            string refused = "McpPlcNo" + suffix;
            string path = "Root Module/" + module + "/" + folder;
            try
            {
                var moduleCreate = await CallAsync("genexus_create", new JObject { ["action"] = "object", ["type"] = "Module", ["name"] = module });
                Assert.Equal("ok", moduleCreate["status"]?.ToString());
                var folderCreate = await CallAsync("genexus_create", new JObject
                {
                    ["action"] = "object", ["type"] = "Folder", ["name"] = folder, ["parentPath"] = "Root Module/" + module
                });
                Assert.Equal("ok", folderCreate["status"]?.ToString());

                // Preview: same resolver, nothing written.
                var preview = await CallAsync("genexus_create", new JObject
                {
                    ["action"] = "object_atomic", ["type"] = "Procedure", ["name"] = created, ["source"] = "// placed", ["parentPath"] = path, ["dryRun"] = true
                });
                Assert.Equal("DryRun", Code(preview));
                Assert.Equal(path + "/" + created, preview["result"]?["placement"]?["wouldCreateAt"]?.ToString());

                // Create in place: the identity is the one a fresh read found.
                var create = await CallAsync("genexus_create", new JObject
                {
                    ["action"] = "object_atomic", ["type"] = "Procedure", ["name"] = created, ["source"] = "// placed", ["parentPath"] = path
                });
                Assert.Equal("ObjectCreatedAtomic", Code(create));
                var result = create["result"];
                Assert.Equal(path + "/" + created, result?["identity"]?["path"]?.ToString());
                Assert.Equal(path, result?["identity"]?["parentPath"]?.ToString());
                Assert.True(Guid.TryParse(result?["identity"]?["guid"]?.ToString(), out _));
                Assert.True(result?["placement"]?["verified"]?.Value<bool>() == true);
                Assert.True(result?["placement"]?["freshRead"]?.Value<bool>() == true);
                Assert.Empty((JArray)result!["implicitOperations"]!);

                // A path that does not exist leaves nothing behind.
                var missing = await CallAsync("genexus_create", new JObject
                {
                    ["action"] = "object_atomic", ["type"] = "Procedure", ["name"] = refused, ["source"] = "// no", ["parentPath"] = path + "/Inexistente"
                });
                Assert.Equal("DestinationNotFound", Code(missing));
                var gone = await CallAsync("genexus_read", new JObject { ["name"] = refused, ["type"] = "Procedure", ["part"] = "Source" });
                Assert.NotEqual("ok", gone["status"]?.ToString());

                // Move: preview and write resolve the same destination; the write needs the token.
                var plain = await CallAsync("genexus_create", new JObject { ["action"] = "object", ["type"] = "Procedure", ["name"] = moved });
                Assert.Equal("ok", plain["status"]?.ToString());
                var dry = await CallAsync("genexus_properties", new JObject
                {
                    ["action"] = "move", ["name"] = moved, ["type"] = "Procedure", ["destination"] = path, ["destKind"] = "Folder", ["dryRun"] = true
                });
                Assert.Equal("DryRun", Code(dry));
                Assert.True(dry["result"]?["canApply"]?.Value<bool>() == true);
                string token = dry["result"]?["versionToken"]?.ToString() ?? string.Empty;
                Assert.NotEmpty(token);

                var noToken = await CallAsync("genexus_properties", new JObject
                {
                    ["action"] = "move", ["name"] = moved, ["type"] = "Procedure", ["destination"] = path, ["destKind"] = "Folder"
                });
                Assert.Equal("BaseVersionRequired", Code(noToken));

                var stale = await CallAsync("genexus_properties", new JObject
                {
                    ["action"] = "move", ["name"] = moved, ["type"] = "Procedure", ["destination"] = path, ["destKind"] = "Folder", ["baseVersion"] = "0:stale"
                });
                Assert.Equal("VersionConflict", Code(stale));

                var move = await CallAsync("genexus_properties", new JObject
                {
                    ["action"] = "move", ["name"] = moved, ["type"] = "Procedure", ["destination"] = path, ["destKind"] = "Folder", ["baseVersion"] = token
                });
                Assert.Equal("ObjectMovedAndVerified", Code(move));
                Assert.Equal(path, move["result"]?["toPath"]?.ToString());
                Assert.True(move["result"]?["verification"]?["freshRead"]?.Value<bool>() == true);
                Assert.True(move["result"]?["verification"]?["partsIdentical"]?.Value<bool>() == true);
                Assert.Empty((JArray)move["result"]!["implicitOperations"]!);
            }
            finally
            {
                await DeleteQuietlyAsync(moved, "Procedure");
                await DeleteQuietlyAsync(created, "Procedure");
                await DeleteQuietlyAsync(refused, "Procedure");
                await DeleteQuietlyAsync(folder, "Folder");
                await DeleteQuietlyAsync(module, "Module");
            }
        }
    }
}
