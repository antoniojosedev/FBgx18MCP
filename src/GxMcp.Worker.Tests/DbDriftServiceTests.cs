using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Item 41 (mcp-improvements-2026-05-22) — Transaction ↔ DB drift detection.
    /// Tests use a fake IReorgSource so no live SDK is needed.
    /// </summary>
    public class DbDriftServiceTests
    {
        private sealed class FakeReorgSource : DbDriftService.IReorgSource
        {
            public string Json { get; set; } = "{\"status\":\"Stub\",\"target\":\"\",\"ddl\":[],\"summary\":{}}";
            public string ReorgPreview(string target) => Json;
        }

        [Fact]
        public void Check_EmptyDdl_StubFlagSetAndNoDrift()
        {
            var src = new FakeReorgSource();
            var svc = new DbDriftService(src);
            var jo = JObject.Parse(svc.Check("Aluno"));

            Assert.Equal("Success", jo["status"]?.ToString());
            Assert.Equal("reorg_plan", jo["source"]?.ToString());
            Assert.Equal(0, jo["summary"]?["tables_with_drift"]?.ToObject<int>());
            Assert.True(jo["summary"]?["reorg_stub"]?.ToObject<bool>());
            Assert.NotNull(jo["note"]);
        }

        [Fact]
        public void Check_AlterAddColumn_ClassifiedAsMissingColumn()
        {
            var src = new FakeReorgSource
            {
                Json = "{\"status\":\"Ready\",\"target\":\"Aluno\",\"ddl\":["
                       + "{\"table\":\"TALUNO\",\"statement\":\"ALTER TABLE TALUNO ADD COLUMN ALUNUMREG VARCHAR(50)\"}"
                       + "],\"summary\":{}}"
            };
            var svc = new DbDriftService(src);
            var jo = JObject.Parse(svc.Check("Aluno"));
            Assert.Equal(1, jo["summary"]?["tables_with_drift"]?.ToObject<int>());
            var tables = (JArray)jo["tables"];
            Assert.Single(tables);
            var t = (JObject)tables[0];
            Assert.Equal("TALUNO", t["name"]?.ToString());
            Assert.Equal("warning", t["severity"]?.ToString());
            var drift = (JArray)t["drift"];
            Assert.Equal("missing_column", drift[0]?["kind"]?.ToString());
        }

        [Fact]
        public void Check_CreateTable_ClassifiedAsMissingTableErrorSeverity()
        {
            var src = new FakeReorgSource
            {
                Json = "{\"status\":\"Ready\",\"ddl\":["
                       + "\"CREATE TABLE TNEW (X INT)\""
                       + "]}"
            };
            var svc = new DbDriftService(src);
            var jo = JObject.Parse(svc.Check(null));
            var tables = (JArray)jo["tables"];
            Assert.Single(tables);
            Assert.Equal("error", tables[0]?["severity"]?.ToString());
            Assert.Equal("missing_table", tables[0]?["drift"]?[0]?["kind"]?.ToString());
            Assert.Equal("TNEW", tables[0]?["name"]?.ToString());
        }

        [Fact]
        public void Report_IncludesMarkdownReportField()
        {
            var src = new FakeReorgSource
            {
                Json = "{\"ddl\":[\"ALTER TABLE TX ADD Y INT\"]}"
            };
            var svc = new DbDriftService(src);
            var jo = JObject.Parse(svc.Report("X"));
            Assert.NotNull(jo["report"]);
            string md = jo["report"]!.ToString();
            Assert.Contains("Transaction", md);
            Assert.Contains("TX", md);
        }

        [Fact]
        public void Check_ReorgFailure_ReturnsStructuredError()
        {
            var src = new FakeReorgSource { Json = "not-json" };
            var svc = new DbDriftService(src);
            var jo = JObject.Parse(svc.Check("X"));
            Assert.Equal("Error", jo["status"]?.ToString());
            Assert.Equal("ReorgPreviewMalformed", jo["code"]?.ToString());
            Assert.Equal("reorg_plan", jo["source"]?.ToString());
        }
    }
}
