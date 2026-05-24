using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class FrictionLogServiceTests
    {
        [Fact]
        public void AppendCore_WritesJsonlEntry()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var json = JObject.Parse(FrictionLogService.AppendCore(tmpKb, "genexus_edit", "patch failed", "warn"));

                Assert.Equal("Success", (string)json["status"]!);
                string path = (string)json["path"]!;
                Assert.True(File.Exists(path));
                var lines = File.ReadAllLines(path);
                Assert.Single(lines);
                var entry = JObject.Parse(lines[0]);
                Assert.Equal("genexus_edit", (string)entry["tool"]!);
                Assert.Equal("patch failed", (string)entry["message"]!);
                Assert.Equal("warn", (string)entry["severity"]!);
                Assert.NotNull(entry["atUtc"]);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TailCore_ReturnsLastN_InChronologicalOrder()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                for (int i = 0; i < 5; i++)
                {
                    FrictionLogService.AppendCore(tmpKb, "tool" + i, "msg" + i, "info");
                }
                var json = JObject.Parse(FrictionLogService.TailCore(tmpKb, 3));

                Assert.Equal("Success", (string)json["status"]!);
                var entries = (JArray)json["entries"]!;
                Assert.Equal(3, entries.Count);
                // Chronological: last 3 should be tool2, tool3, tool4
                Assert.Equal("tool2", (string)((JObject)entries[0])["tool"]!);
                Assert.Equal("tool4", (string)((JObject)entries[2])["tool"]!);
                Assert.Equal(5, (int)json["total"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TailCore_NoFile_ReturnsEmptyArray()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var json = JObject.Parse(FrictionLogService.TailCore(tmpKb, 10));

                Assert.Equal("Success", (string)json["status"]!);
                Assert.Empty((JArray)json["entries"]!);
                Assert.Equal(0, (int)json["total"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void AppendCore_MissingMessage_ReturnsError()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var json = JObject.Parse(FrictionLogService.AppendCore(tmpKb, "tool", "", "info"));
                Assert.Equal("Error", (string)json["status"]!);
                Assert.Equal("MissingMessage", (string)json["code"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }
    }
}
