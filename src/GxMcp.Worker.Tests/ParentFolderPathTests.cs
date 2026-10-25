using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #650: <c>genexus_list_objects pathPrefix="Root Module/FocusOn/"</c> returned 1 of
    /// 6 objects. The SDK walk reports the KB root as a real Module named "Root Module", so an
    /// object's <c>ParentPath</c> already starts with "Root Module/". The lite walk and the
    /// runtime-SDK list built <c>ParentFolderPath</c> as "Root Module/" + ParentPath, which
    /// doubled the prefix; only entries rebuilt by <c>UpdateEntry</c> (tolerant compose) kept
    /// the right value, so the filter matched just those.
    /// </summary>
    public class ParentFolderPathTests
    {
        [Theory]
        [InlineData("Root Module/FocusOn", "Root Module/FocusOn")]
        [InlineData("Root Module", "Root Module")]
        [InlineData("FocusOn", "Root Module/FocusOn")]
        [InlineData("", "Root Module")]
        [InlineData(null, "Root Module")]
        public void ComposeParentFolderPath_NeverDoublesTheRootModulePrefix(string parentPath, string expected)
        {
            Assert.Equal(expected, IndexCacheService.ComposeParentFolderPath(parentPath));
        }

        [Theory]
        // The lite-walk shape that #650 produced for a nested module, a root-level object and a legacy un-prefixed path.
        [InlineData("Root Module/FocusOn", "Root Module/Root Module/FocusOn", "Root Module/FocusOn")]
        [InlineData("Root Module", "Root Module/Root Module", "Root Module")]
        [InlineData("Root Module/Folder/Sub", "Root Module/Root Module/Folder/Sub", "Root Module/Folder/Sub")]
        // Already canonical, blank, or genuinely nested "Root Module" modules are left as the walk reported them.
        [InlineData("Root Module/FocusOn", "Root Module/FocusOn", "Root Module/FocusOn")]
        [InlineData("Root Module/FocusOn", null, "Root Module/FocusOn")]
        [InlineData("FocusOn", "", "Root Module/FocusOn")]
        [InlineData("Root Module/Root Module/X", "Root Module/Root Module/X", "Root Module/Root Module/X")]
        public void CanonicalParentFolderPath_RepairsOnlyTheDoubledPrefixSignature(string parentPath, string stored, string expected)
        {
            var entry = new SearchIndex.IndexEntry { ParentPath = parentPath, ParentFolderPath = stored };

            Assert.Equal(expected, IndexCacheService.CanonicalParentFolderPath(entry));
        }

        [Fact]
        public void CanonicalParentFolderPath_KeepsAFolderPathWithoutAParentPath()
        {
            var entry = new SearchIndex.IndexEntry { ParentFolderPath = "Root Module/Sales" };

            Assert.Equal("Root Module/Sales", IndexCacheService.CanonicalParentFolderPath(entry));
        }

        [Fact]
        public void NormalizeParentFolderPaths_RepairsPersistedLiteEntries()
        {
            var index = new SearchIndex();
            index.Objects["Procedure:Persisted"] = new SearchIndex.IndexEntry
            {
                Name = "Persisted", Type = "Procedure",
                ParentPath = "Root Module/FocusOn",
                ParentFolderPath = "Root Module/Root Module/FocusOn",
                Path = "Root Module/FocusOn/Persisted"
            };

            IndexCacheService.NormalizeParentFolderPaths(index);

            Assert.Equal("Root Module/FocusOn", index.Objects["Procedure:Persisted"].ParentFolderPath);
        }

        private static IndexCacheService IndexWithFocusOnModule()
        {
            string[] names = { "TareasProgramadas", "TareaEjecucion", "TareaDependencia", "TareaSchedulerControl", "TareaEjecucionLog", "ReservarTareaSchedulerGX" };
            var entries = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < names.Length; i++)
            {
                bool enriched = i == 0; // UpdateEntry composes tolerantly; the lite walk did not.
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = names[i],
                    Type = i == 1 ? "Transaction" : "Procedure",
                    Parent = "FocusOn",
                    ParentPath = "Root Module/FocusOn",
                    ParentFolderPath = enriched ? "Root Module/FocusOn" : "Root Module/Root Module/FocusOn",
                    Path = "Root Module/FocusOn/" + names[i],
                    Module = "FocusOn",
                    IsEnriched = enriched
                });
            }
            entries.Add(new SearchIndex.IndexEntry
            {
                Name = "TareaEjecutar", Type = "Procedure", Parent = "Root Module",
                ParentPath = "Root Module", ParentFolderPath = "Root Module/Root Module",
                Path = "Root Module/TareaEjecutar", Module = "Root Module"
            });
            var index = new IndexCacheService();
            index.LoadFromEntries(entries);
            return index;
        }

        [Theory]
        [InlineData("Root Module/FocusOn/")]
        [InlineData("Root Module/FocusOn")]
        public void PathPrefix_ReturnsEveryObjectOfTheModule_NotOnlyTheEnrichedOnes(string prefix)
        {
            ListService.InvalidateCache();
            var svc = new ListService(IndexWithFocusOnModule());

            var json = JObject.Parse(svc.List(new ListCriteria { PathPrefix = prefix, Limit = 200 }));

            var names = ((JArray)json["results"]).Select(r => (string)r["name"]).OrderBy(n => n).ToArray();
            Assert.Equal(
                new[] { "ReservarTareaSchedulerGX", "TareaDependencia", "TareaEjecucion", "TareaEjecucionLog", "TareaSchedulerControl", "TareasProgramadas" },
                names);
            Assert.Equal(6, json["total"].ToObject<int>());
        }

        [Fact]
        public void PathPrefix_OfTheRootModule_ReturnsTheRootLevelObject()
        {
            ListService.InvalidateCache();
            var svc = new ListService(IndexWithFocusOnModule());

            var json = JObject.Parse(svc.List(new ListCriteria { PathPrefix = "Root Module", Limit = 200 }));

            Assert.Equal(7, json["total"].ToObject<int>());
        }

        [Fact]
        public void ListedParentFolderPath_IsTheCanonicalValue()
        {
            ListService.InvalidateCache();
            var svc = new ListService(IndexWithFocusOnModule());

            var json = JObject.Parse(svc.List(new ListCriteria { PathPrefix = "Root Module/FocusOn/", Limit = 200, Verbose = true }));

            Assert.All((JArray)json["results"], r => Assert.Equal("Root Module/FocusOn", (string)r["parentFolderPath"]));
        }

        /// <summary>
        /// The lite walk (KbService) and the runtime-SDK list (ListService) cannot run without a
        /// KB, so they are pinned to the shared compose helper by source shape: neither may build
        /// "Root Module/" + a hierarchy ParentPath by hand again.
        /// </summary>
        [Theory]
        [InlineData("KbService.cs")]
        [InlineData("ListService.cs")]
        public void FolderPathProducers_UseTheSharedComposeHelper(string file)
        {
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", file));

            Assert.Equal(0, SourceAssert.Count(source, "\"Root Module/\" +"));
            Assert.True(SourceAssert.Count(source, "IndexCacheService.ComposeParentFolderPath(") >= 1);
        }
    }
}
