using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #635: <c>genexus_lifecycle action=build mode=compile_check target=X callers=false
    /// includeCallees=transitive wait_until_done=true</c> reached gx-engine, which failed on a KB whose
    /// database login needs SQL credentials, and the caller learned nothing. gx-engine now exits 5 with
    /// one <c>sql_credentials_unavailable</c> diagnostic before it reads, writes or builds anything, which
    /// is the only started-process failure that may continue on the SDK. Every stub here is a .cmd that
    /// replays a recorded stdout/stderr/exit, so the Gateway-to-process contract runs without cargo or a KB.
    /// </summary>
    public class EngineSqlCredentialsPreflightTests : EngineRoutingTestBase
    {
        private const string EngineMessage =
            "the KB database login uses SQL Server authentication and no SQL credentials are available to the engine; build through the SDK or use Windows authentication";

        private static readonly string Contract =
            "{\"category\":\"sql_credentials_unavailable\",\"message\":\"" + EngineMessage + "\",\"location\":null}";

        private const string IssueArguments =
            "{\"action\":\"build\",\"mode\":\"compile_check\",\"target\":\"Alpha\",\"includeCallees\":\"transitive\",\"callers\":false,\"wait_until_done\":true}";

        private const string WholeKbArguments = "{\"action\":\"build\",\"wait_until_done\":true}";

        // ------------------------------------------------------------ the preflight contract

        [Theory]
        [InlineData("auto", true)]
        [InlineData("auto", false)]
        [InlineData("gxengine", true)]
        [InlineData("gxengine", false)]
        public async Task TheCredentialsPreflightContinuesOnTheSdkUnderSdkFallback(string backend, bool targeted)
        {
            var run = await RunAsync(backend, "sdk", targeted ? IssueArguments : WholeKbArguments, "build", 5, "", Contract + "\r\n");

            Assert.True(run.EngineInvoked);
            Assert.Null(run.Result);
            string logged = string.Join("\n", run.Log);
            Assert.Contains("sql_credentials_unavailable", logged);
            Assert.Contains(EngineMessage, logged);
            Assert.Contains("no KB read or build started, continuing on the SDK", logged);
        }

        [Theory]
        [InlineData("auto", "build", true)]
        [InlineData("auto", "build", false)]
        [InlineData("gxengine", "build", true)]
        [InlineData("gxengine", "build", false)]
        [InlineData("gxengine", "rebuild", false)]
        public async Task TheCredentialsPreflightIsRefusedWithoutAReconciliationFenceUnderErrorFallback(
            string backend, string action, bool targeted)
        {
            var run = await RunAsync(backend, "error", targeted ? IssueArguments : WholeKbArguments, action, 5, "", Contract + "\r\n");

            Assert.True(run.EngineInvoked);
            Assert.NotNull(run.Result);
            Assert.True(run.Result!.Value<bool>("isError"));
            JObject payload = PayloadOf(run.Result!);
            Assert.Equal("EngineSqlCredentialsUnavailable", payload["error"]?.ToString());
            Assert.Equal(EngineMessage, payload["message"]?.ToString());
            Assert.Equal(action, payload["action"]?.ToString());
            Assert.Equal(5, payload["engine"]!["exitCode"]!.Value<int>());
            Assert.Equal("error", payload["engine"]!["fallback"]?.ToString());
            Assert.Null(payload["reconciliationRequired"]);
            Assert.Null(payload["mutationOutcomeUnknown"]);
            Assert.Contains("nothing changed", payload["hint"]!.ToString());
            Assert.Contains("Engine.Fallback=sdk", payload["hint"]!.ToString());
            Assert.Contains("refused (Engine.Fallback=error)", string.Join("\n", run.Log));
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task AnExplicitSdkBackendNeverStartsTheEngine(string fallback)
        {
            var run = await RunAsync("sdk", fallback, IssueArguments, "build", 5, "", Contract);

            Assert.False(run.EngineInvoked);
            Assert.Null(run.Result);
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task ThePreflightIsNotAnUncertainBuildSoTheActiveVersionIsNotReReadAfterIt(string fallback)
        {
            int reads = 0;
            var run = await RunAsync("auto", fallback, IssueArguments, "build", 5, "", Contract,
                activeModel: kb =>
                {
                    reads++;
                    // A second read would find a different version and turn this into a reconciliation error.
                    return reads == 1 ? new EngineActiveModel(kb, 4, 2, "Branch") : new EngineActiveModel(kb, 1, 0, "Trunk");
                });

            Assert.Equal(1, reads);
            Assert.Contains("--design-model 4 --kb-version 2", run.EngineArguments);
            if (fallback == "sdk")
            {
                Assert.Null(run.Result);
            }
            else
            {
                Assert.Equal("EngineSqlCredentialsUnavailable", PayloadOf(run.Result!)["error"]?.ToString());
            }
        }

        // ------------------------------------------- anything but the exact contract stays fail-closed

        public static IEnumerable<object[]> NearMisses()
        {
            string Diagnostic(string category = "sql_credentials_unavailable", string message = "\"static\"", string location = "null", string extra = "") =>
                "{\"category\":\"" + category + "\",\"message\":" + message + ",\"location\":" + location + extra + "}";

            yield return new object[] { "stdout-not-empty", "{\"status\":\"complete\"}", Contract + "\n", 5 };
            yield return new object[] { "stdout-blank-line", "\r\n", Contract + "\n", 5 };
            yield return new object[] { "stderr-empty", "", "", 5 };
            yield return new object[] { "stderr-not-json", "", "sqlcmd: login failed\n", 5 };
            yield return new object[] { "json-comment", "", Contract.Insert(1, "/* not the engine protocol */"), 5 };
            yield return new object[] { "json-trailing-comma", "", Contract.Insert(Contract.Length - 1, ","), 5 };
            yield return new object[] { "json-single-quotes", "", "{'category':'sql_credentials_unavailable','message':'static','location':null}", 5 };
            yield return new object[] { "other-category", "", Diagnostic(category: "input") + "\n", 5 };
            yield return new object[] { "category-case", "", Diagnostic(category: "SQL_credentials_unavailable") + "\n", 5 };
            yield return new object[] { "two-diagnostics", "", Contract + "\n" + Contract + "\n", 5 };
            yield return new object[] { "noise-before", "", "gx-engine: sqlcmd failed\n" + Contract + "\n", 5 };
            yield return new object[] { "noise-after", "", Contract + "\nweb theme not read\n", 5 };
            yield return new object[] { "pretty-printed", "", "{\n  \"category\": \"sql_credentials_unavailable\",\n  \"message\": \"static\",\n  \"location\": null\n}\n", 5 };
            yield return new object[] { "two-objects-one-line", "", Contract + Contract + "\n", 5 };
            yield return new object[] { "location-set", "", Diagnostic(location: "{\"line\":1,\"column\":2}") + "\n", 5 };
            yield return new object[] { "extra-property", "", Diagnostic(extra: ",\"detail\":1") + "\n", 5 };
            yield return new object[] { "location-missing", "", "{\"category\":\"sql_credentials_unavailable\",\"message\":\"static\"}\n", 5 };
            yield return new object[] { "message-empty", "", Diagnostic(message: "\"  \"") + "\n", 5 };
            yield return new object[] { "message-not-string", "", Diagnostic(message: "7") + "\n", 5 };
            yield return new object[] { "duplicate-category", "",
                "{\"category\":\"input\",\"category\":\"sql_credentials_unavailable\",\"message\":\"static\",\"location\":null}\n", 5 };
            yield return new object[] { "not-an-object", "", "[" + Contract + "]\n", 5 };
            yield return new object[] { "exit-1", "", Contract + "\n", 1 };
            yield return new object[] { "exit-6", "", Contract + "\n", 6 };
            yield return new object[] { "exit-0", "", Contract + "\n", 0 };
        }

        [Theory]
        [MemberData(nameof(NearMisses))]
        public async Task AnythingButTheExactContractKeepsTheReconciliationFenceAndNeverReplaysOnTheSdk(
            string name, string stdout, string stderr, int exitCode)
        {
            foreach (string fallback in new[] { "sdk", "error" })
            {
                var run = await RunAsync("auto", fallback, IssueArguments, "build", exitCode, stdout, stderr);

                Assert.True(run.EngineInvoked, name);
                Assert.NotNull(run.Result);
                Assert.True(run.Result!.Value<bool>("isError"), name);
                JObject payload = PayloadOf(run.Result!);
                Assert.NotEqual("EngineSqlCredentialsUnavailable", payload["error"]?.ToString());
                Assert.True(payload["reconciliationRequired"]?.Value<bool>(), name + " (" + fallback + ")");
                Assert.True(payload["mutationOutcomeUnknown"]?.Value<bool>(), name + " (" + fallback + ")");
                string logged = string.Join("\n", run.Log);
                Assert.Contains("reconciliation required, no SDK replay", logged);
                Assert.DoesNotContain("continuing on the SDK", logged);
            }
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task ATimedOutEngineKeepsTheFenceEvenWhenItHadPrintedTheCredentialsDiagnostic(string fallback)
        {
            var run = await RunAsync("auto", fallback, IssueArguments, "build", 5, "", Contract + "\n",
                timeoutSeconds: 1, stall: true);

            Assert.True(run.EngineInvoked);
            Assert.NotNull(run.Result);
            JObject payload = PayloadOf(run.Result!);
            Assert.Equal("EngineTimeout", payload["error"]?.ToString());
            Assert.True(payload["reconciliationRequired"]?.Value<bool>());
            Assert.DoesNotContain("continuing on the SDK", string.Join("\n", run.Log));
        }

        // ------------------------------------------------------ diagnostics for every engine outage

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task AFailedBuildLogsItsStageAndMasksCredentials(string fallback)
        {
            string stderr = "gx-engine: sqlcmd execution failed with exit code Some(1): Sqlcmd: Login failed for user 'sa'. Password=hunter2; pwd: s3cret -P topsecret\n"
                + "{\"category\":\"input\",\"message\":\"Password=\\\"escaped-secret\\\" end\"}\n"
                + "Senha:\nweb theme not read\n";
            var run = await RunAsync("auto", fallback, IssueArguments, "build", 1, "", stderr);

            Assert.NotNull(run.Result);
            JObject payload = PayloadOf(run.Result!);
            Assert.Equal("EngineCommandFailed", payload["error"]?.ToString());
            Assert.True(payload["reconciliationRequired"]?.Value<bool>());
            string logged = string.Join("\n", run.Log);
            Assert.Contains("EngineCommandFailed", logged);
            Assert.Contains("exit 1", logged);
            Assert.Contains("Login failed for user", logged);
            Assert.Contains("reconciliation required, no SDK replay", logged);
            Assert.DoesNotContain("\n", run.Log.Single(line => line.Contains("EngineCommandFailed")));
            foreach (string secret in new[] { "hunter2", "s3cret", "topsecret", "escaped-secret" })
            {
                Assert.DoesNotContain(secret, logged);
                Assert.DoesNotContain(secret, payload.ToString());
            }
            Assert.Contains(EngineFailureText.RedactedMarker, payload["stderr"]!.ToString());
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task AVersionScopeRefusalMasksAndBoundsTheTopologyFailureLog(string fallback)
        {
            string diagnostic = new JObject
            {
                ["category"] = "version_scope",
                ["message"] = "topology read failed: SQL_PASSWORD=hunter2\n" + new string('x', 1000),
                ["location"] = null,
            }.ToString(Newtonsoft.Json.Formatting.None);
            var run = await RunAsync("auto", fallback, IssueArguments, "build", 4, "", diagnostic);
            Assert.NotEmpty(run.Log);
            foreach (string line in run.Log)
            {
                Assert.DoesNotContain("hunter2", line);
                Assert.DoesNotContain("\n", line);
                Assert.True(line.Length < 600, "Topology failure log must be bounded");
            }
        }

        [Fact]
        public async Task AMissingEngineIsLoggedWhetherItContinuesOnTheSdkOrIsRefused()
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, "auto");
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, Path.Combine(tempDir, "missing.exe"));
                foreach (string fallback in new[] { "sdk", "error" })
                {
                    SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                    var log = new List<string>();
                    JObject? result = await NewOperations(log, null).TryDispatchAsync(
                        "genexus_lifecycle", "build", JObject.Parse(IssueArguments), tempDir, "kb-alias", CancellationToken.None);

                    Assert.Equal(fallback == "error", result != null);
                    string logged = string.Join("\n", log);
                    Assert.Contains("EngineNotFound", logged);
                    Assert.Contains(fallback == "error" ? "refused (Engine.Fallback=error)" : "continuing on the SDK (engine never started)", logged);
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("password=hunter2;user=sa", "password=[redacted];user=sa")]
        [InlineData("Pwd: hunter2 next", "Pwd: [redacted] next")]
        [InlineData("SQL_PASSWORD=hunter2;DbPassword=other", "SQL_PASSWORD=[redacted];DbPassword=[redacted]")]
        [InlineData("sqlcmd -Phunter2", "sqlcmd -P[redacted]")]
        [InlineData("sqlcmd -P'hunter 2'", "sqlcmd -P[redacted]")]
        [InlineData("SQLCMDPASSWORD=\"hunter 2\" x", "SQLCMDPASSWORD=[redacted] x")]
        [InlineData("{\"message\":\"Password=\\\"hunter2\\\" end\"}", "{\"message\":\"Password=[redacted] end\"}")]
        [InlineData("Password=\\\"hunter 2\\\" next", "Password=[redacted] next")]
        [InlineData("Password=\\\"hunter\\\"2\\\" end", "Password=[redacted] end")]
        [InlineData("Password=abc\\\"def end", "Password=[redacted] end")]
        [InlineData("sqlcmd -P\\\"hunter 2\\\" -d db", "sqlcmd -P[redacted] -d db")]
        [InlineData("sqlcmd -S host -P hunter2 -d db", "sqlcmd -S host -P [redacted] -d db")]
        [InlineData("Senha:\nnext line", "Senha:\nnext line")]
        [InlineData("Login failed for user 'sa'.", "Login failed for user 'sa'.")]
        public void RedactMasksCredentialValuesButNotPromptsOrLogins(string input, string expected)
        {
            Assert.Equal(expected, EngineFailureText.Redact(input));
        }

        [Fact]
        public void AnExcerptNeverSplitsASurrogatePair()
        {
            string text = new string('a', 299) + "\U0001F600" + "tail";
            string excerpt = EngineFailureText.ForLog(text, 300);

            Assert.Equal(new string('a', 299) + "…", excerpt);
        }

        [Fact]
        public void TheEngineMessageIsMaskedAndBounded()
        {
            string longMessage = "Password=hunter2 " + new string('x', 1000);
            string diagnostic = "{\"category\":\"sql_credentials_unavailable\",\"message\":\"" + longMessage + "\",\"location\":null}\n";
            var invocation = GxEngineInvocation.Failure("engine.exe", "EngineCommandFailed", "failed", "", diagnostic, 5);

            string? message = EngineSqlCredentialsPreflight.UnavailableMessage(invocation);

            Assert.NotNull(message);
            Assert.DoesNotContain("hunter2", message);
            Assert.StartsWith("Password=[redacted] ", message);
            Assert.True(message!.Length <= 401);
        }

        // ------------------------------------------------------------------ helpers

        private sealed record Run(JObject? Result, bool EngineInvoked, string EngineArguments, IReadOnlyList<string> Log);

        private static EngineRoutedOperations NewOperations(List<string> log, Func<string, EngineActiveModel?>? activeModel) =>
            new EngineRoutedOperations(
                () => null, log.Add,
                (payload, isError, toolName, toolArgs) => new JObject
                {
                    ["isError"] = isError,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = payload.ToString() }),
                },
                activeModel == null ? null : (kb, _) => Task.FromResult(activeModel(kb)));

        private async Task<Run> RunAsync(
            string backend, string fallback, string argsJson, string action, int exitCode,
            string stdout, string stderr, Func<string, EngineActiveModel?>? activeModel = null,
            int? timeoutSeconds = null, bool stall = false)
        {
            string tempDir = NewTempDir();
            try
            {
                SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
                SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
                if (timeoutSeconds.HasValue)
                    SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, timeoutSeconds.Value.ToString());
                File.WriteAllText(Path.Combine(tempDir, "stdout.txt"), stdout, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, "stderr.txt"), stderr, new UTF8Encoding(false));
                SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(tempDir, "engine.cmd",
                    "@echo off\r\necho %* > \"%~dp0args.txt\"\r\n"
                    + "type \"%~dp0stdout.txt\"\r\n"
                    + ">&2 type \"%~dp0stderr.txt\"\r\n"
                    + (stall ? "ping -n 30 127.0.0.1 >nul\r\n" : "")
                    + $"exit /b {exitCode}\r\n"));
                var log = new List<string>();
                JObject? result = await NewOperations(log, activeModel).TryDispatchAsync(
                    "genexus_lifecycle", action, JObject.Parse(argsJson), tempDir, "kb-alias", CancellationToken.None);
                string argsFile = Path.Combine(tempDir, "args.txt");
                return new Run(result, File.Exists(argsFile), File.Exists(argsFile) ? File.ReadAllText(argsFile) : "", log);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }
}
