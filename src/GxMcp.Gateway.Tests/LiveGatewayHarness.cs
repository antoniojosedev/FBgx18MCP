using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    // Spawns the published Gateway over stdio for E2E tests gated by
    // [LiveKbFact]. Mirrors the JSON-RPC driver used by scripts under
    // scratch/usability_probe.js etc., but in C# so xunit can run it.
    //
    // Lifecycle: ctor spawns the process and runs initialize. Call() sends a
    // tools/call request and awaits the matching response. Dispose closes stdin
    // and gives the process 500ms before killing it.
    internal sealed class LiveGatewayHarness : IDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
        private int _nextId = 1;
        private readonly StringBuilder _stderrBuf = new StringBuilder();

        public LiveGatewayHarness()
        {
            // Resolve the published gateway from the repo root. We walk up from
            // the test bin directory because xunit copies bins to bin/Debug/...
            string exe = LocatePublishedGateway()
                ?? throw new InvalidOperationException(
                    "Published Gateway not found. Run build.ps1 before running LiveKbFact tests.");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _process.OutputDataReceived += OnStdout;
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _stderrBuf.AppendLine(e.Data); };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Give the gateway ~800ms to set up stdio plumbing
            Thread.Sleep(800);
        }

        private static string LocatePublishedGateway()
        {
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

        public async Task InitializeAsync()
        {
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
            // Allow worker bootstrap to settle (BulkIndex etc.)
            await Task.Delay(5000);
        }

        public async Task<JObject> CallToolAsync(string name, JObject args, int timeoutMs = 240_000)
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
                throw new TimeoutException($"RPC {method} timed out after {timeoutMs}ms");
            }
            return await tcs.Task;
        }

        private void SendNotification(string method, JObject @params)
        {
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
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(500)) _process.Kill();
            }
            catch { }
            _process.Dispose();
        }
    }
}
