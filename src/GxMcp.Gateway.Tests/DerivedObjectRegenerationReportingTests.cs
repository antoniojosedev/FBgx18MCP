using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issues #353 and #559: Guards ensuring that WorkWith pattern and filter authoring
    /// report derivedObjectRegeneration as evidence rather than promising regeneration,
    /// and that the retired field generatedObjectsRegenerated is never reintroduced.
    /// </summary>
    public class DerivedObjectRegenerationReportingTests
    {
        [Fact]
        public void WorkWithToolHelp_NeverMentionsGeneratedObjectsRegenerated()
        {
            string help = ToolHelpCatalog.Get("genexus_workwith") ?? string.Empty;
            Assert.NotEmpty(help);
            Assert.DoesNotContain("generatedObjectsRegenerated", help, StringComparison.Ordinal);
        }

        [Fact]
        public void WorkWithToolHelp_MentionsDerivedObjectRegeneration()
        {
            string help = ToolHelpCatalog.Get("genexus_workwith") ?? string.Empty;
            Assert.NotEmpty(help);
            Assert.Contains("derivedObjectRegeneration", help, StringComparison.Ordinal);
        }

        [Fact]
        public void WorkWithToolDefinition_NeverMentionsGeneratedObjectsRegenerated()
        {
            var tools = JArray.Parse(GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            var workWithTool = tools.FirstOrDefault(t => string.Equals(t["name"]?.ToString(), "genexus_workwith", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(workWithTool);
            string json = workWithTool.ToString();
            Assert.DoesNotContain("generatedObjectsRegenerated", json, StringComparison.Ordinal);
        }

        [Fact]
        public void WorkWithFilterReceipt_DeclaresDerivedObjectRegenerationNotGuaranteed()
        {
            var receipt = new JObject
            {
                ["instance"] = "WorkWithCustomer",
                ["applied"] = true,
                ["derivedObjectRegeneration"] = "not_guaranteed"
            };

            Assert.Equal("not_guaranteed", receipt["derivedObjectRegeneration"]?.ToString());
            Assert.Null(receipt["generatedObjectsRegenerated"]);
        }
    }
}
