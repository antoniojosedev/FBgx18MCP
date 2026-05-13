using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class LifecycleStatusPaginationTests
    {
        private static List<string> MakeWarnings(int count)
        {
            var list = new List<string>(count);
            for (int i = 1; i <= count; i++)
                list.Add("warning CS" + i.ToString("D4") + ": fake warning " + i);
            return list;
        }

        [Fact]
        public void Page1_Of200_Size50_Returns50Items_HasMore()
        {
            var warnings = MakeWarnings(200);
            var result = BatchService.BuildStatusPayload(warnings, page: 1, pageSize: 50);

            var arr = (JArray)result["warnings"];
            var meta = (JObject)result["_meta"]["pagination"];

            Assert.Equal(50, arr.Count);
            Assert.Equal(200, meta["total"].ToObject<int>());
            Assert.Equal(1, meta["page"].ToObject<int>());
            Assert.Equal(50, meta["page_size"].ToObject<int>());
            Assert.True(meta["has_more"].ToObject<bool>());
        }

        [Fact]
        public void Page4_Of200_Size50_Returns50Items_NoMore()
        {
            var warnings = MakeWarnings(200);
            var result = BatchService.BuildStatusPayload(warnings, page: 4, pageSize: 50);

            var arr = (JArray)result["warnings"];
            var meta = (JObject)result["_meta"]["pagination"];

            Assert.Equal(50, arr.Count);
            Assert.Equal(200, meta["total"].ToObject<int>());
            Assert.Equal(4, meta["page"].ToObject<int>());
            Assert.False(meta["has_more"].ToObject<bool>());
        }

        [Fact]
        public void Page10_Of200_Size50_ReturnsEmpty_NoMore()
        {
            var warnings = MakeWarnings(200);
            var result = BatchService.BuildStatusPayload(warnings, page: 10, pageSize: 50);

            var arr = (JArray)result["warnings"];
            var meta = (JObject)result["_meta"]["pagination"];

            Assert.Equal(0, arr.Count);
            Assert.Equal(200, meta["total"].ToObject<int>());
            Assert.False(meta["has_more"].ToObject<bool>());
        }

        [Fact]
        public void PageSize500_ClampsTo200()
        {
            var warnings = MakeWarnings(200);
            var result = BatchService.BuildStatusPayload(warnings, page: 1, pageSize: 500);

            var meta = (JObject)result["_meta"]["pagination"];
            Assert.Equal(200, meta["page_size"].ToObject<int>());
            // all 200 items on page 1 with size 200
            var arr = (JArray)result["warnings"];
            Assert.Equal(200, arr.Count);
        }

        [Fact]
        public void Page0_ClampsTo1()
        {
            var warnings = MakeWarnings(200);
            var result = BatchService.BuildStatusPayload(warnings, page: 0, pageSize: 50);

            var meta = (JObject)result["_meta"]["pagination"];
            Assert.Equal(1, meta["page"].ToObject<int>());
            // page 1 with size 50 → 50 items
            var arr = (JArray)result["warnings"];
            Assert.Equal(50, arr.Count);
        }
    }
}
