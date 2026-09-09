using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Spawns the published Gateway over stdio for E2E tests gated by
    // [LiveKbFact]. Mirrors the JSON-RPC driver used by scripts under
    // scratch/usability_probe.js etc., but in C# so xunit can run it.
    //
    // Lifecycle: ctor spawns the process; IAsyncLifetime.InitializeAsync runs
    // the JSON-RPC initialize. Call() sends a tools/call request and awaits
    // the matching response. DisposeAsync closes stdin and gives the process
    // a graceful 2s before killing it (worker needs time to release the KB
    // lock; a 500ms kill leaves shared SDK state that crashes the next spawn).
    //
    // v2.6.9 — exposed as public so the test class can take it via
    // IClassFixture<LiveGatewayHarness>. Rapid per-test spawn cycles were
    // crashing the worker mid-boot on the next test ("Worker for KB
    // 'academicohomolog1' crashed/exited"); sharing the harness across all
    // tests in a class is the canonical xunit pattern for expensive resources.
    public sealed class LiveGatewayHarness : IAsyncLifetime, IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr processHandle,
            int flags,
            StringBuilder executablePath,
            ref int size);

        private Process? _process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
        private int _nextId = 1;
        private readonly StringBuilder _stderrBuf = new StringBuilder();
        private readonly object _diagnosticsGate = new object();
        private readonly string? _gatewayLogPath;
        private bool _initialized;

        public LiveGatewayHarness()
        {
            // Resolve the published gateway from the repo root. We walk up from
            // the test bin directory because xunit copies bins to bin/Debug/...
            // Skip spawn entirely when GXMCP_TEST_KB is not set: IClassFixture
            // instances are constructed for every test class regardless of whether
            // any [LiveKbFact] inside actually runs, and spawning a doomed
            // gateway here just wastes ~5s per class on CI.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GXMCP_TEST_KB")))
            {
                return;
            }
            string exe = LocatePublishedGateway()
                ?? throw new InvalidOperationException(
                    "Published Gateway not found. Run build.ps1 before running LiveKbFact tests.");
            _gatewayLogPath = ResolveGatewayLogPath(exe);

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["GX_MCP_PORT"] = ResolveHttpPort().ToString();
            startInfo.EnvironmentVariables["GX_MCP_STDIO"] = "true";
            // GXMCP_TEST_KB opts xUnit into live discovery, but the Gateway
            // already receives the explicit KB through GX_CONFIG_PATH. Passing
            // both makes startup warmup and the configured default race to
            // spawn the same Worker, producing a false BusyReject.
            startInfo.EnvironmentVariables.Remove("GXMCP_TEST_KB");
            _process = new Process
            {
                StartInfo = startInfo
            };
            _process.OutputDataReceived += OnStdout;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_diagnosticsGate) { _stderrBuf.AppendLine(e.Data); }
            };
            try
            {
                _process.Start();
                AssertProcessImage(_process, exe);
            }
            catch
            {
                StopOwnedProcess(_process);
                _process = null;
                throw;
            }
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Give the gateway ~800ms to set up stdio plumbing
            Thread.Sleep(800);
        }

        private static string? LocatePublishedGateway()
        {
            string? configured = Environment.GetEnvironmentVariable("GXMCP_LIVE_GATEWAY_EXE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured))
                    throw new InvalidOperationException(
                        $"GXMCP_LIVE_GATEWAY_EXE must point to an existing absolute executable: {configured}");
                return Path.GetFullPath(configured);
            }

            // Search from test bin upward to find publish/GxMcp.Gateway.exe
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "publish", "GxMcp.Gateway.exe");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private static string ResolveGatewayLogPath(string executablePath)
        {
            string directory = Environment.GetEnvironmentVariable("GXMCP_LOG_DIR") ??
                Path.GetDirectoryName(executablePath)!;
            return Path.Combine(directory, "gateway_debug.log");
        }

        internal static int ResolveDefaultRpcTimeoutMs()
        {
            string? configured = Environment.GetEnvironmentVariable("GXMCP_LIVE_RPC_TIMEOUT_MS");
            if (int.TryParse(configured, out int timeoutMs) && timeoutMs >= 1_000 && timeoutMs <= 7_200_000)
                return timeoutMs;
            return 240_000;
        }

        internal static int ResolveHttpPort()
        {
            string? configured = Environment.GetEnvironmentVariable("GX_MCP_PORT");
            if (int.TryParse(configured, out int requested) && requested is >= 1_024 and <= 65_535)
            {
                if (!CanBindPort(requested))
                    throw new InvalidOperationException($"GX_MCP_PORT {requested} is already in use; live harness refuses proxy mode.");
                return requested;
            }

            for (int candidate = 55_100; candidate <= 55_199; candidate++)
            {
                if (CanBindPort(candidate)) return candidate;
            }
            throw new InvalidOperationException("No free isolated live HTTP port was found in 55100..55199.");
        }

        private static bool CanBindPort(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void AssertProcessImage(Process process, string expectedPath)
        {
            string? actualPath = GetProcessImagePath(process);
            if (string.IsNullOrWhiteSpace(actualPath) ||
                !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Live Gateway process image mismatch (expected '{Path.GetFullPath(expectedPath)}', actual '{actualPath ?? "<unknown>"}').");
            }
        }

        private static string? GetProcessImagePath(Process process)
        {
            try
            {
                var buffer = new StringBuilder(32_768);
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(process.Handle, 0, buffer, ref size))
                    return buffer.ToString();
            }
            catch { }

            try
            {
                process.Refresh();
                return process.MainModule?.FileName;
            }
            catch { return null; }
        }

        internal static string BuildRpcTimeoutDiagnostics(
            string method,
            int timeoutMs,
            bool processExited,
            string? stderrTail,
            string? gatewayLogPath)
        {
            string safeStderr = SanitizeDiagnostics(stderrTail);
            return $"RPC {method} timed out after {timeoutMs}ms; processExited={processExited}; " +
                $"gatewayLog={gatewayLogPath ?? "<unknown>"}; stderrTail={safeStderr}";
        }

        private static string SanitizeDiagnostics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "<empty>";
            try
            {
                return Regex.Replace(
                    value,
                    @"(?i)\b(password|pwd|user\s*id|userid|token|secret|connection\s*string)\b\s*[:=]\s*[^\s;,\r\n]+",
                    "$1=<redacted>");
            }
            catch { return "<unavailable>"; }
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return; // IClassFixture: only initialize once per class
            if (_process == null) return; // GXMCP_TEST_KB unset: harness is a no-op
            var init = await RpcAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "xunit-harness", ["version"] = "1" }
            }, timeoutMs: 30_000);
            if (init?["result"] == null)
                throw new InvalidOperationException("Gateway initialize did not return a result. stderr: " + _stderrBuf);
            // Send notifications/initialized (no response expected)
            SendNotification("notifications/initialized", new JObject());
            // Open the test KB specified in GXMCP_TEST_KB
            string? testKb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            if (!string.IsNullOrEmpty(testKb))
            {
                await CallToolAsync("genexus_kb", new JObject
                {
                    ["action"] = "open",
                    ["path"] = testKb
                }, timeoutMs: 60_000);
            }
            // Allow worker bootstrap to settle (BulkIndex etc.)
            await Task.Delay(3000);
            _initialized = true;
        }

        // IAsyncLifetime contract — xunit calls this once when the fixture is
        // torn down. Currently a no-op (Dispose handles the heavy lifting);
        // kept here so future async cleanup (e.g. waiting for in-flight writes
        // to flush) has a hook to grow into without breaking callers.
        public Task DisposeAsync() => Task.CompletedTask;

        public async Task<JObject> CallToolAsync(string name, JObject args, int timeoutMs = 0)
        {
            var resp = await RpcAsync("tools/call", new JObject
            {
                ["name"] = name,
                ["arguments"] = args ?? new JObject()
            }, timeoutMs);
            return resp;
        }

        public static JObject? ParseToolPayload(JObject toolResponse)
        {
            try
            {
                string txt = toolResponse?["result"]?["content"]?[0]?["text"]?.ToString() ?? "{}";
                return JObject.Parse(txt);
            }
            catch { return null; }
        }

        public static bool IsToolError(JObject toolResponse)
            => toolResponse?["result"]?["isError"]?.ToObject<bool?>() == true;

        private async Task<JObject> RpcAsync(string method, JObject @params, int timeoutMs)
        {
            if (_process == null)
                throw new InvalidOperationException("LiveGatewayHarness has no process — GXMCP_TEST_KB was not set when the fixture was constructed.");
            if (timeoutMs <= 0) timeoutMs = ResolveDefaultRpcTimeoutMs();
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>();
            _pending[id] = tcs;
            var env = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params
            };
            _process.StandardInput.WriteLine(env.ToString(Newtonsoft.Json.Formatting.None));
            _process.StandardInput.Flush();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                bool processExited = false;
                try { processExited = _process.HasExited; } catch { }
                string stderr;
                lock (_diagnosticsGate) { stderr = _stderrBuf.ToString(); }
                throw new TimeoutException(BuildRpcTimeoutDiagnostics(
                    method, timeoutMs, processExited, stderr, _gatewayLogPath));
            }
            return await tcs.Task;
        }

        private void SendNotification(string method, JObject @params)
        {
            if (_process == null) return;
            var env = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = @params
            };
            _process.StandardInput.WriteLine(env.ToString(Newtonsoft.Json.Formatting.None));
            _process.StandardInput.Flush();
        }

        private void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (!e.Data.StartsWith("{")) return;
            JObject msg;
            try { msg = JObject.Parse(e.Data); } catch { return; }
            var idTok = msg["id"];
            if (idTok == null || idTok.Type == JTokenType.Null) return;
            int id = idTok.ToObject<int>();
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
        }

        public void Dispose()
        {
            if (_process == null) return;
            StopOwnedProcess(_process);
        }

        internal static void StopOwnedProcess(Process process)
        {
            try
            {
                process.StandardInput.Close();
                // 2s grace lets the worker release the KB lock + drain its
                // EditSnapshotStore writes. 500ms was too aggressive — the
                // shared SDK state outlived the kill and crashed the next
                // spawn on rapid test-class cycles.
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch { }
            process.Dispose();
        }
    }
}
