using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // `parent:<name>` (genexus_query) and `parent=<name>` (genexus_list_objects) take a bare
    // container name, but ChildrenByParent is keyed by the full parent path. The lookup used
    // the name as the key, so only a container whose path equals its name matched and a
    // nested Folder (Root Module/NestA1/NestB1) returned no children.
    public class ParentNameFilterTests
    {
        private static readonly IndexCacheService Nested = TestFixtures.IndexWithNestedFolders().Index;

        [Theory]
        [InlineData("parent:NestB1", new[] { "NestWp1" })]
        [InlineData("parent:\"NestA1\"", new[] { "NestB1" })]
        [InlineData("parent:nestb1", new[] { "NestWp1" })]
        [InlineData("parent:\"Root Module\"", new[] { "NestA1", "OtherProc" })]
        [InlineData("parent:NoSuchFolder", new string[0])]
        public void Query_ParentFilter_ReturnsDirectChildren(string query, string[] expected)
        {
            var names = TestFixtures.ResultNames(new SearchService(Nested).Search(query, limit: 50));
            Assert.Equal(expected, names.OrderBy(n => n));
        }

        [Fact]
        public void ListObjects_ParentFilter_ReturnsChildrenOfNestedFolder()
        {
            var json = new ListService(Nested).ListObjects(filter: null, limit: 50, offset: 0, parentFilter: "NestB1");
            Assert.Equal(new[] { "NestWp1" }, TestFixtures.ResultNames(json));
        }

        [Fact]
        public void FindChildrenByParentName_MergesSameNamedFoldersUnderDifferentPaths()
        {
            var svc = TestFixtures.IndexOfPlacedEntries(
                "Procedure:P1@Root Module/Sales/Procs",
                "Procedure:P2@Root Module/HR/Procs",
                "Procedure:P3@Root Module/MyProcs").Index;

            var names = svc.TryGetLoadedIndex().FindChildrenByParentName("Procs").Select(e => e.Name).OrderBy(n => n);
            Assert.Equal(new[] { "P1", "P2" }, names);
        }
    }
}
