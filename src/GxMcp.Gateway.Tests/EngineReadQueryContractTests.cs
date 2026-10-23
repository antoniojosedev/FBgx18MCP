using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Behavioral compatibility of the read -> edit, query and error contracts
    /// across <c>Engine.Backend</c> (sdk, auto, gxengine) and
    /// <c>Engine.Fallback</c> (sdk, error). Every read shape and edit is deferred to
    /// the SDK (Fallback=sdk) or refused with an actionable unsupported-capability
    /// error (Fallback=error), because only the SDK returns identity, freshness and
    /// the versionToken; a query is answered by the engine only as a bare `type:`
    /// filter whose projected fields the engine supplies. The stubs echo canned
    /// JSON, so these tests pin shapes and refusals, not SDK equivalence.
    /// </summary>
    public class EngineReadQueryContractTests : EngineRoutingTestBase
    {
        private const string TwoPartRead =
            "{\"name\":\"Alpha\",\"type\":\"Procedure\",\"parts\":[{\"name\":\"SOURCE\",\"source\":\"// alpha\"},{\"name\":\"RULES\",\"source\":\"parm();\"}],\"missing_parts\":[],\"available_parts\":[\"SOURCE\",\"RULES\"]}";

        private const string AlphaQuery =
            "{\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"}],\"total\":1,\"truncated\":false}";

        private const string EmptyQuery = "{\"objects\":[],\"total\":0,\"truncated\":false}";

        private const string QueryFields = ", \"fields\": [\"name\", \"type\"]";

        // --------------------------------------------------------- reads: never native

        [Theory]
        // Whatever the shape - single part (the versioned read), parts[], full object, with or
        // without `fields`, `refresh`, `type`, any part kind - the SDK answers: it alone returns
        // identity, readFreshness and the versionToken, and it ignores `fields` on a read.
        [InlineData(@"{ ""name"": ""Alpha"", ""part"": ""Source"" }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""part"": ""Source"", ""fields"": [""name"", ""type"", ""parts""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source"", ""rules""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source"", ""rules""], ""fields"": [""name"", ""type"", ""parts""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""fields"": ""parts"" }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""fields"": [""name"", ""identity""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""projection"": ""verbose"" }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""full"": true, ""fields"": [""parts""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""refresh"": true, ""fields"": [""parts""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Variables""], ""fields"": [""parts""] }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""type"": ""Procedure"", ""fields"": [""parts""] }")]
        [InlineData(@"{ ""name"": ""Alpha"" }")]
        [InlineData(@"{ ""name"": ""Alpha"", ""part"": ""all"", ""fields"": [""parts""] }")]
        [InlineData(@"{ ""objectName"": ""Alpha"", ""parts"": [""Source""], ""fields"": [""parts""] }")]
        public async Task EveryReadShape_IsDeferredUnderSdkFallbackAndRefusedUnderErrorFallback(string args)
        {
            var deferred = await RunAsync("gxengine", "sdk", "genexus_read", args, TwoPartRead);
            Assert.Null(deferred.Result);
            Assert.False(deferred.EngineStarted);
            Assert.Contains(deferred.Log, line => line.Contains("genexus_read deferred to the SDK") && line.Contains("versionToken"));

            var refused = await RunAsync("gxengine", "error", "genexus_read", args, TwoPartRead);
            Assert.NotNull(refused.Result);
            Assert.True(refused.Result!.Value<bool>("isError"));
            var payload = PayloadOf(refused.Result);
            Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
            Assert.Contains("identity, readFreshness and versionToken", payload["message"]!.ToString());
            Assert.False(refused.EngineStarted);
        }

        // ------------------------------------------------- admitted native query space

        [Theory]
        [InlineData("type:trn", null, "Transaction")]
        [InlineData("type:PROC", null, "Procedure")]
        [InlineData("type:Table", null, "Table")]
        [InlineData("", "prc", "Procedure")]
        public async Task TypeQuery_ReachesTheEngineUnderItsSpellingOfTheSdkAlias(string query, string? typeFilter, string engineType)
        {
            var args = new JObject { ["query"] = query, ["fields"] = new JArray("name", "type") };
            if (typeFilter != null) args["typeFilter"] = typeFilter;
            var run = await RunAsync("gxengine", "error", "genexus_query", args.ToString(), AlphaQuery);

            Assert.NotNull(run.Result);
            Assert.Contains("--type " + engineType, run.EngineArguments);
            Assert.DoesNotContain("--name", run.EngineArguments);
            Assert.DoesNotContain("--usedby", run.EngineArguments);
        }

        [Theory]
        // A client whose schema declared no array for `fields` sends the array as text. The
        // literal must reach the engine as the two names it holds, not as `["name"` and `"type"]`.
        [InlineData("[\"name\",\"type\"]")]
        [InlineData(" [ \"name\" , \"type\" ] ")]
        [InlineData("name,type")]
        public async Task TypeQuery_FieldsSentAsText_ReachTheEngineLikeTheArray(string fields)
        {
            var args = new JObject { ["query"] = "type:Procedure", ["fields"] = fields };
            var run = await RunAsync("gxengine", "error", "genexus_query", args.ToString(), AlphaQuery);

            Assert.NotNull(run.Result);
            Assert.Contains("--type Procedure", run.EngineArguments);
        }

        [Theory]
        [InlineData(AlphaQuery, "vector", false)]
        [InlineData("{\"objects\":[{\"name\":\"General\",\"type\":\"Module\"},{\"name\":\"Alpha\",\"type\":\"Procedure\"}],\"total\":2,\"truncated\":false}", "substring", false)]
        [InlineData("{\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"}],\"total\":9,\"truncated\":true}", "vector", true)]
        public async Task TypeQuery_CarriesTheSdkEnvelopeOfATermlessQuery(string engineJson, string expectedQuality, bool truncated)
        {
            var run = await RunAsync("gxengine", "error", "genexus_query",
                @"{ ""query"": ""type:Procedure"", ""limit"": 1" + QueryFields + " }", engineJson);

            var payload = PayloadOf(run.Result!);
            Assert.Equal(expectedQuality, payload["_meta"]?["match_quality"]?.ToString());
            Assert.Null(payload["_meta"]?["suggested_next"]);
            Assert.Equal(1, payload["pagination"]!["limit"]!.Value<int>());
            // The SDK worker envelope has no top-level `truncated`; hasMore/nextOffset carry it.
            Assert.Null(payload["truncated"]);
            Assert.Equal(truncated, payload["hasMore"]!.Value<bool>());
            JToken? nextOffset = payload["pagination"]!["nextOffset"];
            if (truncated) Assert.Equal(1, nextOffset!.Value<int>());
            else Assert.Null(nextOffset); // SDK wire serialization omits a null nextOffset.
        }

        // ------------------------------------------- never served by the engine

        [Theory]
        [InlineData("sdk", "sdk")]
        [InlineData("sdk", "error")]
        [InlineData("auto", "sdk")]
        [InlineData("auto", "error")]
        public async Task ReadsQueriesAndEdits_BackendsOtherThanGxengine_NeverStartTheEngine(string backend, string fallback)
        {
            var read = await RunAsync(backend, fallback, "genexus_read",
                @"{ ""name"": ""Alpha"", ""parts"": [""Source""], ""fields"": [""parts""] }", TwoPartRead);
            var query = await RunAsync(backend, fallback, "genexus_query",
                @"{ ""query"": ""type:Procedure""" + QueryFields + " }", AlphaQuery);
            var edit = await RunAsync(backend, fallback, "genexus_edit",
                @"{ ""name"": ""Alpha"", ""part"": ""Source"", ""mode"": ""patch"", ""baseVersion"": ""1:2:3"", ""content"": ""x"" }", TwoPartRead);

            Assert.Null(read.Result);
            Assert.Null(query.Result);
            Assert.Null(edit.Result);
            Assert.False(read.EngineStarted || query.EngineStarted || edit.EngineStarted);
        }

        [Fact]
        public async Task QueryFailureOfTheEngine_IsLeftToTheSdkUnderSdkFallback()
        {
            var run = await RunAsync("gxengine", "sdk", "genexus_query", @"{ ""query"": ""type:Procedure""" + QueryFields + " }", null);

            Assert.True(run.EngineStarted);
            Assert.Null(run.Result);
        }

        [Theory]
        // Decided before the engine runs.
        [InlineData(@"{ ""query"": ""type:Procedure"" }", "explicit `fields`", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""projection"": ""verbose"" }", "explicit `fields`", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""fields"": [""name"", ""guid"", ""path""] }", "`guid`", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""fields"": [""name"", ""lastUpdate""] }", "`lastUpdate`", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""axiCompact"": false }", "explicit `fields`", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""full"": true" + QueryFields + " }", "`full:true`", false)]
        [InlineData(@"{ ""query"": ""Alpha""" + QueryFields + " }", "object name or text search", false)]
        [InlineData(@"{ ""query"": ""Alpha"", ""exactMatch"": false" + QueryFields + " }", "object name or text search", false)]
        [InlineData(@"{ ""query"": ""type:Procedure Alpha"", ""exactMatch"": true" + QueryFields + " }", "exact-name branch", false)]
        [InlineData(@"{ ""query"": ""type:Procedure usedby:Alpha""" + QueryFields + " }", "`usedby:`", false)]
        [InlineData(@"{ ""query"": ""name:Alpha type:Procedure""" + QueryFields + " }", "`name:`", false)]
        [InlineData(@"{ ""query"": """", ""fields"": [""name"", ""type""] }", "empty query", false)]
        [InlineData(@"{ ""query"": ""type:StructuredDataType""" + QueryFields + " }", "StructuredDataType", false)]
        [InlineData(@"{ ""query"": ""type:Web""" + QueryFields + " }", "type `Web`", false)]
        [InlineData(@"{ ""query"": ""type:webpanel""" + QueryFields + " }", "type `webpanel`", false)]
        [InlineData(@"{ ""query"": ""type:WP""" + QueryFields + " }", "type `WP`", false)]
        [InlineData(@"{ ""query"": """", ""typeFilter"": ""WebPanel""" + QueryFields + " }", "type `WebPanel`", false)]
        [InlineData(@"{ ""query"": """", ""typeFilter"": ""wp""" + QueryFields + " }", "type `wp`", false)]
        [InlineData(@"{ ""query"": """", ""typeFilter"": "" prc """ + QueryFields + " }", "whitespace", false)]
        [InlineData(@"{ ""query"": """", ""typeFilter"": ""prc """ + QueryFields + " }", "whitespace", false)]
        [InlineData(@"{ ""query"": """", ""typeFilter"": ""\tprc""" + QueryFields + " }", "whitespace", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""typeFilter"": "" """ + QueryFields + " }", "whitespace", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""limit"": 0" + QueryFields + " }", "`limit` 0", false)]
        [InlineData(@"{ ""query"": ""type:Procedure"", ""limit"": 500" + QueryFields + " }", "`limit` 500", false)]
        // Decided from the engine's answer.
        [InlineData(@"{ ""query"": ""type:Procedure""" + QueryFields + " }", "no match", true)]
        public async Task QueriesOutsideTheAdmittedSpace_AreDeferredUnderSdkFallbackAndRefusedUnderErrorFallback(
            string args, string reasonFragment, bool engineMustRun)
        {
            string engineJson = engineMustRun ? EmptyQuery : AlphaQuery;

            var deferred = await RunAsync("gxengine", "sdk", "genexus_query", args, engineJson);
            Assert.Null(deferred.Result);
            Assert.Equal(engineMustRun, deferred.EngineStarted);
            Assert.Contains(deferred.Log, line => line.Contains("deferred to the SDK") && line.Contains(reasonFragment));

            var refused = await RunAsync("gxengine", "error", "genexus_query", args, engineJson);
            Assert.NotNull(refused.Result);
            Assert.True(refused.Result!.Value<bool>("isError"));
            var payload = PayloadOf(refused.Result);
            Assert.Equal("EngineOperationOutsideAdmittedSpace", payload["error"]?.ToString());
            Assert.Contains(reasonFragment, payload["message"]!.ToString());
        }

        // ----------------------------------------------------------- helpers

        private sealed record EngineRun(JObject? Result, bool EngineStarted, string EngineArguments, IReadOnlyList<string> Log);

        /// <summary>
        /// Dispatches one call to a stub engine that records its arguments and prints
        /// <paramref name="engineJson"/>; a null answer makes the stub fail (exit 1).
        /// </summary>
        private async Task<EngineRun> RunAsync(string backend, string fallback, string tool, string argsJson, string? engineJson)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\n" +
                    "echo %* > \"%~dp0args.txt\"\r\n" +
                    (engineJson == null ? "exit /b 1\r\n" : "echo " + engineJson + "\r\n")));

                var log = new List<string>();
                var operations = new EngineRoutedOperations(
                    () => null, log.Add,
                    (payload, isError, toolName, toolArgs) => new JObject
                    {
                        ["isError"] = isError,
                        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() }),
                    });
                JObject? result = await operations.TryDispatchAsync(
                    tool, null, JObject.Parse(argsJson), tempDir, "kb-alias", CancellationToken.None);

                string argsFile = Path.Combine(tempDir, "args.txt");
                bool started = File.Exists(argsFile);
                return new EngineRun(result, started, started ? File.ReadAllText(argsFile) : "", log);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }
}
