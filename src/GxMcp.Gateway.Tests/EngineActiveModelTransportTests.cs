using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The engine reads the Worker's active KB version through the existing pool entry only: it must
    /// never acquire, respawn or wait for a Worker (the generic send path can wait up to 180 s for
    /// SDK readiness), and every pending request and pool reservation it takes must be released.
    /// </summary>
    [Collection("WorkerLifecycleSerial")]
    public sealed class EngineActiveModelTransportTests : IDisposable
    {
        private const string Alias = "snapshot-kb";
        private static readonly string KbPath = @"C:\Models\SnapshotKb";
        private readonly KbHandle _kb = new KbHandle(Alias, KbPath);
        private readonly Configuration _config = new Configuration();
        private int _spawns;
        private WorkerPool _pool = null!;

        public EngineActiveModelTransportTests()
        {
            Program.ResetWorkerLifecycleForTest();
            Program.IndexBootstrapTriggerForTest = () => { };
            Program.StartWorkerForTest(_config);
            _pool = Program.GetWorkerPool()!;
            _pool.SpawnFactoryForTest = handle =>
            {
                Interlocked.Increment(ref _spawns);
                return new WorkerProcess(_config, handle);
            };
        }

        public void Dispose()
        {
            Program.EngineActiveModelAfterCandidateCheckForTest = null;
            Program.ResetWorkerLifecycleForTest();
        }

        // ------------------------------------------------------------ no usable candidate

        [Fact]
        public async Task AMissingWorkerIsNeitherAcquiredNorWaitedFor()
        {
            Assert.Null(await ReadAsync(timeoutMs: 5000));

            Assert.Equal(0, _spawns);
            Assert.Null(_pool.TryGet(Alias));
            AssertNothingLeaked();
        }

        [Fact]
        public async Task AWorkerLostBeforeTheReadIsNotRespawned()
        {
            WorkerProcess worker = await ReadyWorkerAsync();
            worker.StopWithReason(WorkerStopReason.ExplicitClose);
            Assert.Null(_pool.TryGet(Alias));

            Assert.Null(await ReadAsync(timeoutMs: 5000));

            Assert.Equal(1, _spawns);
            Assert.Null(_pool.TryGet(Alias));
            AssertNothingLeaked();
        }

        [Fact]
        public async Task AnUnreadyWorkerIsNotAskedAndNotWaitedFor()
        {
            WorkerProcess worker = await WorkerAsync();
            using var gate = worker.StallCommandWriterForTest();
            try
            {
                Assert.Null(await ReadAsync(timeoutMs: 5000));

                Assert.Equal(0, worker.QueuedCommandsForTest);
                Assert.Equal(1, _spawns);
                AssertNothingLeaked();
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        [Fact]
        public async Task AWorkerLostBetweenTheCheckAndTheReservationIsNotRespawned()
        {
            WorkerProcess worker = await ReadyWorkerAsync();
            using var gate = worker.StallCommandWriterForTest();
            Program.EngineActiveModelAfterCandidateCheckForTest = () => worker.StopWithReason(WorkerStopReason.ExplicitClose);
            try
            {
                Assert.Null(await ReadAsync(timeoutMs: 5000));

                Assert.Equal(0, worker.QueuedCommandsForTest);
                Assert.Equal(1, _spawns);
                Assert.Null(_pool.TryGet(Alias));
                AssertNothingLeaked();
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        [Fact]
        public async Task AWorkerReplacedBetweenTheCheckAndTheReservationReceivesNoRequest()
        {
            WorkerProcess original = await ReadyWorkerAsync();
            WorkerProcess? replacement = null;
            using var originalGate = original.StallCommandWriterForTest();
            Program.EngineActiveModelAfterCandidateCheckForTest = () =>
            {
                original.StopWithReason(WorkerStopReason.ExplicitClose);
                replacement = _pool.AcquireAsync(_kb, CancellationToken.None).GetAwaiter().GetResult();
                replacement.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
                replacement.StallCommandWriterForTest();
            };
            try
            {
                Assert.Null(await ReadAsync(timeoutMs: 5000));

                Assert.NotNull(replacement);
                Assert.NotSame(original, replacement);
                Assert.Equal(0, original.QueuedCommandsForTest);
                Assert.Equal(0, replacement!.QueuedCommandsForTest);
                Assert.Equal(2, _spawns);
                AssertNothingLeaked();
            }
            finally
            {
                original.ResumeCommandWriterForTest();
                replacement?.ResumeCommandWriterForTest();
            }
        }

        // ------------------------------------------------------------ the request itself

        [Fact]
        public async Task AnAnsweredRequestReturnsTheSnapshotAndReleasesEverything()
        {
            WorkerProcess worker = await ReadyWorkerAsync();
            using var gate = worker.StallCommandWriterForTest();
            try
            {
                Task<EngineActiveModel?> read = ReadAsync(timeoutMs: 10000);
                string requestId = await PendingRequestIdAsync();
                Assert.Equal(1, worker.QueuedCommandsForTest);
                Assert.Equal(1, _pool.InFlightCommandsForTest(Alias));

                DeliverWorkerResponse(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"" + requestId + "\",\"result\":{\"status\":\"ok\",\"code\":\"KbActiveModel\","
                    + "\"result\":{\"kbPath\":\"C:\\\\Models\\\\SnapshotKb\",\"kbVersionId\":2,\"kbVersionName\":\"Branch\",\"designModelId\":4}}}");

                EngineActiveModel? model = await read.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.NotNull(model);
                Assert.Equal(4, model!.DesignModelId);
                Assert.Equal(2, model.KbVersionId);
                Assert.Equal(1, _spawns);
                AssertNothingLeaked();
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        [Fact]
        public async Task AWorkerThatNeverAnswersIsUnknownWithinTheBudget()
        {
            WorkerProcess worker = await ReadyWorkerAsync();
            using var gate = worker.StallCommandWriterForTest();
            try
            {
                var clock = Stopwatch.StartNew();
                EngineActiveModel? model = await ReadAsync(timeoutMs: 150).WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Null(model);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "the read must be bounded by its own budget");
                Assert.Equal(1, worker.QueuedCommandsForTest);
                Assert.Equal(1, _spawns);
                AssertNothingLeaked();
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        [Fact]
        public async Task ACallerCancellationPropagatesAndReleasesEverything()
        {
            WorkerProcess worker = await ReadyWorkerAsync();
            using var gate = worker.StallCommandWriterForTest();
            using var cancellation = new CancellationTokenSource();
            try
            {
                Task<EngineActiveModel?> read = ReadAsync(timeoutMs: 60000, cancellation.Token);
                await PendingRequestIdAsync();
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, _spawns);
                AssertNothingLeaked();
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        [Fact]
        public async Task AStoppedTransportIsRefusedWithoutStartingAWorker()
        {
            // The Worker is registered and ready but has no running process: the writer loop answers
            // with its crashed/exited envelope instead of starting one.
            await ReadyWorkerAsync();

            EngineActiveModel? model = await ReadAsync(timeoutMs: 10000).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Null(model);
            Assert.Equal(1, _spawns);
            AssertNothingLeaked();
        }

        // ------------------------------------------------------------ the Worker goes away mid-wait

        [Theory]
        [InlineData("exit")]
        [InlineData("gateway-shutdown")]
        [InlineData("pending-cancelled")]
        public async Task AWorkerGoneWhileWaitingIsUnknownWithoutRespawnAndReleasesEverything(string how)
        {
            WorkerProcess worker = await ReadyWorkerAsync();
            using var gate = worker.StallCommandWriterForTest();
            try
            {
                Task<EngineActiveModel?> read = ReadAsync(timeoutMs: 60000);
                string requestId = await PendingRequestIdAsync();

                switch (how)
                {
                    case "exit":
                        worker.StopWithReason(WorkerStopReason.ExplicitClose);
                        break;
                    case "gateway-shutdown":
                        worker.StopWithReason(WorkerStopReason.GatewayShutdown);
                        break;
                    default:
                        CancelPendingRequest(requestId);
                        break;
                }

                Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, _spawns);
                if (how != "pending-cancelled") Assert.Null(_pool.TryGet(Alias));
                AssertNothingLeaked();
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        // ------------------------------------------------------------ an abandoned enqueue

        [Fact]
        public async Task AnAbandonedEnqueueThatLaterFailsIsObservedAndLogged()
        {
            var send = new TaskCompletionSource();
            var logged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Program.ObserveAbandonedSend(send.Task, Alias, logged.SetResult);
            Assert.False(logged.Task.IsCompleted);

            send.SetException(new WorkerCommandQueueFullException(Alias, "id", "KbVersion", 512, 5000));

            string line = await logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(Alias, line);
            Assert.Contains("failed to enqueue", line);
        }

        [Fact]
        public async Task AnAbandonedEnqueueThatCompletesLaterLogsNothing()
        {
            var send = new TaskCompletionSource();
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            Program.ObserveAbandonedSend(send.Task, Alias, lines.Enqueue);

            send.SetResult();
            await Task.Delay(50);

            Assert.Empty(lines);
        }

        // ------------------------------------------------------------ helpers

        private Task<EngineActiveModel?> ReadAsync(int timeoutMs, CancellationToken cancellationToken = default) =>
            Program.ReadEngineActiveModelAsync(_pool, Alias, timeoutMs, cancellationToken);

        private async Task<WorkerProcess> WorkerAsync() =>
            await _pool.AcquireAsync(_kb, CancellationToken.None);

        private async Task<WorkerProcess> ReadyWorkerAsync()
        {
            WorkerProcess worker = await WorkerAsync();
            worker.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
            Assert.True(worker.IsSdkReady);
            return worker;
        }

        private void AssertNothingLeaked()
        {
            Assert.Equal(0, Program.PendingRequestCountForTest);
            Assert.Equal(0, _pool.InFlightCommandsForTest(Alias));
        }

        private static async Task<string> PendingRequestIdAsync()
        {
            var pending = (System.Collections.IDictionary)typeof(Program)
                .GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (pending.Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the read never registered a pending request");
                await Task.Delay(10);
            }
            foreach (object key in pending.Keys) return (string)key;
            throw new InvalidOperationException("unreachable");
        }

        private static void CancelPendingRequest(string requestId)
        {
            var pending = (System.Collections.IDictionary)typeof(Program)
                .GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            object request = pending[requestId]!;
            var completion = (TaskCompletionSource<string>)request.GetType().GetProperty("CompletionSource")!.GetValue(request)!;
            // The shutdown path removes the entry, then cancels its completion.
            pending.Remove(requestId);
            Assert.True(completion.TrySetCanceled());
        }

        private static void DeliverWorkerResponse(string json) =>
            typeof(Program).GetMethod("HandleWorkerResponse", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object?[] { json, JObject.Parse(json), Alias });
    }
}
