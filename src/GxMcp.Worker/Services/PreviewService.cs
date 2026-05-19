using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// W4 — Render preview. Drives <c>chrome-devtools-axi</c> CLI to open a built
    /// WebPanel via a launcher page, fill required parms, capture HTML/a11y/screenshot
    /// and optionally diff against a stored baseline.
    /// All shell-out behaviour is funneled through <see cref="ICliRunner"/> so tests
    /// can mock the CLI without touching a real browser.
    /// </summary>
    public class PreviewService
    {
        /// <summary>Abstracts running an external command. Tests inject a fake.</summary>
        public interface ICliRunner
        {
            CliResult Run(string fileName, string arguments, int timeoutMs);
            /// <summary>Returns CLI absolute path if found (via 'where' or shell PATH), else null.</summary>
            string Which(string command);
        }

        public class CliResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
            public bool TimedOut;
        }

        public class DefaultCliRunner : ICliRunner
        {
            public CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return new CliResult { ExitCode = -1, StdOut = so, StdErr = se, TimedOut = true };
                    }
                    return new CliResult { ExitCode = p.ExitCode, StdOut = so, StdErr = se };
                }
            }

            public string Which(string command)
            {
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c where " + command)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string so = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        if (p.ExitCode == 0)
                        {
                            var line = so.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                            return line?.Trim();
                        }
                    }
                }
                catch { }
                return null;
            }
        }

        private readonly ObjectService _objectService;
        private readonly BuildService _buildService;
        private readonly ICliRunner _runner;
        private readonly string _configPath;
        private readonly string _baselineRootOverride;
        private JObject _cachedConfig;
        private string _cachedCliPath;

        private const int DefaultCliTimeoutMs = 30000;

        public PreviewService(ObjectService objectService, BuildService buildService)
            : this(objectService, buildService, new DefaultCliRunner(), null, null) { }

        public PreviewService(ObjectService objectService, BuildService buildService,
            ICliRunner runner, string configPath, string baselineRootOverride)
        {
            _objectService = objectService;
            _buildService = buildService;
            _runner = runner ?? new DefaultCliRunner();
            _configPath = configPath ?? DefaultConfigPath();
            _baselineRootOverride = baselineRootOverride;
        }

        private static string DefaultConfigPath()
        {
            // publish/worker/preview.config.json relative to the worker exe; falls back
            // to %CD%/publish/worker/... for dev runs.
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                return Path.Combine(baseDir, "preview.config.json");
            }
            catch
            {
                return "preview.config.json";
            }
        }

        public static JObject DefaultConfig()
        {
            return new JObject
            {
                ["baseUrl"] = "http://localhost/portal3_desenv",
                ["launcher"] = "dani.aspx",
                ["defaultParms"] = new JObject
                {
                    ["PesCod"] = "5171369",
                    ["ano"] = "27",
                    ["sem"] = "1",
                    ["aluno"] = "27,1,6179"
                },
                ["objectParms"] = new JObject(),
                ["axiCli"] = null,
                ["baselineDir"] = "publish/worker/preview-baselines"
            };
        }

        internal JObject LoadConfig()
        {
            if (_cachedConfig != null) return _cachedConfig;
            try
            {
                if (!File.Exists(_configPath))
                {
                    var def = DefaultConfig();
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? ".");
                        File.WriteAllText(_configPath, def.ToString(Formatting.Indented));
                    }
                    catch { /* best-effort write */ }
                    _cachedConfig = def;
                }
                else
                {
                    _cachedConfig = JObject.Parse(File.ReadAllText(_configPath));
                }
            }
            catch
            {
                _cachedConfig = DefaultConfig();
            }
            return _cachedConfig;
        }

        internal string ResolveCli()
        {
            if (!string.IsNullOrEmpty(_cachedCliPath)) return _cachedCliPath;
            var cfg = LoadConfig();
            string fromCfg = cfg["axiCli"]?.ToString();
            if (!string.IsNullOrEmpty(fromCfg) && File.Exists(fromCfg))
            {
                _cachedCliPath = fromCfg;
                return _cachedCliPath;
            }
            // Probe via PATH
            var probed = _runner.Which("chrome-devtools-axi") ?? _runner.Which("chrome-devtools-axi.cmd");
            if (!string.IsNullOrEmpty(probed))
            {
                _cachedCliPath = probed;
                return _cachedCliPath;
            }
            return null;
        }

        /// <summary>Merge precedence: defaultParms &lt; objectParms[name] &lt; caller.</summary>
        internal static JObject MergeParms(JObject config, string name, JObject caller)
        {
            var merged = new JObject();
            var def = config?["defaultParms"] as JObject;
            if (def != null) foreach (var p in def.Properties()) merged[p.Name] = p.Value?.DeepClone();
            var perObj = config?["objectParms"]?[name] as JObject;
            if (perObj != null) foreach (var p in perObj.Properties()) merged[p.Name] = p.Value?.DeepClone();
            if (caller != null) foreach (var p in caller.Properties()) merged[p.Name] = p.Value?.DeepClone();
            return merged;
        }

        /// <summary>
        /// Main entry. Returns a JObject with at minimum a 'status' field. Never throws.
        /// </summary>
        public Task<JObject> PreviewAsync(
            string name,
            JObject parms = null,
            string launcher = "auto",
            bool buildFirst = false,
            int waitMs = 3000,
            string[] capture = null,
            bool diffBaseline = false,
            bool updateBaseline = false)
        {
            return Task.FromResult(PreviewSync(name, parms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline));
        }

        internal JObject PreviewSync(
            string name,
            JObject parms,
            string launcher,
            bool buildFirst,
            int waitMs,
            string[] capture,
            bool diffBaseline,
            bool updateBaseline)
        {
            var result = new JObject { ["name"] = name };
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    result["status"] = "invalid_request";
                    result["message"] = "name is required";
                    return result;
                }

                // 1) Object type check (best-effort; skipped if ObjectService unavailable in tests)
                if (_objectService != null)
                {
                    try
                    {
                        var obj = _objectService.FindObject(name);
                        if (obj != null)
                        {
                            string typeName = null;
                            try { typeName = obj.TypeDescriptor?.Name; } catch { }
                            if (!string.IsNullOrEmpty(typeName) &&
                                !string.Equals(typeName, "WebPanel", StringComparison.OrdinalIgnoreCase))
                            {
                                result["status"] = "unsupported_object_type";
                                result["type"] = typeName;
                                return result;
                            }
                        }
                    }
                    catch { /* type check best-effort */ }
                }

                var cfg = LoadConfig();
                var mergedParms = MergeParms(cfg, name, parms);
                result["parms"] = mergedParms;

                // 2) Optional buildFirst
                if (buildFirst && _buildService != null)
                {
                    try
                    {
                        var buildJson = _buildService.Build("Build", name);
                        var buildEnv = JObject.Parse(buildJson);
                        string buildStatus = buildEnv["status"]?.ToString();
                        if (string.Equals(buildStatus, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(buildStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(buildStatus, "Error", StringComparison.OrdinalIgnoreCase))
                        {
                            result["status"] = "build_failed";
                            result["buildError"] = buildEnv;
                            return result;
                        }
                        result["build"] = buildEnv;
                        // Note: build is async in this codebase. Caller can poll separately.
                        // For preview parity we proceed; agent may pass buildFirst=false and
                        // build via lifecycle ahead of time for full correctness.
                    }
                    catch (Exception bex)
                    {
                        result["status"] = "build_failed";
                        result["buildError"] = bex.Message;
                        return result;
                    }
                }

                // 3) Resolve CLI
                var cli = ResolveCli();
                if (string.IsNullOrEmpty(cli))
                {
                    result["status"] = "cli_missing";
                    result["message"] = "chrome-devtools-axi not found in PATH and not configured in preview.config.json (axiCli).";
                    return result;
                }
                result["cli"] = cli;

                // 4) Build launcher URL
                string baseUrl = (cfg["baseUrl"]?.ToString() ?? "http://localhost/portal3_desenv").TrimEnd('/');
                string launcherPage = (launcher == null || launcher == "auto") ? (cfg["launcher"]?.ToString() ?? "dani.aspx") : launcher;
                string launcherUrl = baseUrl + "/" + launcherPage.TrimStart('/');
                result["launcherUrl"] = launcherUrl;

                // 5) Open launcher
                var openRes = _runner.Run(cli, "open " + Quote(launcherUrl), DefaultCliTimeoutMs);
                if (openRes.TimedOut || openRes.ExitCode != 0)
                {
                    result["status"] = "launcher_missing";
                    result["url"] = launcherUrl;
                    result["stderr"] = openRes.StdErr;
                    return result;
                }

                // 6) Snapshot launcher to detect auth/form
                var snap1 = _runner.Run(cli, "snapshot", DefaultCliTimeoutMs);
                string snap1Text = snap1.StdOut ?? "";
                if (LooksLikeAuthScreen(snap1Text))
                {
                    result["status"] = "auth_required";
                    result["url"] = launcherUrl;
                    return result;
                }
                if (!LooksLikeLauncherForm(snap1Text, mergedParms))
                {
                    result["status"] = "launcher_missing";
                    result["url"] = launcherUrl;
                    result["message"] = "Launcher page loaded but expected form fields were not detected.";
                    return result;
                }

                // 7) Fill form fields per merged parms (eval document.getElementsByName)
                foreach (var p in mergedParms.Properties())
                {
                    string js = string.Format(
                        "var el=document.getElementsByName('{0}')[0]; if(el){{el.value={1};el.dispatchEvent(new Event('change'));}}",
                        EscapeJs(p.Name),
                        JsonConvert.SerializeObject(p.Value?.ToString() ?? ""));
                    _runner.Run(cli, "eval " + Quote(js), DefaultCliTimeoutMs);
                }

                // 8) Click the link matching name OR href contains <name>.aspx
                string clickJs = string.Format(
                    "var links=document.querySelectorAll('a'); for(var i=0;i<links.length;i++){{var l=links[i]; if((l.textContent||'').trim().toLowerCase()==='{0}'.toLowerCase() || (l.getAttribute('href')||'').toLowerCase().indexOf('{0}.aspx'.toLowerCase())>=0){{l.click(); break;}}}}",
                    EscapeJs(name));
                _runner.Run(cli, "eval " + Quote(clickJs), DefaultCliTimeoutMs);

                // 9) Wait
                if (waitMs > 0)
                {
                    try { System.Threading.Thread.Sleep(Math.Min(waitMs, 120000)); } catch { }
                }

                // 10) Capture
                var captureSet = new HashSet<string>(
                    capture ?? new[] { "html", "a11y" },
                    StringComparer.OrdinalIgnoreCase);
                var captures = new JObject();

                JObject a11yObj = null;
                if (captureSet.Contains("a11y"))
                {
                    var snap2 = _runner.Run(cli, "snapshot", DefaultCliTimeoutMs);
                    captures["a11y_raw"] = snap2.StdOut ?? "";
                    a11yObj = TryParseJson(snap2.StdOut);
                    if (a11yObj != null) captures["a11y"] = a11yObj;
                }
                if (captureSet.Contains("html"))
                {
                    var htmlRes = _runner.Run(cli, "eval " + Quote("document.documentElement.outerHTML"), DefaultCliTimeoutMs);
                    captures["html"] = htmlRes.StdOut ?? "";
                }
                if (captureSet.Contains("console"))
                {
                    var conRes = _runner.Run(cli, "eval " + Quote("JSON.stringify(window.__gxConsoleErrors||[])"), DefaultCliTimeoutMs);
                    captures["console"] = conRes.StdOut ?? "";
                }
                if (captureSet.Contains("screenshot"))
                {
                    string dir = ResolveBaselineDir(cfg);
                    try { Directory.CreateDirectory(dir); } catch { }
                    string shotPath = Path.Combine(dir, name + ".png");
                    var shotRes = _runner.Run(cli, "screenshot " + Quote(shotPath), DefaultCliTimeoutMs);
                    captures["screenshot"] = shotPath;
                    if (shotRes.ExitCode != 0) captures["screenshotError"] = shotRes.StdErr;
                }

                result["captures"] = captures;

                // 11) Baseline diff / update
                string baselineDir = ResolveBaselineDir(cfg);
                string baselinePath = Path.Combine(baselineDir, name + ".a11y.json");
                if (diffBaseline)
                {
                    if (File.Exists(baselinePath) && a11yObj != null)
                    {
                        try
                        {
                            var baseline = JObject.Parse(File.ReadAllText(baselinePath));
                            result["diff"] = ComputeStructuralDiff(baseline, a11yObj);
                        }
                        catch (Exception dex)
                        {
                            result["diff"] = null;
                            result["diffError"] = dex.Message;
                        }
                    }
                    else
                    {
                        result["diff"] = null;
                    }
                }
                if (updateBaseline && a11yObj != null)
                {
                    try
                    {
                        Directory.CreateDirectory(baselineDir);
                        File.WriteAllText(baselinePath, a11yObj.ToString(Formatting.Indented));
                        result["baselineUpdated"] = baselinePath;
                    }
                    catch (Exception wex)
                    {
                        result["baselineUpdateError"] = wex.Message;
                    }
                }

                result["status"] = "ok";
                return result;
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["message"] = ex.Message;
                return result;
            }
        }

        private string ResolveBaselineDir(JObject cfg)
        {
            if (!string.IsNullOrEmpty(_baselineRootOverride)) return _baselineRootOverride;
            string fromCfg = cfg?["baselineDir"]?.ToString();
            if (string.IsNullOrEmpty(fromCfg)) fromCfg = "publish/worker/preview-baselines";
            if (Path.IsPathRooted(fromCfg)) return fromCfg;
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                return Path.Combine(baseDir, "preview-baselines");
            }
            catch
            {
                return fromCfg;
            }
        }

        internal static bool LooksLikeAuthScreen(string snapshot)
        {
            if (string.IsNullOrEmpty(snapshot)) return false;
            // Heuristic mentioned in spec — Usuario textbox is the prod login marker.
            var s = snapshot;
            return (s.IndexOf("Usuario", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    s.IndexOf("textbox", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   s.IndexOf("login.aspx", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool LooksLikeLauncherForm(string snapshot, JObject parms)
        {
            if (string.IsNullOrEmpty(snapshot)) return false;
            // Require at least one configured parm name to appear in snapshot.
            if (parms == null || parms.Count == 0)
            {
                // fall back to the documented defaults
                return ContainsAny(snapshot, "PesCod", "ano", "sem", "aluno");
            }
            foreach (var p in parms.Properties())
            {
                if (snapshot.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static JObject ComputeStructuralDiff(JObject baseline, JObject current)
        {
            var added = new JArray();
            var removed = new JArray();
            var changed = new JArray();
            DiffWalk("", baseline, current, added, removed, changed);
            return new JObject
            {
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed
            };
        }

        private static void DiffWalk(string path, JToken a, JToken b, JArray added, JArray removed, JArray changed)
        {
            if (a == null && b == null) return;
            if (a == null) { added.Add(path); return; }
            if (b == null) { removed.Add(path); return; }
            if (a.Type != b.Type)
            {
                changed.Add(new JObject { ["path"] = path, ["from"] = a.Type.ToString(), ["to"] = b.Type.ToString() });
                return;
            }
            if (a is JObject ao && b is JObject bo)
            {
                var keys = new HashSet<string>(ao.Properties().Select(p => p.Name));
                keys.UnionWith(bo.Properties().Select(p => p.Name));
                foreach (var k in keys)
                {
                    DiffWalk(path + "/" + k, ao[k], bo[k], added, removed, changed);
                }
            }
            else if (a is JArray aa && b is JArray ba)
            {
                int max = Math.Max(aa.Count, ba.Count);
                for (int i = 0; i < max; i++)
                {
                    JToken ax = i < aa.Count ? aa[i] : null;
                    JToken bx = i < ba.Count ? ba[i] : null;
                    DiffWalk(path + "[" + i + "]", ax, bx, added, removed, changed);
                }
            }
            else
            {
                if (!JToken.DeepEquals(a, b))
                    changed.Add(new JObject { ["path"] = path, ["from"] = a.ToString(), ["to"] = b.ToString() });
            }
        }

        private static JObject TryParseJson(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return JObject.Parse(s); } catch { return null; }
        }

        private static string Quote(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string EscapeJs(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
