using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class EngineProcedureInspectRoutingTests : EngineRoutingTestBase
{
    // Native kb-inspect output captured on the isolated KBTesteTimestamp clone.
    private const string Library =
        "{\"availableParts\":[\"Documentation\",\"Rules\",\"Source\",\"Variables\"],\"description\":\"Add Device Groups\",\"entityId\":1," +
        "\"guid\":\"83a55789-716f-4c13-a03c-215b167050e8\",\"lastUpdateTicks\":636163074130000000,\"name\":\"AddDeviceGroups\"," +
        "\"path\":\"Root Module/GeneXus/Common/Notifications/AddDeviceGroups\",\"userName\":\"GeneXus\"}";

    private const string Args = "{\"name\":\"AddDeviceGroups\",\"type\":\"Procedure\",\"projection\":\"minimal\"}";

    [Theory]
    [InlineData("sdk", "error", false)]
    [InlineData("auto", "error", false)]
    [InlineData("gxengine", "error", true)]
    [InlineData("gxengine", "sdk", true)]
    public async Task Inspect_RespectsPolicyAndShapesTheWorkerMinimalProjection(string backend, string fallback, bool native)
    {
        var run = await RunAsync(backend, fallback, Args, Library);
        Assert.Equal(native, run.Invoked);
        if (!native) { Assert.Null(run.Result); return; }
        Assert.False(run.Result!["isError"]!.Value<bool>());
        JObject payload = PayloadOf(run.Result);
        Assert.Equal("AddDeviceGroups", payload["name"]?.ToString());
        Assert.Equal("Procedure", payload["type"]?.ToString());
        Assert.Equal("83a55789-716f-4c13-a03c-215b167050e8", payload["identity"]?["guid"]?.ToString());
        Assert.Equal("84a12160-f59b-4ad7-a683-ea4481ac23e9-1", payload["identity"]?["entityKey"]?.ToString());
        Assert.Equal(1, payload["identity"]?["entityId"]?.Value<int>());
        Assert.Equal("Root Module/GeneXus/Common/Notifications/AddDeviceGroups", payload["identity"]?["path"]?.ToString());
        Assert.Equal("Add Device Groups", payload["description"]?.ToString());
        Assert.Equal(new DateTime(2016, 12, 2, 20, 30, 13, DateTimeKind.Utc), payload["lifecycle"]!["lastUpdate"]!.Value<DateTime>().ToUniversalTime());
        Assert.Equal("GeneXus", payload["lifecycle"]?["lastModifiedBy"]?.ToString());
        Assert.Equal("minimal", payload["projection"]?.ToString());
        Assert.Equal(new[] { "Documentation", "Rules", "Source", "Variables" }, payload["availableParts"]!.ToObject<string[]>());
        Assert.Equal(EngineProcedureInspectContract.Hint, payload["hint"]?.ToString());
        Assert.Equal("genexus_inspect", payload["engine"]?["operation"]?.ToString());
        Assert.Contains("kb-inspect", run.Arguments);
        Assert.Contains("AddDeviceGroups --design-model 4 --kb-version 2", run.Arguments);
    }

    [Theory]
    [InlineData("{\"name\":\"AddDeviceGroups\",\"type\":\"Procedure\"}")]                                  // default projection
    [InlineData("{\"name\":\"AddDeviceGroups\",\"type\":\"Procedure\",\"projection\":\"standard\"}")]
    [InlineData("{\"name\":\"AddDeviceGroups\",\"projection\":\"minimal\"}")]                               // no type
    [InlineData("{\"name\":\"AddDeviceGroups\",\"type\":\"Transaction\",\"projection\":\"minimal\"}")]
    [InlineData("{\"name\":\"Ação\",\"type\":\"Procedure\",\"projection\":\"minimal\"}")]                    // non-ASCII
    [InlineData("{\"name\":\"AddDeviceGroups\",\"type\":\"Procedure\",\"projection\":\"minimal\",\"include\":[\"source\"]}")]
    [InlineData("{\"name\":\"AddDeviceGroups\",\"type\":\"Procedure\",\"projection\":\"minimal\",\"guid\":\"x\"}")]
    [InlineData("{\"name\":\"AddDeviceGroups\",\"type\":\"Procedure\",\"projection\":\"minimal\",\"kb\":\"other\"}")]
    public async Task UnsupportedRequests_DoNotStartNativeAndRespectFallback(string args)
    {
        foreach (string fallback in new[] { "sdk", "error" })
        {
            var run = await RunAsync("gxengine", fallback, args, Library);
            Assert.False(run.Invoked);
            Assert.Equal(fallback == "error", run.Result != null);
            if (run.Result != null) Assert.True(run.Result["isError"]!.Value<bool>());
        }
    }

    [Fact]
    public async Task KbWithAMemoryStore_StaysOnTheSdk()
    {
        var run = await RunAsync("gxengine", "sdk", Args, Library, withMemoryStore: true);
        Assert.False(run.Invoked);
        Assert.Null(run.Result);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("error")]
    public async Task UnknownOrChangingActiveScope_CannotReturnNativeSuccess(string fallback)
    {
        foreach (string state in new[] { "missing", "wrong-kb", "changed", "lost" })
        {
            var run = await RunAsync("gxengine", fallback, Args, Library, state: state);
            Assert.Equal(state is "changed" or "lost", run.Invoked);
            Assert.Equal(fallback == "error", run.Result != null);
            if (run.Result != null) Assert.True(run.Result["isError"]!.Value<bool>());
        }
    }

    public static TheoryData<string> MalformedPayloads()
    {
        var data = new TheoryData<string>
        {
            "{}",
            // A consistent case variant (name and path): the SDK's answer for it is not established.
            Library.Replace("\"name\":\"AddDeviceGroups\"", "\"name\":\"adddevicegroups\"")
                .Replace("/AddDeviceGroups\"", "/adddevicegroups\""),
        };
        // Each mutation breaks one guarantee the shaped inspection relies on.
        foreach (var (from, to) in new[]
        {
            ("\"guid\":\"83a55789-716f-4c13-a03c-215b167050e8\"", "\"guid\":\"x\""),    // malformed GUID
            ("\"entityId\":1,", "\"entityId\":0,"),                                     // not a stored id
            ("\"lastUpdateTicks\":636163074130000000", "\"lastUpdateTicks\":-1"),       // out of range
            ("/Notifications/AddDeviceGroups\"", "/Notifications/Other\""),             // path names another object
            ("\"userName\":\"GeneXus\"", "\"userName\":\"\""),                          // empty user
            ("\"Rules\",\"Source\"", "\"Rules\",\"rules\""),                            // duplicated part
            ("\"description\":\"Add Device Groups\"", "\"description\":7"),             // wrong type
            ("\"entityId\":1,", "\"entityId\":1,\"extra\":true,"),                      // unexpected key
        })
        {
            Assert.Contains(from, Library);
            data.Add(Library.Replace(from, to));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public async Task MalformedNativeInspection_CannotClaimSuccess(string json)
    {
        var run = await RunAsync("gxengine", "error", Args, json);
        Assert.True(run.Invoked);
        Assert.True(run.Result!["isError"]!.Value<bool>());
    }

    private sealed record Run(JObject? Result, bool Invoked, string Arguments);

    private async Task<Run> RunAsync(string backend, string fallback, string args, string json, string state = "stable", bool withMemoryStore = false)
    {
        string directory = NewTempDir();
        try
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
            File.WriteAllText(Path.Combine(directory, "stdout.json"), json);
            if (withMemoryStore)
            {
                Directory.CreateDirectory(Path.Combine(directory, ".gx", "memory"));
                File.WriteAllText(Path.Combine(directory, ".gx", "memory", "memory.jsonl"), "");
            }
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
            JObject? result = await operations.TryDispatchAsync("genexus_inspect", null, JObject.Parse(args), directory, "kb-alias", CancellationToken.None);
            string argumentFile = Path.Combine(directory, "args.txt");
            return new Run(result, File.Exists(argumentFile), File.Exists(argumentFile) ? File.ReadAllText(argumentFile) : "");
        }
        finally { TryDeleteDirectory(directory); }
    }
}
