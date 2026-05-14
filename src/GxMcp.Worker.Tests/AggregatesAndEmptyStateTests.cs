using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AggregatesAndEmptyStateTests
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
        public void NonEmptyList_IncludesAggregates()
        {
            var items = new JArray
            {
                MakeItem("Proc1", "Procedure"),
                MakeItem("Proc2", "Procedure"),
                MakeItem("TrnA", "Transaction"),
                MakeItem("WebPanelX", "WebPanel")
            };

            var response = ListService.BuildPagedResponseForTest(items, total: 4, offset: 0, pageSize: 10);

            Assert.NotNull(response["_meta"]);
            var aggregates = response["_meta"]?["aggregates"];
            Assert.NotNull(aggregates);
            Assert.Equal(4, aggregates["total"]);

            var byType = aggregates["by_type"];
            Assert.NotNull(byType);
            Assert.Equal(2, byType["Procedure"]);
            Assert.Equal(1, byType["Transaction"]);
            Assert.Equal(1, byType["WebPanel"]);
        }

        [Fact]
        public void NonEmptyList_IncludesSuggestedNext()
        {
            var items = new JArray
            {
                MakeItem("FirstProc", "Procedure"),
                MakeItem("SecondProc", "Procedure")
            };

            var response = ListService.BuildPagedResponseForTest(items, total: 2, offset: 0, pageSize: 10);

            Assert.NotNull(response["_meta"]);
            var suggestion = response["_meta"]?["suggested_next"];
            Assert.NotNull(suggestion);
            Assert.Equal("genexus_read", suggestion["tool"]?.ToString());
            Assert.Equal("FirstProc", suggestion["args"]?["name"]?.ToString());
        }

        [Fact]
        public void EmptyListNoFilter_EmptyReasonIsNoMatches()
        {
            var items = new JArray();

            // total = 0 means KB is loaded but no objects exist
            var response = ListService.BuildPagedResponseForTest(items, total: 0, offset: 0, pageSize: 10);

            Assert.NotNull(response["_meta"]);
            var emptyReason = response["_meta"]?["empty_reason"]?.ToString();
            Assert.Equal("no_matches", emptyReason);
        }

        [Fact]
        public void EmptyListWithFilter_EmptyReasonIsFilteredOut()
        {
            var items = new JArray();

            // total > 0 but results.Count == 0 means a filter was applied and excluded all results
            var response = ListService.BuildPagedResponseForTest(items, total: 100, offset: 0, pageSize: 10);

            Assert.NotNull(response["_meta"]);
            var emptyReason = response["_meta"]?["empty_reason"]?.ToString();
            Assert.Equal("filtered_out", emptyReason);
        }

        [Fact]
        public void SingleTypeInList_ByTypeCountsCorrectly()
        {
            var items = new JArray
            {
                MakeItem("Item1", "Module"),
                MakeItem("Item2", "Module"),
                MakeItem("Item3", "Module")
            };

            var response = ListService.BuildPagedResponseForTest(items, total: 3, offset: 0, pageSize: 10);

            var aggregates = response["_meta"]?["aggregates"];
            Assert.NotNull(aggregates);
            Assert.Equal(3, aggregates["total"]);
            Assert.Equal(3, aggregates["by_type"]["Module"]);
        }

        [Fact]
        public void MultipleTypesInList_ByTypeCountsAllTypes()
        {
            var items = new JArray
            {
                MakeItem("ProcA", "Procedure"),
                MakeItem("ProcB", "Procedure"),
                MakeItem("ProcC", "Procedure"),
                MakeItem("TrnX", "Transaction"),
                MakeItem("TrnY", "Transaction"),
                MakeItem("FolderZ", "Folder")
            };

            var response = ListService.BuildPagedResponseForTest(items, total: 6, offset: 0, pageSize: 10);

            var aggregates = response["_meta"]?["aggregates"];
            Assert.NotNull(aggregates);
            var byType = aggregates["by_type"];
            Assert.Equal(3, byType["Procedure"]);
            Assert.Equal(2, byType["Transaction"]);
            Assert.Equal(1, byType["Folder"]);
        }

        [Fact]
        public void NonEmptyList_NoEmptyReason()
        {
            var items = new JArray
            {
                MakeItem("Something", "Procedure")
            };

            var response = ListService.BuildPagedResponseForTest(items, total: 1, offset: 0, pageSize: 10);

            var emptyReason = response["_meta"]?["empty_reason"];
            Assert.Null(emptyReason);
        }

        [Fact]
        public void EmptyList_NoAggregates()
        {
            var items = new JArray();

            var response = ListService.BuildPagedResponseForTest(items, total: 0, offset: 0, pageSize: 10);

            var aggregates = response["_meta"]?["aggregates"];
            Assert.Null(aggregates);
        }
    }
}
