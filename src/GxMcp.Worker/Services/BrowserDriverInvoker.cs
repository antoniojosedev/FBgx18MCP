using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Shared abstraction over the headless browser CLI (chrome-devtools-axi). Wave-3 browser-verify
    /// services (BrowserCaptureService, SmokeTestService, A11yAuditService) all funnel shell-out
    /// through this seam so tests can mock the CLI without touching a real browser.
    /// </summary>
    public interface IBrowserDriverInvoker
    {
        /// <summary>Absolute path to the chrome-devtools-axi CLI, or null when not installed.</summary>
        string ResolveDriverPath();

        /// <summary>Run a single CLI verb with raw argument string. Never throws — failures land
        /// in <see cref="DriverResult.ExitCode"/> / <see cref="DriverResult.StdErr"/>.</summary>
        DriverResult Invoke(string arguments, int timeoutMs);
    }

    public class DriverResult
    {
        public int ExitCode;
        public string StdOut = string.Empty;
        public string StdErr = string.Empty;
        public bool TimedOut;
        public bool DriverMissing;
    }

    /// <summary>Production invoker. Probes PATH for <c>chrome-devtools-axi</c> on first call,
    /// caches the result; spawns via <c>cmd.exe /c</c> so <c>.cmd</c>/<c>.bat</c> shims resolve.</summary>
    public class DefaultBrowserDriverInvoker : IBrowserDriverInvoker
    {
        private string _cachedPath;
        private bool _probed;
        private readonly object _lock = new object();

        public string ResolveDriverPath()
        {
            if (_probed) return _cachedPath;
            lock (_lock)
            {
                if (_probed) return _cachedPath;
                string resolved = null;
                try
                {
                    foreach (var name in new[] { "chrome-devtools-axi", "chrome-devtools-axi.cmd" })
                    {
                        var psi = new ProcessStartInfo("cmd.exe", "/c where " + name)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var p = Process.Start(psi))
                        {
                            var soBuf = new StringBuilder();
                            p.OutputDataReceived += (s, e) => { if (e.Data != null) soBuf.AppendLine(e.Data); };
                            p.ErrorDataReceived += (s, e) => { /* drain, discard */ };
                            p.BeginOutputReadLine();
                            p.BeginErrorReadLine();
                            p.WaitForExit(5000);
                            if (p.ExitCode == 0)
                            {
                                var line = soBuf.ToString().Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                                if (!string.IsNullOrEmpty(line)) { resolved = line.Trim(); break; }
                            }
                        }
                    }
                }
                catch { }
                _cachedPath = resolved;
                _probed = true;
                return _cachedPath;
            }
        }

        public DriverResult Invoke(string arguments, int timeoutMs)
        {
            var cli = ResolveDriverPath();
            if (string.IsNullOrEmpty(cli))
                return new DriverResult { ExitCode = -1, DriverMissing = true, StdErr = "chrome-devtools-axi not found in PATH" };

            try
            {
                var ext = Path.GetExtension(cli);
                bool isNativeExe = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase);
                ProcessStartInfo psi = isNativeExe
                    ? new ProcessStartInfo(cli, arguments)
                    : new ProcessStartInfo("cmd.exe", "/c \"\"" + cli + "\" " + arguments + "\"");
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (var p = Process.Start(psi))
                {
                    var soBuf = new StringBuilder();
                    var seBuf = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) soBuf.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) seBuf.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(1000); } catch { }
                        return new DriverResult { ExitCode = -1, StdOut = soBuf.ToString(), StdErr = seBuf.ToString(), TimedOut = true };
                    }
                    try { p.WaitForExit(500); } catch { }
                    return new DriverResult { ExitCode = p.ExitCode, StdOut = soBuf.ToString(), StdErr = seBuf.ToString() };
                }
            }
            catch (Exception ex)
            {
                return new DriverResult { ExitCode = -1, StdErr = ex.Message };
            }
        }
    }
}
