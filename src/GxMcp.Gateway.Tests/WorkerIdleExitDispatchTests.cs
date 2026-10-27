using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #692: a request accepted while the idle reaper was stopping the Worker was parked
    /// in a command channel nobody drained and then aborted with -32603 "crashed/exited",
    /// although no byte of it had reached the Worker. An undelivered command must reach a
    /// replacement Worker; a delivered but unanswered one must ask for reconciliation.
    /// </summary>
    [Collection("WorkerLifecycleSerial")]
    public sealed class WorkerIdleExitDispatchTests : IDisposable
    {
        private const string Alias = "idle-kb";
        private readonly KbHandle _kb = new KbHandle(Alias, @"C:\Models\IdleKb");
        private readonly Configuration _config = new Configuration();

        public WorkerIdleExitDispatchTests()
        {
            Program.ResetWorkerLifecycleForTest();
            Program.IndexBootstrapTriggerForTest = () => { };
            Program.StartWorkerForTest(_config);
        }

        public void Dispose() => Program.ResetWorkerLifecycleForTest();

        [Fact]
        public async Task IdleExit_CommandQueuedButNotWritten_IsReportedNotDelivered()
        {
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = handle => new WorkerProcess(_config, handle);
            WorkerProcess worker = await pool.AcquireAsync(_kb, CancellationToken.None);
            using var gate = worker.StallCommandWriterForTest();
            try
            {
                Task<string> pending = Program.AddPendingRequestForTest("req-queued", Alias, worker);
                await worker.SendCommandAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = "req-queued", ["method"] = "execute_command" });

                worker.StopWithReason(WorkerStopReason.IdleTimeout);

                JObject response = JObject.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(WorkerDispatchState.NotDelivered, (string?)response["error"]?["data"]?["dispatchState"]);
                Assert.Equal("IdleTimeout", (string?)response["error"]?["data"]?["stopReason"]);
                Assert.True(Program.ShouldRedispatchUndeliveredCommand(response, 0));
                Assert.False(Program.ShouldRedispatchUndeliveredCommand(response, Program.MaxUndeliveredRedispatches));
                Assert.Null(pool.TryGet(Alias));
            }
            finally { worker.ResumeCommandWriterForTest(); }
        }

        [Fact]
        public async Task IdleExit_DeliveredUnansweredCommand_RequiresReconcileAndIsNeverRedispatched()
        {
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = handle => new WorkerProcess(_config, handle);
            WorkerProcess worker = await pool.AcquireAsync(_kb, CancellationToken.None);
            // Not offered through SendCommandAsync: the Worker side already has it.
            Task<string> pending = Program.AddPendingRequestForTest("req-delivered", Alias, worker);

            worker.StopWithReason(WorkerStopReason.IdleTimeout);

            JObject response = JObject.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(WorkerDispatchState.DeliveredUnanswered, (string?)response["error"]?["data"]?["dispatchState"]);
            Assert.True((bool?)response["error"]?["data"]?["reconcileRequired"]);
            Assert.Contains("crashed/exited", (string?)response["error"]?["message"]);
            Assert.False(Program.ShouldRedispatchUndeliveredCommand(response, 0));
            Assert.False(Program.ShouldRetryWorkerCrash(response, "genexus_delete_object",
                new JObject { ["name"] = "X", ["confirm"] = true }, 1));
        }

        [Fact]
        public async Task CommandOfferedToStoppedWorker_IsRefusedInsteadOfParked()
        {
            var pool = Program.GetWorkerPool()!;
            pool.SpawnFactoryForTest = handle => new WorkerProcess(_config, handle);
            WorkerProcess worker = await pool.AcquireAsync(_kb, CancellationToken.None);
            worker.StopWithReason(WorkerStopReason.IdleTimeout);

            await Assert.ThrowsAsync<WorkerCommandNotDeliveredException>(() =>
                worker.SendCommandAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = "late", ["method"] = "execute_command" }));
            Assert.Equal(0, worker.QueuedCommandsForTest);
            Assert.False(worker.IsCommandUndelivered("late"));
        }

        [Fact]
        public async Task MutatingRequestDuringIdleExit_IsServedByReplacementWorker()
        {
            var pool = Program.GetWorkerPool()!;
            WorkerProcess? initial = null;
            WorkerProcess? replacement = null;
            int spawns = 0;
            pool.SpawnFactoryForTest = handle =>
            {
                var created = new WorkerProcess(_config, handle);
                created.StallCommandWriterForTest();
                if (Interlocked.Increment(ref spawns) == 1) initial = created; else replacement = created;
                return created;
            };
            WorkerProcess first = await pool.AcquireAsync(_kb, CancellationToken.None);
            first.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
            try
            {
                Task<JObject?> dispatch = SendWorkerCommandAsync("genexus_delete_object",
                    new JObject { ["module"] = "Object", ["action"] = "Delete", ["target"] = "Mcp692" });
                string firstRequestId = await PendingRequestIdAsync(except: null);

                first.StopWithReason(WorkerStopReason.IdleTimeout);

                await EventuallyAsync(() => replacement != null && ReferenceEquals(pool.TryGet(Alias), replacement));
                replacement!.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
                string secondRequestId = await PendingRequestIdAsync(except: firstRequestId);
                DeliverWorkerResponse("{\"jsonrpc\":\"2.0\",\"id\":\"" + secondRequestId
                    + "\",\"result\":{\"status\":\"ok\",\"code\":\"ObjectDeleted\"}}");

                JObject? result = await dispatch.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.NotNull(result);
                Assert.Equal("ObjectDeleted", (string?)result!["result"]?["code"]);
                Assert.Null(result["error"]);
                Assert.Equal(2, spawns);
                Assert.Equal(0, Program.PendingRequestCountForTest);
            }
            finally
            {
                initial?.ResumeCommandWriterForTest();
                replacement?.ResumeCommandWriterForTest();
            }
        }

        private Task<JObject?> SendWorkerCommandAsync(string toolName, JObject command)
        {
            var currentKb = typeof(Program).GetField("_currentKb", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            currentKb.GetType().GetProperty("Value")!.SetValue(currentKb, _kb);
            var method = typeof(Program).GetMethod("SendWorkerCommandAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Func<JObject, JObject> onSuccess = env => env;
            Func<string?, string, JObject> onTimeout = (_, cid) => new JObject { ["timeout"] = true, ["correlationId"] = cid };
            return (Task<JObject?>)method.Invoke(null, new object?[]
            {
                command, 20000, "test timeout", onSuccess, onTimeout, toolName, new JObject(), false,
                null, null, null, null, null, null, CancellationToken.None
            })!;
        }

        private static async Task<string> PendingRequestIdAsync(string? except)
        {
            var pending = (IDictionary)typeof(Program)
                .GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                foreach (object key in pending.Keys)
                {
                    if (!string.Equals((string)key, except, StringComparison.Ordinal)) return (string)key;
                }
                Assert.True(DateTime.UtcNow < deadline, "the dispatch never registered a pending request");
                await Task.Delay(10);
            }
        }

        private static async Task EventuallyAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "condition was not reached");
                await Task.Delay(10);
            }
        }

        private static void DeliverWorkerResponse(string json) =>
            typeof(Program).GetMethod("HandleWorkerResponse", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object?[] { json, JObject.Parse(json), Alias });
    }
}
