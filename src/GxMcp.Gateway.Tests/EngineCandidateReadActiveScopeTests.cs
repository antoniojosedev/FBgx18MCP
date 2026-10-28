using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class EngineCandidateReadActiveScopeTests : EngineRoutingTestBase, IDisposable
    {
        private const string SinglePartPageRead =
            "{\"part\":\"Source\",\"source\":\"// alpha\",\"isBase64\":false,\"isEmpty\":false,\"offset\":0,\"limit\":1,\"totalLines\":1,\"totalBytes\":8,\"truncated\":false,\"isTruncatedByWorker\":false,\"versionToken\":\"636163074130000000:root:8d6e1d4ad45838c31ecd8f897bb9909f6f81f58695b62e20b932165db2ac3e8b\"}";

        private const string ReadArgs = @"{ ""name"": ""Alpha"", ""part"": ""Source"" }";
        private readonly string _sandboxDir;
        private readonly string _argsCapturePath;

        public EngineCandidateReadActiveScopeTests()
        {
            _sandboxDir = NewTempDir();
            _argsCapturePath = Path.Combine(_sandboxDir, "args.txt");
            string stubPath = CreateStub(_sandboxDir, "engine.cmd",
                "@echo off\r\necho %* > \"%~dp0args.txt\"\r\necho " + SinglePartPageRead + "\r\n");
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stubPath);
        }

        public new void Dispose()
        {
            TryDeleteDirectory(_sandboxDir);
            base.Dispose();
        }

        [Fact]
        public async Task CandidateRead_RequiresAReadyActiveVersionSnapshotBeforeStartingTheEngine()
        {
            Func<string, EngineActiveModel?>[] invalidSnapshots =
            {
                _ => null,
                _ => new EngineActiveModel(@"C:\KBs\OtherKb", 1, 0, "Trunk"),
                kbPath => new EngineActiveModel(kbPath, 0, 0, "Trunk"),
                kbPath => new EngineActiveModel(kbPath, 1, -1, "Trunk"),
            };

            foreach (var invalid in invalidSnapshots)
            {
                var deferred = await InvokeReadWithSnapshotsAsync("sdk", invalid, invalid);
                Assert.Null(deferred.Result);
                Assert.False(deferred.Started);
                Assert.Contains(deferred.Log, l => l.Contains("a ready Worker active-version snapshot is required for native kb-read"));

                var refused = await InvokeReadWithSnapshotsAsync("error", invalid, invalid);
                Assert.True(refused.Result!.Value<bool>("isError"));
                Assert.False(refused.Started);
                Assert.Contains("a ready Worker active-version snapshot is required for native kb-read", PayloadOf(refused.Result)["message"]!.ToString());
            }
        }

        [Fact]
        public async Task CandidateRead_RefusesWhenActiveVersionChangesOrVanishesDuringTheRead()
        {
            Func<string, EngineActiveModel?> before = kbPath => new EngineActiveModel(kbPath, 4, 2, "Branch");
            Func<string, EngineActiveModel?>[] changedAfter =
            {
                _ => null,
                _ => new EngineActiveModel(@"C:\KBs\OtherKb", 4, 2, "Branch"),
                kbPath => new EngineActiveModel(kbPath, 1, 2, "Branch"),
                kbPath => new EngineActiveModel(kbPath, 4, 3, "Branch"),
            };

            foreach (var after in changedAfter)
            {
                var deferred = await InvokeReadWithSnapshotsAsync("sdk", before, after);
                Assert.Null(deferred.Result);
                Assert.True(deferred.Started);
                Assert.Contains("--design-model 4 --kb-version 2", deferred.Args);
                Assert.Contains(deferred.Log, l => l.Contains("the active version changed or became unknown during native kb-read"));

                var refused = await InvokeReadWithSnapshotsAsync("error", before, after);
                Assert.True(refused.Result!.Value<bool>("isError"));
                Assert.True(refused.Started);
                Assert.Contains("the active version changed or became unknown during native kb-read", PayloadOf(refused.Result)["message"]!.ToString());
            }
        }

        [Fact]
        public async Task CandidateRead_PinsTheActiveVersionOnTheCommandLineAndShapesThePageWhenUnchanged()
        {
            Func<string, EngineActiveModel?> active = kbPath => new EngineActiveModel(kbPath, 4, 2, "Branch");

            var run = await InvokeReadWithSnapshotsAsync("error", active, active);
            Assert.NotNull(run.Result);
            Assert.False(run.Result!.Value<bool>("isError"));
            Assert.True(run.Started);
            Assert.Contains("--design-model 4 --kb-version 2", run.Args);

            var payload = PayloadOf(run.Result);
            Assert.Equal("Source", payload["part"]?.ToString());
            Assert.Equal("// alpha", payload["source"]?.ToString());
            Assert.Equal("engine-object-state", payload["readFreshness"]?["origin"]?.ToString());
        }

        private async Task<(JObject? Result, bool Started, string Args, List<string> Log)> InvokeReadWithSnapshotsAsync(
            string fallbackPolicy,
            Func<string, EngineActiveModel?> firstSnapshot,
            Func<string, EngineActiveModel?> secondSnapshot)
        {
            if (File.Exists(_argsCapturePath)) File.Delete(_argsCapturePath);
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallbackPolicy);

            int callCount = 0;
            var logMessages = new List<string>();
            var routed = new EngineRoutedOperations(
                () => null,
                logMessages.Add,
                (payload, isError, _, _) => new JObject
                {
                    ["isError"] = isError,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() }),
                },
                (kb, _) => Task.FromResult(++callCount == 1 ? firstSnapshot(kb) : secondSnapshot(kb)));

            var routeCtx = new EngineRoutedOperations.EngineRouteContext(
                EngineBackendSettings.Resolve(null), "udm_storage_access", "genexus_read", "genexus_read",
                JObject.Parse(ReadArgs), _sandboxDir, "kb-alias", CancellationToken.None);
            JObject? response = await routed.DispatchEngineReadAsync(routeCtx);
            bool invoked = File.Exists(_argsCapturePath);
            return (response, invoked, invoked ? File.ReadAllText(_argsCapturePath) : string.Empty, logMessages);
        }
    }
}
