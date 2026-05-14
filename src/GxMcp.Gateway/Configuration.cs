using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using System.Threading;
using System.Collections.Generic;

namespace GxMcp.Gateway
{
    public class Configuration
    {
        [JsonProperty("GeneXus")]
        public GeneXusConfig? GeneXus { get; set; }

        [JsonProperty("Server")]
        public ServerConfig? Server { get; set; }

        [JsonProperty("Logging")]
        public LoggingConfig? Logging { get; set; }

        [JsonProperty("Environment")]
        public EnvironmentConfig? Environment { get; set; }

        public static string? CurrentConfigPath { get; private set; }
        private static FileSystemWatcher? _watcher;
        public static event Action<Configuration>? OnConfigurationChanged;

        public static Configuration Load()
        {
            if (CurrentConfigPath == null)
            {
                string? explicitConfigPath = global::System.Environment.GetEnvironmentVariable("GX_CONFIG_PATH");
                if (!string.IsNullOrWhiteSpace(explicitConfigPath))
                {
                    string fullPath = Path.GetFullPath(explicitConfigPath);
                    if (!File.Exists(fullPath))
                    {
                        throw new FileNotFoundException($"GX_CONFIG_PATH points to a missing config.json: {fullPath}");
                    }

                    CurrentConfigPath = fullPath;
                }
                else
                {
                    // Reliable path discovery: look for config.json starting from .exe up to root
                    string? currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                    while (currentDir != null)
                    {
                        string check = Path.Combine(currentDir, "config.json");
                        if (File.Exists(check)) { CurrentConfigPath = check; break; }
                        currentDir = Path.GetDirectoryName(currentDir);
                    }

                    if (CurrentConfigPath == null)
                    {
                        if (File.Exists("config.json")) CurrentConfigPath = Path.GetFullPath("config.json");
                        else throw new FileNotFoundException("Could not find config.json in any parent directory.");
                    }
                }
            }

            Program.Log($"[Gateway] Loading config from: {CurrentConfigPath}");
            var config = ParseConfig(CurrentConfigPath);

            SetupWatcher(CurrentConfigPath);

            return config;
        }

        private static Configuration ParseConfig(string path)
        {
            // Retry logic for file locks
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var config = JsonConvert.DeserializeObject<Configuration>(json);
                    if (config == null) throw new Exception("Failed to parse config.json");
                    
                    if (string.IsNullOrEmpty(config.Environment?.KBPath))
                        Program.Log("[Gateway] WARNING: Environment.KBPath is missing in config.json!");
                    else 
                        Program.Log($"[Gateway] KB Path configured: {config.Environment.KBPath}");

                    string? portOverride = global::System.Environment.GetEnvironmentVariable("GX_MCP_PORT");
                    if (int.TryParse(portOverride, out int httpPortOverride) && httpPortOverride > 0)
                    {
                        config.Server ??= new ServerConfig();
                        config.Server.HttpPort = httpPortOverride;
                        Program.Log($"[Gateway] HTTP port overridden by GX_MCP_PORT={httpPortOverride}");
                    }

                    string? stdioOverride = global::System.Environment.GetEnvironmentVariable("GX_MCP_STDIO");
                    if (bool.TryParse(stdioOverride, out bool mcpStdioOverride))
                    {
                        config.Server ??= new ServerConfig();
                        config.Server.McpStdio = mcpStdioOverride;
                        Program.Log($"[Gateway] MCP stdio overridden by GX_MCP_STDIO={mcpStdioOverride}");
                    }

                    // Legacy migration: Environment.KBPath without KBs[] synthesises one entry.
                    if (config.Environment != null &&
                        (config.Environment.KBs == null || config.Environment.KBs.Count == 0) &&
                        !string.IsNullOrWhiteSpace(config.Environment.KBPath))
                    {
                        string legacyPath = config.Environment.KBPath!;
                        string alias = Path.GetFileName(legacyPath.TrimEnd('\\', '/')).ToLowerInvariant();
                        if (string.IsNullOrEmpty(alias)) alias = "default";
                        config.Environment.KBs = new List<KbEntry>
                        {
                            new KbEntry { Alias = alias, Path = legacyPath }
                        };
                        if (string.IsNullOrWhiteSpace(config.Environment.DefaultKb))
                        {
                            config.Environment.DefaultKb = alias;
                        }
                        Program.Log($"[Gateway] Legacy KBPath migrated to KBs[{alias}], DefaultKb={alias}");
                    }

                    return config;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }
            throw new Exception("Could not read config.json after multiple attempts.");
        }

        private static void SetupWatcher(string path)
        {
            if (_watcher != null) return;

            string dir = Path.GetDirectoryName(path)!;
            string file = Path.GetFileName(path);

            _watcher = new FileSystemWatcher(dir, file);
            _watcher.NotifyFilter = NotifyFilters.LastWrite;
            _watcher.Changed += (s, e) => {
                Program.Log($"[Gateway] Configuration file changed: {e.FullPath}");
                // Add a small delay to ensure writing process has released the lock
                Thread.Sleep(200);
                try {
                    var newConfig = ParseConfig(path);
                    OnConfigurationChanged?.Invoke(newConfig);
                } catch (Exception ex) {
                    Program.Log($"[Gateway] Failed to reload configuration: {ex.Message}");
                }
            };
            _watcher.EnableRaisingEvents = true;
        }
    }

    public class GeneXusConfig
    {
        public string? InstallationPath { get; set; }
        public string? WorkerExecutable { get; set; }
    }

    public class ServerConfig
    {
        public int HttpPort { get; set; } = 5000;
        public bool McpStdio { get; set; } = true;
        public string BindAddress { get; set; } = "127.0.0.1";
        public List<string> AllowedOrigins { get; set; } = new List<string>();
        public int SessionIdleTimeoutMinutes { get; set; } = 10;
        public int WorkerIdleTimeoutMinutes { get; set; } = 5;
        public int IdempotencyTtlMinutes { get; set; } = 15;
        public int IdempotencyCacheSize { get; set; } = 1000;
        /// <summary>
        /// Lifecycle build requests whose estimated_seconds is below this threshold are
        /// executed synchronously (fast-path) and return the build result directly.
        /// Requests at or above the threshold are dispatched asynchronously and return a
        /// job_id immediately. Default: 20 seconds.
        /// </summary>
        public int BuildSyncThresholdSeconds { get; set; } = 20;
        /// <summary>
        /// Maximum number of KBs that may be open simultaneously. Each KB runs in a
        /// dedicated Worker process. When exceeded, the oldest idle Worker is evicted
        /// (LRU); if all are busy, the request fails with KB_POOL_FULL.
        /// </summary>
        public int MaxOpenKbs { get; set; } = 3;
    }

    public class LoggingConfig
    {
        public string? Level { get; set; }
        public string? Path { get; set; }
    }

    public class EnvironmentConfig
    {
        public string? KBPath { get; set; }
        public string? GX_SHADOW_PATH { get; set; }
        public string? DefaultKb { get; set; }
        public List<KbEntry> KBs { get; set; } = new List<KbEntry>();
    }

    public class KbEntry
    {
        public string Alias { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }
}
