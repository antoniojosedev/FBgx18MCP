using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class EngineProcedureListRoutingTests : EngineRoutingTestBase
{
    private const string Args = "{\"typeFilter\":\"Procedure\",\"limit\":3,\"offset\":3,\"fields\":[\"name\",\"type\"]}";
    private const string Page = "{\"objects\":[" +
        "{\"name\":\"DCH55940100\",\"type\":\"Procedure\",\"last_update_ticks\":639276684890000000}," +
        "{\"name\":\"DCM37961700\",\"type\":\"Procedure\",\"last_update_ticks\":639276681750000000}," +
        "{\"name\":\"DCM55940100\",\"type\":\"Procedure\",\"last_update_ticks\":639276684970000000}]," +
        "\"total\":201,\"offset\":3,\"limit\":3}";

    [Theory]
    [InlineData("sdk", false)]
    [InlineData("auto", false)]
    [InlineData("gxengine", true)]
    public async Task ProcedurePage_RespectsPolicyAndMatchesTheSdkWorkerEnvelope(string backend, bool native)
    {
        var run = await RunAsync(backend, "error", Args, Page);
        Assert.Equal(native, run.Invoked);
        if (!native) { Assert.Null(run.Result); return; }
        JObject payload = PayloadOf(run.Result!);
        Assert.False(run.Result!["isError"]!.Value<bool>());
        Assert.Equal(3, payload["count"]!.Value<int>());
        Assert.Equal(201, payload["total"]!.Value<int>());
        Assert.Equal(6, payload["nextOffset"]!.Value<int>());
        Assert.Equal("DCH55940100", payload["results"]![0]!["name"]!.ToString());
        Assert.Equal(new JObject { ["offset"] = 3, ["limit"] = 3, ["returned"] = 3, ["total"] = 201, ["hasMore"] = true, ["nextOffset"] = 6 },
            payload["pagination"]);
        JObject aggregates = (JObject)payload["_meta"]!["aggregates"]!;
        Assert.Equal(new DateTime(2026, 10, 15, 13, 36, 15, DateTimeKind.Utc), aggregates["lastUpdate"]!["min"]!.Value<DateTime>());
        Assert.Equal(new DateTime(2026, 10, 15, 13, 41, 37, DateTimeKind.Utc), aggregates["lastUpdate"]!["max"]!.Value<DateTime>());
        Assert.Equal(3, aggregates["by_type"]!["Procedure"]!.Value<int>());
        Assert.Equal("DCH55940100", payload["_meta"]!["suggested_next"]!["args"]!["name"]!.ToString());
        Assert.NotNull(payload["_meta"]!["alternative_views"]);
        Assert.Equal("genexus_list_objects", payload["engine"]?["operation"]?.ToString());
        Assert.Contains("kb-list-procedures", run.Arguments);
        Assert.Contains("--offset 3 --limit 3 --design-model 4 --kb-version 2", run.Arguments);
    }

    [Theory]
    [InlineData("{\"typeFilter\":\"Transaction\",\"limit\":3,\"fields\":[\"name\"]}")]
    [InlineData("{\"typeFilter\":\"Procedure,Transaction\",\"limit\":3,\"fields\":[\"name\"]}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"fields\":[\"name\"]}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":101,\"fields\":[\"name\"]}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":3,\"offset\":-1,\"fields\":[\"name\"]}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":3}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":3,\"fields\":[\"name\",\"path\"]}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":3,\"fields\":[\"name\"],\"sort\":\"lastUpdate\"}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":3,\"fields\":[\"name\"],\"verbose\":true}")]
    [InlineData("{\"typeFilter\":\"Procedure\",\"limit\":3,\"fields\":[\"name\"],\"kb\":\"other\"}")]
    public async Task OutsideTheAdmittedPage_DoesNotStartNative(string args)
    {
        foreach (string fallback in new[] { "sdk", "error" })
        {
            var run = await RunAsync("gxengine", fallback, args, Page);
            Assert.False(run.Invoked);
            Assert.Equal(fallback == "error", run.Result != null);
        }
    }

    [Theory]
    [InlineData("{\"objects\":[],\"total\":201,\"offset\":3,\"limit\":3}")]
    [InlineData("{\"objects\":[{\"name\":\"A\",\"type\":\"Procedure\",\"last_update_ticks\":null}],\"total\":201,\"offset\":3,\"limit\":3}")]
    [InlineData("{\"objects\":[{\"name\":\"A\",\"type\":\"Transaction\",\"last_update_ticks\":null},{\"name\":\"B\",\"type\":\"Procedure\",\"last_update_ticks\":null},{\"name\":\"C\",\"type\":\"Procedure\",\"last_update_ticks\":null}],\"total\":201,\"offset\":3,\"limit\":3}")]
    [InlineData("{\"objects\":[],\"total\":201,\"offset\":0,\"limit\":3}")]
    [InlineData("{\"objects\":[],\"total\":201,\"offset\":3,\"limit\":3,\"extra\":1}")]
    public async Task MalformedOrEmptyNativePage_CannotClaimSuccess(string json)
    {
        var run = await RunAsync("gxengine", "error", Args, json);
        Assert.True(run.Invoked);
        Assert.True(run.Result!["isError"]!.Value<bool>());
        // A deliberate refusal, not an exception reported as an engine failure.
        Assert.Equal(EngineRoutedOperations.EngineOutsideSpaceCode, PayloadOf(run.Result)["error"]?.ToString());
        Assert.Null((await RunAsync("gxengine", "sdk", Args, json)).Result);
    }

    [Fact]
    public async Task PageBeyondTheEnd_IsRefusedSoTheSdkKeepsItsTypesAvailableHint()
    {
        const string beyond = "{\"typeFilter\":\"Procedure\",\"limit\":3,\"offset\":2147483647,\"fields\":[\"name\",\"type\"]}";
        var run = await RunAsync("gxengine", "error", beyond, "{\"objects\":[],\"total\":201,\"offset\":2147483647,\"limit\":3}");
        Assert.True(run.Invoked);
        Assert.Equal(EngineRoutedOperations.EngineOutsideSpaceCode, PayloadOf(run.Result!)["error"]?.ToString());
    }

    [Fact]
    public async Task ChangedActiveScope_CannotReturnNativeSuccess()
    {
        var run = await RunAsync("gxengine", "error", Args, Page, changeScope: true);
        Assert.True(run.Invoked);
        Assert.True(run.Result!["isError"]!.Value<bool>());
    }

    private sealed record Run(JObject? Result, bool Invoked, string Arguments);

    private async Task<Run> RunAsync(string backend, string fallback, string args, string json, bool changeScope = false)
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
                (kb, _) => Task.FromResult<EngineActiveModel?>(changeScope && ++reads > 1
                    ? new EngineActiveModel(kb, 1, 0, "trunk")
                    : new EngineActiveModel(kb, 4, 2, "branch")));
            JObject? result = await operations.TryDispatchAsync("genexus_list_objects", null, JObject.Parse(args), directory, "kb-alias", CancellationToken.None);
            string argumentFile = Path.Combine(directory, "args.txt");
            return new Run(result, File.Exists(argumentFile), File.Exists(argumentFile) ? File.ReadAllText(argumentFile) : "");
        }
        finally { TryDeleteDirectory(directory); }
    }
}
