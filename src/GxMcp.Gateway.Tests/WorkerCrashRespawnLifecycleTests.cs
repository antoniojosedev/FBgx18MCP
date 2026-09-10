using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [CollectionDefinition("WorkerLifecycleSerial", DisableParallelization = true)]
    public sealed class WorkerLifecycleSerialCollection { }

    [Collection("WorkerLifecycleSerial")]
    public sealed class WorkerCrashRespawnLifecycleTests : IDisposable
    {
        public WorkerCrashRespawnLifecycleTests()
        {
            Program.ResetWorkerLifecycleForTest();
        }

        public void Dispose() => Program.ResetWorkerLifecycleForTest();

        [Fact]
        public async Task UnexpectedExit_AbortsAllPendingRequests_ExactlyOnce()
        {
            var config = new Configuration();
            var kb = new KbHandle("crash-kb", @"C:\Models\CrashKb");
            var worker = new WorkerProcess(config, kb);
            var first = Program.AddPendingRequestForTest("pending-1", "crash-kb");
            var second = Program.AddPendingRequestForTest("pending-2", "crash-kb");
            Program.StartWorkerForTest(config);
            Program.GetWorkerPool()!.SpawnFactoryForTest = _ => worker;
            await Program.GetWorkerPool()!.AcquireAsync(kb, CancellationToken.None);

            worker.SimulateUnexpectedExitForTest();
            worker.SimulateUnexpectedExitForTest();

            var results = await Task.WhenAll(first, second);
            Assert.Equal(0, Program.PendingRequestCountForTest);
            Assert.Equal(2, results.Length);
            Assert.All(results, response =>
            {
                Assert.Contains("crashed/exited", response);
                Assert.Contains("\"error\"", response);
            });
        }

        [Fact]
        public async Task UnexpectedExit_RetriesFailedRespawns_ThenRecoversAndBootstrapsReplacementOnly()
        {
            var config = new Configuration();
            var kb = new KbHandle("respawn-kb", @"C:\Models\RespawnKb");
            var initial = new WorkerProcess(config, kb);
            var replacement = new WorkerProcess(config, kb);
            int spawnAttempts = 0;
            int bootstrapCount = 0;
            var delays = new List<TimeSpan>();

            Program.IndexBootstrapTriggerForTest = () => Interlocked.Increment(ref bootstrapCount);
            Program.RespawnDelayForTest = delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            };
            Program.StartWorkerForTest(config);
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = _ =>
            {
                int attempt = Interlocked.Increment(ref spawnAttempts);
                if (attempt == 1) return initial;
                if (attempt <= 3) throw new InvalidOperationException("deterministic spawn failure");
                return replacement;
            };
            await pool.AcquireAsync(kb, CancellationToken.None);

            initial.SimulateUnexpectedExitForTest();

            await EventuallyAsync(() => ReferenceEquals(pool.TryGet("respawn-kb"), replacement));
            Assert.Equal(4, spawnAttempts);
            Assert.Equal(2, delays.Count);
            Assert.Equal(1, bootstrapCount);
            Assert.Same(replacement, pool.TryGet("respawn-kb"));
        }

        private static async Task EventuallyAsync(Func<bool> condition)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow >= timeout)
                    throw new TimeoutException("condition was not reached");
                await Task.Delay(10);
            }
        }
    }
}
