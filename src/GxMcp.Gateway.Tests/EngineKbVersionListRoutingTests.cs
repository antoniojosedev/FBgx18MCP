using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class EngineKbVersionListRoutingTests : EngineRoutingTestBase
{
    // Native kb-versions output captured on the isolated KBTesteTimestamp clone with branch 2 active.
    private const string BranchActive =
        "{\"activeVersion\":\"TimestampAuthorityBranch\",\"activeVersionId\":2,\"trunk\":\"KBTeste\",\"versions\":[" +
        "{\"description\":\"Disposable timestamp authority experiment only\",\"designModelId\":3,\"id\":1,\"isFrozen\":true,\"lastUpdateTicks\":639281936340000000,\"name\":\"TimestampAuthorityFreeze\",\"parent\":\"KBTeste\",\"parentId\":0,\"userName\":\"DESKTOP-ULH7AS3\\\\swluc\"}," +
        "{\"description\":\"Disposable timestamp authority experiment only\",\"designModelId\":4,\"id\":2,\"isFrozen\":false,\"lastUpdateTicks\":639281936350000000,\"name\":\"TimestampAuthorityBranch\",\"parent\":\"TimestampAuthorityFreeze\",\"parentId\":1,\"userName\":\"DESKTOP-ULH7AS3\\\\swluc\"}]}";

    [Theory]
    [InlineData("sdk", "error", false)]
    [InlineData("auto", "error", false)]
    [InlineData("gxengine", "error", true)]
    [InlineData("gxengine", "sdk", true)]
    public async Task VersionList_RespectsPolicyAndShapesTheWorkerList(string backend, string fallback, bool native)
    {
        var run = await RunAsync(backend, fallback, "{\"action\":\"list\"}", BranchActive);
        Assert.Equal(native, run.Invoked);
        if (!native) { Assert.Null(run.Result); return; }
        Assert.False(run.Result!["isError"]!.Value<bool>());
        JObject payload = PayloadOf(run.Result);
        Assert.Equal("ok", payload["status"]?.ToString());
        Assert.Equal("KbVersionListRetrieved", payload["code"]?.ToString());
        Assert.Equal("TimestampAuthorityBranch", payload["result"]?["activeVersion"]?.ToString());
        Assert.Equal(EngineKbVersionListContract.Source, payload["result"]?["source"]?.ToString());
        var versions = (JArray)payload["result"]!["versions"]!;
        Assert.Equal(new[] { "TimestampAuthorityFreeze", "TimestampAuthorityBranch" }, versions.Select(v => v["name"]!.ToString()));
        JToken freeze = versions[0], branch = versions[1];
        Assert.True(freeze["isFrozen"]!.Value<bool>());
        Assert.False(freeze["isBranch"]!.Value<bool>());
        Assert.False(freeze["isActive"]!.Value<bool>());
        Assert.Equal("KBTeste", freeze["parent"]?.ToString());
        Assert.True(branch["isBranch"]!.Value<bool>());
        Assert.True(branch["isActive"]!.Value<bool>());
        Assert.False(branch["isTrunk"]!.Value<bool>());
        Assert.Equal("TimestampAuthorityFreeze", branch["parent"]?.ToString());
        Assert.Equal(new DateTime(2026, 10, 21, 15, 33, 55, DateTimeKind.Utc), branch["lastUpdate"]!.Value<DateTime>().ToUniversalTime());
        Assert.Equal("DESKTOP-ULH7AS3\\swluc", branch["userName"]?.ToString());
        Assert.False(branch["createdAtAvailable"]!.Value<bool>());
        Assert.Null(branch["createdAt"]); // absent, as in the SDK-visible list
        Assert.Equal("genexus_kb_version.list", payload["engine"]?["operation"]?.ToString());
        Assert.Contains("kb-versions", run.Arguments);
        Assert.Contains("--design-model 4 --kb-version 2", run.Arguments);
    }

    [Theory]
    [InlineData("sdk", "{\"action\":\"list\",\"unexpected\":true}", false)]
    [InlineData("error", "{\"action\":\"list\",\"unexpected\":true}", true)]
    [InlineData("sdk", "{\"action\":\"list\",\"kb\":\"other\"}", false)]
    [InlineData("error", "{\"action\":\"list\",\"kb\":\"other\"}", true)]
    public async Task UnsupportedArguments_DoNotStartNativeAndRespectFallback(string fallback, string args, bool error)
    {
        var run = await RunAsync("gxengine", fallback, args, BranchActive);
        Assert.False(run.Invoked);
        Assert.Equal(error, run.Result != null);
        if (error) Assert.True(run.Result!["isError"]!.Value<bool>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("changed_objects")]
    [InlineData("freeze")]
    [InlineData("branch")]
    [InlineData("set_active")]
    [InlineData("revert")]
    public void OtherVersionActions_AreNotNativeConsumers(string? action) =>
        Assert.Null(EngineRoutedOperations.EngineOperationKeyFor("genexus_kb_version", action));

    [Theory]
    [InlineData("sdk")]
    [InlineData("error")]
    public async Task UnknownOrChangingActiveScope_CannotReturnNativeSuccess(string fallback)
    {
        foreach (string state in new[] { "missing", "wrong-kb", "changed", "lost" })
        {
            var run = await RunAsync("gxengine", fallback, "{\"action\":\"list\"}", BranchActive, state);
            Assert.Equal(state is "changed" or "lost", run.Invoked);
            Assert.Equal(fallback == "error", run.Result != null);
            if (run.Result != null) Assert.True(run.Result["isError"]!.Value<bool>());
        }
    }

    public static TheoryData<string> MalformedPayloads()
    {
        var data = new TheoryData<string> { "{}", "{\"activeVersion\":\"KBTeste\",\"activeVersionId\":2,\"trunk\":\"KBTeste\",\"versions\":[]}" };
        // Each mutation breaks one guarantee the shaped list relies on.
        foreach (var (from, to) in new[]
        {
            ("\"activeVersionId\":2", "\"activeVersionId\":0"),                 // differs from the Worker scope
            ("\"activeVersion\":\"TimestampAuthorityBranch\"", "\"activeVersion\":\"Other\""), // names an unlisted version
            ("\"parentId\":1,", "\"parentId\":7,"),                             // dangling parent
            ("\"parent\":\"TimestampAuthorityFreeze\"", "\"parent\":\"KBTeste\""), // parent name disagrees with id
            ("\"id\":2,", "\"id\":1,"),                                         // duplicated id
            ("\"isFrozen\":true", "\"isFrozen\":\"True\""),                     // wrong type
            ("\"userName\":\"DESKTOP-ULH7AS3\\\\swluc\"}]", "\"userName\":\"\"}]"), // empty user
            ("\"lastUpdateTicks\":639281936340000000", "\"lastUpdateTicks\":-1"), // out of range
            (",\"trunk\":\"KBTeste\"", ",\"trunk\":\"KBTeste\",\"extra\":1"),    // unexpected envelope key
            ("\"designModelId\":3,", "\"designModelId\":3,\"extra\":1,"),       // unexpected version key
        })
        {
            Assert.Contains(from, BranchActive);
            data.Add(BranchActive.Replace(from, to));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public async Task MalformedNativeList_CannotClaimSuccess(string json)
    {
        var run = await RunAsync("gxengine", "error", "{\"action\":\"list\"}", json);
        Assert.True(run.Invoked);
        Assert.True(run.Result!["isError"]!.Value<bool>());
    }

    private sealed record Run(JObject? Result, bool Invoked, string Arguments);

    private async Task<Run> RunAsync(string backend, string fallback, string args, string json, string state = "stable")
    {
        string directory = NewTempDir();
        try
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
            File.WriteAllText(Path.Combine(directory, "stdout.json"), json);
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(directory, "engine.cmd",
                "@echo off\r\necho %* > \"%~dp0args.txt\"\r\ntype \"%~dp0stdout.json\"\r\nexit /b 0\r\n"));
            int reads = 0;
            var operations = new EngineRoutedOperations(() => null, _ => { },
                (payload, error, _, _) => new JObject
                {
                    ["isError"] = error,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() })
                },
                (kb, _) =>
                {
                    reads++;
                    EngineActiveModel? snapshot = state switch
                    {
                        "missing" => null,
                        "lost" when reads > 1 => null,
                        "wrong-kb" => new EngineActiveModel(Path.Combine(kb, "other"), 4, 2, "branch"),
                        "changed" when reads > 1 => new EngineActiveModel(kb, 1, 0, "trunk"),
                        _ => new EngineActiveModel(kb, 4, 2, "branch")
                    };
                    return Task.FromResult(snapshot);
                });
            string? action = JObject.Parse(args)["action"]?.ToString();
            JObject? result = await operations.TryDispatchAsync("genexus_kb_version", action, JObject.Parse(args), directory, "kb-alias", CancellationToken.None);
            string argumentFile = Path.Combine(directory, "args.txt");
            return new Run(result, File.Exists(argumentFile), File.Exists(argumentFile) ? File.ReadAllText(argumentFile) : "");
        }
        finally { TryDeleteDirectory(directory); }
    }
}
