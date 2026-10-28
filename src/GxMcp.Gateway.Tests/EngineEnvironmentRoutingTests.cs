using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class EngineEnvironmentRoutingTests : EngineRoutingTestBase
{
    [Theory]
    [InlineData("sdk", "error", false)]
    [InlineData("auto", "error", false)]
    [InlineData("gxengine", "error", true)]
    [InlineData("gxengine", "sdk", true)]
    public async Task EnvironmentRoute_RespectsPolicyAndPinsTheActiveVersion(string backend, string fallback, bool native)
    {
        var run = await RunAsync(backend, fallback, "{\"action\":\"get_environment\"}", "{\"environment\":\".NET\"}");
        Assert.Equal(native, run.Invoked);
        if (!native) { Assert.Null(run.Result); return; }
        JObject payload = PayloadOf(run.Result!);
        Assert.False(run.Result!["isError"]!.Value<bool>());
        Assert.Equal(".NET", payload["environment"]?.ToString());
        Assert.Equal("gxengine", payload["engine"]?["backend"]?.ToString());
        Assert.Equal("genexus_kb.get_environment", payload["engine"]?["operation"]?.ToString());
        Assert.Contains("kb-environment", run.Arguments);
        Assert.Contains("--design-model 4 --kb-version 2", run.Arguments);
    }

    [Theory]
    [InlineData("sdk", "{\"action\":\"get_environment\",\"unexpected\":true}", false)]
    [InlineData("error", "{\"action\":\"get_environment\",\"unexpected\":true}", true)]
    [InlineData("sdk", "{\"action\":\"get_environment\",\"kb\":\"other\"}", false)]
    [InlineData("error", "{\"action\":\"get_environment\",\"kb\":\"other\"}", true)]
    public async Task UnsupportedArguments_DoNotStartNativeAndRespectFallback(string fallback, string args, bool error)
    {
        var run = await RunAsync("gxengine", fallback, args, "{\"environment\":\".NET\"}");
        Assert.False(run.Invoked);
        Assert.Equal(error, run.Result != null);
        if (error) Assert.True(run.Result!["isError"]!.Value<bool>());
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("error")]
    public async Task UnknownOrChangingActiveScope_CannotReturnNativeSuccess(string fallback)
    {
        foreach (string state in new[] { "missing", "wrong-kb", "changed", "lost" })
        {
            var run = await RunAsync("gxengine", fallback, "{\"action\":\"get_environment\"}", "{\"environment\":\".NET\"}", state);
            Assert.Equal(state is "changed" or "lost", run.Invoked);
            Assert.Equal(fallback == "error", run.Result != null);
            if (run.Result != null) Assert.True(run.Result["isError"]!.Value<bool>());
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"environment\":17}")]
    [InlineData("{\"environment\":\"\"}")]
    [InlineData("{\"environment\":\"a\\nb\"}")]
    [InlineData("{\"environment\":\".NET\",\"unexpected\":true}")]
    public async Task MalformedNativePayload_CannotClaimSuccess(string json)
    {
        var run = await RunAsync("gxengine", "error", "{\"action\":\"get_environment\"}", json);
        Assert.True(run.Invoked);
        Assert.True(run.Result!["isError"]!.Value<bool>());
    }

    [Theory]
    [InlineData("set_environment")]
    [InlineData("list_environments")]
    [InlineData("open")]
    [InlineData("close")]
    [InlineData("get_startup")]
    public void UnrelatedKbActions_AreNotNativeConsumers(string action) =>
        Assert.Null(EngineRoutedOperations.EngineOperationKeyFor("genexus_kb", action));

    private sealed record Run(JObject? Result, bool Invoked, string Arguments);

    private async Task<Run> RunAsync(string backend, string fallback, string args, string json, string state = "stable")
    {
        string directory = NewTempDir();
        try
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
            string stdout = Path.Combine(directory, "stdout.json");
            File.WriteAllText(stdout, json);
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
            JObject? result = await operations.TryDispatchAsync("genexus_kb", "get_environment", JObject.Parse(args), directory, "kb-alias", CancellationToken.None);
            string argumentFile = Path.Combine(directory, "args.txt");
            return new Run(result, File.Exists(argumentFile), File.Exists(argumentFile) ? File.ReadAllText(argumentFile) : "");
        }
        finally { TryDeleteDirectory(directory); }
    }
}
