using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GxServerSyncServiceTests
    {
        private static string NewTmpKb()
        {
            string p = Path.Combine(Path.GetTempPath(), "gxmcp_gxsrv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }

        [Fact]
        public void Status_NoMetadata_ReturnsConnectedFalseWithHint()
        {
            string kb = NewTmpKb();
            try
            {
                var jo = JObject.Parse(GxServerSyncService.StatusEnvelope(kb, "MyKb"));
                Assert.Equal("Success", (string)jo["status"]);
                Assert.False((bool)jo["connected"]);
                Assert.Equal("MyKb", (string)jo["kbAlias"]);
                Assert.Contains("not connected", ((string)jo["hint"]) ?? string.Empty);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Status_WithRepositoryGxs_ReturnsConnectedTrue()
        {
            string kb = NewTmpKb();
            try
            {
                string repoDir = Path.Combine(kb, "Repository");
                Directory.CreateDirectory(repoDir);
                File.WriteAllText(Path.Combine(repoDir, "Repository.gxs"), "<gxserver/>");

                var jo = JObject.Parse(GxServerSyncService.StatusEnvelope(kb, "MyKb"));
                Assert.Equal("Success", (string)jo["status"]);
                Assert.True((bool)jo["connected"]);
                Assert.NotNull(jo["detectedVia"]);
                Assert.Contains("Repository.gxs", (string)jo["detectedVia"]);
                Assert.Contains("parsing pending", (string)jo["note"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Pending_NoMetadata_ReturnsConnectedFalse()
        {
            string kb = NewTmpKb();
            try
            {
                var jo = JObject.Parse(GxServerSyncService.PendingEnvelope(kb));
                Assert.False((bool)jo["connected"]);
                Assert.Null(jo["objects"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void History_WithDotGxState_ReturnsEmptyHistoryArray()
        {
            string kb = NewTmpKb();
            try
            {
                string dot = Path.Combine(kb, ".gx");
                Directory.CreateDirectory(dot);
                File.WriteAllText(Path.Combine(dot, "gxserver-state.xml"), "<state/>");

                var jo = JObject.Parse(GxServerSyncService.HistoryEnvelope(kb, 25));
                Assert.True((bool)jo["connected"]);
                Assert.NotNull(jo["history"]);
                Assert.Equal(JTokenType.Array, jo["history"].Type);
                Assert.Equal(25, (int)jo["limit"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Run_BadAction_ReturnsGracefulError()
        {
            var svc = new GxServerSyncService(null);
            var jo = JObject.Parse(svc.Run(new JObject { ["action"] = "bogus" }));
            Assert.Equal("Error", (string)jo["status"]);
            Assert.Equal("BadAction", (string)jo["code"]);
            Assert.Contains("bogus", (string)jo["message"]);
        }
    }
}
