using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3: BlameService — porcelain parser + KbNotInGit envelope.
    // Tests target the pure helpers + the path that doesn't need a real
    // git binary. End-to-end git integration is left to the live worker.
    public class BlameServiceTests
    {
        [Fact]
        public void ParsePorcelain_TwoCommits_ReturnsBothEntries()
        {
            string sample =
                "abcdef0123456789abcdef0123456789abcdef01 1 1 2\n" +
                "author Alice\n" +
                "author-mail <alice@example.com>\n" +
                "author-time 1700000000\n" +
                "summary First commit\n" +
                "\tline one\n" +
                "1234567890abcdef1234567890abcdef12345678 2 2 1\n" +
                "author Bob\n" +
                "author-mail <bob@example.com>\n" +
                "author-time 1700001000\n" +
                "summary Second commit\n" +
                "\tline two\n";

            // ParsePorcelain is private static; exercise via reflection.
            var mi = typeof(BlameService).GetMethod(
                "ParsePorcelain",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(mi);
            var entries = (System.Collections.IEnumerable)mi!.Invoke(null, new object[] { sample })!;
            var list = entries.Cast<object>().ToList();
            Assert.Equal(2, list.Count);

            string FieldOf(object o, string n) =>
                o.GetType().GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                 ?.GetValue(o)?.ToString();
            Assert.Equal("Alice", FieldOf(list[0], "Author"));
            Assert.Equal("Bob", FieldOf(list[1], "Author"));
            Assert.Equal("line one", FieldOf(list[0], "LineContent"));
            Assert.Equal("First commit", FieldOf(list[0], "Summary"));
        }

        [Fact]
        public void Blame_NoKb_ReturnsNoKbError()
        {
            var svc = new BlameService(kbService: null, objectService: null);
            var json = JObject.Parse(svc.Blame(new BlameService.BlameRequest { Name = "Foo" }));
            Assert.Equal("NoKb", (string)json["code"]);
        }

        [Fact]
        public void Blame_MissingNameAndFilePath_ReturnsMissingArgs()
        {
            var svc = new BlameService(kbService: null, objectService: null);
            var json = JObject.Parse(svc.Blame(new BlameService.BlameRequest()));
            Assert.Equal("missingArgs", (string)json["code"]);
        }
    }
}
