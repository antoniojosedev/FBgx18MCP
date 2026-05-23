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

        [Fact]
        public void Compact_True_SurfacesSuggestedRetry_WhenSuggestedRebuildTargetsPresent()
        {
            // FR (2026-05-21): BuildService already extracts the missing object
            // names from CS0246/CS2001 into SuggestedRebuildTargets. The compact
            // shaper must surface them as a ready-to-fire retry hint so the agent
            // doesn't have to scrape error[] paths by hand.
            var rawObj = JObject.Parse(MakeBuildStatus(2, 0));
            rawObj["SuggestedRebuildTargets"] = new JArray { "ConvenioValorizza", "PremioMerito", "ClasseTotalAlunos" };
            var compact = LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true);
            var obj = JObject.Parse(compact);

            var retry = obj["suggested_retry"] as JObject;
            Assert.NotNull(retry);
            Assert.Equal("build", retry!["action"]!.ToString());
            Assert.Equal("ConvenioValorizza,PremioMerito,ClasseTotalAlunos", retry["target"]!.ToString());
            Assert.Equal("direct", retry["includeCallees"]!.ToString());
            Assert.False(string.IsNullOrWhiteSpace(retry["hint"]!.ToString()));
        }

        [Fact]
        public void Compact_True_OmitsSuggestedRetry_WhenNoMissingObjects()
        {
            var raw = MakeBuildStatus(1, 0);
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);
            Assert.Null(obj["suggested_retry"]);
        }

        [Fact]
        public void ShouldCompact_DefaultsToTrue_AndHonorsExplicitFalseFlags()
        {
            Assert.True(LifecycleResponseShaper.ShouldCompact(null!));
            Assert.True(LifecycleResponseShaper.ShouldCompact(new JObject()));
            Assert.True(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = true }));
            Assert.False(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = false }));
            Assert.False(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = "false" }));
            Assert.False(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = "0" }));
        }

        // Friction 2026-05-22 item 10: build envelope classification. The async path
        // was previously matching "completed" (which the registry never emits) and
        // forcing "Build succeeded: 0w/0e/exit=0" into an <e>error{}> envelope.
        // ClassifyBuildOutcome is the single source of truth — covers all three
        // terminal cases.
        [Fact]
        public void ClassifyBuildOutcome_ZeroZeroExitClean_IsSuccess()
        {
            var payload = JObject.Parse(@"{""Status"":""Succeeded"",""ErrorCount"":0,""WarningCount"":0,""ExitCode"":0}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Success,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_PartialSuccess_IsWarning()
        {
            var payload = JObject.Parse(@"{""Status"":""Failed"",""ErrorCount"":0,""WarningCount"":0,""ExitCode"":1,""PartialSuccess"":true}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.PartialSuccess,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_RealFailure_IsError()
        {
            var payload = JObject.Parse(@"{""Status"":""Failed"",""ErrorCount"":3,""WarningCount"":1,""ExitCode"":1}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_HonorsCompactShape_LowercaseCountsAndPartial()
        {
            // Post-Compact shape uses lowercase errorCount/warningCount + partial_success
            // bool on the outer object. Classifier must accept both shapes.
            var compactShape = JObject.Parse(@"{""Status"":""Failed"",""errorCount"":0,""warningCount"":0,""ExitCode"":1,""partial_success"":true}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.PartialSuccess,
                LifecycleResponseShaper.ClassifyBuildOutcome(compactShape));
        }

        [Fact]
        public void ClassifyBuildOutcome_NullPayload_IsError()
        {
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(null));
        }

        [Fact]
        public void ClassifyBuildOutcome_StatusOnly_NoCounts_RespectsStatus()
        {
            var succ = JObject.Parse(@"{""Status"":""Succeeded""}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Success,
                LifecycleResponseShaper.ClassifyBuildOutcome(succ));
            var fail = JObject.Parse(@"{""Status"":""Failed""}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(fail));
        }
    }
}
