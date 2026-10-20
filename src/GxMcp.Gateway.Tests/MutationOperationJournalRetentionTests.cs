using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Regression for the 7-day journal retention. Settled records age out, but an
    /// unknown-outcome ("started") record is the durable fence that stops a possibly
    /// committed mutation from replaying, so it must survive load and prune passes.
    /// </summary>
    public sealed class MutationOperationJournalRetentionTests
    {
        private const string Kb = "C:\\KB\\Orders";
        private const string Tool = "genexus_lifecycle";

        [Fact]
        public void AgedStartedRecordKeepsItsFenceWhileAgedCompletedRecordExpires()
        {
            using var file = new JournalFile();
            var first = new MutationOperationJournal(file.FilePath);
            Assert.Equal(MutationOperationJournal.BeginResult.Started,
                first.Begin(Kb, Tool, "started-key", "payload-s"));
            Assert.Equal(MutationOperationJournal.BeginResult.Started,
                first.Begin(Kb, Tool, "completed-key", "payload-c"));
            first.Complete(Kb, Tool, "completed-key", "payload-c");

            AgeEveryRecord(file.FilePath, TimeSpan.FromDays(30));

            var restarted = new MutationOperationJournal(file.FilePath);
            // Control: established completed retention still expires, so the key is free again.
            Assert.Equal(MutationOperationJournal.BeginResult.Started,
                restarted.Begin(Kb, Tool, "completed-key", "payload-c"));
            // Regression: the aged unknown-outcome record must still refuse a same-key replay.
            Assert.Equal(MutationOperationJournal.BeginResult.UnknownAfterRestart,
                restarted.Begin(Kb, Tool, "started-key", "payload-s"));
        }

        [Fact]
        public void AgedStartedRecordSurvivesAPrunePassOnALiveJournal()
        {
            using var file = new JournalFile();
            var journal = new MutationOperationJournal(file.FilePath);
            Assert.Equal(MutationOperationJournal.BeginResult.Started,
                journal.Begin(Kb, Tool, "started-key", "payload-s"));

            AgeEveryRecord(file.FilePath, TimeSpan.FromDays(30));

            // Each Begin reloads the file and then prunes it; the unknown fence has to survive both.
            Assert.Equal(MutationOperationJournal.BeginResult.Started,
                journal.Begin(Kb, Tool, "fresh-key", "payload-f"));
            Assert.Equal(MutationOperationJournal.BeginResult.UnknownAfterRestart,
                journal.Begin(Kb, Tool, "started-key", "payload-s"));
        }

        [Fact]
        public void AgedStartedRecordCanBeReconciledAfterRestart()
        {
            using var file = new JournalFile();
            var first = new MutationOperationJournal(file.FilePath);
            Assert.Equal(MutationOperationJournal.BeginResult.Started,
                first.Begin(Kb, Tool, "started-key", "payload-s"));

            AgeEveryRecord(file.FilePath, TimeSpan.FromDays(30));

            var restarted = new MutationOperationJournal(file.FilePath);
            JObject closed = restarted.Reconcile(Kb, Tool, "started-key", "target verified absent");
            Assert.Equal("reconciled", closed["status"]?.ToString());
            Assert.Equal(MutationOperationJournal.BeginResult.Completed,
                restarted.Begin(Kb, Tool, "started-key", "payload-s"));
        }

        private static void AgeEveryRecord(string path, TimeSpan age)
        {
            JObject document = JObject.Parse(File.ReadAllText(path));
            foreach (JObject row in (JArray)document["entries"]!)
                row["UpdatedAtUtc"] = new JValue(DateTime.UtcNow - age);
            File.WriteAllText(path, document.ToString(Newtonsoft.Json.Formatting.None));
        }

        private sealed class JournalFile : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(), "gxmcp-journal-retention-" + Guid.NewGuid().ToString("N"));

            public string FilePath => Path.Combine(Root, "operations.json");

            public void Dispose()
            {
                try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
            }
        }
    }
}
