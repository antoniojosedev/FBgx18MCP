using System;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Resolved gx-engine backend policy for the Gateway (docs/engine_backend.md).
    ///
    /// Resolution order: the config.json Engine section first, then the
    /// environment overrides, which win. A strict document rejects every engine
    /// environment variable at load time
    /// (Configuration.RejectStrictStructuralEnvironment), so by the time these
    /// settings resolve the file is the only source that can be set. Invalid
    /// non-strict values are logged and ignored rather than taking the gateway
    /// down; strict values are rejected at parse time.
    /// </summary>
    internal sealed class EngineBackendSettings
    {
        internal const string BackendEnvVar = "GXMCP_ENGINE_BACKEND";
        internal const string FallbackEnvVar = "GXMCP_ENGINE_FALLBACK";
        internal const string ExecutableEnvVar = "GXMCP_ENGINE_EXE";
        internal const string TimeoutEnvVar = "GXMCP_ENGINE_TIMEOUT_SECONDS";

        internal const string AutoBackend = "auto";
        internal const string SdkBackend = "sdk";
        internal const string GxEngineBackend = "gxengine";
        internal const string SdkFallback = "sdk";
        internal const string ErrorFallback = "error";

        /// <summary>
        /// Matches the engine README's 120-second native compilation deadline.
        /// </summary>
        internal const int DefaultTimeoutSeconds = 120;

        internal string Backend { get; private set; } = AutoBackend;
        internal string Fallback { get; private set; } = SdkFallback;
        internal string? Executable { get; private set; }
        internal int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

        /// <summary>Auto admits builds only; gxengine also admits eligible reads/queries.</summary>
        internal bool EngineEnabled => Backend != SdkBackend;

        /// <summary>
        /// True when a capability the engine cannot serve must fail instead of
        /// silently continuing on the SDK.
        /// </summary>
        internal bool RejectUnsupported =>
            string.Equals(Fallback, ErrorFallback, StringComparison.OrdinalIgnoreCase);

        internal static EngineBackendSettings Resolve(Configuration? config)
        {
            var settings = new EngineBackendSettings();
            var section = config?.Engine;

            if (section?.Backend != null)
                settings.Backend = NormalizeBackend(section.Backend, "Engine.Backend");
            if (section?.Fallback != null)
                settings.Fallback = NormalizeFallback(section.Fallback, "Engine.Fallback");
            if (!string.IsNullOrWhiteSpace(section?.Executable))
                settings.Executable = section!.Executable!.Trim();
            if (section?.TimeoutSeconds is int fileTimeout)
                settings.TimeoutSeconds = NormalizeTimeout(fileTimeout, "Engine.TimeoutSeconds");

            string? envBackend = global::System.Environment.GetEnvironmentVariable(BackendEnvVar);
            if (!string.IsNullOrWhiteSpace(envBackend))
                settings.Backend = NormalizeBackend(envBackend, BackendEnvVar);

            string? envFallback = global::System.Environment.GetEnvironmentVariable(FallbackEnvVar);
            if (!string.IsNullOrWhiteSpace(envFallback))
                settings.Fallback = NormalizeFallback(envFallback, FallbackEnvVar);

            string? envExecutable = global::System.Environment.GetEnvironmentVariable(ExecutableEnvVar);
            if (!string.IsNullOrWhiteSpace(envExecutable))
                settings.Executable = envExecutable.Trim();

            string? envTimeout = global::System.Environment.GetEnvironmentVariable(TimeoutEnvVar);
            if (!string.IsNullOrWhiteSpace(envTimeout))
            {
                if (int.TryParse(envTimeout.Trim(), out int parsed) && parsed > 0)
                    settings.TimeoutSeconds = parsed;
                else
                    Program.Log($"[Gateway] WARNING: {TimeoutEnvVar} must be a positive integer; ignoring '{envTimeout}'.");
            }

            return settings;
        }

        private static string NormalizeBackend(string value, string source)
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (normalized == AutoBackend || normalized == SdkBackend || normalized == GxEngineBackend) return normalized;
            Program.Log($"[Gateway] WARNING: {source} must be 'auto', 'sdk' or 'gxengine' (got '{value}'); keeping 'sdk'.");
            return SdkBackend;
        }

        private static string NormalizeFallback(string value, string source)
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (normalized == SdkFallback || normalized == ErrorFallback) return normalized;
            Program.Log($"[Gateway] WARNING: {source} must be 'sdk' or 'error' (got '{value}'); keeping 'sdk'.");
            return SdkFallback;
        }

        private static int NormalizeTimeout(int seconds, string source)
        {
            if (seconds > 0) return seconds;
            Program.Log($"[Gateway] WARNING: {source} must be a positive integer (got '{seconds}'); keeping {DefaultTimeoutSeconds}.");
            return DefaultTimeoutSeconds;
        }
    }
}
