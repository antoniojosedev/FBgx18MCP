using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Guards the real gx-engine routing seam (docs/engine_backend.md): with
    /// Engine.Backend=gxengine the registered consumers
    /// (genexus_query, genexus_lifecycle.build, genexus_lifecycle.rebuild) are
    /// served by the engine when the call is inside the admitted space, and are
    /// either handed back to the SDK path or refused under Fallback=error when it
    /// is not. `genexus_read` and `genexus_edit` are not capability consumers: the
    /// explicit deferral policy (EngineRoutedOperations.DeferredOperationCapabilityIds)
    /// holds them outside the engine (it cannot supply the SDK read
    /// identity/freshness/versionToken, nor persist a write to a live Knowledge Base
    /// database), so under gxengine they always refuse (error) or fall back (sdk),
    /// and under auto/sdk they never reach the engine path - never a silent no-op.
    ///
    /// Every stub engine here is a .cmd that echoes canned JSON, so the tests
    /// exercise the full Gateway↔process contract without cargo or a KB. The
    /// assembly disables test parallelization (TestCollections.cs), so the
    /// GXMCP_ENGINE_* environment mutation is race-free.
    /// </summary>
    public class EngineRoutedOperationTests : EngineRoutingTestBase
    {
        // ------------------------------------------------------- operation keys

        [Theory]
        [InlineData("genexus_read", null, "genexus_read")]
        [InlineData("genexus_query", null, "genexus_query")]
        [InlineData("genexus_edit", null, "genexus_edit")]
        [InlineData("genexus_lifecycle", "build", "genexus_lifecycle.build")]
        [InlineData("genexus_lifecycle", "rebuild", "genexus_lifecycle.rebuild")]
        [InlineData("genexus_lifecycle", "BUILD", "genexus_lifecycle.build")]
        [InlineData("genexus_lifecycle", "status", null)]
        [InlineData("genexus_lifecycle", null, null)]
        [InlineData("genexus_create_object", null, null)]
        [InlineData("genexus_whoami", null, null)]
        // #650/#651: the location-bearing reads are SDK-Worker-only; the engine query answers name/type alone.
        [InlineData("genexus_list_objects", null, null)]
        [InlineData("genexus_object_context", null, null)]
        public void OperationKey_OnlyRoutedConsumersAreMapped(string tool, string? action, string? expected)
        {
            Assert.Equal(expected, EngineRoutedOperations.EngineOperationKeyFor(tool, action));
        }

        [Theory]
        [InlineData("genexus_read", "udm_storage_access")]
        [InlineData("genexus_query", "udm_type_query")]
        [InlineData("genexus_edit", "udm_storage_access")]
        [InlineData("genexus_lifecycle.build", "parallel_rebuild_all")]
        [InlineData("genexus_lifecycle.rebuild", "parallel_rebuild_all")]
        [InlineData("genexus_unknown", null)]
        [InlineData("", null)]
        public void CapabilityId_ComesFromTheRegistryOrTheDeferralPolicy(string operationKey, string? expected)
        {
            Assert.Equal(expected, EngineRoutedOperations.EngineCapabilityIdForOperation(operationKey));
        }

        // ---------------------------------------------------------- gate: sdk

        [Theory]
        [InlineData("genexus_read", null, @"{ ""name"": ""Alpha"", ""part"": ""Source"" }")]
        [InlineData("genexus_lifecycle", "build", @"{ ""action"": ""build"", ""wait_until_done"": true }")]
        [InlineData("genexus_lifecycle", "rebuild", @"{ ""action"": ""rebuild"", ""wait_until_done"": true }")]
        public async Task WithSdkBackend_TheEngineIsNeverInvoked(string tool, string? action, string args)
        {
            string tempDir = NewTempDir();
            try
            {
                string ranMarker = Path.Combine(tempDir, "ran.txt");
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\necho {\"name\":\"Alpha\"}\r\n");
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, "sdk");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync(tool, action, args, tempDir);

                Assert.Null(result);
                Assert.False(File.Exists(ranMarker), "the SDK backend must never start gx-engine");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("build")]
        [InlineData("rebuild")]
        public async Task DefaultBackend_AdmittedBuildUsesEngineWithoutBackendOptIn(string action)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                    CreateStub(tempDir, "engine.cmd", BuildReportStubBody));

                var result = await DispatchAsync("genexus_lifecycle", action,
                    new JObject { ["action"] = action, ["wait_until_done"] = true }.ToString(), tempDir);

                Assert.NotNull(result);
                Assert.False(result!["isError"]!.Value<bool>());
                Assert.Equal("gxengine", PayloadOf(result)["engine"]?["backend"]?.ToString());
                Assert.Equal("genexus_lifecycle." + action, PayloadOf(result)["engine"]?["operation"]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData(null, "genexus_edit", null, @"{ ""name"": ""Alpha"", ""content"": ""// changed"" }")]
        [InlineData(null, "genexus_lifecycle", "build", @"{ ""action"": ""build"" }")]
        [InlineData(null, "genexus_lifecycle", "build", @"{ ""action"": ""build"", ""wait_until_done"": true, ""target"": ""Alpha"" }")]
        [InlineData("auto", "genexus_read", null, @"{ ""name"": ""Alpha"", ""part"": ""Source"" }")]
        [InlineData("auto", "genexus_query", null, @"{ ""query"": ""Alpha"" }")]
        [InlineData("auto", "genexus_edit", null, @"{ ""name"": ""Alpha"", ""content"": ""// changed"" }")]
        [InlineData("auto", "genexus_lifecycle", "build", @"{ ""action"": ""build"" }")]
        [InlineData("auto", "genexus_lifecycle", "build", @"{ ""action"": ""build"", ""wait_until_done"": true, ""target"": ""Alpha"" }")]
        public async Task OperationsOutsideTheBackendSpaceStayOnSdkWithoutInvokingEngine(
            string? backend, string tool, string? action, string args)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                string marker = Path.Combine(tempDir, "ran.txt");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                    CreateStub(tempDir, "engine.cmd", "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\n" + BuildReportStubBody));

                Assert.Null(await DispatchAsync(tool, action, args, tempDir));
                Assert.False(File.Exists(marker));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task DefaultBackend_MissingEngineFallsBackBeforeBuildStarts()
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, Path.Combine(tempDir, "missing.exe"));

                Assert.Null(await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("{}", "EngineCommandFailed", false)]
        [InlineData("{\"status\":\"failed\",\"errors\":[\"unsupported object\"]}", "EngineBuildFailed", false)]
        [InlineData("{\"status\":\"partial\",\"errors\":[]}", "EngineBuildPartial", false)]
        [InlineData("{}", "EngineCommandFailed", true)]
        [InlineData("{\"status\":\"failed\",\"errors\":[\"unsupported object\"]}", "EngineBuildFailed", true)]
        [InlineData("{\"status\":\"partial\",\"errors\":[]}", "EngineBuildPartial", true)]
        public async Task DefaultBackend_StartedBuildNeverFallsBackToSdk(string report, string error, bool targeted)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                    CreateStub(tempDir, "engine.cmd", "@echo off\r\necho " + report + "\r\nexit /b 1\r\n"));

                var args = new JObject { ["action"] = "build", ["wait_until_done"] = true };
                if (targeted)
                {
                    args["target"] = "Alpha";
                    args["mode"] = "compile_check";
                    args["callers"] = false;
                }
                var result = await DispatchAsync("genexus_lifecycle", "build", args.ToString(), tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                Assert.Equal(error, PayloadOf(result)["error"]?.ToString());
                if (report == "{}")
                    Assert.True(PayloadOf(result)["reconciliationRequired"]!.Value<bool>());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // -------------------------------------------------------------- query

        [Theory]
        [InlineData(null)] // The default must route without an explicit backend opt-in.
        [InlineData("gxengine")]
        public async Task Query_ServedByTheEngine_ProjectsResultsAndPassesTheFilters(string? backend)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\n" +
                    "echo %* > \"%~dp0args.txt\"\r\n" +
                    "echo {\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"},{\"name\":\"Beta\",\"type\":\"Procedure\"}],\"total\":2,\"truncated\":false}\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_query", null,
                    @"{ ""query"": ""type:Procedure"", ""limit"": 10, ""fields"": [""name"", ""type""] }", tempDir);

                Assert.NotNull(result);
                var payload = PayloadOf(result!);
                var results = (JArray)payload["results"]!;
                Assert.Equal(2, results.Count);
                Assert.Equal("Alpha", results[0]!["name"]?.ToString());
                Assert.Equal("Procedure", results[0]!["type"]?.ToString());
                Assert.Equal(2, payload["total"]!.Value<int>());
                Assert.Equal(10, payload["pagination"]!["limit"]!.Value<int>());
                Assert.Equal("genexus_query", payload["engine"]?["operation"]?.ToString());
                Assert.Equal("udm_type_query", payload["engine"]?["capability"]?.ToString());

                string argsSeen = File.ReadAllText(Path.Combine(tempDir, "args.txt"));
                Assert.Contains("kb-query", argsSeen);
                Assert.Contains("--type", argsSeen);
                Assert.Contains("Procedure", argsSeen);
                Assert.Contains("--limit 10", argsSeen);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Query_FuzzyIntent_FallsBackToTheSdkInsteadOfAnsweringExactOnly()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho {\"objects\":[],\"total\":0,\"truncated\":false}\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_query", null,
                    @"{ ""query"": ""Customer"", ""exactMatch"": false }", tempDir);

                Assert.Null(result);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // --------------------------------------------------------------- edit

        [Fact]
        public async Task Edit_IsRefusedUnderErrorFallback_BecauseEngineWritesWouldNotPersist()
        {
            string tempDir = NewTempDir();
            try
            {
                string ranMarker = Path.Combine(tempDir, "ran.txt");
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\n" +
                    "echo {\"status\":\"ok\",\"name\":\"Alpha\",\"part\":\"SOURCE\",\"version_id\":7,\"created\":false,\"bytes\":6}\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");

                var result = await DispatchAsync("genexus_edit", null,
                    @"{ ""name"": ""Alpha"", ""part"": ""Source"", ""content"": ""// x\n"" }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
                Assert.Equal("udm_storage_access", payload["engine"]?["capability"]?.ToString());
                Assert.Equal("genexus_edit", payload["engine"]?["operation"]?.ToString());
                Assert.Contains("not persisted", payload["message"]?.ToString());
                Assert.False(File.Exists(ranMarker),
                    "an edit the engine cannot persist must never be reported as saved");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Edit_FallsBackToTheSdkUnderSdkFallback()
        {
            string tempDir = NewTempDir();
            try
            {
                string ranMarker = Path.Combine(tempDir, "ran.txt");
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\necho {\"status\":\"ok\"}\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_edit", null,
                    @"{ ""name"": ""Alpha"", ""part"": ""Source"", ""content"": ""x"" }", tempDir);

                Assert.Null(result);
                Assert.False(File.Exists(ranMarker),
                    "the SDK path serves edits while engine writes are held back");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------- read/edit deferral policy

        /// <summary>
        /// The deferral is routing policy keyed on the backend and fallback only: it refuses
        /// (with the original udm_storage_access id) only under gxengine + Fallback=error,
        /// and in every other combination hands the call to the SDK without ever starting
        /// the engine. auto and sdk bypass it even with Fallback=error.
        /// </summary>
        [Theory]
        [InlineData("genexus_read", @"{ ""name"": ""Alpha"", ""part"": ""Source"" }")]
        [InlineData("genexus_edit", @"{ ""name"": ""Alpha"", ""part"": ""Source"", ""content"": ""x"" }")]
        public async Task ReadAndEditDeferral_FollowsBackendAndFallbackWithoutInvokingTheEngine(string tool, string args)
        {
            foreach (string? backend in new string?[] { null, "gxengine", "auto", "sdk" })
            {
                foreach (string fallback in new[] { "sdk", "error" })
                {
                    string tempDir = NewTempDir();
                    try
                    {
                        string ranMarker = Path.Combine(tempDir, "ran.txt");
                        SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                        SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                        SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd", "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\necho {\"status\":\"ok\"}\r\n"));

                        var result = await DispatchAsync(tool, null, args, tempDir);

                        string label = $"backend={backend ?? "(default)"} fallback={fallback}";
                        bool refusalExpected = backend is null or "gxengine" && fallback == "error";
                        if (refusalExpected)
                        {
                            Assert.True(result != null, label);
                            Assert.True(result!["isError"]!.Value<bool>(), label);
                            var payload = PayloadOf(result);
                            Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
                            Assert.Equal("udm_storage_access", payload["engine"]?["capability"]?.ToString());
                            Assert.Equal(tool, payload["engine"]?["operation"]?.ToString());
                        }
                        else
                        {
                            Assert.True(result == null, label);
                        }
                        Assert.False(File.Exists(ranMarker), "read/edit are never served by the engine: " + label);
                    }
                    finally
                    {
                        TryDeleteDirectory(tempDir);
                    }
                }
            }
        }

        // ----------------------------------------------------------- lifecycle

        [Fact]
        public async Task Lifecycle_BareBuild_RefusesUnderErrorFallbackWithTheBlockingHint()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", BuildReportStubBody);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");

                // No wait_until_done / estimate: the SDK path would answer with an
                // accepted async job, a contract the engine does not produce yet.
                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"" }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
                Assert.Contains("wait_until_done", payload["message"]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Lifecycle_BuildWithWaitUntilDone_IsServedAndShapesTheReport()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd", BuildReportStubBody);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.False(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("Success", payload["status"]?.ToString());
                Assert.Equal("build", payload["action"]?.ToString());
                Assert.Equal("parallel_rebuild_all", payload["engine"]?["capability"]?.ToString());
                Assert.Equal("genexus_lifecycle.build", payload["engine"]?["operation"]?.ToString());
                Assert.Equal(3, payload["report"]?["total_objects"]!.Value<int>());
                Assert.Equal(5, payload["report"]?["catalog_load_ms"]?.Value<int>());
                Assert.Equal(2, payload["report"]?["emitted_file_count"]!.Value<int>());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("transitive")]
        public async Task DefaultBackend_TargetCompileCheckBuildsOnlyTheCalleeClosure(string? callees)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, null);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho %* > \"%~dp0args.txt\"\r\n" + BuildReportStubBody));
                var args = new JObject
                {
                    ["action"] = "build", ["target"] = "Module.Alpha", ["mode"] = "compile_check",
                    ["callers"] = false, ["wait_until_done"] = true
                };
                if (callees != null) args["includeCallees"] = callees;
                var result = await DispatchAsync("genexus_lifecycle", "build", args.ToString(), tempDir);
                Assert.NotNull(result);
                Assert.False(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("target-closure", payload["engine"]?["scope"]?.ToString());
                Assert.Equal("Module.Alpha", payload["engine"]?["target"]?.ToString());
                string seen = File.ReadAllText(Path.Combine(tempDir, "args.txt"));
                Assert.Contains("--object Module.Alpha", seen);
                Assert.Contains("--closure", seen);
                Assert.Contains("build-targets", seen);
                Assert.DoesNotContain("--clean", seen);
            }
            finally { TryDeleteDirectory(tempDir); }
        }

        [Theory]
        [InlineData(@"{ 'target':'Alpha', 'mode':'compile_check' }")]
        [InlineData(@"{ 'target':'Alpha', 'mode':'compile_check', 'callers':true }")]
        [InlineData(@"{ 'target':'Alpha', 'mode':'compile_check', 'callers':false, 'includeCallees':'none' }")]
        [InlineData(@"{ 'target':'Alpha', 'mode':'compile_check', 'callers':false, 'includeCallees':'direct' }")]
        [InlineData(@"{ 'target':'../Alpha', 'mode':'compile_check', 'callers':false }")]
        [InlineData(@"{ 'target':'Procedure:Alpha', 'mode':'compile_check', 'callers':false }")]
        [InlineData(@"{ 'mode':'compile_check', 'callers':false }")]
        public async Task TargetCompileCheckOutsideTheExactClosureContractNeverStartsTheEngine(string input)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "ran.txt");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\n" + BuildReportStubBody));
                var args = JObject.Parse(input);
                args["action"] = "build";
                args["wait_until_done"] = true;
                Assert.Null(await DispatchAsync("genexus_lifecycle", "build", args.ToString(), tempDir));
                Assert.False(File.Exists(marker));
            }
            finally { TryDeleteDirectory(tempDir); }
        }

        [Fact]
        public async Task Lifecycle_Rebuild_PassesTheCleanFlagAndTheKbPath()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\n" +
                    "echo %* > \"%~dp0args.txt\"\r\n" +
                    "echo {\"read_ms\":1,\"spec_ms\":1,\"emit_ms\":1,\"compile_ms\":1,\"total_ms\":4,\"procedures_count\":0,\"transactions_count\":0,\"sdts_count\":0,\"webpanels_count\":0,\"total_objects\":0,\"errors\":[],\"warnings\":[],\"emitted_files\":[]}\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_lifecycle", "rebuild",
                    @"{ ""action"": ""rebuild"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                var payload = PayloadOf(result!);
                Assert.Equal("genexus_lifecycle.rebuild", payload["engine"]?["operation"]?.ToString());

                string argsSeen = File.ReadAllText(Path.Combine(tempDir, "args.txt"));
                Assert.Contains("rebuild-all", argsSeen);
                Assert.Contains("--clean", argsSeen);
                Assert.Contains("--kb", argsSeen);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Lifecycle_PartialBuild_IsNotSuccessAndNotMislabelledAsFailed()
        {
            // rebuild-all exits 3 when a run skipped web UI or refused an object. The run is
            // partial: never Success, and its counts reach the caller instead of a generic failure.
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\n" +
                    "echo {\"status\":\"partial\",\"read_ms\":1,\"spec_ms\":1,\"emit_ms\":1,\"compile_ms\":1,\"total_ms\":4,\"total_objects\":3,\"generated_count\":1,\"compiled_count\":1,\"skipped_count\":2,\"skipped_objects\":[{\"name\":\"Customer\",\"kind\":\"Transaction\",\"reason\":\"web UI, not generated\"},{\"name\":\"Invoice\",\"kind\":\"Transaction\",\"reason\":\"web UI, not generated\"}],\"errors\":[],\"warnings\":[],\"emitted_files\":[\"a.cs\"]}\r\n" +
                    "exit /b 3\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("Partial", payload["status"]?.ToString());
                Assert.Equal("EngineBuildPartial", payload["error"]?.ToString());
                Assert.DoesNotContain("error(s)", payload["message"]?.ToString());
                Assert.Equal(2, payload["report"]?["skipped_count"]!.Value<int>());
                Assert.Equal("Customer", payload["report"]?["skipped_objects"]?[0]?["name"]?.ToString());
                Assert.Equal(1, payload["report"]?["generated_count"]!.Value<int>());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Lifecycle_PartialBuild_FromTheRealEngineReport_IsErrorTrueWithItsCounts()
        {
            // Real rebuild-all output for KBTesteFix16 (a GX16 fixture): exit 3, status "partial",
            // no errors, 6 WebPanels skipped as web UI. The partial build must stay isError: true, so
            // a client that reads only isError cannot take it as done; the counts must reach it unchanged.
            string reportPath = FindUp("src", "GxMcp.Gateway.Tests", "Fixtures", "Engine", "rebuild_all_partial_kbtestefix16.json");
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\n" +
                    "type \"" + reportPath + "\"\r\n" +
                    "exit /b 3\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("Partial", payload["status"]?.ToString());
                Assert.Equal("EngineBuildPartial", payload["error"]?.ToString());
                Assert.Equal("gxengine", payload["engine"]?["backend"]?.ToString());
                Assert.Contains("6 web UI object(s) skipped, 0 without a generator, 0 refused", payload["message"]?.ToString());

                var report = payload["report"]!;
                Assert.Equal(103, report["total_objects"]!.Value<int>());
                Assert.Equal(8, report["sdts_count"]!.Value<int>());
                Assert.Equal(6, report["webpanels_count"]!.Value<int>());
                Assert.Equal(9, report["emitted_file_count"]!.Value<int>());
                Assert.Equal(8, report["generated_count"]!.Value<int>());
                Assert.Equal(8, report["compiled_count"]!.Value<int>());
                Assert.Equal(6, report["skipped_count"]!.Value<int>());
                Assert.Equal(0, report["not_generated_count"]!.Value<int>());
                Assert.Equal(0, report["refused_count"]!.Value<int>());
                Assert.Null(report["errors"]);
                var skipped = (JArray)report["skipped_objects"]!;
                Assert.Equal(6, skipped.Count);
                foreach (var obj in skipped)
                {
                    Assert.Equal("WebPanel", obj["kind"]?.ToString());
                    Assert.Equal("web UI, not generated", obj["reason"]?.ToString());
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Lifecycle_BuildFailureWithReport_IsASemanticFailureEvenUnderSdkFallback()
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\n" +
                    "echo {\"read_ms\":1,\"spec_ms\":1,\"emit_ms\":1,\"compile_ms\":1,\"total_ms\":4,\"procedures_count\":0,\"transactions_count\":0,\"sdts_count\":0,\"webpanels_count\":0,\"total_objects\":1,\"errors\":[\"boom\"],\"warnings\":[],\"emitted_files\":[]}\r\n" +
                    "exit /b 1\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);

                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal("Failed", payload["status"]?.ToString());
                Assert.Equal("boom", payload["report"]?["errors"]?[0]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task Lifecycle_EngineUnavailable_FallsBackUnderSdkAndRefusesUnderError()
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, Path.Combine(tempDir, "missing", "gx-engine.exe"));

                var fallback = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);
                Assert.Null(fallback);

                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");
                var refused = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(refused);
                Assert.True(refused!["isError"]!.Value<bool>());
                var payload = PayloadOf(refused);
                Assert.Equal("EngineNotFound", payload["error"]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk", "genexus_query", true)]
        [InlineData("error", "genexus_query", true)]
        [InlineData("sdk", "genexus_query", false)]
        [InlineData("sdk", "genexus_lifecycle", true)]
        [InlineData("error", "genexus_lifecycle", true)]
        [InlineData("sdk", "genexus_lifecycle", false)]
        public async Task CancelledRequest_DoesNotStartEngineOrFallBack(string fallback, string tool, bool admitted)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "started.txt");
                string stub = CreateStub(tempDir, "cancelled.cmd",
                    "@echo off\r\n" +
                    $"echo started>\"{marker}\"\r\n" +
                    "exit /b 1\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                bool lifecycle = tool == "genexus_lifecycle";
                var args = lifecycle
                    ? new JObject { ["action"] = "build", ["wait_until_done"] = admitted }
                    : admitted
                        ? new JObject { ["query"] = "type:Procedure", ["fields"] = new JArray("name", "type") }
                        : new JObject { ["query"] = "Alpha" };
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    Program.TryDispatchEngineRoutedOperationAsync(
                        tool, lifecycle ? "build" : null, args,
                        tempDir, "kb-alias", cancellation.Token));

                Assert.False(File.Exists(marker));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task CoreDispatch_PreCancellationPrecedesModulePreview()
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var method = typeof(Program).GetMethod("DispatchToolCallCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            // Unsupported module previews are answered locally, so removing the guard
            // cannot launch a Worker or touch a KB in this regression.
            var parameters = JObject.Parse(
                @"{ ""name"": ""genexus_module"", ""arguments"": { ""action"": ""remove"", ""dryRun"": true } }");
            var dispatch = (Task<JObject>)method!.Invoke(null, new object?[]
            {
                parameters, new JObject(), "cancelled-test", false, null, cancelled.Token,
            })!;
            await Assert.ThrowsAsync<PreDispatchCancelledException>(() => dispatch);
        }

        [Fact]
        public async Task PreCancelledKeyedEngineBuild_CanRetrySameKey()
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                    CreateStub(tempDir, "retry-build.cmd", BuildReportStubBody));
                var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), Path.Combine(tempDir, "journal.json"));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                const string tool = "genexus_lifecycle";
                const string key = "pre-cancel-build";
                const string hash = "same-build-payload";
                var args = JObject.Parse(@"{ ""action"": ""build"", ""wait_until_done"": true }");
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    cache.GetOrCompute(tempDir, tool, key, hash, async () =>
                        await Program.TryDispatchEngineRoutedOperationAsync(
                            tool, "build", args, tempDir, "kb-alias", cancelled.Token)
                        ?? throw new InvalidOperationException("unexpected SDK fallback")));

                Assert.Equal("not_found", cache.InspectOperation(tempDir, tool, key)["status"]?.ToString());
                var retry = await cache.GetOrCompute(tempDir, tool, key, hash, async () =>
                    await Program.TryDispatchEngineRoutedOperationAsync(
                        tool, "build", args, tempDir, "kb-alias", CancellationToken.None)
                    ?? throw new InvalidOperationException("unexpected SDK fallback"));
                Assert.Equal("Success", PayloadOf(retry)["status"]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk", "genexus_query")]
        [InlineData("error", "genexus_query")]
        [InlineData("sdk", "genexus_lifecycle")]
        [InlineData("error", "genexus_lifecycle")]
        public async Task CancelledEngineInvocation_DoesNotFallBack(string fallback, string tool)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "started.txt");
                string stub = CreateStub(tempDir, "running.cmd",
                    "@echo off\r\n" +
                    $"echo started>\"{marker}\"\r\n" +
                    "ping -n 30 127.0.0.1 >nul\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                using var cancellation = new CancellationTokenSource();
                using var watcher = new FileSystemWatcher(tempDir, "started.txt");
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                watcher.Created += (_, _) => started.TrySetResult();
                watcher.EnableRaisingEvents = true;

                bool lifecycle = tool == "genexus_lifecycle";
                var args = lifecycle
                    ? JObject.Parse(@"{ ""action"": ""build"", ""wait_until_done"": true }")
                    : JObject.Parse(@"{ ""query"": ""type:Procedure"", ""fields"": [""name"", ""type""] }");
                var dispatch = Program.TryDispatchEngineRoutedOperationAsync(
                    tool, lifecycle ? "build" : null, args,
                    tempDir, "kb-alias", cancellation.Token);
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    cancellation.Cancel();
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        dispatch.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.IsNotType<PreDispatchCancelledException>(error);
                }
                finally
                {
                    cancellation.Cancel();
                    try { await dispatch; }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task CancelledBuildGateWait_DoesNotBlockNextBuild(string fallback)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "started.txt");
                string stub = CreateStub(tempDir, "holding-gate.cmd",
                    "@echo off\r\n" + $"echo started>\"{marker}\"\r\n" +
                    "ping -n 30 127.0.0.1 >nul\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                using var runningCancellation = new CancellationTokenSource();
                using var queuedCancellation = new CancellationTokenSource();
                using var watcher = new FileSystemWatcher(tempDir, "started.txt");
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                watcher.Created += (_, _) => started.TrySetResult();
                watcher.EnableRaisingEvents = true;
                var args = JObject.Parse(@"{ ""action"": ""build"", ""wait_until_done"": true }");
                var running = Program.TryDispatchEngineRoutedOperationAsync(
                    "genexus_lifecycle", "build", args, tempDir, "kb-alias", runningCancellation.Token);
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var queued = Program.TryDispatchEngineRoutedOperationAsync(
                        "genexus_lifecycle", "build", args, tempDir, "kb-alias", queuedCancellation.Token);
                    Assert.False(queued.IsCompleted);
                    queuedCancellation.Cancel();
                    await Assert.ThrowsAsync<PreDispatchCancelledException>(() =>
                        queued.WaitAsync(TimeSpan.FromSeconds(10)));
                    runningCancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        running.WaitAsync(TimeSpan.FromSeconds(10)));

                    SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                        CreateStub(tempDir, "next-build.cmd", BuildReportStubBody));
                    var next = await DispatchAsync("genexus_lifecycle", "build", args.ToString(), tempDir)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.NotNull(next);
                    Assert.Equal("Success", PayloadOf(next!)["status"]?.ToString());
                }
                finally
                {
                    runningCancellation.Cancel();
                    try { await running; }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk", 0)]
        [InlineData("error", 0)]
        [InlineData("sdk", 1)]
        [InlineData("error", 1)]
        public async Task BuildWithoutReport_DoesNotAssumeNoWorkOccurred(string fallback, int exitCode)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "build-started.txt");
                string stub = CreateStub(tempDir, "no-report.cmd",
                    "@echo off\r\n" + $"echo started>\"{marker}\"\r\nexit /b {exitCode}\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.True(File.Exists(marker));
                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                Assert.True(PayloadOf(result)["reconciliationRequired"]?.Value<bool>());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task KeyedReportlessBuild_PreservesFenceAndRejectsRetry(string fallback)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                    CreateStub(tempDir, "keyed-no-report.cmd", "@echo off\r\nexit /b 0\r\n"));
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), Path.Combine(tempDir, "journal.json"));
                var middleware = new IdempotencyMiddleware(cache, tempDir);
                var call = JObject.Parse(@"{ ""name"": ""genexus_lifecycle"", ""arguments"": {
                    ""action"": ""build"", ""wait_until_done"": true, ""idempotencyKey"": ""unknown-build"" } }");
                int invoked = 0;
                Task<JObject> Dispatch(JObject parameters)
                {
                    invoked++;
                    return DispatchBuild(parameters);
                }
                async Task<JObject> DispatchBuild(JObject parameters) =>
                    await Program.TryDispatchEngineRoutedOperationAsync(
                        "genexus_lifecycle", "build", (JObject)parameters["arguments"]!,
                        tempDir, "kb-alias", CancellationToken.None)
                    ?? throw new InvalidOperationException("unexpected SDK fallback");

                var first = await middleware.Invoke(call, Dispatch);
                Assert.True(first["isError"]?.Value<bool>());
                Assert.True(PayloadOf(first)["reconciliationRequired"]?.Value<bool>());
                Assert.Equal("started", cache.InspectOperation(tempDir, "genexus_lifecycle", "unknown-build")["status"]?.ToString());
                var error = await Assert.ThrowsAsync<UsageException>(() => middleware.Invoke(call, Dispatch));
                Assert.Equal("operation_unknown", error.Code);
                var restarted = new IdempotencyMiddleware(
                    new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), Path.Combine(tempDir, "journal.json")), tempDir);
                var afterRestart = await Assert.ThrowsAsync<UsageException>(() => restarted.Invoke(call, Dispatch));
                Assert.Equal("operation_unknown", afterRestart.Code);
                Assert.Equal(1, invoked);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task BuildObservationFailure_AfterProcessStart_DoesNotReplayOnSdk(string fallback)
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "observation-failure.cmd", "@echo off\r\nexit /b 0\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                // Accepted by configuration, but too large for the runner's CTS.
                // That exception occurs after the child process has been started.
                SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, int.MaxValue.ToString());
                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                Assert.True(PayloadOf(result)["reconciliationRequired"]?.Value<bool>());
                Assert.Equal("EngineObservationFailed", PayloadOf(result)["error"]?.ToString());
                Assert.True(PayloadOf(result)["mutationOutcomeUnknown"]?.Value<bool>());
                Assert.Contains("Process exit observed after cleanup.", PayloadOf(result)["message"]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task NonzeroExitWithSuccessfulReport_RequiresReconciliation(string fallback)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar,
                    CreateStub(tempDir, "contradictory-report.cmd",
                        "@echo off\r\necho {\"status\":\"complete\",\"errors\":[]}\r\nexit /b 1\r\n"));
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);
                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                Assert.True(PayloadOf(result)["reconciliationRequired"]?.Value<bool>());
                Assert.True(PayloadOf(result)["mutationOutcomeUnknown"]?.Value<bool>());
                Assert.Equal("EngineOutputUnrecognized", PayloadOf(result)["error"]?.ToString());
            }
            finally { TryDeleteDirectory(tempDir); }
        }

        [Theory]
        [InlineData("sdk", "{}")]
        [InlineData("error", "{}")]
        [InlineData("sdk", "{\"status\":\"unexpected\",\"errors\":[]}")]
        [InlineData("error", "{\"status\":\"unexpected\",\"errors\":[]}")]
        [InlineData("sdk", "{\"status\":\"complete\",\"errors\":\"not-an-array\"}")]
        [InlineData("error", "{\"status\":\"complete\",\"errors\":\"not-an-array\"}")]
        public async Task UnrecognizedBuildReport_DoesNotClaimSuccess(string fallback, string report)
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "unrecognized-report.cmd",
                    "@echo off\r\necho " + report + "\r\nexit /b 0\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                Assert.True(PayloadOf(result)["reconciliationRequired"]?.Value<bool>());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk", "partial")]
        [InlineData("error", "partial")]
        [InlineData("sdk", "errors")]
        [InlineData("error", "errors")]
        public async Task ExitZero_DoesNotOverrideIncompleteBuildReport(string fallback, string reportKind)
        {
            string tempDir = NewTempDir();
            try
            {
                string report = reportKind == "partial"
                    ? "{\"status\":\"partial\",\"skipped_count\":1,\"errors\":[]}"
                    : "{\"status\":\"failed\",\"errors\":[\"compile failed\"]}";
                string stub = CreateStub(tempDir, "incomplete-build.cmd",
                    "@echo off\r\necho " + report + "\r\nexit /b 0\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                var result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                var payload = PayloadOf(result);
                Assert.Equal(reportKind == "partial" ? "Partial" : "Failed", payload["status"]?.ToString());
                Assert.NotNull(payload["report"]);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task MalformedBuildReport_DoesNotReplayOnSdk(string fallback)
        {
            string tempDir = NewTempDir();
            try
            {
                string stub = CreateStub(tempDir, "malformed-build.cmd",
                    "@echo off\r\n" +
                    "echo {\"status\":\"partial\",\"skipped_count\":\"not-a-number\"}\r\n" +
                    "exit /b 3\r\n");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);

                JObject? result = await DispatchAsync("genexus_lifecycle", "build",
                    @"{ ""action"": ""build"", ""wait_until_done"": true }", tempDir);

                Assert.NotNull(result);
                Assert.True(result!["isError"]!.Value<bool>());
                Assert.Equal("EngineInvocationFailed", PayloadOf(result)["error"]?.ToString());
                Assert.True(PayloadOf(result)["reconciliationRequired"]?.Value<bool>());
                Assert.DoesNotContain("set Engine.Fallback=sdk", PayloadOf(result)["hint"]?.ToString() ?? "");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------- admitted argument space

        [Theory]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'kbs':['a'] }", "kbs")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'cursor':'c' }", "cursor")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'cursors':{'a':'c'} }", "cursors")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'domainFilter':'d' }", "domainFilter")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'inline_read_top':2 }", "inline_read_top")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'sort':'name' }", "sort")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'since':'2020' }", "since")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'modifiedBefore':'2020' }", "modifiedBefore")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'maxConcurrency':2 }", "maxConcurrency")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'maxTotalResults':2 }", "maxTotalResults")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'perKbTimeoutMs':2 }", "perKbTimeoutMs")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'unlistedArgument':'x' }", "unlistedArgument")]
        [InlineData("genexus_lifecycle", "build", "{ 'dryRun':true }", "dryRun")]
        [InlineData("genexus_lifecycle", "build", "{ 'environment':'Default' }", "environment")]
        [InlineData("genexus_lifecycle", "build", "{ 'deploy':true }", "deploy")]
        [InlineData("genexus_lifecycle", "build", "{ 'fastIncremental':true }", "fastIncremental")]
        [InlineData("genexus_lifecycle", "build", "{ 'wait_seconds':5 }", "wait_seconds")]
        [InlineData("genexus_lifecycle", "build", "{ 'force':true }", "force")]
        [InlineData("genexus_lifecycle", "build", "{ 'callerCap':5 }", "callerCap")]
        [InlineData("genexus_lifecycle", "build", "{ 'buildPlanCap':5 }", "buildPlanCap")]
        [InlineData("genexus_lifecycle", "build", "{ 'limit':5 }", "limit")]
        [InlineData("genexus_lifecycle", "rebuild", "{ 'unlistedArgument':'x' }", "unlistedArgument")]
        public async Task UnlistedArgument_IsRefusedWithAMessageGeneratedFromTheSupportedList(
            string tool, string? action, string extraArgs, string argument)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "ran.txt");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\n" + BuildReportStubBody));
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");
                var args = JObject.Parse(extraArgs);
                if (action != null)
                {
                    args["action"] = action;
                    args["wait_until_done"] = true;
                }

                var result = await DispatchAsync(tool, action, args.ToString(), tempDir);

                Assert.NotNull(result);
                Assert.True(result!.Value<bool>("isError"));
                var payload = PayloadOf(result);
                Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
                string message = payload["message"]!.ToString();
                Assert.Contains($"`{argument}`", message);
                Assert.Contains("supported arguments:", message);
                Assert.False(File.Exists(marker), "a refused call must never start gx-engine");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("genexus_read", null, "{ 'name':'Alpha', 'part':'Source', 'targets':['Beta'] }")]
        [InlineData("genexus_query", null, "{ 'query':'Alpha', 'sort':'name' }")]
        [InlineData("genexus_lifecycle", "build", "{ 'dryRun':true }")]
        public async Task UnlistedArgument_FallsBackToTheSdkUnderSdkFallback(string tool, string? action, string extraArgs)
        {
            string tempDir = NewTempDir();
            try
            {
                string marker = Path.Combine(tempDir, "ran.txt");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho ran > \"%~dp0ran.txt\"\r\n" + BuildReportStubBody));
                var args = JObject.Parse(extraArgs);
                if (action != null)
                {
                    args["action"] = action;
                    args["wait_until_done"] = true;
                }

                Assert.Null(await DispatchAsync(tool, action, args.ToString(), tempDir));
                Assert.False(File.Exists(marker));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("genexus_query", null, "{ 'query':'type:Procedure', 'typeFilter':'', 'limit':10, 'exactMatch':true, 'kb':'kb-alias', 'fields':['name','type'], 'axiCompact':true }")]
        [InlineData("genexus_lifecycle", "build", "{ 'wait_until_done':true, 'estimated_seconds':5, 'dryRun':false, 'deploy':false, 'skipFullDeploy':true, 'kb':'kb-alias', 'fields':['status'] }")]
        [InlineData("genexus_lifecycle", "rebuild", "{ 'wait_until_done':true, 'fastIncremental':false }")]
        public async Task ListedArguments_AreServedByTheEngine(string tool, string? action, string argsJson)
        {
            string tempDir = NewTempDir();
            try
            {
                string body = tool == "genexus_query" ? "@echo off\r\necho {\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"}],\"total\":1,\"truncated\":false}\r\n"
                    : BuildReportStubBody;
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd", body));
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "error");
                var args = JObject.Parse(argsJson);
                if (action != null) args["action"] = action;

                var result = await DispatchAsync(tool, action, args.ToString(), tempDir);

                Assert.NotNull(result);
                Assert.False(result!.Value<bool>("isError"));
                Assert.Equal("gxengine", PayloadOf(result)["engine"]?["backend"]?.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------------------- helpers

        [Fact]
        public void TryParseEngineJson_ToleratesLeadingNoise()
        {
            Assert.True(EngineRoutedOperations.TryParseEngineJson("note: using cache\n{\"a\":1}", out JObject? json, out _));
            Assert.Equal(1, json!["a"]!.Value<int>());

            Assert.False(EngineRoutedOperations.TryParseEngineJson("", out _, out string? emptyError));
            Assert.False(string.IsNullOrWhiteSpace(emptyError));
            Assert.False(EngineRoutedOperations.TryParseEngineJson("no json here", out _, out _));
        }

        private static string BuildReportStubBody =>
            "@echo off\r\n" +
            "echo {\"catalog_load_ms\":5,\"read_ms\":1,\"spec_ms\":2,\"emit_ms\":3,\"compile_ms\":4,\"total_ms\":10,\"procedures_count\":1,\"transactions_count\":1,\"sdts_count\":0,\"webpanels_count\":1,\"total_objects\":3,\"errors\":[],\"warnings\":[],\"emitted_files\":[\"a.cs\",\"b.cs\"]}\r\n";

        private static string FindUp(params string[] relativeSegments)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(new[] { dir }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("fixture not found: " + string.Join("/", relativeSegments));
        }
    }
}
