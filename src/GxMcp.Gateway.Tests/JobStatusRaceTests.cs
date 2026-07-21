using System;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Plan 026: Complete() and Cancel() mutate the same JobEntry fields with no lock.
    // Complete() did a non-atomic read-then-write ("if status != cancelled, set
    // succeeded/failed"); a Cancel() landing between the check and the assignment
    // could clobber "cancelled" back to "succeeded"/"failed". JobEntry.SyncRoot makes
    // both methods atomic with respect to each other.
    public class JobStatusRaceTests
    {
        [Fact]
        public void CancelThenComplete_LeavesStatusCancelled()
        {
            var reg = new BackgroundJobRegistry(600);
            var j = reg.Start("s1", "build", 60);
            Assert.True(reg.Cancel(j.Id, "user requested stop"));
            reg.Complete(j.Id, success: true, summary: "Build finished anyway");
            Assert.Equal("cancelled", reg.Get(j.Id)!.Status);
        }

        [Fact]
        public void ConcurrentCancelAndComplete_AcrossManyJobs_AllReachTerminalStatus()
        {
            var reg = new BackgroundJobRegistry(600);
            const int jobCount = 200;
            var ids = Enumerable.Range(0, jobCount)
                .Select(i => reg.Start("s1", "build", 60).Id)
                .ToList();

            Parallel.ForEach(ids, id =>
            {
                var t1 = Task.Run(() => reg.Cancel(id, "race-cancel"));
                var t2 = Task.Run(() => reg.Complete(id, success: true, summary: "race-complete"));
                Task.WaitAll(t1, t2);
            });

            foreach (var id in ids)
            {
                var job = reg.Get(id);
                Assert.NotNull(job);
                // Terminal status regardless of interleaving; if Cancel ever "won"
                // partially and Complete raced past it, status must still be one of
                // the recognized terminal states, never left running / corrupted.
                Assert.True(
                    job!.Status == "cancelled" || job.Status == "succeeded" || job.Status == "failed",
                    $"unexpected status '{job.Status}' for job {id}");
                Assert.NotNull(job.CompletedAt);
            }
        }
    }
}
