using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // genexus_create object_atomic with parentPath="Root Module/<module>/<folder>" created the object in the
    // Root Module, and genexus_properties action=move said the same path was fine in dryRun and then failed.
    // The destination text is now resolved to one Folder/Module by a single resolver both operations call.
    public class PlacementResolverTests
    {
        private static readonly Guid StockModule = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid StockReturns = new Guid("22222222-2222-2222-2222-222222222222");
        private static readonly Guid Ops = new Guid("33333333-3333-3333-3333-333333333333");
        private static readonly Guid OpsReturns = new Guid("44444444-4444-4444-4444-444444444444");
        private static readonly Guid Reports = new Guid("55555555-5555-5555-5555-555555555555");
        private static readonly Guid Archive = new Guid("66666666-6666-6666-6666-666666666666");
        private static readonly Guid SharedFolder = new Guid("77777777-7777-7777-7777-777777777777");
        private static readonly Guid SharedModule = new Guid("88888888-8888-8888-8888-888888888888");

        private static PlacementCatalog Tree() => new PlacementCatalog(new[]
        {
            new PlacementContainer(StockModule, "Stock", "Module", null),
            new PlacementContainer(StockReturns, "Returns", "Folder", StockModule),
            new PlacementContainer(Ops, "ops", "Module", null),
            new PlacementContainer(OpsReturns, "Returns", "Folder", Ops),
            new PlacementContainer(Reports, "Reports", "Folder", null),
            new PlacementContainer(Archive, "Archive", "Folder", Reports),
            new PlacementContainer(SharedFolder, "Shared", "Folder", null),
            new PlacementContainer(SharedModule, "Shared", "Module", null)
        });

        [Fact]
        public void Resolve_ModuleAndNestedFolderPath_ReturnsThatFolder()
        {
            var r = PlacementResolver.Resolve("Root Module/Stock/Returns", "Folder", Tree());

            Assert.Equal(PlacementStatus.Resolved, r.Status);
            Assert.Equal(StockReturns, r.Container.Guid);
            Assert.Equal("Root Module/Stock/Returns", r.ResolvedPath);
        }

        [Theory]
        [InlineData("Stock/Returns")]
        [InlineData("Root Module/Stock/Returns")]
        [InlineData("root module\\STOCK\\returns")]
        [InlineData("  Root Module / Stock / Returns / ")]
        [InlineData("Stock.Returns")]
        public void Resolve_EverySpellingOfTheSamePath_ReachesTheSameFolder(string text)
        {
            var r = PlacementResolver.Resolve(text, null, Tree());

            Assert.True(r.Ok);
            Assert.Equal(StockReturns, r.Container.Guid);
        }

        [Fact]
        public void Resolve_BareNameOfOneContainer_Resolves()
        {
            var r = PlacementResolver.Resolve("Archive", null, Tree());

            Assert.Equal(PlacementStatus.Resolved, r.Status);
            Assert.Equal(Archive, r.Container.Guid);
            Assert.Equal("Root Module/Reports/Archive", r.Container.Path);
        }

        [Fact]
        public void Resolve_BareNameOfTwoContainers_IsAmbiguousAndListsThem()
        {
            var r = PlacementResolver.Resolve("Returns", "Folder", Tree());

            Assert.Equal(PlacementStatus.Ambiguous, r.Status);
            Assert.Equal("DestinationAmbiguous", r.Code);
            Assert.Equal(
                new[] { "Root Module/ops/Returns", "Root Module/Stock/Returns" },
                r.Candidates.Select(c => c.Path).ToArray());
            Assert.Contains("Root Module/Stock/Returns", r.Message);
            var diagnostic = r.ToDiagnostic();
            Assert.Equal(2, ((Newtonsoft.Json.Linq.JArray)diagnostic["candidates"]).Count);
        }

        [Fact]
        public void Resolve_PathThatDoesNotExist_NamesTheFailedSegmentAndWhatIsThere()
        {
            var r = PlacementResolver.Resolve("Root Module/Stock/Inexistente", "Folder", Tree());

            Assert.Equal(PlacementStatus.NotFound, r.Status);
            Assert.Equal("DestinationNotFound", r.Code);
            Assert.Equal("Inexistente", r.FailedSegment);
            Assert.Equal("Root Module/Stock", r.ResolvedPrefix);
            Assert.Equal(new[] { "Returns" }, r.Available.ToArray());
            Assert.Contains("'Inexistente' is not a Folder or Module under 'Root Module/Stock'", r.Message);
        }

        [Fact]
        public void Resolve_MissingIntermediateContainer_FailsAtThatSegment()
        {
            var r = PlacementResolver.Resolve("Root Module/nada/Returns", null, Tree());

            Assert.Equal(PlacementStatus.NotFound, r.Status);
            Assert.Equal("nada", r.FailedSegment);
            Assert.Equal("Root Module", r.ResolvedPrefix);
        }

        [Fact]
        public void Resolve_FolderCannotBeReachedUnderTheWrongParent()
        {
            // "Archive" lives under Reports, not directly under the Root Module.
            var r = PlacementResolver.Resolve("Root Module/Archive", null, Tree());

            Assert.Equal(PlacementStatus.NotFound, r.Status);
            Assert.Equal("Archive", r.FailedSegment);
        }

        [Fact]
        public void Resolve_ModuleAskedAsFolder_IsAKindMismatch()
        {
            var r = PlacementResolver.Resolve("Root Module/Stock", "Folder", Tree());

            Assert.Equal(PlacementStatus.KindMismatch, r.Status);
            Assert.Equal("DestinationKindMismatch", r.Code);
            Assert.Equal("Module", r.ResolvedKind);
            Assert.Contains("is a Module, not a Folder", r.Message);
        }

        [Fact]
        public void Resolve_KindPicksBetweenAFolderAndAModuleOfTheSameName()
        {
            Assert.Equal(PlacementStatus.Ambiguous, PlacementResolver.Resolve("Shared", null, Tree()).Status);
            Assert.Equal(SharedModule, PlacementResolver.Resolve("Shared", "Module", Tree()).Container.Guid);
            Assert.Equal(SharedFolder, PlacementResolver.Resolve("Root Module/Shared", "folder", Tree()).Container.Guid);
        }

        [Fact]
        public void Resolve_Guid_ResolvesTheContainerByIdentity()
        {
            var r = PlacementResolver.Resolve(OpsReturns.ToString(), null, Tree());

            Assert.True(r.Ok);
            Assert.Equal("Root Module/ops/Returns", r.Container.Path);
            Assert.Equal(PlacementStatus.NotFound, PlacementResolver.Resolve(Guid.NewGuid().ToString(), null, Tree()).Status);
        }

        [Fact]
        public void Resolve_RootModuleOnItsOwn_IsTheRoot_AndNotAFolderOrModule()
        {
            var root = PlacementResolver.Resolve("Root Module", null, Tree());
            Assert.Equal(PlacementStatus.Root, root.Status);
            Assert.True(root.Ok);
            Assert.Equal("Root Module", root.ResolvedPath);

            Assert.Equal(PlacementStatus.Invalid, PlacementResolver.Resolve("Root Module", "Folder", Tree()).Status);
        }

        [Theory]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("Stock", "Procedure")]
        public void Resolve_UnusableInput_IsInvalid(string text, string kind)
        {
            var r = PlacementResolver.Resolve(text, kind, Tree());

            Assert.Equal(PlacementStatus.Invalid, r.Status);
            Assert.Equal("DestinationInvalid", r.Code);
        }

        [Fact]
        public void Catalog_ComposesNestedPaths_AndFindsDescendants()
        {
            var catalog = Tree();

            Assert.Equal("Root Module/Reports/Archive", catalog.Get(Archive).Path);
            Assert.True(catalog.IsSelfOrDescendant(Archive, Reports));
            Assert.True(catalog.IsSelfOrDescendant(Reports, Reports));
            Assert.False(catalog.IsSelfOrDescendant(Reports, Archive));
            Assert.False(catalog.IsSelfOrDescendant(StockReturns, Reports));
        }

        [Fact]
        public void Catalog_ParentOutsideTheCatalog_IsTreatedAsTheRoot()
        {
            var catalog = new PlacementCatalog(new[] { new PlacementContainer(Archive, "Archive", "Folder", Guid.NewGuid()) });

            Assert.Equal("Root Module/Archive", catalog.Get(Archive).Path);
            Assert.Null(catalog.Get(Archive).ParentGuid);
        }

        [Fact]
        public void Catalog_SelfCycle_NormalizesParentToNull()
        {
            var catalog = new PlacementCatalog(new[]
            {
                new PlacementContainer(StockModule, "Stock", "Folder", StockModule)
            });

            Assert.Equal("Root Module/Stock", catalog.Get(StockModule).Path);
            Assert.Null(catalog.Get(StockModule).ParentGuid);
            Assert.Contains(catalog.Get(StockModule), catalog.ChildrenOf(null));
        }

        [Fact]
        public void Catalog_ParentCycle_DoesNotLoop()
        {
            var catalog = new PlacementCatalog(new[]
            {
                new PlacementContainer(StockModule, "a", "Folder", Ops),
                new PlacementContainer(Ops, "b", "Folder", StockModule)
            });

            Assert.NotNull(catalog.Get(StockModule).Path);
            Assert.True(catalog.IsSelfOrDescendant(StockModule, Ops));
        }

        [Fact]
        public void Describe_ReportsTheRequestedTextAndTheResolvedContainer()
        {
            var r = PlacementResolver.Resolve("Stock/Returns", "Folder", Tree());
            var json = r.Describe();

            Assert.Equal("Stock/Returns", (string)json["requested"]);
            Assert.Equal("Folder", (string)json["kind"]);
            Assert.Equal(StockReturns.ToString(), (string)json["resolved"]["guid"]);
            Assert.Equal("Root Module/Stock/Returns", (string)json["resolved"]["path"]);
        }
    }
}
