using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A refused destination or move tells the caller which step refused it (reasonCode, phase), what it
    // was asked to reach (placement) and that nothing ran implicitly (implicitOperations: []).
    public class PlacementEnvelopeTests
    {
        private static readonly Guid StockModule = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid ReturnsFolder = new Guid("22222222-2222-2222-2222-222222222222");

        private static PlacementCatalog Tree() => new PlacementCatalog(new[]
        {
            new PlacementContainer(StockModule, "Stock", "Module", null),
            new PlacementContainer(ReturnsFolder, "Returns", "Folder", StockModule)
        });

        private static JObject Error(string json)
        {
            var parsed = JObject.Parse(json);
            Assert.Equal("error", (string)parsed["status"]);
            return parsed;
        }

        [Fact]
        public void PlacementRefused_NotFound_NamesTheFailedSegmentAndKeepsTheStatelessGuarantees()
        {
            var resolution = PlacementResolver.Resolve("Root Module/Stock/Inexistente", "Folder", Tree());

            var error = Error(ObjectService.PlacementRefused(resolution, "ProcTeste", "destination-resolution"))["error"];

            Assert.Equal("DestinationNotFound", (string)error["code"]);
            Assert.Equal("DestinationNotFound", (string)error["reasonCode"]);
            Assert.Equal("destination-resolution", (string)error["phase"]);
            Assert.Equal("Inexistente", (string)error["placement"]["failedSegment"]);
            Assert.Equal("Root Module/Stock", (string)error["placement"]["resolvedPrefix"]);
            Assert.False((bool)error["persisted"]);
            Assert.Empty((JArray)error["implicitOperations"]);
        }

        [Fact]
        public void PlacementRefused_Ambiguous_ListsTheCandidatesWithTheirGuids()
        {
            var tree = new PlacementCatalog(new[]
            {
                new PlacementContainer(StockModule, "Returns", "Folder", null),
                new PlacementContainer(ReturnsFolder, "Returns", "Folder", null)
            });
            var resolution = PlacementResolver.Resolve("Returns", null, tree);

            var error = Error(ObjectService.PlacementRefused(resolution, "P", "destination-resolution"))["error"];

            Assert.Equal("DestinationAmbiguous", (string)error["code"]);
            var candidates = (JArray)error["placement"]["candidates"];
            Assert.Equal(2, candidates.Count);
            Assert.Equal(new[] { StockModule.ToString(), ReturnsFolder.ToString() }.OrderBy(x => x),
                candidates.Select(c => (string)c["guid"]).OrderBy(x => x));
        }

        private static MoveFacts Facts(bool dryRun, string baseVersion, string currentVersion)
            => new MoveFacts
            {
                DryRun = dryRun,
                BaseVersion = baseVersion,
                CurrentVersion = currentVersion,
                ObjectName = "ProcTeste",
                Destination = PlacementResolver.Resolve("Root Module/Stock/Returns", "Folder", Tree())
            };

        [Fact]
        public void MoveRefused_BaseVersionRequired_HandsOutTheCurrentToken()
        {
            var facts = Facts(false, null, "638000:abc");
            var verdict = MovePreflight.Evaluate(facts);

            var json = Error(ObjectService.MoveRefused(verdict, "ProcTeste", facts, facts.Destination, null));

            Assert.Equal("BaseVersionRequired", (string)json["error"]["code"]);
            Assert.Equal("preflight", (string)json["error"]["phase"]);
            Assert.Equal("638000:abc", (string)json["error"]["currentVersion"]);
            Assert.Equal("BaseVersionRequired", (string)json["error"]["blockers"][0]["code"]);
            Assert.Empty((JArray)json["error"]["implicitOperations"]);
        }

        [Fact]
        public void MoveRefused_VersionConflict_KeepsTheTopLevelFieldsThePreviousContractHad()
        {
            var facts = Facts(false, "old", "new");
            var verdict = MovePreflight.Evaluate(facts);

            var json = Error(ObjectService.MoveRefused(verdict, "ProcTeste", facts, facts.Destination, null));

            Assert.Equal("VersionConflict", (string)json["error"]["code"]);
            Assert.Equal("old", (string)json["baseVersion"]);
            Assert.Equal("new", (string)json["currentVersion"]);
            Assert.Equal("old", (string)json["error"]["expectedVersion"]);
            Assert.Equal("new", (string)json["error"]["currentVersion"]);
        }

        [Fact]
        public void MoveRefused_PreviewAndWrite_ProduceTheSameErrorShapeAndCode()
        {
            Func<bool, JObject> refuse = dryRun =>
            {
                var facts = Facts(dryRun, "old", "new");
                facts.NameConflict = new MoveNameConflict { Guid = ReturnsFolder, Path = "Root Module/Stock/Returns/ProcTeste", QualifiedName = "Stock.ProcTeste" };
                var verdict = MovePreflight.Evaluate(facts);
                return Error(ObjectService.MoveRefused(verdict, "ProcTeste", facts, facts.Destination, null));
            };

            var preview = refuse(true);
            var write = refuse(false);

            Assert.Equal((string)write["error"]["code"], (string)preview["error"]["code"]);
            Assert.Equal(write["error"]["blockers"].ToString(), preview["error"]["blockers"].ToString());
            Assert.Equal(
                new[] { "VersionConflict", "DestinationNameConflict" },
                ((JArray)write["error"]["blockers"]).Select(b => (string)b["code"]).ToArray());
        }

        [Fact]
        public void MoveRefused_StrategyUnavailable_CarriesTheSdkDetail()
        {
            var facts = Facts(false, "v", "v");
            facts.PersistStrategyAvailable = false;
            var verdict = MovePreflight.Evaluate(facts);

            var json = Error(ObjectService.MoveRefused(verdict, "P", facts, facts.Destination, "EntityManager type not found"));

            Assert.Equal("MoveStrategyUnavailable", (string)json["error"]["code"]);
            Assert.Equal("EntityManager type not found", (string)json["error"]["strategyDetail"]);
        }

        // Neither the placement code nor the move code may run Specify, Generate, Build, a reorganization,
        // an execution or a test: they only read and save the object's parent. The receipts say so with
        // implicitOperations: [] and this guard keeps the code from growing such a call.
        [Theory]
        [InlineData("Services", "ObjectService.Move.cs")]
        [InlineData("Helpers", "PlacementResolver.cs")]
        [InlineData("Helpers", "MovePreflight.cs")]
        [InlineData("Helpers", "AtomicPlacementFlow.cs")]
        [InlineData("Helpers", "ObjectMover.cs")]
        public void PlacementAndMoveSources_NeverReachSpecifyGenerateBuildReorgRunOrTests(string folder, string file)
        {
            string text = File.ReadAllText(Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", folder, file));

            AssertNoImplicitOperation(text, file);
        }

        [Fact]
        public void AtomicCreatePlacementHelpers_NeverReachSpecifyGenerateBuildReorgRunOrTests()
        {
            string text = File.ReadAllText(Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "AtomicCreateService.cs"));
            int start = text.IndexOf("// The destination of this call, resolved once", StringComparison.Ordinal);
            int end = text.IndexOf("// Capture pre-write EditSnapshotStore snapshots", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start, "the placement helper region of AtomicCreateService moved; update this guard");

            AssertNoImplicitOperation(text.Substring(start, end - start), "AtomicCreateService placement helpers");
        }

        private static void AssertNoImplicitOperation(string text, string label)
        {
            string[] forbidden =
            {
                "SaveSpecifyOrchestrator", "SpecifierService", "LifecycleService", "BuildService", "InProcessBuildEngine",
                "ReorgService", "RunObjectService", "TestService", "ISpecifierService", "IBuildService",
                ".Specify(", ".Generate(", ".Build(", ".Rebuild(", ".Reorganize(", "BuildAll"
            };
            foreach (string token in forbidden)
                Assert.False(text.Contains(token), label + " must not reference '" + token + "': a move or a placement never runs it implicitly.");
        }
    }
}
