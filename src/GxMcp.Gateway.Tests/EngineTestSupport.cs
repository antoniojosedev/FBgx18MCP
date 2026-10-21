using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Saves and restores the GXMCP_ENGINE_* environment a test mutates. The
    /// assembly disables test parallelization (TestCollections.cs), so the
    /// mutation is race-free.
    /// </summary>
    public abstract class EngineEnvironmentTestBase : IDisposable
    {
        private readonly List<(string Name, string? Value)> _savedEnvironment = new();

        protected void SetEngineEnv(string name, string? value)
        {
            _savedEnvironment.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            // Reverse order: a test (and a constructor baseline) may set the same variable
            // more than once, and only the last-set value reflects the state that existed
            // before this instance touched it.
            for (int i = _savedEnvironment.Count - 1; i >= 0; i--)
                Environment.SetEnvironmentVariable(_savedEnvironment[i].Name, _savedEnvironment[i].Value);
        }
    }

    /// <summary>
    /// Baseline (gxengine backend, SDK fallback) and the stub-engine helpers shared by
    /// the engine routing tests: every stub engine is a .cmd that echoes canned JSON,
    /// so a test exercises the full Gateway-to-process contract without cargo or a KB.
    /// </summary>
    public abstract class EngineRoutingTestBase : EngineEnvironmentTestBase
    {
        protected EngineRoutingTestBase()
        {
            SetEngineEnv(EngineBackendSettings.BackendEnvVar, "gxengine");
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, "sdk");
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, null);
            SetEngineEnv(EngineBackendSettings.TimeoutEnvVar, null);
        }

        protected static Task<JObject?> DispatchAsync(string tool, string? action, string argsJson, string kbPath)
        {
            return Program.TryDispatchEngineRoutedOperationAsync(
                tool, action, JObject.Parse(argsJson), kbPath, "kb-alias", CancellationToken.None);
        }

        protected static JObject PayloadOf(JObject toolResult)
        {
            string text = toolResult["content"]![0]!["text"]!.ToString();
            return JObject.Parse(text);
        }

        protected static string NewTempDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-engine-routing-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        protected static string CreateStub(string directory, string name, string content)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, content, Encoding.ASCII);
            return path;
        }

        // An absent directory is harmless; deletion errors must fail the test, not hide leaks.
        protected static void TryDeleteDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
