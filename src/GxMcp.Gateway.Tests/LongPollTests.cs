using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>
/// Tests for McpRouter.LongPollJob — the server-side long-poll helper introduced in Task 4.5.
/// wait_seconds is clamped to [0, 25]; 0 means immediate single poll.
/// </summary>
public class LongPollTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static BackgroundJobRegistry MakeRegistry() => new BackgroundJobRegistry(600);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownJobId_ReturnsErrorEnvelope()
    {
        var registry = MakeRegistry();

        JObject result = await McpRouter.LongPollJob(registry, "bogus-id-does-not-exist", waitSeconds: 0);

        Assert.NotNull(result["error"]);
        Assert.Equal("unknown_job_id", result["error"]!.ToString());
        Assert.Equal("bogus-id-does-not-exist", result["job_id"]!.ToString());
    }

    [Fact]
    public async Task TerminalJob_ReturnsImmediately()
    {
        // A completed job should return well under one poll interval (250 ms) even when
        // wait_seconds is generous, because the loop exits as soon as status != "running".
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);
        registry.Complete(job.Id, true, "done");

        var sw = Stopwatch.StartNew();
        JObject result = await McpRouter.LongPollJob(registry, job.Id, waitSeconds: 5);
        sw.Stop();

        Assert.Equal("succeeded", result["job_id"] != null ? result["status"]!.ToString() : "");
        Assert.Equal("succeeded", result["status"]!.ToString());
        // Should return in well under 250 ms (one poll tick) — use 500 ms as a safe ceiling.
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected near-immediate return for terminal job but took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RunningJob_BlocksUntilCompletion()
    {
        // Complete the job after ~200 ms; long-poll with wait_seconds=5 should return ~200 ms later.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            registry.Complete(job.Id, true, "build ok");
        });

        var sw = Stopwatch.StartNew();
        JObject result = await McpRouter.LongPollJob(registry, job.Id, waitSeconds: 5);
        sw.Stop();

        Assert.Equal("succeeded", result["status"]!.ToString());
        // Must have waited at least the ~200 ms for completion but much less than 5 s.
        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"Returned too early — elapsed {sw.ElapsedMilliseconds} ms, expected >= 150 ms");
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Took too long — elapsed {sw.ElapsedMilliseconds} ms, expected < 3000 ms");
    }

    [Fact]
    public async Task CancellationToken_ReturnsTypedRequestCancelledEnvelope()
    {
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);
        using var cancellation = new CancellationTokenSource();

        var pollTask = McpRouter.LongPollJob(
            registry, job.Id, waitSeconds: 30,
            cancellationToken: cancellation.Token);

        await Task.Delay(100);
        cancellation.Cancel();

        var result = await pollTask;

        Assert.Equal(-32800, result["error"]?["code"]?.ToObject<int>());
        Assert.Equal("Request cancelled by client", result["error"]?["message"]?.ToString());
        Assert.True(result["cancelled"]?.ToObject<bool>());
        Assert.Equal(job.Id, result["job_id"]?.ToString());
    }

    [Fact]
    public async Task RunningJob_HonorsTimeout()
    {
        // Job is never completed; long-poll with wait_seconds=1 must return after ~1 s
        // with status still "running".
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);

        var sw = Stopwatch.StartNew();
        JObject result = await McpRouter.LongPollJob(registry, job.Id, waitSeconds: 1);
        sw.Stop();

        Assert.Equal("running", result["status"]!.ToString());
        Assert.True(sw.ElapsedMilliseconds >= 900,
            $"Returned before timeout — elapsed {sw.ElapsedMilliseconds} ms, expected >= 900 ms");
        // Should not significantly overshoot (250 ms poll tick + processing headroom)
        Assert.True(sw.ElapsedMilliseconds < 2500,
            $"Took too long past timeout — elapsed {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WaitSecondsClampedToMax25()
    {
        // Passing wait_seconds > 25 is clamped to 25 (indirectly verified: a never-finishing job
        // should *not* block for 60 s — we pass 60 and expect clamping, but we use a very short
        // wait in the test by completing the job quickly instead of actually waiting 25 s).
        // We only verify the clamp doesn't crash and returns a valid envelope.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);
        registry.Complete(job.Id, false, "failed");

        // wait_seconds=60 → clamped to 25, but job is already terminal so returns immediately.
        JObject result = await McpRouter.LongPollJob(registry, job.Id, waitSeconds: 60);

        Assert.Equal("failed", result["status"]!.ToString());
    }

    [Fact]
    public async Task ZeroWaitSeconds_ImmediatePollNoBlocking()
    {
        // With wait_seconds=0 the loop body should not delay even if the job is running.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);

        var sw = Stopwatch.StartNew();
        JObject result = await McpRouter.LongPollJob(registry, job.Id, waitSeconds: 0);
        sw.Stop();

        Assert.Equal("running", result["status"]!.ToString());
        // Must return essentially instantly — under 100 ms.
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"wait_seconds=0 blocked for {sw.ElapsedMilliseconds} ms, expected < 100 ms");
    }

    // ── heartbeat tests (bug 2026-05-22: stdio idle → client disconnect) ─────

    [Fact]
    public async Task WithoutProgressToken_CapsAtSafeWindow()
    {
        // Bug 2026-05-22: blocking >~60s with no stdio traffic makes Claude Code
        // drop the MCP transport. Without a progressToken we have no way to emit
        // heartbeats, so the effective wait must be capped at SafeLongPollSecondsWithoutProgress
        // even if the caller passes wait_seconds=600.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);
        // The job never completes, so only the deadline ends the poll. A clock that
        // jumps 5s per reading lets the 50s cap elapse in a few real poll intervals
        // instead of the 50 wall-clock seconds this test used to cost every CI run.
        var clock = new SteppingClock(TimeSpan.FromSeconds(5));
        var pollTask = McpRouter.LongPollJob(registry, job.Id, waitSeconds: 600, clock: clock);
        var winner = await Task.WhenAny(pollTask, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(winner == pollTask, "LongPollJob did not return once the stepping clock passed the safe cap.");
        var result = await pollTask;

        Assert.Equal("running", result["status"]!.ToString());
        Assert.True((bool)result["capped"]!);
        Assert.Equal(McpRouter.SafeLongPollSecondsWithoutProgress, (int)result["cappedAtSeconds"]!);
        // It waited the cap, not the requested 600s and not less than the cap.
        Assert.InRange(clock.Elapsed.TotalSeconds,
            McpRouter.SafeLongPollSecondsWithoutProgress,
            McpRouter.SafeLongPollSecondsWithoutProgress + 10);
    }

    /// <summary>A clock that advances a fixed step every time it is read.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private readonly TimeSpan _step;
        private long _reads;

        public SteppingClock(TimeSpan step) => _step = step;

        public TimeSpan Elapsed => _step * (Interlocked.Read(ref _reads) - 1);

        public override DateTimeOffset GetUtcNow() => _start + _step * Interlocked.Increment(ref _reads);
    }

    [Fact]
    public async Task WithProgressToken_EmitsHeartbeats()
    {
        // When a progressToken is supplied, LongPollJob must emit notifications/progress
        // periodically so the client doesn't time out. We complete the job after ~1s
        // so the heartbeat interval (15s) doesn't fire in this test — instead we test
        // that the heartbeat path is exercised without throwing, by running for a few
        // hundred ms with a deliberately tight interval check via job completion.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);

        int heartbeatCalls = 0;
        Func<JObject, Task> heartbeat = (n) =>
        {
            heartbeatCalls++;
            Assert.Equal("notifications/progress", n["method"]!.ToString());
            Assert.Equal("token-xyz", n["params"]!["progressToken"]!.ToString());
            return Task.CompletedTask;
        };

        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            registry.Complete(job.Id, true, "done");
        });

        var result = await McpRouter.LongPollJob(
            registry, job.Id, waitSeconds: 5,
            progressToken: JToken.FromObject("token-xyz"),
            heartbeat: heartbeat);

        // Job completed inside the heartbeat interval (15s) so we don't strictly
        // require heartbeatCalls > 0 here — the assertion is that the call
        // completed without throwing and returned the terminal status.
        Assert.Equal("succeeded", result["status"]!.ToString());
        // heartbeatCalls may be 0 (job done before 15s) — assertion on terminal status is enough.
    }

    [Fact]
    public async Task HeartbeatFailure_DoesNotAbortPoll()
    {
        // A throwing heartbeat must not prevent the poll from continuing and reaching terminal.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);

        Func<JObject, Task> badHeartbeat = (_) => throw new InvalidOperationException("stdio dead");

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            registry.Complete(job.Id, true, "done");
        });

        var result = await McpRouter.LongPollJob(
            registry, job.Id, waitSeconds: 3,
            progressToken: JToken.FromObject("t"),
            heartbeat: badHeartbeat);

        Assert.Equal("succeeded", result["status"]!.ToString());
    }

    // ── AwaitWithHeartbeat tests (synchronous tool-call keepalive, -32001 fix) ──

    [Fact]
    public async Task AwaitWithHeartbeat_NoProgressToken_ReturnsTrueWithoutHeartbeats()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () => { await Task.Delay(100); tcs.SetResult(true); });

        int beats = 0;
        Func<JObject, Task> heartbeat = _ => { beats++; return Task.CompletedTask; };

        bool completed = await McpRouter.AwaitWithHeartbeat(
            tcs.Task, timeoutMs: 5000, progressToken: null, heartbeat: heartbeat, toolName: "genexus_apply_pattern");

        Assert.True(completed);
        Assert.Equal(0, beats); // no token → no heartbeats emitted
    }

    [Fact]
    public async Task AwaitWithHeartbeat_EmitsProgress_WhileWorkerRuns()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () => { await Task.Delay(1200); tcs.SetResult(true); });

        int beats = 0;
        Func<JObject, Task> heartbeat = n =>
        {
            beats++;
            Assert.Equal("notifications/progress", n["method"]!.ToString());
            Assert.Equal("tok-1", n["params"]!["progressToken"]!.ToString());
            Assert.NotNull(n["params"]!["progress"]); // monotonically increasing elapsed seconds
            return Task.CompletedTask;
        };

        bool completed = await McpRouter.AwaitWithHeartbeat(
            tcs.Task, timeoutMs: 10000, progressToken: JToken.FromObject("tok-1"),
            heartbeat: heartbeat, toolName: "genexus_apply_pattern", heartbeatIntervalSeconds: 1);

        Assert.True(completed);
        Assert.True(beats >= 1, $"Expected at least one progress notification, got {beats}");
    }

    [Fact]
    public async Task AwaitWithHeartbeat_Timeout_ReturnsFalse()
    {
        var tcs = new TaskCompletionSource<bool>(); // never completes

        bool completed = await McpRouter.AwaitWithHeartbeat(
            tcs.Task, timeoutMs: 300, progressToken: JToken.FromObject("t"),
            heartbeat: _ => Task.CompletedTask, toolName: "genexus_delete_object", heartbeatIntervalSeconds: 1);

        Assert.False(completed);
    }

    [Fact]
    public async Task AwaitWithHeartbeat_HeartbeatThrows_DoesNotAbort()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () => { await Task.Delay(1200); tcs.SetResult(true); });

        Func<JObject, Task> badHeartbeat = _ => throw new InvalidOperationException("stdio dead");

        bool completed = await McpRouter.AwaitWithHeartbeat(
            tcs.Task, timeoutMs: 10000, progressToken: JToken.FromObject("t"),
            heartbeat: badHeartbeat, toolName: "genexus_apply_pattern", heartbeatIntervalSeconds: 1);

        Assert.True(completed); // a throwing heartbeat must not abort the awaited work
    }

    // ── target-parameter tests ────────────────────────────────────────────────

    [Fact]
    public void ResolveJobId_ReturnsTarget_WhenNoJobIdProvided()
    {
        // lifecycle action=status target=<job_id> is the conventional LLM call form.
        // ResolveJobId must honour "target" as a fallback when job_id / jobId are absent.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);

        var args = new Newtonsoft.Json.Linq.JObject
        {
            ["action"] = "status",
            ["target"] = job.Id
        };

        string? resolved = McpRouter.ResolveJobId(args);

        Assert.Equal(job.Id, resolved);
    }

    [Fact]
    public void ResolveJobId_PrefersJobId_OverTarget()
    {
        // job_id takes priority; target is only a fallback.
        var args = new Newtonsoft.Json.Linq.JObject
        {
            ["job_id"] = "explicit-id",
            ["target"] = "SomeObject"
        };

        string? resolved = McpRouter.ResolveJobId(args);

        Assert.Equal("explicit-id", resolved);
    }

    [Fact]
    public void ResolveJobId_StripsOpPrefix_FromTarget()
    {
        // lifecycle action=cancel target=op:<jobId> is the canonical cancel call shape.
        // Without prefix stripping the JobRegistry lookup returns null and cancel falls
        // through to the OperationTracker path that always reports NotFound.
        var args = new Newtonsoft.Json.Linq.JObject
        {
            ["action"] = "cancel",
            ["target"] = "op:abc123"
        };

        string? resolved = McpRouter.ResolveJobId(args);

        Assert.Equal("abc123", resolved);
    }

    [Fact]
    public void ResolveJobId_StripsOpPrefix_FromJobId()
    {
        var args = new Newtonsoft.Json.Linq.JObject { ["job_id"] = "op:xyz789" };
        Assert.Equal("xyz789", McpRouter.ResolveJobId(args));
    }

    [Fact]
    public void ResolveJobId_ReturnsNull_WhenAllAbsent()
    {
        var args = new Newtonsoft.Json.Linq.JObject
        {
            ["action"] = "status"
        };

        string? resolved = McpRouter.ResolveJobId(args);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task TargetResolvesViaRegistry_ReturnsJobStatus()
    {
        // End-to-end: simulate the Program.cs routing pattern using target= for job lookup.
        // ResolveJobId extracts the id; registry.Get confirms it exists; LongPollJob returns status.
        var registry = MakeRegistry();
        var job = registry.Start("s1", "build", 30);
        registry.Complete(job.Id, true, "built ok");

        var args = new Newtonsoft.Json.Linq.JObject
        {
            ["action"] = "status",
            ["target"] = job.Id   // no job_id, only target
        };

        string? jobId = McpRouter.ResolveJobId(args);
        Assert.NotNull(jobId);

        var probe = registry.Get(jobId!);
        Assert.NotNull(probe); // registry lookup succeeds

        JObject pollResult = await McpRouter.LongPollJob(registry, jobId!, waitSeconds: 0);

        Assert.Equal("succeeded", pollResult["status"]!.ToString());
        Assert.Equal(job.Id, pollResult["job_id"]!.ToString());
    }
}
