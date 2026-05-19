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
            public PreviewService.CliResult Default = new PreviewService.CliResult { ExitCode = 0, StdOut = "", StdErr = "" };

            public PreviewService.CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                Calls.Add((fileName, arguments));
                string verb = arguments?.Split(' ')[0] ?? "";
                if (ByVerb.TryGetValue(verb, out var r)) return r;
                return Default;
            }

            public string Which(string command) => WhichResult;
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
    }
}
