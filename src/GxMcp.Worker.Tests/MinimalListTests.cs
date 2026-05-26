using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class MinimalListTests
    {
        private static JObject MakeItem(string name, string type, bool verbose = false)
        {
            return ListService.BuildItemForTest(name, type, "Test description", "ParentModule", "MainModule", "ParentModule/" + name, "ParentModule", verbose);
        }

        [Fact]
        public void DefaultVerboseFalse_Returns4Fields()
        {
            // Set legacy mode off (V1 enabled)
            System.Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);

            var item = MakeItem("InvoiceProc", "Procedure", verbose: false);

            Assert.Equal("InvoiceProc", item["name"]?.ToString());
            Assert.Equal("Procedure", item["type"]?.ToString());
            Assert.NotNull(item["path"]);
            Assert.NotNull(item["parent"]);
            Assert.NotNull(item["parentPath"]);

            Assert.Null(item["description"]);
            Assert.Null(item["module"]);

            Assert.Equal(5, item.Count);
        }

        [Fact]
        public void VerboseTrue_ReturnsFullShape()
        {
            // Set legacy mode off (V1 enabled)
            System.Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);

            var item = MakeItem("InvoiceProc", "Procedure", verbose: true);

            // Should have all 7 fields
            Assert.Equal("InvoiceProc", item["name"]?.ToString());
            Assert.Equal("Procedure", item["type"]?.ToString());
            Assert.NotNull(item["description"]);
            Assert.NotNull(item["parent"]);
            Assert.NotNull(item["module"]);
            Assert.NotNull(item["path"]);
            Assert.NotNull(item["parentPath"]);

            Assert.Equal(7, item.Count);
        }

        [Fact]
        public void LegacyMode_AlwaysReturnsFullShape()
        {
            // Set legacy mode on
            System.Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", "legacy");

            var item = MakeItem("InvoiceProc", "Procedure", verbose: false);

            // Even with verbose=false, legacy mode returns full shape
            Assert.Equal(7, item.Count);
            Assert.NotNull(item["description"]);
            Assert.NotNull(item["parent"]);
            Assert.NotNull(item["module"]);
            Assert.NotNull(item["path"]);
            Assert.NotNull(item["parentPath"]);

            // Cleanup
            System.Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);
        }

        [Fact]
        public void LegacyMode_IgnoresVerboseFlag()
        {
            // Set legacy mode on
            System.Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", "legacy");

            var itemNonVerbose = MakeItem("InvoiceProc", "Procedure", verbose: false);
            var itemVerbose = MakeItem("InvoiceProc", "Procedure", verbose: true);

            // Both should have same shape in legacy mode
            Assert.Equal(itemNonVerbose.Count, itemVerbose.Count);
            Assert.Equal(7, itemNonVerbose.Count);

            // Cleanup
            System.Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);
        }
    }
}
