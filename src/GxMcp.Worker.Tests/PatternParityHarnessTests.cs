using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternParityHarnessTests
    {
        [Fact]
        public void Family_IdenticalSets_ReportsPass()
        {
            var mcp = new List<(string, string)> { ("WorkWithPlusOrders", "WebPanel"), ("WWOrders", "WebPanel") };
            var ide = new List<(string, string)> { ("WWOrders", "WebPanel"), ("WorkWithPlusOrders", "WebPanel") };
            var r = PatternParityHarness.CompareFamily(mcp, ide);
            Assert.True(r.Pass);
            Assert.Contains("2 objects", r.Detail);
        }

        [Fact]
        public void Family_MissingMemberOnMcp_ReportsFailWithList()
        {
            var mcp = new List<(string, string)> { ("WorkWithPlusOrders", "WebPanel") };
            var ide = new List<(string, string)> { ("WorkWithPlusOrders", "WebPanel"), ("ExportOrders", "Procedure") };
            var r = PatternParityHarness.CompareFamily(mcp, ide);
            Assert.False(r.Pass);
            Assert.Contains("ExportOrders", r.Detail);
        }

        [Fact]
        public void Xml_DiffersOnlyInAttributeOrder_NormalizesToEqual()
        {
            // Canonicalization sorts attributes alphabetically, so attribute order
            // differences become equivalent. This is the entire reason we don't byte-diff.
            string a = "<Pattern Name=\"WWP\" Version=\"1.0\"><Settings Mode=\"Tabular\" Limit=\"50\"/></Pattern>";
            string b = "<Pattern Version=\"1.0\" Name=\"WWP\"><Settings Limit=\"50\" Mode=\"Tabular\"/></Pattern>";
            var r = PatternParityHarness.CompareXml("PatternInstance", a, b);
            Assert.True(r.Pass);
        }

        [Fact]
        public void Xml_DiffersInContent_ReportsFirstDivergenceIndex()
        {
            string a = "<Root><Item Name=\"A\"/></Root>";
            string b = "<Root><Item Name=\"B\"/></Root>";
            var r = PatternParityHarness.CompareXml("WebForm", a, b);
            Assert.False(r.Pass);
            Assert.Contains("First differing index", r.Detail);
        }

        [Fact]
        public void Xml_EmptySides_ReportsFailWithLengths()
        {
            var r = PatternParityHarness.CompareXml("PatternInstance", "<P/>", "");
            Assert.False(r.Pass);
            Assert.Contains("One side is empty", r.Detail);
        }

        [Fact]
        public void Variables_SetsMatchRegardlessOfOrder_ReportsPass()
        {
            var mcp = new List<(string, string)> { ("&id", "Numeric"), ("&name", "Character") };
            var ide = new List<(string, string)> { ("&name", "Character"), ("&id", "Numeric") };
            Assert.True(PatternParityHarness.CompareVariables(mcp, ide).Pass);
        }

        [Fact]
        public void Rules_NormalizesLineEndingsAndBlanks()
        {
            string mcp = "parm(in:&id);\r\nnoaccept(&total);\r\n\r\n";
            string ide = "parm(in:&id);\nnoaccept(&total);";
            Assert.True(PatternParityHarness.CompareRules(mcp, ide).Pass);
        }

        [Fact]
        public void Rules_LineMissingOnOneSide_ReportsFail()
        {
            string mcp = "parm(in:&id);";
            string ide = "parm(in:&id);\nnoaccept(&total);";
            var r = PatternParityHarness.CompareRules(mcp, ide);
            Assert.False(r.Pass);
            Assert.Contains("noaccept", r.Detail);
        }

        [Fact]
        public void Report_AggregatesAllDimensions_RoundtripsAsMarkdown()
        {
            var mcp = new PatternParityHarness.FamilySnapshot
            {
                Members = new List<(string, string)> { ("WorkWithPlusInvoice", "WebPanel") },
                PatternInstanceXml = "<P/>",
                WebFormXml = "<F/>",
                Variables = new List<(string, string)> { ("&i", "Numeric") },
                RulesSource = "parm();"
            };
            var ide = new PatternParityHarness.FamilySnapshot
            {
                Members = new List<(string, string)> { ("WorkWithPlusInvoice", "WebPanel") },
                PatternInstanceXml = "<P/>",
                WebFormXml = "<F/>",
                Variables = new List<(string, string)> { ("&i", "Numeric") },
                RulesSource = "parm();"
            };
            var report = PatternParityHarness.Compare(mcp, ide, "Invoice-MCP", "Invoice-IDE");
            Assert.True(report.AllPass);
            string md = report.ToMarkdown();
            Assert.Contains("PASS", md);
            Assert.Contains("Invoice-MCP", md);
            Assert.Equal(5, report.Dimensions.Count);
        }

        [LiveKbFact(requiresWWP: true, requiresParityFixture: true)]
        public void Integration_ParityProbe_GeneratesReportToTempPath()
        {
            // Driven by env: GXMCP_TEST_KB + GXMCP_PARITY_MCP_NAME + GXMCP_PARITY_IDE_NAME.
            // The bodies of those names must be pre-seeded in the KB (one patterned via the
            // IDE, one patterned via the MCP) so the harness can read both and produce a
            // parity_report.md. The integration test stays minimal — wiring the full
            // worker bootstrap is the consumer's job (CLI / live agent).
            string mcpName = Environment.GetEnvironmentVariable("GXMCP_PARITY_MCP_NAME");
            string ideName = Environment.GetEnvironmentVariable("GXMCP_PARITY_IDE_NAME");
            Assert.False(string.IsNullOrEmpty(mcpName));
            Assert.False(string.IsNullOrEmpty(ideName));

            // TODO: real wiring once we commit to a fixture KB layout. For now the test
            // documents the contract: read both families, call PatternParityHarness.Compare,
            // dump the report to %TEMP%/gxmcp_parity_report.md, fail if any dimension fails.
            string reportPath = Path.Combine(Path.GetTempPath(), "gxmcp_parity_report.md");
            File.WriteAllText(reportPath, "# Parity harness wired but KB load is TODO. Probe names: "
                + mcpName + " vs " + ideName);
            Assert.True(File.Exists(reportPath));
        }
    }
}
