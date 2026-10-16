using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerOwnershipRegistryTests
    {
        [Fact]
        public void OwnershipKey_IsolatedByWorkerExecutablePath()
        {
            string kb = @"C:\KBs\Shared";
            string checkoutA = WorkerOwnershipRegistry.BuildKey(@"C:\checkout-a\worker\GxMcp.Worker.exe", kb);
            string checkoutB = WorkerOwnershipRegistry.BuildKey(@"C:\checkout-b\worker\GxMcp.Worker.exe", kb);

            Assert.NotEqual(checkoutA, checkoutB);
            Assert.Equal(checkoutA, WorkerOwnershipRegistry.BuildKey(@"c:\CHECKOUT-A\worker\GxMcp.Worker.exe\", kb + "\\"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GatewayPidReuse_DoesNotKeepAStaleReservation(bool reconcile)
        {
            string identity = Guid.NewGuid().ToString("N");
            string worker = Path.Combine(Path.GetTempPath(), identity, "worker.exe");
            string kb = Path.Combine(Path.GetTempPath(), identity, "kb");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GxMcp", "worker-ownership", WorkerOwnershipRegistry.BuildKey(worker, kb) + ".json");
            using var original = WorkerOwnershipRegistry.Acquire(worker, kb);
            var record = JObject.Parse(File.ReadAllText(path));
            using var current = Process.GetCurrentProcess();
            record["GatewayStartTimeUtcTicks"] = current.StartTime.ToUniversalTime().Ticks - 1;
            File.WriteAllText(path, record.ToString());

            if (reconcile)
            {
                WorkerOwnershipRegistry.Reconcile(worker, kb);
                Assert.False(File.Exists(path));
            }
            using var replacement = WorkerOwnershipRegistry.Acquire(worker, kb);
            var replaced = JObject.Parse(File.ReadAllText(path));
            Assert.Equal(current.StartTime.ToUniversalTime().Ticks,
                replaced.Value<long>("GatewayStartTimeUtcTicks"));
            Assert.False(current.HasExited);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExistingGatewayReservation_IsPreserved(bool legacyRecord)
        {
            string identity = Guid.NewGuid().ToString("N");
            string worker = Path.Combine(Path.GetTempPath(), identity, "worker.exe");
            string kb = Path.Combine(Path.GetTempPath(), identity, "kb");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GxMcp", "worker-ownership", WorkerOwnershipRegistry.BuildKey(worker, kb) + ".json");
            using var lease = WorkerOwnershipRegistry.Acquire(worker, kb);
            if (legacyRecord)
            {
                var record = JObject.Parse(File.ReadAllText(path));
                record.Remove("GatewayStartTimeUtcTicks");
                File.WriteAllText(path, record.ToString());
            }
            WorkerOwnershipRegistry.Reconcile(worker, kb);
            Assert.True(File.Exists(path));
            Assert.Throws<InvalidOperationException>(() => WorkerOwnershipRegistry.Acquire(worker, kb));
        }

        [Fact]
        public void ProcessIdentity_RejectsPidReuseOrDifferentStartTime()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var matching = new WorkerOwnershipRecord
                {
                    WorkerPid = process.Id,
                    WorkerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
                };
                var reused = new WorkerOwnershipRecord
                {
                    WorkerPid = process.Id,
                    WorkerStartTimeUtcTicks = matching.WorkerStartTimeUtcTicks + 1
                };

                Assert.True(WorkerOwnershipRegistry.IsSameProcess(process, matching));
                Assert.False(WorkerOwnershipRegistry.IsSameProcess(process, reused));
            }
        }
    }
}
