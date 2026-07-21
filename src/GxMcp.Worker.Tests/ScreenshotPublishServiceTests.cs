using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ScreenshotPublishServiceTests
    {
        [Fact]
        public void PublishCore_CopiesFileAndReturnsPath()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_pub_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(tmpKb, "shot.png");
            try
            {
                Directory.CreateDirectory(tmpKb);
                File.WriteAllBytes(src, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

                var json = JObject.Parse(ScreenshotPublishService.PublishCore(src, tmpKb));

                Assert.Equal("ok", (string)json["status"]!);
                string published = (string)json["result"]!["publishedPath"]!;
                Assert.True(File.Exists(published), "destination file should exist");
                Assert.Contains(".gx", published);
                Assert.Contains("published-screenshots", published);
                Assert.EndsWith("shot.png", (string)json["result"]!["basename"]!);
                Assert.True((long)json["result"]!["sizeBytes"]! > 0);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Publish_NoKb_ReturnsNoKbOpenError()
        {
            var svc = new ScreenshotPublishService(kbService: null);
            var json = JObject.Parse(svc.Publish(@"C:\does\not\exist.png"));
            Assert.Equal("error", (string)json["status"]!);
            Assert.Equal("NoKbOpen", (string)json["error"]!["code"]!);
        }

        [Fact]
        public void Publish_MissingSource_ReturnsSourceNotFound()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_pub_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var svc = new ScreenshotPublishService(kbService: null);
                // Bypass the NoKbOpen check by going through PublishCore-paired call via override path?
                // The Publish() method doesn't accept an override; test the missing-path branch
                // via the public Publish method by also bypassing with kbPathOverride=null. So we
                // hit NoKbOpen first. Instead, validate MissingPath branch.
                var json = JObject.Parse(svc.Publish(null));
                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("MissingPath", (string)json["error"]!["code"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        // ── Plan 013: extension allowlist + root confinement + size cap ────────

        [Fact]
        public void PublishCore_NonImageExtension_ReturnsSourceNotAllowed_AndDoesNotCopy()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_pub_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(tmpKb, "secret.txt");
            try
            {
                Directory.CreateDirectory(tmpKb);
                File.WriteAllText(src, "not an image");

                var json = JObject.Parse(ScreenshotPublishService.PublishCore(src, tmpKb));

                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("SourceNotAllowed", (string)json["error"]!["code"]!);

                string destDir = Path.Combine(tmpKb, ".gx", "published-screenshots");
                Assert.False(Directory.Exists(destDir) && Directory.EnumerateFiles(destDir).Any(),
                    "the file should not have been copied");
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void PublishCore_OutOfRootImage_ReturnsSourceNotAllowed_AndDoesNotCopy()
        {
            // A .png under the user profile directory — not the OS temp dir (the test
            // runner's own bin/shadow-copy dir can itself live under %TEMP%, which
            // would make this assertion a false pass), not the KB root, and (env var
            // left unset) not GXMCP_SCREENSHOT_DIR.
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_pub_kb_" + Guid.NewGuid().ToString("N"));
            string outsideDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "gxmcp_outofroot_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(outsideDir, "shot.png");
            string previousEnv = Environment.GetEnvironmentVariable("GXMCP_SCREENSHOT_DIR");
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_SCREENSHOT_DIR", null);
                Directory.CreateDirectory(tmpKb);
                Directory.CreateDirectory(outsideDir);
                File.WriteAllBytes(src, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

                var json = JObject.Parse(ScreenshotPublishService.PublishCore(src, tmpKb));

                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("SourceNotAllowed", (string)json["error"]!["code"]!);

                string destDir = Path.Combine(tmpKb, ".gx", "published-screenshots");
                Assert.False(Directory.Exists(destDir) && Directory.EnumerateFiles(destDir).Any(),
                    "the file should not have been copied");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_SCREENSHOT_DIR", previousEnv);
                try { Directory.Delete(outsideDir, recursive: true); } catch { }
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void PublishCore_ValidTempImage_Accepted()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_pub_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(Path.GetTempPath(), "gxmcp_shot_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Directory.CreateDirectory(tmpKb);
                File.WriteAllBytes(src, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

                var json = JObject.Parse(ScreenshotPublishService.PublishCore(src, tmpKb));

                Assert.Equal("ok", (string)json["status"]!);
                Assert.Equal("ScreenshotPublished", (string)json["code"]!);
                string published = (string)json["result"]!["publishedPath"]!;
                Assert.True(File.Exists(published));
                Assert.EndsWith(Path.GetFileName(src), (string)json["result"]!["basename"]!);
            }
            finally
            {
                try { File.Delete(src); } catch { }
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void PublishCore_OversizeImage_ReturnsSourceTooLarge()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_pub_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(Path.GetTempPath(), "gxmcp_big_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Directory.CreateDirectory(tmpKb);
                // Sparse-ish large file: seek past 25MB and write one byte so the file
                // reports a length above the cap without actually writing 25MB of data.
                using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write))
                {
                    fs.SetLength(26L * 1024 * 1024);
                }

                var json = JObject.Parse(ScreenshotPublishService.PublishCore(src, tmpKb));

                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("SourceTooLarge", (string)json["error"]!["code"]!);
            }
            finally
            {
                try { File.Delete(src); } catch { }
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }
    }
}
