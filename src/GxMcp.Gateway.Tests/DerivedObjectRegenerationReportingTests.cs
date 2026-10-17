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
            // The Worker is net48 and cannot be referenced from here, so this pins the
            // production receipt builder's source; its behaviour is exercised by
            // WorkWithServiceTests.BuildFilterReceipt_ReportsNativeRouteAndInMemoryRereadScope.
            string adapter = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Compatibility", "WorkWithSdkAdapter.cs");
            int builder = adapter.IndexOf("static JObject BuildFilterReceipt(", StringComparison.Ordinal);
            Assert.True(builder >= 0, "the filter receipt builder must exist in WorkWithSdkAdapter");

            string body = adapter.Substring(builder);
            Assert.Contains("[\"derivedObjectRegeneration\"] = \"not_guaranteed\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("generatedObjectsRegenerated", adapter, StringComparison.Ordinal);
        }
    }
}
