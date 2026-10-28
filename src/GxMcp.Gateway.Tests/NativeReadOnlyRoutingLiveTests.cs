using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

/// <summary>Read-only SDK oracle for candidate native routes. No authoring, builds or version activation.</summary>
[Trait("Category", "ProcessSmoke")]
[Trait("Category", "LiveNativeReadOnly")]
public sealed class NativeReadOnlyRoutingLiveTests : IClassFixture<LiveGatewayHarness>
{
    private readonly LiveGatewayHarness _harness;

    public NativeReadOnlyRoutingLiveTests(LiveGatewayHarness harness) => _harness = harness;

    [LiveKbFact]
    public async Task CaptureSdkContracts_ListsInspectEnvironmentsAndVersions()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        string canonical = Path.GetFullPath(kb).TrimEnd(Path.DirectorySeparatorChar);
        Assert.True(canonical.StartsWith(@"C:\KBs\", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(canonical).Contains("teste", StringComparison.OrdinalIgnoreCase),
            "This oracle requires an explicitly verified disposable C:/KBs/*teste* KB.");
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_READ_ORACLE_DIR to a new directory under C:/tmp.");
        string directory = Path.GetFullPath(output);
        Assert.StartsWith(@"C:\tmp\", directory, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(directory), "Refusing to overwrite an earlier oracle capture.");
        Directory.CreateDirectory(directory);
        string connection = Path.Combine(canonical, "knowledgebase.connection");
        byte[] before = File.ReadAllBytes(connection);
        try
        {
            JObject versions = await CaptureAsync(directory, "versions", "genexus_kb_version",
                new JObject { ["action"] = "list" });
            JObject versionResult = versions["result"] as JObject ?? versions;
            Assert.IsType<JArray>(versionResult["versions"]);
            Assert.False(string.IsNullOrWhiteSpace(versionResult["activeVersion"]?.ToString()));
            Assert.Equal("sdk:KBVersion", versionResult["source"]?.ToString());

            JObject environments = await CaptureAsync(directory, "environments", "genexus_kb",
                new JObject { ["action"] = "list_environments" });
            Assert.IsType<JArray>(environments["environments"]);
            await CaptureAsync(directory, "environment", "genexus_kb",
                new JObject { ["action"] = "get_environment" });

            JObject first = await CaptureAsync(directory, "list-first", "genexus_list_objects",
                ListArgs(0));
            Assert.True(first["count"]!.Value<int>() > 0);
            Assert.Equal(0, first["offset"]!.Value<int>());
            var rows = Assert.IsType<JArray>(first["results"]);
            string name = rows[0]!["name"]!.Value<string>()!;
            Assert.Equal("Procedure", rows[0]!["type"]?.ToString());
            if (first["hasMore"]?.Value<bool>() == true)
            {
                int next = first["nextOffset"]!.Value<int>();
                JObject second = await CaptureAsync(directory, "list-next", "genexus_list_objects", ListArgs(next));
                Assert.Equal(first["total"], second["total"]);
                Assert.Equal(next, second["offset"]!.Value<int>());
            }
            await CaptureAsync(directory, "list-beyond", "genexus_list_objects", ListArgs(int.MaxValue));
            JObject inspected = await CaptureAsync(directory, "inspect", "genexus_inspect", InspectArgs(name));
            Assert.Equal(name, inspected["name"]?.ToString());
            Assert.Equal("Procedure", inspected["type"]?.ToString());
            Assert.Equal("minimal", inspected["projection"]?.ToString());
            Assert.IsType<JObject>(inspected["identity"]);
            Assert.IsType<JArray>(inspected["availableParts"]);
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(connection));
        }
    }

