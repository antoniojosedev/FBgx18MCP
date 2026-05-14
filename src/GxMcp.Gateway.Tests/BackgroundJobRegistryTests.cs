using GxMcp.Gateway;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

public class BackgroundJobRegistryTests
{
    [Fact]
    public void Start_CreatesRunningJob()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("s1", "build", 30);
        Assert.Equal("running", j.Status);
        Assert.NotNull(j.Id);
        Assert.NotEqual("", j.Id);
    }

    [Fact]
    public void Complete_TransitionsToSucceeded()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("s1", "build", 30);
        r.Complete(j.Id, true, "ok");
        Assert.Equal("succeeded", r.Get(j.Id)!.Status);
        Assert.Equal("ok", r.Get(j.Id)!.Summary);
    }

    [Fact]
    public void Complete_TransitionsToFailed()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("s1", "build", 30);
        r.Complete(j.Id, false, "boom");
        Assert.Equal("failed", r.Get(j.Id)!.Status);
    }

    [Fact]
    public void Snapshot_ReturnsUnseenCompletions()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("s1", "build", 30);
        r.Complete(j.Id, true, "ok");
        var snap = r.SnapshotForSession("s1");
        Assert.Single(snap);
        Assert.Equal(j.Id, snap[0].Id);
    }

    [Fact]
    public void Snapshot_RemovesAfterSeen()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("s1", "build", 30);
        r.Complete(j.Id, true, "ok");
        var first = r.SnapshotForSession("s1");
        r.MarkSeen("s1", new[] { first[0].Id });
        Assert.Empty(r.SnapshotForSession("s1"));
    }

    [Fact]
    public void Snapshot_RunningJobsAlwaysAppear()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("s1", "build", 30);
        r.MarkSeen("s1", new[] { j.Id });
        // Still running, so still shows
        Assert.NotEmpty(r.SnapshotForSession("s1"));
    }

    [Fact]
    public void Sweep_RemovesExpiredTerminalJobs()
    {
        var r = new BackgroundJobRegistry(0);
        var j = r.Start("s1", "build", 0);
        r.Complete(j.Id, true, "ok");
        Thread.Sleep(50);
        r.SweepExpired();
        Assert.Null(r.Get(j.Id));
    }

    [Fact]
    public void Sessions_Isolated()
    {
        var r = new BackgroundJobRegistry(600);
        var a = r.Start("s1", "build", 30);
        var b = r.Start("s2", "build", 30);
        r.Complete(a.Id, true, "ok");
        r.Complete(b.Id, true, "ok");
        Assert.Single(r.SnapshotForSession("s1"));
        Assert.Single(r.SnapshotForSession("s2"));
    }

    [Fact]
    public void Get_UnknownReturnsNull()
    {
        var r = new BackgroundJobRegistry(600);
        Assert.Null(r.Get("nonexistent"));
    }
}
