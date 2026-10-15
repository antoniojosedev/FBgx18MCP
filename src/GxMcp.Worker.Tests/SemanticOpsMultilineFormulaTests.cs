using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SemanticOpsMultilineFormulaTests
    {
        private const string DslWithMultilineFormulaAtEnd =
@"McpTstFml3Id* : NUMERIC(4) // ""Mcp Tst Fml3 Id""
McpTstFml3Valor : NUMERIC(4) // ""Mcp Tst Fml3 Valor""
McpTstFml3Resultado : NUMERIC(6) // ""Mcp Tst Fml3 Resultado"", [Formula: McpTstFml3Valor * 2
	IF McpTstFml3Valor > 0;]";

        private const string DslWithMultilineFormulaInMiddle =
@"McpTstFml3Id* : NUMERIC(4) // ""Mcp Tst Fml3 Id""
McpTstFml3Resultado : NUMERIC(6) // ""Mcp Tst Fml3 Resultado"", [Formula: McpTstFml3Valor * 2
	IF McpTstFml3Valor > 0;]
McpTstFml3Depois : NUMERIC(4) // ""Mcp Tst Fml3 Depois""";

        [Fact]
        public void AddAttribute_WithAfter_InsertsAfterCompleteMultilineFormula()
        {
            var service = new SemanticOpsService();
            var ops = new List<SemanticOp>
            {
                new SemanticOp
                {
                    Op = "add_attribute",
                    Args = new JObject
                    {
                        ["name"] = "McpTstFml3Extra",
                        ["type"] = "NUMERIC(4)",
                        ["after"] = "McpTstFml3Resultado"
                    }
                }
            };

            var outcome = service.ApplyTransactionStructureDsl(DslWithMultilineFormulaInMiddle, ops, "strict");

            Assert.True(outcome.Results[0].Ok);
            var lines = outcome.Text.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            
            // The formula continuation line must remain intact, and McpTstFml3Extra must be AFTER it.
            int formulaContinuationIdx = -1;
            int newAttrIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("IF McpTstFml3Valor > 0;]"))
                    formulaContinuationIdx = i;
                if (lines[i].StartsWith("McpTstFml3Extra"))
                    newAttrIdx = i;
            }

            Assert.True(formulaContinuationIdx >= 0);
            Assert.True(newAttrIdx > formulaContinuationIdx, "New attribute must be inserted after the multiline formula continuation line.");
        }

        [Fact]
        public void AddAttribute_WithoutAfter_InsertsAfterLastAttributeMultilineFormula()
        {
            var service = new SemanticOpsService();
            var ops = new List<SemanticOp>
            {
                new SemanticOp
                {
                    Op = "add_attribute",
                    Args = new JObject
                    {
                        ["name"] = "McpTstFml3Extra",
                        ["type"] = "NUMERIC(4)"
                    }
                }
            };

            var outcome = service.ApplyTransactionStructureDsl(DslWithMultilineFormulaAtEnd, ops, "strict");

            Assert.True(outcome.Results[0].Ok);
            var lines = outcome.Text.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            
            int formulaContinuationIdx = -1;
            int newAttrIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("IF McpTstFml3Valor > 0;]"))
                    formulaContinuationIdx = i;
                if (lines[i].StartsWith("McpTstFml3Extra"))
                    newAttrIdx = i;
            }

            Assert.True(formulaContinuationIdx >= 0);
            Assert.True(newAttrIdx > formulaContinuationIdx, "New attribute must be appended after the multiline formula continuation line at the end.");
        }

        [Fact]
        public void SetAttribute_ReplacesEntireMultilineFormulaEntry()
        {
            var service = new SemanticOpsService();
            var ops = new List<SemanticOp>
            {
                new SemanticOp
                {
                    Op = "set_attribute",
                    Args = new JObject
                    {
                        ["name"] = "McpTstFml3Resultado",
                        ["type"] = "NUMERIC(8)",
                        ["description"] = "New Description",
                        ["formula"] = "McpTstFml3Valor * 3"
                    }
                }
            };

            var outcome = service.ApplyTransactionStructureDsl(DslWithMultilineFormulaInMiddle, ops, "strict");

            Assert.True(outcome.Results[0].Ok);
            Assert.DoesNotContain("IF McpTstFml3Valor > 0", outcome.Text);
            Assert.Contains("McpTstFml3Resultado : NUMERIC(8) // \"New Description\", [Formula: McpTstFml3Valor * 3]", outcome.Text);
            Assert.Contains("McpTstFml3Depois", outcome.Text);
        }

        [Fact]
        public void RemoveAttribute_RemovesEntireMultilineFormulaEntry()
        {
            var service = new SemanticOpsService();
            var ops = new List<SemanticOp>
            {
                new SemanticOp
                {
                    Op = "remove_attribute",
                    Args = new JObject
                    {
                        ["name"] = "McpTstFml3Resultado"
                    }
                }
            };

            var outcome = service.ApplyTransactionStructureDsl(DslWithMultilineFormulaInMiddle, ops, "strict");

            Assert.True(outcome.Results[0].Ok);
            Assert.DoesNotContain("McpTstFml3Resultado", outcome.Text);
            Assert.DoesNotContain("IF McpTstFml3Valor > 0", outcome.Text);
            Assert.Contains("McpTstFml3Id", outcome.Text);
            Assert.Contains("McpTstFml3Depois", outcome.Text);
        }

        [Fact]
        public void StructurePersistenceVerification_DefaultsToSemantic_AndStripsGeneratedDescriptions()
        {
            string requested = "McpTstFml3Id* : NUMERIC(4) // \"Mcp Tst Fml3 Id\"\nMcpTstFml3Valor : NUMERIC(4)";
            string persisted = "McpTstFml3Id* : NUMERIC(4) // \"Mcp Tst Fml3 Id\"\r\nMcpTstFml3Valor : NUMERIC(4) // \"Mcp Tst Fml3 Valor\"";

            // With verifyMode omitted (null) and partName="Structure", should verify cleanly via semantic strip.
            var result = WriteService.EvaluatePersistedVerification(requested, persisted, false, null, null, "Structure");

            Assert.True(result.Matches);
            Assert.Equal("verified", result.State);
        }
    }
}
