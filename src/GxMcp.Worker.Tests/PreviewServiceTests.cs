using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PreviewServiceTests
    {
        /// <summary>Records every CLI invocation and returns scripted responses by verb.</summary>
        private class FakeRunner : PreviewService.ICliRunner
        {
            public List<(string fileName, string arguments)> Calls = new List<(string, string)>();
            public Dictionary<string, PreviewService.CliResult> ByVerb = new Dictionary<string, PreviewService.CliResult>();
            public string WhichResult = "C:/fake/chrome-devtools-axi.cmd";
            public bool ThrowOnWhich;
            public PreviewService.CliResult Default = new PreviewService.CliResult { ExitCode = 0, StdOut = "", StdErr = "" };

            public PreviewService.CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                Calls.Add((fileName, arguments));
                string verb = arguments?.Split(' ')[0] ?? "";
                if (ByVerb.TryGetValue(verb, out var r)) return r;
                return Default;
            }

            public string Which(string command)
            {
                if (ThrowOnWhich) throw new InvalidOperationException("C:\\secrets\\preview-token");
                return WhichResult;
            }
        }

        private static string TempDir()
        {
            string p = Path.Combine(Path.GetTempPath(), "PreviewSvcTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(p);
            return p;
        }

        [Fact]
        public void LoadConfig_CreatesDefaultsWhenMissing()
        {
            var dir = TempDir();
            string cfgPath = Path.Combine(dir, "preview.config.json");
            var runner = new FakeRunner();

            var svc = new PreviewService(null, null, runner, cfgPath, dir);
            var cfg = svc.LoadConfig();

            Assert.True(File.Exists(cfgPath));
            Assert.Equal("http://localhost/portal3_desenv", cfg["baseUrl"]?.ToString());
            Assert.Equal("dani.aspx", cfg["launcher"]?.ToString());
            Assert.NotNull(cfg["defaultParms"]);
            Assert.Equal("5171369", cfg["defaultParms"]?["PesCod"]?.ToString());
        }

        [Fact]
        public void MergeParms_PrecedenceCallerOverObjectOverDefault()
        {
            var cfg = new JObject
            {
                ["defaultParms"] = new JObject { ["a"] = "1", ["b"] = "1", ["c"] = "1" },
                ["objectParms"] = new JObject
                {
                    ["MyPanel"] = new JObject { ["b"] = "2", ["c"] = "2" }
                }
            };
            var caller = new JObject { ["c"] = "3", ["d"] = "3" };
            var merged = PreviewService.MergeParms(cfg, "MyPanel", caller);

            Assert.Equal("1", merged["a"]?.ToString());
            Assert.Equal("2", merged["b"]?.ToString());
            Assert.Equal("3", merged["c"]?.ToString());
            Assert.Equal("3", merged["d"]?.ToString());
        }

        [Fact]
        public void PreviewSync_ReturnsCliMissingWhenProbeFails()
        {
            var dir = TempDir();
            var runner = new FakeRunner { WhichResult = null };
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var r = svc.PreviewSync("AnyPanel", null, "auto", false, 0, new[] { "html" }, false, false);
            Assert.Equal("cli_missing", r["status"]?.ToString());
        }

        [Fact]
        public void PreviewSync_ReturnsAuthRequiredWhenSnapshotShowsLogin()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            runner.ByVerb["snapshot"] = new PreviewService.CliResult
            {
                ExitCode = 0,
                StdOut = "<role=textbox name=Usuario>"
            };
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var r = svc.PreviewSync("AnyPanel", null, "auto", false, 0, new[] { "html" }, false, false);
            Assert.Equal("auth_required", r["status"]?.ToString());
        }

        [Fact]
        public void PreviewSync_ReturnsLauncherMissingWhenFormFieldsAbsent()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            runner.ByVerb["snapshot"] = new PreviewService.CliResult
            {
                ExitCode = 0,
                StdOut = "<html><body>nothing useful here</body></html>"
            };
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var r = svc.PreviewSync("AnyPanel", null, "auto", false, 0, new[] { "html" }, false, false);
            Assert.Equal("launcher_missing", r["status"]?.ToString());
        }

        [Fact]
        public void PreviewSync_UnexpectedFailure_HidesExceptionTextAndReturnsOperationId()
        {
            string dir = TempDir();
            var runner = new FakeRunner { ThrowOnWhich = true };
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);
            var result = svc.PreviewSync("PanelX", null, "auto", false, 0, new[] { "html" }, false, false);
            Assert.Equal("error", result["status"]?.ToString());
            Assert.Equal("Preview failed. See server logs for details.", result["message"]?.ToString());
            Assert.DoesNotContain("preview-token", result.ToString());
            Assert.NotNull(result["operationId"]);
        }

        [Fact]
        public void LogValue_RedactsQuotedPasswordTokenAndAuthorizationValues()
        {
            const string input = "PreviewException: {\"password\":\"password-value\", \"token\": \"token-value\", \"authorization\": \"Bearer auth-value\"}";

            string result = PreviewService.LogValue(input);

            Assert.DoesNotContain("password-value", result);
            Assert.DoesNotContain("token-value", result);
            Assert.DoesNotContain("auth-value", result);
            Assert.Contains("<redacted>", result);
        }

        [Fact]
        public void PreviewSync_OkPathInvokesExpectedCliVerbs()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            // Snapshot returns a launcher-like blob containing PesCod so the form-detect heuristic passes.
            runner.ByVerb["snapshot"] = new PreviewService.CliResult
            {
                ExitCode = 0,
                StdOut = "form with PesCod ano sem aluno fields"
            };
            runner.ByVerb["eval"] = new PreviewService.CliResult { ExitCode = 0, StdOut = "<html></html>" };
            runner.ByVerb["open"] = new PreviewService.CliResult { ExitCode = 0, StdOut = "" };

            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var r = svc.PreviewSync("MyPanel", null, "auto", false, 0, new[] { "html" }, false, false);
            Assert.Equal("ok", r["status"]?.ToString());

            // Expect at least: open, snapshot, eval (form fills) and click eval.
            Assert.Contains(runner.Calls, c => c.arguments.StartsWith("open "));
            Assert.Contains(runner.Calls, c => c.arguments.StartsWith("snapshot"));
            Assert.Contains(runner.Calls, c => c.arguments.StartsWith("eval "));
        }

        [Theory]
        [InlineData("MyPanel_1")]
        [InlineData("Panel123")]
        public void PreviewSync_AcceptsValidGeneXusNamesForArtifactWrites(string name)
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            runner.ByVerb["snapshot"] = new PreviewService.CliResult
            {
                ExitCode = 0,
                StdOut = "{\"root\":{\"role\":\"WebArea\",\"PesCod\":\"x\"}}"
            };
            runner.ByVerb["eval"] = new PreviewService.CliResult { ExitCode = 0, StdOut = "" };

            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);
            var r = svc.PreviewSync(name, null, "auto", false, 0, new[] { "screenshot", "a11y" }, false, true);

            Assert.Equal("ok", r["status"]?.ToString());
            Assert.True(File.Exists(Path.Combine(dir, name + ".a11y.json")));
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, name + ".png")),
                Path.GetFullPath(r["captures"]?["screenshot"]?.ToString()));
            Assert.DoesNotContain(runner.Calls, c => c.arguments.Contains(".."));
        }

        [Theory]
        [InlineData("../escape")]
        [InlineData("..\\escape")]
        [InlineData("C:\\escape")]
        [InlineData("/escape")]
        [InlineData("Panel/name")]
        [InlineData("Panel\\name")]
        [InlineData("Panel:name")]
        [InlineData("Panel*name")]
        [InlineData("Panel?name")]
        [InlineData("Panel\"name")]
        [InlineData("Panel<name")]
        [InlineData("Panel>name")]
        [InlineData("Panel|name")]
        public void PreviewSync_RejectsUnsafeArtifactNamesWithoutRunningCli(string name)
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var r = svc.PreviewSync(name, null, "auto", false, 0,
                new[] { "screenshot", "a11y" }, false, true);

            Assert.Equal("invalid_request", r["status"]?.ToString());
            Assert.Equal("name must be a valid logical preview name", r["message"]?.ToString());
            Assert.Empty(runner.Calls);
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public void PreviewSync_UpdateBaselineWritesA11yFile()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            runner.ByVerb["snapshot"] = new PreviewService.CliResult
            {
                ExitCode = 0,
                StdOut = "{\"root\":{\"role\":\"WebArea\",\"PesCod\":\"x\"}}"
            };
            runner.ByVerb["eval"] = new PreviewService.CliResult { ExitCode = 0, StdOut = "" };

            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var r = svc.PreviewSync("PanelX", null, "auto", false, 0, new[] { "a11y" }, false, true);
            Assert.Equal("ok", r["status"]?.ToString());

            string baseline = Path.Combine(dir, "PanelX.a11y.json");
            Assert.True(File.Exists(baseline));
        }

        [Fact]
        public void ComputeStructuralDiff_DetectsAddedRemovedChanged()
        {
            var a = JObject.Parse("{\"x\":1,\"y\":2,\"nested\":{\"a\":1}}");
            var b = JObject.Parse("{\"x\":1,\"y\":3,\"nested\":{\"b\":1}}");

            var diff = PreviewService.ComputeStructuralDiff(a, b);
            var added = (JArray)diff["added"];
            var removed = (JArray)diff["removed"];
            var changed = (JArray)diff["changed"];

            Assert.Contains(added, t => t.ToString() == "/nested/b");
            Assert.Contains(removed, t => t.ToString() == "/nested/a");
            Assert.Contains(changed, t => t["path"]?.ToString() == "/y");
        }

        // ------------------------------------------------------------------
        // Item 39 — device emulation passthrough
        // Item 97 — slow-network throttle passthrough
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("iPhone12")]
        [InlineData("iPhone15Pro")]
        [InlineData("iPadPro")]
        [InlineData("Pixel7")]
        [InlineData("desktop1920")]
        [InlineData("desktop1280")]
        public void BuildEmulateNetworkArgs_AcceptedEmulateProfiles_AppendFlag(string profile)
        {
            string args = PreviewService.BuildEmulateNetworkArgs(profile, null);
            Assert.Contains("--emulate " + profile, args);
        }

        [Fact]
        public void BuildEmulateNetworkArgs_UnknownEmulateIsDropped()
        {
            string args = PreviewService.BuildEmulateNetworkArgs("NotARealPhone", null);
            Assert.DoesNotContain("--emulate", args);
        }

        [Theory]
        [InlineData("slow3g")]
        [InlineData("fast3g")]
        [InlineData("offline")]
        public void BuildEmulateNetworkArgs_NetworkProfilesEmitThrottle(string profile)
        {
            string args = PreviewService.BuildEmulateNetworkArgs(null, profile);
            Assert.Contains("--throttle " + profile, args);
        }

        [Fact]
        public void BuildEmulateNetworkArgs_FastNetworkProfileIsSkippedToKeepBaselinesStable()
        {
            string args = PreviewService.BuildEmulateNetworkArgs(null, "fast");
            Assert.DoesNotContain("--throttle", args);
        }

        [Fact]
        public void PreviewSync_PassesEmulateAndThrottleToOpenCommand()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            runner.ByVerb["snapshot"] = new PreviewService.CliResult
            {
                ExitCode = 0,
                StdOut = "form with PesCod ano sem aluno"
            };
            runner.ByVerb["eval"] = new PreviewService.CliResult { ExitCode = 0, StdOut = "" };
            runner.ByVerb["open"] = new PreviewService.CliResult { ExitCode = 0, StdOut = "" };

            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);
            var r = svc.PreviewSync("MyPanel", null, "auto", false, 0, new[] { "html" }, false, false,
                fill: null, click: null, auth: null, emulate: "iPhone12", network: "slow3g");

            Assert.Equal("ok", r["status"]?.ToString());
            var openCall = System.Linq.Enumerable.FirstOrDefault(runner.Calls, c => c.arguments.StartsWith("open "));
            Assert.NotNull(openCall.arguments);
            Assert.Contains("--emulate iPhone12", openCall.arguments);
            Assert.Contains("--throttle slow3g", openCall.arguments);
            Assert.Equal("iPhone12", r["emulation"]?["emulate"]?.ToString());
            Assert.Equal("slow3g", r["emulation"]?["network"]?.ToString());
        }
        [Fact]
        public void PreviewSync_RejectsUnsafeObjectNameBeforeDriverCall()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var result = svc.PreviewSync("Panel;alert(1)", null, "auto", false, 0, new[] { "html" }, false, false);

            Assert.Equal("invalid_request", result["status"]?.ToString());
            Assert.Empty(runner.Calls);
        }

        [Fact]
        public void PreviewSync_RejectsControlCharactersInDerivedValues()
        {
            var dir = TempDir();
            var runner = new FakeRunner();
            var svc = new PreviewService(null, null, runner, Path.Combine(dir, "preview.config.json"), dir);

            var result = svc.PreviewSync("Panel", new JObject { ["PesCod"] = new string(new [] { (char)49, (char)13, (char)10, (char)50 }) }, "auto", false, 0, new [] { "html" }, false, false);

            Assert.Equal("invalid_request", result["status"]?.ToString());
            Assert.Empty(runner.Calls);
        }
    }
}