    [LiveKbFact]
    public async Task NativeCurrentEnvironment_MatchesSdkWithoutChangingActiveVersion()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        Assert.StartsWith(@"C:\KBs\KBTesteTimestamp_", Path.GetFullPath(kb), StringComparison.OrdinalIgnoreCase);
        string executable = Environment.GetEnvironmentVariable("GXMCP_NATIVE_CANDIDATE_EXE")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_CANDIDATE_EXE to the current built engine.");
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set a new local oracle output directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output), "Refusing to overwrite prior evidence.");
        Directory.CreateDirectory(output);
        JObject versions = await CaptureAsync(output, "versions-before", "genexus_kb_version", new JObject { ["action"] = "list" });
        Assert.Equal("KBTeste", versions["result"]?["activeVersion"]?.ToString());
        JObject sdk = await CaptureAsync(output, "sdk-environment", "genexus_kb", new JObject { ["action"] = "get_environment" });
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        // The recorded isolated timestamp fixture's trunk is Design model 1 / KB version 0.
        // Never activate a version to make this assertion pass.
        foreach (string arg in new[] { "--diagnostics=json", "kb-environment", kb, "--design-model", "1", "--kb-version", "0" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native engine did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            File.WriteAllText(Path.Combine(output, "native.stdout.json"), await stdout);
            File.WriteAllText(Path.Combine(output, "native.stderr.log"), await stderr);
            Assert.Equal(0, process.ExitCode);
            JObject native = JObject.Parse(await stdout);
            Assert.Equal(sdk["environment"], native["environment"]);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        JObject after = await CaptureAsync(output, "versions-after", "genexus_kb_version", new JObject { ["action"] = "list" });
        Assert.True(JToken.DeepEquals(versions["result"], after["result"]), "The active version and version tree must remain unchanged.");
    }

    [LiveKbFact]
    public async Task PackagedCurrentEnvironment_UsesExpectedBackendAndMatchesSdk()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        Assert.StartsWith(@"C:\KBs\KBTesteTimestamp_", Path.GetFullPath(kb), StringComparison.OrdinalIgnoreCase);
        string expected = Environment.GetEnvironmentVariable("GXMCP_NATIVE_EXPECT_BACKEND")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_EXPECT_BACKEND to sdk, auto or gxengine.");
        Assert.Contains(expected, new[] { "sdk", "auto", "gxengine" });
        string oracle = Environment.GetEnvironmentVariable("GXMCP_NATIVE_SDK_ORACLE_FILE")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_SDK_ORACLE_FILE to the retained SDK oracle.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(oracle), StringComparison.OrdinalIgnoreCase);
        var captured = JObject.Parse(File.ReadAllText(oracle));
        JObject sdk = LiveGatewayHarness.ParseToolPayload((JObject)captured["response"]!)!;
        Assert.NotNull(sdk);
        Assert.Null(sdk["engine"]);
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set a fresh output directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output), "Refusing to overwrite prior evidence.");
        Directory.CreateDirectory(output);
        string connection = Path.Combine(kb, "knowledgebase.connection");
        byte[] before = File.ReadAllBytes(connection);
        try
        {
            JObject versions = await CaptureAsync(output, "versions-before", "genexus_kb_version", new JObject { ["action"] = "list" }, expected == "gxengine");
            JObject environment = await CaptureAsync(output, "environment", "genexus_kb",
                new JObject { ["action"] = "get_environment" }, expected == "gxengine");
            Assert.Equal(sdk["environment"], environment["environment"]);
            if (expected == "gxengine")
            {
                Assert.Equal("genexus_kb.get_environment", environment["engine"]?["operation"]?.ToString());
                Assert.Equal("udm_current_environment", environment["engine"]?["capability"]?.ToString());
            }
            JObject repeated = await CaptureAsync(output, "environment-again", "genexus_kb",
                new JObject { ["action"] = "get_environment" }, expected == "gxengine");
            Assert.Equal(environment["environment"], repeated["environment"]);
            JObject after = await CaptureAsync(output, "versions-after", "genexus_kb_version", new JObject { ["action"] = "list" }, expected == "gxengine");
            Assert.True(JToken.DeepEquals(versions["result"], after["result"]), "Read-only routing must not change the active version or version tree.");
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(connection));
        }
    }

    [LiveKbFact]
    public async Task PackagedProcedureList_UsesExpectedBackendAndMatchesSdk()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        Assert.StartsWith(@"C:\KBs\KBTesteTimestamp_", Path.GetFullPath(kb), StringComparison.OrdinalIgnoreCase);
        string expected = Environment.GetEnvironmentVariable("GXMCP_NATIVE_EXPECT_BACKEND")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_EXPECT_BACKEND to sdk, auto or gxengine.");
        Assert.Contains(expected, new[] { "sdk", "auto", "gxengine" });
        string oracleDirectory = Environment.GetEnvironmentVariable("GXMCP_NATIVE_SDK_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_SDK_ORACLE_DIR to the retained SDK oracle directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(oracleDirectory), StringComparison.OrdinalIgnoreCase);
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set a fresh output directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output), "Refusing to overwrite prior evidence.");
        Directory.CreateDirectory(output);
        string connection = Path.Combine(kb, "knowledgebase.connection");
        byte[] before = File.ReadAllBytes(connection);
        try
        {
            JObject versions = await CaptureAsync(output, "versions-before", "genexus_kb_version", new JObject { ["action"] = "list" }, expected == "gxengine");
            // Nonempty pages are native candidates; the beyond-end page keeps the SDK typesAvailable hint.
            foreach (var (label, native) in new[] { ("list-first", true), ("list-next", true), ("list-beyond", false) })
            {
                var captured = JObject.Parse(File.ReadAllText(Path.Combine(oracleDirectory, label + ".json")));
                JObject sdk = LiveGatewayHarness.ParseToolPayload((JObject)captured["response"]!)!;
                JObject actual = await CaptureAsync(output, label, "genexus_list_objects",
                    (JObject)captured["arguments"]!, native && expected == "gxengine");
                Assert.True(JToken.DeepEquals(Comparable(sdk), Comparable(actual)), label + " differs from the SDK oracle.");
            }
            JObject after = await CaptureAsync(output, "versions-after", "genexus_kb_version", new JObject { ["action"] = "list" }, expected == "gxengine");
            Assert.True(JToken.DeepEquals(versions["result"], after["result"]), "Read-only routing must not change the active version or version tree.");
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(connection));
        }
    }

    /// <summary>Retains SDK minimal inspections of the named Procedures (GXMCP_NATIVE_INSPECT_NAMES, comma-separated).</summary>
    [LiveKbFact]
    public async Task CaptureSdkInspectMinimal_ForNamedProcedures()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        Assert.StartsWith(@"C:\KBs\KBTesteTimestamp_", Path.GetFullPath(kb), StringComparison.OrdinalIgnoreCase);
        string[] names = (Environment.GetEnvironmentVariable("GXMCP_NATIVE_INSPECT_NAMES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(names);
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set a fresh output directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output), "Refusing to overwrite prior evidence.");
        Directory.CreateDirectory(output);
        string connection = Path.Combine(kb, "knowledgebase.connection");
        byte[] before = File.ReadAllBytes(connection);
        try
        {
            foreach (string name in names)
            {
                JObject inspected = await CaptureAsync(output, "inspect-" + name, "genexus_inspect", InspectArgs(name));
                Assert.Equal("minimal", inspected["projection"]?.ToString());
            }
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(connection));
        }
    }

    [LiveKbFact]
    public async Task PackagedInspectMinimal_UsesExpectedBackendAndMatchesSdk()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        Assert.StartsWith(@"C:\KBs\KBTesteTimestamp_", Path.GetFullPath(kb), StringComparison.OrdinalIgnoreCase);
        string expected = Environment.GetEnvironmentVariable("GXMCP_NATIVE_EXPECT_BACKEND")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_EXPECT_BACKEND to sdk, auto or gxengine.");
        Assert.Contains(expected, new[] { "sdk", "auto", "gxengine" });
        string oracleDirectory = Environment.GetEnvironmentVariable("GXMCP_NATIVE_SDK_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_SDK_ORACLE_DIR to the retained SDK inspections.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(oracleDirectory), StringComparison.OrdinalIgnoreCase);
        string[] oracles = Directory.GetFiles(oracleDirectory, "inspect-*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(oracles);
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set a fresh output directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output), "Refusing to overwrite prior evidence.");
        Directory.CreateDirectory(output);
        string connection = Path.Combine(kb, "knowledgebase.connection");
        byte[] before = File.ReadAllBytes(connection);
        try
        {
            bool native = expected == "gxengine";
            JObject versions = await CaptureAsync(output, "versions-before", "genexus_kb_version", new JObject { ["action"] = "list" }, native);
            foreach (string oracle in oracles)
            {
                var captured = JObject.Parse(File.ReadAllText(oracle));
                Assert.Equal("genexus_inspect", captured["tool"]?.ToString());
                JObject sdk = LiveGatewayHarness.ParseToolPayload((JObject)captured["response"]!)!;
                Assert.Null(sdk["engine"]);
                string label = Path.GetFileNameWithoutExtension(oracle);
                JObject actual = await CaptureAsync(output, label, "genexus_inspect", (JObject)captured["arguments"]!, native);
                if (native) Assert.Equal("udm_procedure_inspect", actual["engine"]?["capability"]?.ToString());
                Assert.True(JToken.DeepEquals(Comparable(sdk), Comparable(actual)), label + " differs from the SDK oracle.");
            }
            JObject after = await CaptureAsync(output, "versions-after", "genexus_kb_version", new JObject { ["action"] = "list" }, native);
            Assert.True(JToken.DeepEquals(versions["result"], after["result"]), "Read-only routing must not change the active version or version tree.");
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(connection));
        }
    }

    private static JObject InspectArgs(string name) => new()
    {
        ["name"] = name, ["type"] = "Procedure", ["projection"] = "minimal"
    };

    [LiveKbFact]
    public async Task PackagedKbVersionList_UsesExpectedBackendAndMatchesSdk()
    {
        string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") ?? "";
        Assert.StartsWith(@"C:\KBs\KBTesteTimestamp_", Path.GetFullPath(kb), StringComparison.OrdinalIgnoreCase);
        string expected = Environment.GetEnvironmentVariable("GXMCP_NATIVE_EXPECT_BACKEND")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_EXPECT_BACKEND to sdk, auto or gxengine.");
        Assert.Contains(expected, new[] { "sdk", "auto", "gxengine" });
        string oracle = Environment.GetEnvironmentVariable("GXMCP_NATIVE_SDK_ORACLE_FILE")
            ?? throw new InvalidOperationException("Set GXMCP_NATIVE_SDK_ORACLE_FILE to the retained SDK version list.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(oracle), StringComparison.OrdinalIgnoreCase);
        var captured = JObject.Parse(File.ReadAllText(oracle));
        Assert.Equal("genexus_kb_version", captured["tool"]?.ToString());
        JObject sdk = LiveGatewayHarness.ParseToolPayload((JObject)captured["response"]!)!;
        Assert.Null(sdk["engine"]);
        string output = Environment.GetEnvironmentVariable("GXMCP_NATIVE_READ_ORACLE_DIR")
            ?? throw new InvalidOperationException("Set a fresh output directory.");
        Assert.StartsWith(@"C:\tmp\", Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output), "Refusing to overwrite prior evidence.");
        Directory.CreateDirectory(output);
        string connection = Path.Combine(kb, "knowledgebase.connection");
        byte[] before = File.ReadAllBytes(connection);
        try
        {
            bool native = expected == "gxengine";
            JObject versions = await CaptureAsync(output, "versions", "genexus_kb_version", new JObject { ["action"] = "list" }, native);
            if (native)
            {
                Assert.Equal("genexus_kb_version.list", versions["engine"]?["operation"]?.ToString());
                Assert.Equal("udm_kb_version_list", versions["engine"]?["capability"]?.ToString());
            }
            Assert.True(JToken.DeepEquals(ComparableVersions(sdk, false), ComparableVersions(versions, native)),
                "The version list differs from the SDK oracle beyond its provenance labels.");
            JObject again = await CaptureAsync(output, "versions-again", "genexus_kb_version", new JObject { ["action"] = "list" }, native);
            Assert.True(JToken.DeepEquals(versions["result"], again["result"]), "Repeated reads must agree and change nothing.");
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(connection));
        }
    }

    /// <summary>
    /// <see cref="Comparable"/> after checking, then removing, the provenance labels a version list carries:
    /// the SDK names KBVersion properties and the native route names the stored entity it read.
    /// </summary>
    private static JObject ComparableVersions(JObject payload, bool native)
    {
        JObject copy = Comparable(payload);
        var result = (JObject)copy["result"]!;
        Assert.Equal(native ? EngineKbVersionListContract.Source : "sdk:KBVersion", result["source"]?.ToString());
        result.Remove("source");
        foreach (JObject version in ((JArray)result["versions"]!).Cast<JObject>())
        {
            Assert.Equal(native ? EngineKbVersionListContract.LastUpdateSource : "sdk:KBVersion.LastUpdate", version["lastUpdateSource"]?.ToString());
            Assert.Equal(native ? EngineKbVersionListContract.CreatedAtSource : "unavailable:sdk-KBVersion", version["createdAtSource"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(version["createdAtNote"]?.ToString()));
            version.Remove("lastUpdateSource");
            version.Remove("createdAtSource");
            version.Remove("createdAtNote");
        }
        return copy;
    }

    /// <summary>
    /// Removes per-call correlation, Worker telemetry and backend provenance, plus the response-size counter
    /// that necessarily grows with the provenance block; every contract field and the token limit remain.
    /// </summary>
    private static JObject Comparable(JObject payload)
    {
        var copy = (JObject)payload.DeepClone();
        copy.Remove("correlationId");
        copy.Remove("engine");
        var meta = copy["_meta"] as JObject;
        meta?.Remove("telemetry");
        (meta?["tokens"] as JObject)?.Remove("used");
        return copy;
    }

    private static JObject ListArgs(int offset) => new()
    {
        ["typeFilter"] = "Procedure", ["limit"] = 3, ["offset"] = offset,
        ["fields"] = new JArray("name", "type")
    };

    private async Task<JObject> CaptureAsync(string directory, string label, string tool, JObject args, bool nativeExpected = false)
    {
        JObject response = await _harness.CallToolAsync(tool, args);
        File.WriteAllText(Path.Combine(directory, label + ".json"), new JObject
        {
            ["tool"] = tool, ["arguments"] = args, ["response"] = response
        }.ToString());
        Assert.False(LiveGatewayHarness.IsToolError(response), tool + " failed; see the local oracle capture.");
        JObject? payload = LiveGatewayHarness.ParseToolPayload(response);
        Assert.NotNull(payload);
        Assert.Equal(nativeExpected, payload!["engine"]?["backend"]?.ToString() == "gxengine");
        return payload;
    }
}
