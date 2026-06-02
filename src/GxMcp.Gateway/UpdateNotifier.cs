using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Checks GitHub for a newer release on first `initialize` and, if found,
    // emits an MCP `notifications/message` so the AI client (Claude Desktop /
    // Cursor / Antigravity) shows the update banner inside the chat instead
    // of on a terminal stderr the user can't see.
    //
    // Disable with GENEXUS_MCP_NO_UPDATE_CHECK=1. Result cached 24h in
    // %LOCALAPPDATA%\GenexusMCP\update-check.json (mirrors cli/lib/update-check.js).
    internal static class UpdateNotifier
    {
        private const string Repo = "lennix1337/Genexus18MCP";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);

        private static int _triggered;

        /// <summary>
        /// Read-only snapshot of the last update check (cache only — does not trigger a fetch).
        /// Surfaced via <c>genexus_whoami.update</c> so the LLM can detect update availability
        /// as structured data, not just as a stderr notification the user might miss.
        /// </summary>
        public static JObject? GetCachedStatusSync()
        {
            try
            {
                string? current = GetCurrentVersion();
                if (string.IsNullOrEmpty(current)) return null;

                var cached = ReadCache();
                if (cached == null || string.IsNullOrEmpty(cached.LatestVersion))
                {
                    // No prior check yet — return current version only, no update info.
                    return new JObject {
                        ["currentVersion"] = current,
                        ["latestVersion"] = null,
                        ["updateAvailable"] = false,
                        ["checkedAt"] = null,
                        ["note"] = "no update-check yet (runs on next initialize handshake)"
                    };
                }

                bool available = CompareSemver(cached.LatestVersion!, current!) > 0;
                var result = new JObject {
                    ["currentVersion"] = current,
                    ["latestVersion"] = cached.LatestVersion,
                    ["updateAvailable"] = available,
                    ["checkedAt"] = cached.CheckedAt.ToString("o"),
                    ["releaseUrl"] = cached.ReleaseUrl,
                    ["command"] = available ? "npx genexus-mcp@latest init" : null,
                    ["restartRequired"] = available
                };
                // Corporate self-update: surface a staged build waiting for restart so
                // the LLM can tell the user "restart to finish updating to vX".
                var staged = SelfUpdater.GetStagedStatusSync();
                if (staged != null) result["staged"] = staged;
                return result;
            }
            catch (Exception ex)
            {
                Program.Log($"[UpdateCheck] GetCachedStatusSync: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public static void TriggerOnce()
        {
            if (Interlocked.CompareExchange(ref _triggered, 1, 0) != 0) return;
            if (Environment.GetEnvironmentVariable("GENEXUS_MCP_NO_UPDATE_CHECK") == "1") return;

            _ = Task.Run(RunAsync);
        }

        private static async Task RunAsync()
        {
            try
            {
                string? current = GetCurrentVersion();
                if (string.IsNullOrEmpty(current)) return;

                var (latest, releaseUrl) = await ResolveLatestVersionAsync();
                if (string.IsNullOrEmpty(latest)) return;

                if (CompareSemver(latest!, current!) <= 0) return;

                // Corporate fixed-path installs can't auto-update via npx, so stage
                // the new build in the background; it applies on the next launch.
                // No-op (and silent) for npx-cache launches.
                if (SelfUpdater.IsManagedInstall())
                {
                    try { await SelfUpdater.MaybeStageAsync(latest!); }
                    catch (Exception ex) { Program.Log($"[UpdateCheck] staging error: {ex.Message}"); }
                }

                await EmitNotificationAsync(current!, latest!, releaseUrl);
            }
            catch (Exception ex)
            {
                Program.Log($"[UpdateCheck] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string? GetCurrentVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string? v = info?.InformationalVersion ?? asm.GetName().Version?.ToString();
            if (string.IsNullOrEmpty(v)) return null;
            int plus = v!.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }

        private static async Task<(string? version, string? url)> ResolveLatestVersionAsync()
        {
            var cached = ReadCache();
            if (cached != null && DateTime.UtcNow - cached.CheckedAt < CacheTtl)
            {
                return (cached.LatestVersion, cached.ReleaseUrl);
            }

            var fetched = await FetchLatestReleaseAsync();
            if (fetched.version != null)
            {
                WriteCache(new CacheEntry
                {
                    CheckedAt = DateTime.UtcNow,
                    LatestVersion = fetched.version,
                    ReleaseUrl = fetched.url
                });
            }
            return fetched;
        }

        // Authority is the npm registry (that's what `npm install -g <pkg>@latest`
        // resolves), so we never advertise a version npm can't install yet — the
        // GitHub-release-before-npm-publish window used to cause exactly that — and
        // we keep working on networks that allow npm but block api.github.com.
        // GitHub releases is a fallback only; the release URL is derived from the
        // version so no GitHub API call is needed on the happy path.
        private const string NpmPackage = "genexus-mcp";

        private static string ReleaseUrlFor(string version) => $"https://github.com/{Repo}/releases/tag/v{StripV(version)}";

        private static async Task<(string? version, string? url)> FetchLatestReleaseAsync()
        {
            using var http = new HttpClient { Timeout = FetchTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("genexus-mcp-gateway");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 1. npm registry dist-tags (lightweight; the source of truth for installs).
            try
            {
                var npmResp = await http.GetAsync($"https://registry.npmjs.org/-/package/{NpmPackage}/dist-tags");
                if (npmResp.IsSuccessStatusCode)
                {
                    var tags = JObject.Parse(await npmResp.Content.ReadAsStringAsync());
                    string npmTag = StripV(tags["latest"]?.ToString() ?? string.Empty);
                    if (!string.IsNullOrEmpty(npmTag)) return (npmTag, ReleaseUrlFor(npmTag));
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[UpdateCheck] npm registry lookup failed: {ex.GetType().Name}: {ex.Message}");
            }

            // 2. GitHub releases fallback.
            try
            {
                using var gh = new HttpClient { Timeout = FetchTimeout };
                gh.DefaultRequestHeaders.UserAgent.ParseAdd("genexus-mcp-gateway");
                gh.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                var resp = await gh.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                if (!resp.IsSuccessStatusCode) return (null, null);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                string tag = StripV(json["tag_name"]?.ToString() ?? string.Empty);
                string? url = json["html_url"]?.ToString() ?? (string.IsNullOrEmpty(tag) ? null : ReleaseUrlFor(tag));
                return (string.IsNullOrEmpty(tag) ? null : tag, url);
            }
            catch (Exception ex)
            {
                Program.Log($"[UpdateCheck] GitHub fallback failed: {ex.GetType().Name}: {ex.Message}");
                return (null, null);
            }
        }

        private static async Task EmitNotificationAsync(string current, string latest, string? releaseUrl)
        {
            var lines = new System.Collections.Generic.List<string>
            {
                $"GeneXus MCP update available: v{current} → v{latest}",
                "Run: npm install -g genexus-mcp@latest"
            };
            if (!string.IsNullOrEmpty(releaseUrl)) lines.Add($"Release notes: {releaseUrl}");

            var notification = new
            {
                jsonrpc = "2.0",
                method = "notifications/message",
                @params = new
                {
                    level = "info",
                    logger = "update-check",
                    data = string.Join("\n", lines)
                }
            };

            string serialized = JsonConvert.SerializeObject(notification);
            await Program.TryWriteStdout(serialized);
            Program.Log($"[UpdateCheck] Notified client: v{current} -> v{latest}");
        }

        private static string GetCacheFile()
        {
            string baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            return Path.Combine(baseDir, "GenexusMCP", "update-check.json");
        }

        private static CacheEntry? ReadCache()
        {
            try
            {
                string file = GetCacheFile();
                if (!File.Exists(file)) return null;
                return JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(file));
            }
            catch { return null; }
        }

        private static void WriteCache(CacheEntry entry)
        {
            try
            {
                string file = GetCacheFile();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonConvert.SerializeObject(entry));
            }
            catch { }
        }

        private static string StripV(string v) => v.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? v.Substring(1) : v;

        // Returns >0 if a > b, <0 if a < b, 0 if equal or unparseable.
        internal static int CompareSemver(string a, string b)
        {
            int[]? pa = ParseSemver(a);
            int[]? pb = ParseSemver(b);
            if (pa == null || pb == null) return 0;
            for (int i = 0; i < 3; i++)
            {
                if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
            }
            return 0;
        }

        private static int[]? ParseSemver(string v)
        {
            string s = StripV(v).Trim();
            int dash = s.IndexOf('-');
            if (dash > 0) s = s.Substring(0, dash);
            var parts = s.Split('.');
            if (parts.Length < 3) return null;
            var result = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i], out result[i])) return null;
            }
            return result;
        }

        private class CacheEntry
        {
            [JsonProperty("checkedAt")] public DateTime CheckedAt { get; set; }
            [JsonProperty("latestVersion")] public string? LatestVersion { get; set; }
            [JsonProperty("releaseUrl")] public string? ReleaseUrl { get; set; }
        }
    }
}
