using System;
using System.IO;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Finds the gx-engine executable. Mirrors LocateGxwireExecutable
    /// (Program.ToolDispatch.cs): the gateway directory, the packaged publish
    /// layout, then the checkout cargo output. An explicitly configured path
    /// (Engine.Executable / GXMCP_ENGINE_EXE) is authoritative: when it is set
    /// but missing, the locator returns null instead of silently running a
    /// different binary.
    /// </summary>
    internal static class GxEngineLocator
    {
        internal const string ExecutableFileName = "gx-engine.exe";

        internal static string? Locate(EngineBackendSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!string.IsNullOrWhiteSpace(settings.Executable))
                return File.Exists(settings.Executable) ? settings.Executable : null;

            string appDir = AppContext.BaseDirectory;
            foreach (string candidate in new[]
            {
                Path.Combine(appDir, ExecutableFileName),
                Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "publish", ExecutableFileName)),
                Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "src", "gx-engine", "target", "release", ExecutableFileName)),
                Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "src", "gx-engine", "target", "debug", ExecutableFileName)),
                Path.Combine(Directory.GetCurrentDirectory(), "publish", ExecutableFileName),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "gx-engine", "target", "release", ExecutableFileName),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "gx-engine", "target", "debug", ExecutableFileName),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }

            string? pathEnv = global::System.Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string onPath = Path.Combine(directory, ExecutableFileName);
                        if (File.Exists(onPath)) return onPath;
                    }
                    catch { /* malformed PATH entry — skip it */ }
                }
            }

            return null;
        }
    }
}
