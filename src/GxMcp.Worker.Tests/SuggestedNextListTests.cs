using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SuggestedNextListTests
    {
        private static JObject MakeItem(string name, string type)
        {
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["description"] = "",
                ["parent"] = "",
                ["module"] = "",
                ["path"] = name,
                ["parentPath"] = ""
            };
        }

        [Fact]
        public void ListWithResults_SuggestsReadOnTopMatch()
        {
            var items = new JArray
            {
                MakeItem("InvoiceProc", "Procedure"),
                MakeItem("CustomerTrn", "Transaction"),
                MakeItem("ReportPanel", "WebPanel")
            };

            var suggestion = ListService.BuildSuggestedNext(items);

            Assert.NotNull(suggestion);
            Assert.Equal("genexus_read", suggestion["tool"]?.ToString());
            Assert.Equal("InvoiceProc", suggestion["args"]?["name"]?.ToString());
            Assert.Equal("Procedure", suggestion["args"]?["type"]?.ToString());
        }

        [Fact]
        public void EmptyList_OmitsSuggestion()
        {
            var items = new JArray();

            var suggestion = ListService.BuildSuggestedNext(items);

            Assert.Null(suggestion);
        }

        [Fact]
        public void NullItems_OmitsSuggestion()
        {
            var suggestion = ListService.BuildSuggestedNext(null);

            Assert.Null(suggestion);
        }

        [Fact]
        public void SingleItem_SuggestsItself()
        {
            var items = new JArray
            {
                MakeItem("MyWebPanel", "WebPanel")
            };

            var suggestion = ListService.BuildSuggestedNext(items);

            Assert.NotNull(suggestion);
            Assert.Equal("genexus_read", suggestion["tool"]?.ToString());
            Assert.Equal("MyWebPanel", suggestion["args"]?["name"]?.ToString());
            Assert.Equal("WebPanel", suggestion["args"]?["type"]?.ToString());
        }
    }
}
