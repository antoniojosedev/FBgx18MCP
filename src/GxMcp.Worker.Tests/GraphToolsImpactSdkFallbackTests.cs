using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class GraphToolsImpactSdkFallbackTests
    {
        private sealed class TestableGraphToolsService : GraphToolsService
        {
            public List<string> SdkCallersToReturn { get; set; }

            public TestableGraphToolsService(IndexCacheService indexCache, CallerGraphService graph)
                : base(null, null, indexCache, graph)
            {
            }

            internal override List<string> TryGetSdkCallers(string name)
            {
                return SdkCallersToReturn;
            }
        }

        [Fact]
        public void ImpactDirectionUp_UsesSdkFallback_WhenIndexHasNoCallerEdges()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "procTarget", Type = "Procedure", IsEnriched = true, Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "procCaller1", Type = "Procedure", IsEnriched = true, Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "procCaller2", Type = "Procedure", IsEnriched = true, Calls = new List<string>() }
            });

            var graph = new CallerGraphService(index);
            var service = new TestableGraphToolsService(index, graph)
            {
                SdkCallersToReturn = new List<string> { "procCaller1", "procCaller2" }
            };

            var args = new JObject
            {
                ["name"] = "procTarget",
                ["direction"] = "up",
                ["maxDepth"] = 1
            };

            var raw = service.Impact(args);
            var parsed = JObject.Parse(raw);
            var result = parsed["result"] as JObject;

            Assert.NotNull(result);
            Assert.Equal("partial:sdk-fallback", result["fieldTrust"]?.ToString());
            Assert.Contains("augmented from SDK references", result["coverageNote"]?.ToString());

            var affected = result["affected"] as JArray;
            Assert.NotNull(affected);
            Assert.Equal(2, affected.Count);
            Assert.Contains(affected, a => a["name"]?.ToString() == "procCaller1");
            Assert.Contains(affected, a => a["name"]?.ToString() == "procCaller2");
        }

        [Fact]
        public void ImpactDirectionUp_WithCompleteIndexEdges_ReportsComplete()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "procTarget", Type = "Procedure", IsEnriched = true, Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "procCaller", Type = "Procedure", IsEnriched = true, Calls = new List<string> { "procTarget" } }
            });

            var graph = new CallerGraphService(index);
            var service = new TestableGraphToolsService(index, graph)
            {
                SdkCallersToReturn = null
            };

            var args = new JObject
            {
                ["name"] = "procTarget",
                ["direction"] = "up",
                ["maxDepth"] = 1
            };

            var raw = service.Impact(args);
            var parsed = JObject.Parse(raw);
            var result = parsed["result"] as JObject;

            Assert.NotNull(result);
            Assert.Equal("complete", result["fieldTrust"]?.ToString());
            Assert.Null(result["coverageNote"]?.Value<string>());

            var affected = result["affected"] as JArray;
            Assert.NotNull(affected);
            Assert.Single(affected);
            Assert.Equal("procCaller", affected[0]["name"]?.ToString());
        }
    }
}
