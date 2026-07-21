using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class MultiAgentLockAtomicWriteTests
    {
        private static string NewKb()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gxmcp_lock_atomic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return tmp;
        }

        [Fact]
        public void Acquire_FreshLock_HappyPathUnchanged()
        {
            string kb = NewKb();
            try
            {
                var json = JObject.Parse(MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Events", "agent-A", 300));
                Assert.Equal("ok", (string)json["status"]);
                Assert.Equal("LockAcquired", (string)json["code"]);
                Assert.True((bool)json["result"]["held"]);
                Assert.Equal("agent-A", (string)json["result"]["holder"]["ownerId"]);
                string lockPath = (string)json["result"]["path"];
                Assert.True(File.Exists(lockPath));

                var status = JObject.Parse(MultiAgentLockService.DispatchCore(kb, "status", "Invoice", "Events", null, 300));
                Assert.True((bool)status["result"]["held"]);
                Assert.Equal("agent-A", (string)status["result"]["holder"]["ownerId"]);
            }
            finally { try { Directory.Delete(kb, recursive: true); } catch { } }
        }

        [Fact]
        public void Acquire_Success_LeavesNoTempFileBehind()
        {
            string kb = NewKb();
            try
            {
                var json = JObject.Parse(MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Events", "agent-A", 300));
                string lockPath = (string)json["result"]["path"];
                Assert.True(File.Exists(lockPath));
                Assert.False(File.Exists(lockPath + ".tmp"));
            }
            finally { try { Directory.Delete(kb, recursive: true); } catch { } }
        }

        [Fact]
        public void Acquire_SameOwnerReacquires_RefreshesTtlAndLeavesNoTempFile()
        {
            string kb = NewKb();
            try
            {
                MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Events", "agent-A", 300);
                var json = JObject.Parse(MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Events", "agent-A", 600));
                Assert.Equal("ok", (string)json["status"]);
                Assert.Equal("LockAcquired", (string)json["code"]);
                Assert.Equal("agent-A", (string)json["result"]["holder"]["ownerId"]);
                Assert.Equal(600, (int)json["result"]["holder"]["ttlSec"]);

                string lockPath = (string)json["result"]["path"];
                Assert.True(File.Exists(lockPath));
                Assert.False(File.Exists(lockPath + ".tmp"));
            }
            finally { try { Directory.Delete(kb, recursive: true); } catch { } }
        }
    }
}
