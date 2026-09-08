using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression guard for the Team Development pending list.
    //
    // KBModel.LastCommitDate is the "everything up to this instant is already
    // committed" baseline that ITeamDevClientService.GetLocalChanges(model)
    // measures against — the same list surfaced by genexus_gxserver
    // action=pending and by the IDE's Team Dev > Commit tab. Stamping it to
    // UtcNow after a write tells Team Development that nothing is pending, and
    // because it lives on the MODEL it wipes the entry for every object in the
    // KB, including objects the worker never touched.
    //
    // Measured 2026-09-08 against v2.56.0: the IDE marked one object
    // (pending count = 1); a single genexus_edit on a DIFFERENT object dropped
    // the count to 0. Introduced by 4f7cc9c "IDE concurrency detection, sync
    // flushing, and revision stamping (#128)", which needs only
    // LastObjectsVersionDate to make the IDE reload the worker's writes.
    public class TeamDevBaselineGuardTests
    {
        private static string[] CodeLines(string fileName)
        {
            string path = Path.GetFullPath(Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", fileName));
            Assert.True(File.Exists(path), fileName + " must exist at: " + path);

            // Comments legitimately name LastCommitDate to explain why it is
            // left alone; only real code counts.
            return File.ReadAllLines(path)
                .Where(l => !l.TrimStart().StartsWith("//"))
                .ToArray();
        }

        [Fact]
        public void WriteService_NeverStamps_ModelLastCommitDate()
        {
            var offenders = CodeLines("WriteService.cs")
                .Select((line, i) => new { line, n = i + 1 })
                .Where(x => Regex.IsMatch(x.line, @"LastCommitDate\s*="))
                .Select(x => "line " + x.n + ": " + x.line.Trim())
                .ToArray();

            Assert.True(offenders.Length == 0,
                "Assigning KBModel.LastCommitDate moves the Team Development commit baseline and " +
                "empties the pending-commit list for every object in the KB. Stamp " +
                "LastObjectsVersionDate instead — that is what makes the IDE notice the worker's " +
                "writes. Offending lines:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void WriteService_StillStamps_ModelLastObjectsVersionDate()
        {
            // The #128 intent must survive the guard above: the IDE has to be
            // told the model's objects changed, or it serves stale objects.
            Assert.Contains(
                CodeLines("WriteService.cs"),
                l => Regex.IsMatch(l, @"LastObjectsVersionDate\s*="));
        }
    }
}
