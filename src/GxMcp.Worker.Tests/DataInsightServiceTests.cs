using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression (friction 2026-06-02): genexus_db sql_ddl returns structure-derived
    // ("heuristic") DDL when no native reorg SQL is available. Agents were treating
    // it as the authoritative schema. The response must self-describe its accuracy
    // so a heuristic result is never mistaken for an exact one.
    public class DataInsightServiceTests
    {
        [Fact]
        public void DdlAccuracy_Heuristic_LabelsAndPointsToReorg()
        {
            var acc = DataInsightService.BuildDdlAccuracy(hasNativeSql: false);
            Assert.Equal("heuristic", acc["accuracy"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(acc["accuracyNote"]?.ToString()));
            Assert.Contains("reorg", acc["verifyVia"]?.ToString(), System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DdlAccuracy_Native_IsExact_NoCaveats()
        {
            var acc = DataInsightService.BuildDdlAccuracy(hasNativeSql: true);
            Assert.Equal("exact", acc["accuracy"]?.ToString());
            Assert.Null(acc["accuracyNote"]);
            Assert.Null(acc["verifyVia"]);
        }
    }
}
