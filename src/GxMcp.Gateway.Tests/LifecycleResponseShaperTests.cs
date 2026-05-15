using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // v2.3.8 (Task 6.1): compact lifecycle status output by default.
    // Friction report 2026-05-15 #9: a failed build's full Errors[]/Warnings[]/Output
    // payload routinely overflows the assistant's tool-result budget. Compacting
    // returns counts + top-10 errors + warning dedup; opt out with compact=false.
    public class LifecycleResponseShaperTests
    {
        private static string MakeBuildStatus(int errors, int warnings, string repeatedWarning = null)
        {
            var errArr = new JArray();
            for (int i = 0; i < errors; i++) errArr.Add(new JObject { ["message"] = $"err{i}: CS0246", ["location"] = $"file{i}.cs(10,5)" });
            var warnArr = new JArray();
            if (repeatedWarning != null)
                for (int i = 0; i < warnings; i++) warnArr.Add(new JObject { ["message"] = repeatedWarning, ["location"] = $"file{i}.cs(1,1)" });
            else
                for (int i = 0; i < warnings; i++) warnArr.Add(new JObject { ["message"] = $"warn{i}", ["location"] = $"f{i}.cs(1,1)" });

            return new JObject
            {
                ["Status"] = "Failed",
                ["Phase"] = "Done",
                ["ExitCode"] = 1,
                ["ErrorCount"] = errors,
                ["WarningCount"] = warnings,
                ["Errors"] = errArr,
                ["Warnings"] = warnArr,
                ["Output"] = new string('x', 8000),
                ["TaskId"] = "abc12345"
            }.ToString();
        }

        [Fact]
        public void Compact_True_CapsErrorsAndDropsOutput()
        {
            var raw = MakeBuildStatus(30, 5);
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);

            Assert.Null(obj["Output"]);
            Assert.Equal(30, obj["errorCount"].Value<int>());
            Assert.Equal(5, obj["warningCount"].Value<int>());
            Assert.True(((JArray)obj["errors"]).Count <= 10);
            Assert.True(obj["truncated"].Value<bool>());
        }

        [Fact]
        public void Compact_True_DedupsRepeatedWarning()
        {
            var raw = MakeBuildStatus(0, 6, repeatedWarning: "GAM nao sera reorganizado");
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);

            var warns = (JArray)obj["warnings"];
            Assert.Single(warns);
            Assert.Equal(6, warns[0]["count"].Value<int>());
            Assert.Equal("GAM nao sera reorganizado", warns[0]["message"].ToString());
        }

        [Fact]
        public void Compact_False_PreservesOriginal()
        {
            var raw = MakeBuildStatus(3, 2);
            var same = LifecycleResponseShaper.Compact(raw, compact: false);
            Assert.Equal(raw, same);
        }

        [Fact]
        public void Compact_NonJsonInput_ReturnsAsIs()
        {
            // Defensive: shaper must never throw on unexpected payloads (e.g. text error messages).
            var raw = "not json";
            var same = LifecycleResponseShaper.Compact(raw, compact: true);
            Assert.Equal(raw, same);
        }

        [Fact]
        public void Compact_SmallResponse_NoTruncation()
        {
            var raw = MakeBuildStatus(2, 1);
            var obj = JObject.Parse(LifecycleResponseShaper.Compact(raw, compact: true));
            Assert.False(obj["truncated"].Value<bool>());
            Assert.Equal(2, ((JArray)obj["errors"]).Count);
        }
    }
}
