using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// gx-engine read the trunk Design model of a KB whose SDK session had a branch active, and answered
    /// without an error. Every engine invocation now names the SDK's active version, and the engine's
    /// version-scope refusal (exit 4, nothing read or written) keeps the call on the SDK path.
    /// </summary>
    public class EngineActiveVersionTests : EngineRoutingTestBase
    {
        private const string AlphaQuery =
            "{\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"}],\"total\":1,\"truncated\":false}";

        private const string QueryArgs = @"{ ""query"": ""type:Procedure"", ""fields"": [""name"", ""type""] }";

        private const string BuildReport =
            "{\"catalog_load_ms\":5,\"read_ms\":1,\"spec_ms\":2,\"emit_ms\":3,\"compile_ms\":4,\"total_ms\":10,\"procedures_count\":1,\"transactions_count\":1,\"sdts_count\":0,\"webpanels_count\":1,\"total_objects\":3,\"errors\":[],\"warnings\":[],\"emitted_files\":[\"a.cs\",\"b.cs\"]}";

        private const string ScopeRefusal =
            "{\"category\":\"version_scope\",\"message\":\"the active version's Design model is 4 (KB version 2); native builds cover only the trunk Design model 1\",\"location\":null}";

        // ------------------------------------------------------------ the worker reply

        [Fact]
        public void TheWorkerSnapshotParsesIntoTheEngineArguments()
        {
            var envelope = JObject.Parse(
                @"{ ""result"": { ""status"": ""ok"", ""code"": ""KbActiveModel"", ""result"": { ""kbPath"": ""C:\\KBs\\Clone"", ""kbVersionId"": 2, ""kbVersionName"": ""Branch"", ""designModelId"": 4 } } }");

            EngineActiveModel? model = EngineActiveModel.FromWorkerReply(envelope);

            Assert.NotNull(model);
            Assert.Equal(new[] { "--design-model", "4", "--kb-version", "2" }, model!.EngineArguments());
            Assert.True(model.Describes(@"C:\KBs\Clone\"));
            Assert.False(model.Describes(@"C:\KBs\Other"));
        }

        [Theory]
        [InlineData(@"{ ""__timeout"": true }")]
        [InlineData(@"{ ""result"": { ""status"": ""error"", ""error"": { ""code"": ""KbActiveModelUnknown"" } } }")]
        [InlineData(@"{ ""result"": { ""status"": ""ok"", ""code"": ""KbVersionListRetrieved"", ""result"": { ""kbPath"": ""C:\\K"", ""kbVersionId"": 0, ""designModelId"": 1 } } }")]
        [InlineData(@"{ ""result"": { ""status"": ""ok"", ""code"": ""KbActiveModel"", ""result"": { ""kbPath"": ""C:\\K"", ""kbVersionId"": 0 } } }")]
        [InlineData(@"{ ""result"": { ""status"": ""ok"", ""code"": ""KbActiveModel"", ""result"": { ""kbPath"": ""C:\\K"", ""kbVersionId"": ""0"", ""designModelId"": 1 } } }")]
        [InlineData(@"{ ""result"": { ""status"": ""ok"", ""code"": ""KbActiveModel"", ""result"": { ""kbVersionId"": 0, ""designModelId"": 1 } } }")]
        public void AnythingButAKnownSnapshotNamesNoVersion(string envelope)
        {
            Assert.Null(EngineActiveModel.FromWorkerReply(JObject.Parse(envelope)));
        }

        // ------------------------------------------------------------- routing

        [Fact]
        public async Task AQueryNamesTheActiveVersionOfItsKnowledgeBase()
        {
            var run = await RunAsync("sdk", "genexus_query", null, QueryArgs, "echo " + AlphaQuery,
                kb => new EngineActiveModel(kb, 4, 2, "Branch"));

            Assert.NotNull(run.Result);
            Assert.False(run.Result!.Value<bool>("isError"));
            Assert.Contains("--design-model 4 --kb-version 2", run.EngineArguments);
        }

        [Fact]
        public async Task ASnapshotOfAnotherKnowledgeBaseIsNotPassed()
        {
            var run = await RunAsync("sdk", "genexus_query", null, QueryArgs, "echo " + AlphaQuery,
                _ => new EngineActiveModel(@"C:\KBs\SomeOtherKb", 4, 2, "Branch"));

            Assert.NotNull(run.Result);
            Assert.DoesNotContain("--design-model", run.EngineArguments);
        }

        [Fact]
        public async Task AFailingSnapshotReadLeavesTheVersionToTheEngine()
        {
            var run = await RunAsync("sdk", "genexus_query", null, QueryArgs, "echo " + AlphaQuery,
                _ => throw new IOException("pipe closed"));

            Assert.NotNull(run.Result);
            Assert.DoesNotContain("--design-model", run.EngineArguments);
            Assert.Contains(run.Log, line => line.Contains("active KB version not read") && line.Contains("pipe closed"));
        }

        [Fact]
        public async Task AVersionScopeRefusedQueryIsLeftToTheSdkOrRefused()
        {
            string refuse = "echo " + ScopeRefusal + " 1>&2\r\nexit /b 4";

            var deferred = await RunAsync("sdk", "genexus_query", null, QueryArgs, refuse, null);
            Assert.Null(deferred.Result);
            Assert.Contains(deferred.Log, line => line.Contains("deferred to the SDK") && line.Contains("KB version in force"));

            var refused = await RunAsync("error", "genexus_query", null, QueryArgs, refuse, null);
            Assert.NotNull(refused.Result);
            var payload = PayloadOf(refused.Result!);
            Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
            Assert.Contains("native builds cover only the trunk", payload["message"]!.ToString());
        }

        [Fact]
        public async Task AVersionScopeRefusedBuildIsReplayedOnTheSdkWithoutReconciliation()
        {
            string refuse = "echo " + ScopeRefusal + " 1>&2\r\nexit /b 4";
            string buildArgs = @"{ ""action"": ""build"", ""wait_until_done"": true }";

            var deferred = await RunAsync("sdk", "genexus_lifecycle", "build", buildArgs, refuse,
                kb => new EngineActiveModel(kb, 4, 2, "Branch"));
            Assert.Null(deferred.Result);
            Assert.Contains("--design-model 4 --kb-version 2", deferred.EngineArguments);

            var refused = await RunAsync("error", "genexus_lifecycle", "build", buildArgs, refuse, null);
            var payload = PayloadOf(refused.Result!);
            Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
            Assert.Null(payload["reconciliationRequired"]);
        }

        [Fact]
        public async Task AnExit4WithoutTheVersionScopeDiagnosticIsAnOrdinaryFailure()
        {
            string other = "echo {\"category\":\"input\",\"message\":\"boom\"} 1>&2\r\nexit /b 4";
            var refused = await RunAsync("error", "genexus_query", null, QueryArgs, other, null);

            Assert.NotEqual("EngineOperationOutsideAdmittedSpace", PayloadOf(refused.Result!)["error"]?.ToString());
        }

        [Theory]
        [InlineData("{'category':'version_scope','message':'x','location':null}")]
        [InlineData("{/*not the engine protocol*/\"category\":\"version_scope\",\"message\":\"x\",\"location\":null}")]
        [InlineData("{\"category\":\"version_scope\",\"message\":\"x\",\"location\":null,}")]
        [InlineData("{\"category\":\"version_scope\",\"message\":\"x\"}")]
        [InlineData("{\"category\":\"version_scope\",\"message\":\"x\",\"location\":{\"line\":1}}")]
        [InlineData("{\"category\":\"version_scope\",\"message\":\"  \",\"location\":null}")]
        [InlineData("{\"category\":\"version_scope\",\"message\":\"x\",\"location\":null,\"detail\":1}")]
        public async Task AnExit4OutsideTheStrictDiagnosticContractIsNotAReplayAuthorization(string diagnostic)
        {
            string stub = "echo " + diagnostic + " 1>&2\r\nexit /b 4";

            var run = await RunAsync("sdk", "genexus_lifecycle", "build", BuildArgs, stub, null);

            Assert.NotNull(run.Result);
            var payload = PayloadOf(run.Result!);
            Assert.Equal("EngineCommandFailed", payload["error"]?.ToString());
            Assert.True(payload["reconciliationRequired"]?.Value<bool>());
            Assert.True(payload["mutationOutcomeUnknown"]?.Value<bool>());
        }

        [Fact]
        public async Task AnExit4WithStdoutIsNotAReplayAuthorization()
        {
            string stub = "echo partial output\r\necho " + ScopeRefusal + " 1>&2\r\nexit /b 4";

            var run = await RunAsync("sdk", "genexus_lifecycle", "build", BuildArgs, stub, null);

            Assert.NotNull(run.Result);
            var payload = PayloadOf(run.Result!);
            Assert.Equal("EngineCommandFailed", payload["error"]?.ToString());
            Assert.True(payload["reconciliationRequired"]?.Value<bool>());
        }

        [Fact]
        public async Task AnExit4WithNoiseOnStandardErrorIsNotAReplayAuthorization()
        {
            string stub = "echo noise 1>&2\r\necho " + ScopeRefusal + " 1>&2\r\nexit /b 4";

            var run = await RunAsync("sdk", "genexus_lifecycle", "build", BuildArgs, stub, null);

            Assert.NotNull(run.Result);
            var payload = PayloadOf(run.Result!);
            Assert.Equal("EngineCommandFailed", payload["error"]?.ToString());
            Assert.True(payload["reconciliationRequired"]?.Value<bool>());
        }

        [Fact]
        public async Task ABuildNamesTheActiveVersion()
        {
            var run = await RunAsync("sdk", "genexus_lifecycle", "build", @"{ ""action"": ""build"", ""wait_until_done"": true }",
                "echo " + BuildReport, kb => new EngineActiveModel(kb, 1, 0, "KBTeste"));

            Assert.NotNull(run.Result);
            Assert.False(run.Result!.Value<bool>("isError"));
            Assert.Contains("--design-model 1 --kb-version 0", run.EngineArguments);
        }

        // ------------------------------------------------- build snapshot lifetime

        private const string BuildArgs = @"{ ""action"": ""build"", ""wait_until_done"": true }";

        [Fact]
        public async Task AQueuedBuildNamesTheVersionActiveWhenItStartsNotWhenItWasRequested()
        {
            string tempDir = NewTempDir();
            string release = Path.Combine(tempDir, "release.txt");
            Task<JObject?>? holder = null;
            Task<JObject?>? queued = null;
            try
            {
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho %* >> \"%~dp0args.log\"\r\necho started> \"%~dp0started.txt\"\r\n" +
                    ":wait\r\nif not exist \"%~dp0release.txt\" (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
                    "echo " + BuildReport + "\r\n"));
                var log = new List<string>();
                JObject Shape(JToken payload, bool isError, string? toolName, JObject? toolArgs) => new JObject
                {
                    ["isError"] = isError,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() }),
                };

                // The holder occupies the engine build gate; it samples no version.
                var holderOperations = new EngineRoutedOperations(() => null, log.Add, Shape, null);
                holder = holderOperations.TryDispatchAsync(
                    "genexus_lifecycle", "build", JObject.Parse(BuildArgs), tempDir, "kb-alias", CancellationToken.None);
                await WaitForFileAsync(Path.Combine(tempDir, "started.txt"));

                var samples = new List<string>();
                EngineActiveModel activeAtSampling = new EngineActiveModel(tempDir, 1, 0, "KBTeste");
                var queuedOperations = new EngineRoutedOperations(() => null, log.Add, Shape, (kb, _) =>
                {
                    samples.Add(activeAtSampling.KbVersionName ?? "");
                    return Task.FromResult<EngineActiveModel?>(activeAtSampling);
                });
                queued = queuedOperations.TryDispatchAsync(
                    "genexus_lifecycle", "build", JObject.Parse(BuildArgs), tempDir, "kb-alias", CancellationToken.None);

                // The queued build is still waiting for the gate: it must not have read the version yet.
                Assert.False(queued.IsCompleted);
                Assert.Empty(samples);

                // The branch is selected while the queued build waits.
                activeAtSampling = new EngineActiveModel(tempDir, 4, 2, "Branch");
                File.WriteAllText(release, "go");
                JObject? queuedResult = await queued.WaitAsync(System.TimeSpan.FromSeconds(30));
                await holder.WaitAsync(System.TimeSpan.FromSeconds(30));

                Assert.NotNull(queuedResult);
                Assert.False(queuedResult!.Value<bool>("isError"));
                Assert.Equal(new[] { "Branch", "Branch" }, samples);
                string[] invocations = File.ReadAllLines(Path.Combine(tempDir, "args.log"));
                Assert.Equal(2, invocations.Length);
                Assert.DoesNotContain("--design-model", invocations[0]);
                Assert.Contains("--design-model 4 --kb-version 2", invocations[1]);
            }
            finally
            {
                File.WriteAllText(release, "go");
                if (holder != null) await holder.WaitAsync(System.TimeSpan.FromSeconds(30));
                if (queued != null) await queued.WaitAsync(System.TimeSpan.FromSeconds(30));
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("sdk", "different")]
        [InlineData("error", "different")]
        [InlineData("sdk", "vanished")]
        [InlineData("error", "vanished")]
        [InlineData("sdk", "unreadable")]
        [InlineData("error", "unreadable")]
        public async Task ABuildWhoseActiveVersionChangesWhileRunningIsUncertainAndNotReplayed(string fallback, string change)
        {
            int reads = 0;
            var run = await RunAsync(fallback, "genexus_lifecycle", "build", BuildArgs, "echo " + BuildReport,
                kb =>
                {
                    if (++reads == 1) return new EngineActiveModel(kb, 4, 2, "Branch");
                    return change switch
                    {
                        "different" => new EngineActiveModel(kb, 1, 0, "KBTeste"),
                        "vanished" => null,
                        _ => throw new IOException("pipe closed"),
                    };
                });

            // Never null: a null result would replay the started build on the SDK.
            Assert.NotNull(run.Result);
            Assert.True(run.Result!.Value<bool>("isError"));
            var payload = PayloadOf(run.Result!);
            Assert.Equal(EngineRoutedOperations.EngineInvocationFailedCode, payload["error"]?.ToString());
            Assert.Contains("outcome is uncertain", payload["message"]!.ToString());
            Assert.True(payload["reconciliationRequired"]!.Value<bool>());
            Assert.True(payload["mutationOutcomeUnknown"]!.Value<bool>());
            Assert.Contains("--design-model 4 --kb-version 2", run.EngineArguments);
            Assert.Equal(2, reads);
        }

        [Fact]
        public async Task ABuildWhoseActiveVersionIsUnchangedIsReportedAsSuccess()
        {
            int reads = 0;
            var run = await RunAsync("error", "genexus_lifecycle", "build", BuildArgs, "echo " + BuildReport,
                kb => { reads++; return new EngineActiveModel(kb, 4, 2, "Branch"); });

            Assert.Equal("Success", PayloadOf(run.Result!)["status"]?.ToString());
            Assert.Equal(2, reads);
        }

        [Fact]
        public async Task ABuildStartedWithoutAKnownVersionKeepsItsOrdinaryOutcome()
        {
            int reads = 0;
            var run = await RunAsync("error", "genexus_lifecycle", "build", BuildArgs, "echo " + BuildReport,
                kb => ++reads == 1 ? null : new EngineActiveModel(kb, 1, 0, "KBTeste"));

            Assert.Equal("Success", PayloadOf(run.Result!)["status"]?.ToString());
            Assert.DoesNotContain("--design-model", run.EngineArguments);
        }

        // ------------------------------------------------------------- helpers

        private static async Task WaitForFileAsync(string path)
        {
            var deadline = System.DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(path))
            {
                Assert.True(System.DateTime.UtcNow < deadline, "timed out waiting for " + path);
                await Task.Delay(25);
            }
        }

        private sealed record EngineRun(JObject? Result, string EngineArguments, IReadOnlyList<string> Log);

        private async Task<EngineRun> RunAsync(
            string fallback, string tool, string? action, string argsJson, string stubBody,
            System.Func<string, EngineActiveModel?>? activeModel)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho %* > \"%~dp0args.txt\"\r\n" + stubBody + "\r\n"));
                var log = new List<string>();
                var operations = new EngineRoutedOperations(
                    () => null, log.Add,
                    (payload, isError, toolName, toolArgs) => new JObject
                    {
                        ["isError"] = isError,
                        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() }),
                    },
                    activeModel == null ? null : (kb, _) => Task.FromResult(activeModel(kb)));
                JObject? result = await operations.TryDispatchAsync(
                    tool, action, JObject.Parse(argsJson), tempDir, "kb-alias", CancellationToken.None);
                string argsFile = Path.Combine(tempDir, "args.txt");
                return new EngineRun(result, File.Exists(argsFile) ? File.ReadAllText(argsFile) : "", log);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }
}
