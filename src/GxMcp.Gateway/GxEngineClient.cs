using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    /// <summary>Outcome of one engine version probe.</summary>
    internal sealed class GxEngineProbe
    {
        private GxEngineProbe(bool available, string? executable, string? version, string? errorCode, string? errorMessage)
        {
            Available = available;
            Executable = executable;
            Version = version;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        internal bool Available { get; }
        internal string? Executable { get; }
        internal string? Version { get; }
        internal string? ErrorCode { get; }
        internal string? ErrorMessage { get; }

        internal static GxEngineProbe Ok(string executable, string version) =>
            new GxEngineProbe(true, executable, version, null, null);

        internal static GxEngineProbe Unavailable(string? executable, string errorCode, string errorMessage) =>
            new GxEngineProbe(false, executable, null, errorCode, errorMessage);
    }

    /// <summary>
    /// The Gateway-side client for the experimental gx-engine backend. For now it
    /// resolves the executable and runs the <c>--version</c> handshake; capability
    /// invocations (gx-engine check/build/run and friends) plug in here when the
    /// first parity capability with a consumer is registered
    /// (docs/engine_backend.md).
    /// </summary>
    internal static class GxEngineClient
    {
        internal const string VersionLinePrefix = "gx-engine ";

        /// <summary>
        /// Version probes answer a diagnostic call and must never stall it; the
        /// build/invocation budget (Engine.TimeoutSeconds) is deliberately not
        /// used here.
        /// </summary>
        internal const int ProbeTimeoutSeconds = 10;

        /// <summary>
        /// Parses the <c>--version</c> line: exactly <c>gx-engine &lt;version&gt;</c>
        /// on the first non-empty line, so a stray compiler warning cannot be
        /// mistaken for the handshake.
        /// </summary>
        internal static bool TryParseVersionOutput(string? stdout, out string version)
        {
            version = string.Empty;
            if (string.IsNullOrWhiteSpace(stdout)) return false;

            foreach (string rawLine in stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (!line.StartsWith(VersionLinePrefix, StringComparison.Ordinal)) return false;
                string candidate = line.Substring(VersionLinePrefix.Length).Trim();
                if (candidate.Length == 0 || candidate.Contains(' ')) return false;
                version = candidate;
                return true;
            }

            return false;
        }

        internal static async Task<GxEngineProbe> ProbeAsync(EngineBackendSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            string? executable = GxEngineLocator.Locate(settings);
            if (executable == null)
            {
                string detail = string.IsNullOrWhiteSpace(settings.Executable)
                    ? "gx-engine.exe was not found next to the Gateway, in publish/ or src/gx-engine/target/{release,debug}, or on PATH."
                    : $"Engine.Executable/GXMCP_ENGINE_EXE points at '{settings.Executable}', which does not exist.";
                return GxEngineProbe.Unavailable(null, "EngineNotFound", detail);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");

            using var proc = new Process { StartInfo = startInfo };
            try
            {
                if (!proc.Start())
                    return GxEngineProbe.Unavailable(executable, "EngineStartFailed", "the process did not start.");
            }
            catch (Exception ex)
            {
                return GxEngineProbe.Unavailable(executable, "EngineStartFailed", ex.Message);
            }

            GxEngineProcessRun run = await GxEngineProcessRunner.RunAsync(proc, ProbeTimeoutSeconds).ConfigureAwait(false);
            if (run.TimedOut)
                return GxEngineProbe.Unavailable(executable, "EngineTimeout", $"no version line within {ProbeTimeoutSeconds}s.");
            if (run.ExitCode != 0)
                return GxEngineProbe.Unavailable(executable, "EngineProbeFailed", $"--version exited with {run.ExitCode}: {Excerpt(run.StandardError)}");
            if (!TryParseVersionOutput(run.StandardOutput, out string version))
                return GxEngineProbe.Unavailable(executable, "EngineVersionUnrecognized", $"--version output was not recognized: {Excerpt(run.StandardOutput)}");

            return GxEngineProbe.Ok(executable, version);
        }

        private static string Excerpt(string text)
        {
            string single = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return single.Length <= 240 ? single : single.Substring(0, 240) + "…";
        }
    }
}
