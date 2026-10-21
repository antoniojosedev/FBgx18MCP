using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleIndexCompletionTests
    {
        private static JObject Acknowledgement() => new JObject
        {
            ["status"] = "ok", ["code"] = "LiteStarted",
            ["result"] = new JObject { ["operationId"] = "idx-test", ["operationState"] = "Building" }
        };

        private static JObject Status(string state, bool busy) => new JObject
        {
            ["operationId"] = "idx-test", ["operationState"] = state,
            ["isIndexing"] = busy, ["indexStatus"] = "Ready", ["freshness"] = "current"
        };

        [Fact]
        public async Task ReadyCurrentWhileBuildingDoesNotCompleteUntilSameOperationIsIdle()
        {
            var secondRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var elapsed = Stopwatch.StartNew();
            var completion = LifecycleIndexCompletion.WaitAsync(Acknowledgement(), ct =>
            {
                if (++calls == 1) return Task.FromResult<JObject?>(Status("Building", true));
                secondRead.SetResult(true);
                return terminal.Task.WaitAsync(ct);
            }, timeout.Token);

            Assert.False(completion.IsCompleted);
            await secondRead.Task.WaitAsync(timeout.Token);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(800), "Active index status must not hot-poll older Workers.");
            Assert.False(completion.IsCompleted);
            terminal.SetResult(Status("Idle", false));
            JObject result = await completion;
            Assert.Equal("IndexCompleted", result["code"]?.ToString());
            Assert.Equal("idx-test", result["result"]?["operationId"]?.ToString());
            Assert.True(Program.IsSuccessfulBackgroundToolCompletion(result));
        }

        [Fact]
        public async Task RegistryJobStaysRunningUntilTerminalStatusAndRetainsCompletionResult()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/index", 60);
            var terminal = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var completion = LifecycleIndexCompletion.CompleteJobAsync(registry, job, Acknowledgement(),
                ct => terminal.Task.WaitAsync(ct), TimeSpan.FromSeconds(10));
            Assert.Equal("running", job.Status);
            terminal.SetResult(Status("Idle", false));
            await completion.WaitAsync(timeout.Token);
            Assert.Equal("succeeded", job.Status);
            Assert.Equal("IndexCompleted", job.Result?["code"]?.ToString());
            Assert.Equal("idx-test", job.Result?["result"]?["operationId"]?.ToString());
        }

        [Fact]
        public async Task RegistryJobFailsClosedWhenStatusReadThrows()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/index", 60);
            await LifecycleIndexCompletion.CompleteJobAsync(registry, job, Acknowledgement(),
                _ => throw new InvalidOperationException("status unavailable"), TimeSpan.FromSeconds(10));
            Assert.Equal("failed", job.Status);
            Assert.Contains("status unavailable", job.Summary);
            Assert.Null(job.Result);
        }

        [Fact]
        public async Task RegistryCancellationCannotBeReplacedByLateSuccess()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/index", 60);
            var terminal = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var completion = LifecycleIndexCompletion.CompleteJobAsync(registry, job, Acknowledgement(),
                _ => terminal.Task, TimeSpan.FromSeconds(10));
            Assert.True(registry.Cancel(job.Id));
            terminal.SetResult(Status("Idle", false));
            await completion.WaitAsync(timeout.Token);
            Assert.Equal("cancelled", job.Status);
            Assert.Null(job.Result);
        }

        [Fact]
        public async Task AlreadyCancelledJobDoesNotRegisterOrReadStatus()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/index", 60);
            registry.Cancel(job.Id);
            int calls = 0;
            await LifecycleIndexCompletion.CompleteJobAsync(registry, job, Acknowledgement(), _ =>
            {
                calls++;
                return Task.FromResult<JObject?>(Status("Idle", false));
            }, TimeSpan.FromSeconds(10));
            Assert.Equal(0, calls);
            Assert.Equal("cancelled", job.Status);
        }

        [Fact]
        public async Task HardCapFailsObserverWithoutClaimingUnderlyingIndexStopped()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/index", 60);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LifecycleIndexCompletion.CompleteJobAsync(registry, job, Acknowledgement(), async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Status("Idle", false);
            }, TimeSpan.FromMilliseconds(50)).WaitAsync(timeout.Token);
            Assert.Equal("failed", job.Status);
            Assert.Contains("underlying operation completion is unverified", job.Summary);
            Assert.Null(job.Result);
        }

        [Fact]
        public async Task ErrorAcknowledgementCannotCompleteRegistryJobSuccessfully()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/index", 60);
            await LifecycleIndexCompletion.CompleteJobAsync(registry, job,
                new JObject { ["status"] = "Error", ["error"] = "index refused" },
                _ => throw new InvalidOperationException("must not read status"), TimeSpan.FromSeconds(10));
            Assert.Equal("failed", job.Status);
            Assert.Equal("index refused", job.Result?["error"]?.ToString());
        }

        [Theory]
        [InlineData("Stalled", "Ready", "current", "idx-test")]
        [InlineData("WorkerExited", "Ready", "current", "idx-test")]
        [InlineData("Recovering", "Ready", "stale", "idx-test")]
        [InlineData("Idle", "Failed", "stale", "idx-test")]
        [InlineData("Idle", "Ready", "stale", "idx-test")]
        [InlineData("Idle", "Ready", "current", "idx-other")]
        [InlineData("Idle", "Ready", "current", null)]
        public async Task FailedStaleOrSupersededOperationCannotSucceed(
            string state, string indexStatus, string freshness, string? operationId)
        {
            var status = Status(state, false);
            status["indexStatus"] = indexStatus;
            status["freshness"] = freshness;
            status["operationId"] = operationId;
            await Assert.ThrowsAsync<InvalidOperationException>(() => LifecycleIndexCompletion.WaitAsync(
                Acknowledgement(), _ => Task.FromResult<JObject?>(status), CancellationToken.None));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MissingOrErrorStatusCannotSucceed(bool hasError)
        {
            JObject? status = hasError ? new JObject { ["error"] = "worker exited" } : null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => LifecycleIndexCompletion.WaitAsync(
                Acknowledgement(), _ => Task.FromResult(status), CancellationToken.None));
        }

        [Fact]
        public async Task AcknowledgementWithoutOperationIdentityCannotBorrowAnotherReadyIndex()
        {
            var ack = Acknowledgement();
            ((JObject)ack["result"]!).Remove("operationId");
            var status = Status("Idle", false);
            status.Remove("operationId");
            await Assert.ThrowsAsync<InvalidOperationException>(() => LifecycleIndexCompletion.WaitAsync(
                ack, _ => Task.FromResult<JObject?>(status), CancellationToken.None));
        }

        [Fact]
        public async Task StatusWithoutActivityEvidenceCannotSucceed()
        {
            var status = Status("Idle", false);
            status.Remove("isIndexing");
            await Assert.ThrowsAsync<InvalidOperationException>(() => LifecycleIndexCompletion.WaitAsync(
                Acknowledgement(), _ => Task.FromResult<JObject?>(status), CancellationToken.None));
        }

        [Fact]
        public async Task CancelledObserverDoesNotReportIndexSuccessOrSendAnotherCommand()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            int calls = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LifecycleIndexCompletion.WaitAsync(
                Acknowledgement(), _ =>
                {
                    calls++;
                    return Task.FromResult<JObject?>(Status("Idle", false));
                }, cancellation.Token));
            Assert.Equal(0, calls);
        }
    }
}
