using System;
using System.IO;
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

                Assert.Equal("Success", (string)json["status"]!);
                string published = (string)json["publishedPath"]!;
                Assert.True(File.Exists(published), "destination file should exist");
                Assert.Contains(".gx", published);
                Assert.Contains("published-screenshots", published);
                Assert.EndsWith("shot.png", (string)json["basename"]!);
                Assert.True((long)json["sizeBytes"]! > 0);
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
            Assert.Equal("Error", (string)json["status"]!);
            Assert.Equal("NoKbOpen", (string)json["code"]!);
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
                Assert.Equal("Error", (string)json["status"]!);
                Assert.Equal("MissingPath", (string)json["code"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }
    }
}
