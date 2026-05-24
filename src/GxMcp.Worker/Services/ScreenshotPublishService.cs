using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 89 — genexus_screenshot_publish. Local-only: copies a screenshot
    /// PNG to <c>&lt;kbPath&gt;/.gx/published-screenshots/&lt;UTC&gt;-&lt;basename&gt;</c>
    /// and returns the destination path. No remote upload.
    /// </summary>
    public class ScreenshotPublishService
    {
        private readonly KbService _kbService;

        public ScreenshotPublishService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Publish(string sourcePath, string kbPathOverride = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return Error("MissingPath", "sourcePath is required.");
            }

            string kbPath = kbPathOverride;
            if (string.IsNullOrEmpty(kbPath))
            {
                try { kbPath = _kbService?.GetKbPath(); } catch { }
            }
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open; pass kbPathOverride or open a KB first.");
            }
            if (!File.Exists(sourcePath))
            {
                return Error("SourceNotFound", "Screenshot file does not exist: " + sourcePath);
            }

            return PublishCore(sourcePath, kbPath);
        }

        // Exposed for tests so they don't need a live KbService.
        public static string PublishCore(string sourcePath, string kbPath)
        {
            try
            {
                string destDir = Path.Combine(kbPath, ".gx", "published-screenshots");
                Directory.CreateDirectory(destDir);

                string basename = Path.GetFileName(sourcePath);
                string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                string destName = stamp + "-" + basename;
                string destPath = Path.Combine(destDir, destName);
                File.Copy(sourcePath, destPath, overwrite: false);

                long size = 0;
                try { size = new FileInfo(destPath).Length; } catch { }

                return new JObject
                {
                    ["status"] = "Success",
                    ["sourcePath"] = sourcePath,
                    ["publishedPath"] = destPath,
                    ["basename"] = destName,
                    ["sizeBytes"] = size,
                    ["publishedAtUtc"] = DateTime.UtcNow.ToString("o")
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "PublishFailed",
                    ["message"] = ex.Message,
                    ["sourcePath"] = sourcePath ?? ""
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        private static string Error(string code, string message) =>
            new JObject
            {
                ["status"] = "Error",
                ["code"] = code,
                ["message"] = message
            }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
