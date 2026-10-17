using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GraphToolsServiceTests
    {
        private static IndexCacheService BuildIndex(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(new SearchIndex.IndexEntry[0]);
            var idx = svc.TryGetLoadedIndex();
            foreach (var e in entries)
            {
                string key = !string.IsNullOrEmpty(e.Guid) ? e.Guid : (e.Type + ":" + e.Name + ":" + e.Path);
                idx.Objects[key] = e;
            }
            return svc;
        }

        [Fact]
        public void AmbiguousName_WhenCandidatesHaveDistinctTypes_SuggestsTypeInMessageAndHint()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Customer",
                    Type = "Transaction",
                    Guid = "00000000-0000-0000-0000-000000000001",
                    Path = "Root Module/Customer"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "Customer",
                    Type = "WebPanel",
                    Guid = "00000000-0000-0000-0000-000000000002",
                    Path = "Root Module/UI/Customer"
                }
            };

            var indexCache = BuildIndex(entries);
            var graph = new CallerGraphService(indexCache);
            var graphTools = new GraphToolsService(null, null, indexCache, graph);

            string impactResponse = graphTools.Impact(new JObject { ["name"] = "Customer" });
            var json = JObject.Parse(impactResponse);

            Assert.Equal("error", json["status"]?.ToString());
            var error = json["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("AmbiguousName", error["code"]?.ToString());
            Assert.Equal("'Customer' matches 2 objects; select one with guid, path or type.", error["message"]?.ToString());
            string hint = error["hint"]?.ToString();
            Assert.NotNull(hint);
            Assert.Contains("Pass type=", hint);
            Assert.Contains("guid=", hint);
            Assert.Contains("path=", hint);
            Assert.Contains("to disambiguate", hint);

            var candidates = json["candidates"] as JArray;
            Assert.NotNull(candidates);
            Assert.Equal(2, candidates.Count);

            // Also test ObjectContext
            string contextResponse = graphTools.ObjectContext(new JObject { ["name"] = "Customer" });
            var ctxJson = JObject.Parse(contextResponse);
            Assert.Equal("error", ctxJson["status"]?.ToString());
            Assert.Equal("AmbiguousName", ctxJson["error"]?["code"]?.ToString());
            Assert.Equal("'Customer' matches 2 objects; select one with guid, path or type.", ctxJson["error"]?["message"]?.ToString());
        }

        [Fact]
        public void AmbiguousName_WhenAllCandidatesHaveSameType_KeepsGuidOrPathMessageWithoutType()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry
                {
                    Name = "InvoiceReport",
                    Type = "Procedure",
                    Guid = "00000000-0000-0000-0000-000000000011",
                    Path = "Root Module/Reports/InvoiceReport"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "InvoiceReport",
                    Type = "Procedure",
                    Guid = "00000000-0000-0000-0000-000000000012",
                    Path = "Root Module/Billing/InvoiceReport"
                }
            };

            var indexCache = BuildIndex(entries);
            var graph = new CallerGraphService(indexCache);
            var graphTools = new GraphToolsService(null, null, indexCache, graph);

            string impactResponse = graphTools.Impact(new JObject { ["name"] = "InvoiceReport" });
            var json = JObject.Parse(impactResponse);

            Assert.Equal("error", json["status"]?.ToString());
            var error = json["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("AmbiguousName", error["code"]?.ToString());
            Assert.Equal("'InvoiceReport' matches 2 objects; select one with guid or path.", error["message"]?.ToString());
            Assert.DoesNotContain("type", error["message"]?.ToString());
            Assert.True(string.IsNullOrEmpty(error["hint"]?.ToString()));
        }

        private static SearchIndex.IndexEntry[] TwoProceduresAndOne(string thirdType)
            => new[]
            {
                new SearchIndex.IndexEntry
                {
                    Name = "InvoiceReport",
                    Type = "Procedure",
                    Guid = "00000000-0000-0000-0000-000000000021",
                    Path = "Root Module/Reports/InvoiceReport"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "InvoiceReport",
                    Type = "Procedure",
                    Guid = "00000000-0000-0000-0000-000000000022",
                    Path = "Root Module/Billing/InvoiceReport"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "InvoiceReport",
                    Type = thirdType,
                    Guid = "00000000-0000-0000-0000-000000000023",
                    Path = "Root Module/InvoiceReport"
                }
            };

        [Fact]
        public void AmbiguousName_WhenOnlyOneTypeIsUnique_SuggestsThatTypeAndNotTheDuplicatedOne()
        {
            var indexCache = BuildIndex(TwoProceduresAndOne("Procedure"));
            // Objects is a ConcurrentDictionary, so candidate order follows key hashing: make the last
            // enumerated candidate the unique-typed one so a positional pick (candidates[0]) cannot pass.
            var ordered = indexCache.TryGetLoadedIndex().FindByName("InvoiceReport");
            var unique = ordered[ordered.Count - 1];
            unique.Type = "Transaction";
            var graphTools = new GraphToolsService(null, null, indexCache, new CallerGraphService(indexCache));

            foreach (string response in new[]
            {
                graphTools.Impact(new JObject { ["name"] = "InvoiceReport" }),
                graphTools.ObjectContext(new JObject { ["name"] = "InvoiceReport" })
            })
            {
                var json = JObject.Parse(response);
                var error = json["error"] as JObject;
                Assert.NotNull(error);
                Assert.Equal("AmbiguousName", error["code"]?.ToString());
                Assert.Equal("'InvoiceReport' matches 3 objects; select one with guid, path or type.", error["message"]?.ToString());
                string hint = error["hint"]?.ToString();
                Assert.NotNull(hint);
                Assert.Contains("type=Transaction", hint);
                Assert.Contains("guid=" + unique.Guid, hint);
                Assert.DoesNotContain("type=Procedure", hint);
                Assert.Equal(3, (json["candidates"] as JArray)?.Count);
            }
        }

        [Fact]
        public void AmbiguousName_WhenTypeProvidedAndStillAmbiguous_DoesNotSuggestTypeAgain()
        {
            var indexCache = BuildIndex(TwoProceduresAndOne("Transaction"));
            var graphTools = new GraphToolsService(null, null, indexCache, new CallerGraphService(indexCache));

            string response = graphTools.Impact(new JObject { ["name"] = "InvoiceReport", ["type"] = "Procedure" });
            var json = JObject.Parse(response);

            var error = json["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("AmbiguousName", error["code"]?.ToString());
            Assert.Equal("'InvoiceReport' matches 2 objects; select one with guid or path.", error["message"]?.ToString());
            Assert.True(string.IsNullOrEmpty(error["hint"]?.ToString()));

            var candidates = json["candidates"] as JArray;
            Assert.NotNull(candidates);
            Assert.Equal(2, candidates.Count);
            Assert.All(candidates, c => Assert.Equal("Procedure", c["type"]?.ToString()));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AmbiguousName_WhenOnlyUniqueTypeIsEmpty_KeepsGuidOrPathMessageWithoutTypeHint(string emptyType)
        {
            var indexCache = BuildIndex(TwoProceduresAndOne(emptyType));
            var graphTools = new GraphToolsService(null, null, indexCache, new CallerGraphService(indexCache));

            string response = graphTools.Impact(new JObject { ["name"] = "InvoiceReport" });
            var json = JObject.Parse(response);

            var error = json["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("AmbiguousName", error["code"]?.ToString());
            Assert.Equal("'InvoiceReport' matches 3 objects; select one with guid or path.", error["message"]?.ToString());
            Assert.True(string.IsNullOrEmpty(error["hint"]?.ToString()));
            Assert.Equal(3, (json["candidates"] as JArray)?.Count);
        }

        [Fact]
        public void Resolve_WithTypeProvided_DisambiguatesAmbiguousCandidate()
        {
            var entries = new[]
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Customer",
                    Type = "Transaction",
                    Guid = "00000000-0000-0000-0000-000000000001",
                    Path = "Root Module/Customer"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "Customer",
                    Type = "WebPanel",
                    Guid = "00000000-0000-0000-0000-000000000002",
                    Path = "Root Module/UI/Customer"
                }
            };

            var indexCache = BuildIndex(entries);
            var graph = new CallerGraphService(indexCache);
            var graphTools = new GraphToolsService(null, null, indexCache, graph);

            // Passing type="WebPanel" should resolve unambiguously and not return AmbiguousName error
            string response = graphTools.Impact(new JObject { ["name"] = "Customer", ["type"] = "WebPanel" });
            var json = JObject.Parse(response);

            // It resolves and runs impact (status "ok")
            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }
    }
}
