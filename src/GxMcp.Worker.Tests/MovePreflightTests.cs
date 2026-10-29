using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The dry run of genexus_properties action=move accepted a destination that the write then failed
    // with MoveFailed. Both now evaluate the same facts with MovePreflight.Evaluate, so a preview refuses
    // exactly what the write refuses, with the same code.
    public class MovePreflightTests
    {
        private static readonly Guid StockModule = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid StockReturns = new Guid("22222222-2222-2222-2222-222222222222");
        private static readonly Guid Other = new Guid("33333333-3333-3333-3333-333333333333");

        private static PlacementResolution Folder(Guid guid = default(Guid))
            => new PlacementResolution
            {
                Status = PlacementStatus.Resolved,
                Requested = "Root Module/Stock/Returns",
                Container = new PlacementContainer(guid == default(Guid) ? StockReturns : guid, "Returns", "Folder", StockModule,
                    "Root Module/Stock/Returns")
            };

        private static MoveFacts Facts(bool dryRun = false, string baseVersion = "v1", string currentVersion = "v1")
            => new MoveFacts
            {
                DryRun = dryRun,
                BaseVersion = baseVersion,
                CurrentVersion = currentVersion,
                ObjectName = "ProcTeste",
                CurrentParentGuid = null,
                Destination = Folder()
            };

        [Fact]
        public void Evaluate_ValidMove_CanApply()
        {
            var verdict = MovePreflight.Evaluate(Facts());

            Assert.True(verdict.CanApply);
            Assert.False(verdict.AlreadyInDestination);
            Assert.Empty(verdict.Blockers);
        }

        [Fact]
        public void Evaluate_WriteWithoutBaseVersion_IsRefusedBeforeAnythingElse()
        {
            var facts = Facts(baseVersion: null);
            facts.DestinationInsideObject = true;

            var verdict = MovePreflight.Evaluate(facts);

            Assert.False(verdict.CanApply);
            Assert.Equal("BaseVersionRequired", verdict.First.Code);
            Assert.Contains("baseVersion", verdict.First.Message);
            Assert.Contains("dryRun=true", verdict.First.Remedy);
        }

        [Fact]
        public void Evaluate_DryRunWithoutBaseVersion_IsAllowed_SoThePreviewCanHandOutTheToken()
        {
            Assert.True(MovePreflight.Evaluate(Facts(dryRun: true, baseVersion: null)).CanApply);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Evaluate_StaleBaseVersion_IsAVersionConflictInThePreviewAndInTheWrite(bool dryRun)
        {
            var verdict = MovePreflight.Evaluate(Facts(dryRun: dryRun, baseVersion: "old", currentVersion: "new"));

            Assert.Equal("VersionConflict", verdict.First.Code);
        }

        [Fact]
        public void Evaluate_DestinationInsideTheMovedObject_IsRefused()
        {
            var facts = Facts();
            facts.DestinationInsideObject = true;

            var verdict = MovePreflight.Evaluate(facts);

            Assert.Equal("DestinationInsideMovedObject", verdict.First.Code);
        }

        [Fact]
        public void Evaluate_NameAlreadyTakenInTheDestinationModule_IsRefused()
        {
            var facts = Facts();
            facts.NameConflict = new MoveNameConflict { Guid = Other, Path = "Root Module/Stock/Returns/ProcTeste", QualifiedName = "Stock.ProcTeste" };

            var verdict = MovePreflight.Evaluate(facts);

            Assert.Equal("DestinationNameConflict", verdict.First.Code);
            Assert.Contains("Stock.ProcTeste", verdict.First.Message);
        }

        [Fact]
        public void Evaluate_SdkWithoutAPersistPath_IsRefusedAsUnavailable()
        {
            var facts = Facts();
            facts.PersistStrategyAvailable = false;

            Assert.Equal("MoveStrategyUnavailable", MovePreflight.Evaluate(facts).First.Code);
        }

        // EntityManager.UpdateParent does not run the validation that a normal save does, so a move into an
        // imported (referenced) module went through while creating an object there was refused by the SDK.
        [Fact]
        public void Evaluate_DestinationInsideAnImportedModule_IsRefusedLikeTheSdkWouldAtSave()
        {
            var facts = Facts();
            facts.DestinationImportedPath = "Root Module/GeneXus/Common";

            var verdict = MovePreflight.Evaluate(facts);

            Assert.Equal("DestinationReadOnly", verdict.First.Code);
            Assert.Contains("Root Module/GeneXus/Common", verdict.First.Message);
            Assert.Contains("imported (referenced) module", verdict.First.Message);
        }

        [Fact]
        public void Evaluate_ImportedObject_CannotBeMoved()
        {
            var facts = Facts();
            facts.ObjectIsImported = true;

            Assert.Equal("ObjectReadOnly", MovePreflight.Evaluate(facts).First.Code);
        }

        // Seen live: after a Folder with objects moved to another module the database re-qualified the objects,
        // but the running Worker kept their loaded copies with the old module (FullyQualifiedName) until reload.
        [Fact]
        public void Evaluate_FolderWithObjectsMovedToAnotherModule_IsRefused()
        {
            var facts = Facts();
            facts.ObjectHoldsChildren = true;
            facts.CrossesModules = true;

            var verdict = MovePreflight.Evaluate(facts);

            Assert.Equal("ContainerMoveAcrossModules", verdict.First.Code);
            Assert.Contains("old module", verdict.First.Message);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void Evaluate_FolderMovedWithinItsModuleOrEmpty_IsAllowed(bool holdsChildren, bool crossesModules)
        {
            var facts = Facts();
            facts.ObjectHoldsChildren = holdsChildren;
            facts.CrossesModules = crossesModules;

            Assert.True(MovePreflight.Evaluate(facts).CanApply);
        }

        [Fact]
        public void DestinationReadOnly_IsTheOneRefusalMoveAndAtomicCreateShare()
        {
            var blocker = MovePreflight.DestinationReadOnly("Root Module/GeneXus/Common");

            Assert.Equal(MovePreflight.DestinationReadOnlyCode, blocker.Code);
            Assert.Contains("outside the imported module", blocker.Remedy);
        }

        [Fact]
        public void Evaluate_ObjectAlreadyInTheDestination_NeedsNoWriteAndIgnoresWriteOnlyBlockers()
        {
            var facts = Facts();
            facts.CurrentParentGuid = StockReturns;
            facts.NameConflict = new MoveNameConflict { Guid = Other };
            facts.PersistStrategyAvailable = false;

            var verdict = MovePreflight.Evaluate(facts);

            Assert.True(verdict.CanApply);
            Assert.True(verdict.AlreadyInDestination);
        }

        [Fact]
        public void Evaluate_MovingBackToTheRoot_IsRefusedUnlessAlreadyThere()
        {
            var root = new PlacementResolution { Status = PlacementStatus.Root, Requested = "Root Module" };

            var fromFolder = Facts();
            fromFolder.Destination = root;
            fromFolder.CurrentParentGuid = StockReturns;
            Assert.Equal("DestinationRootUnsupported", MovePreflight.Evaluate(fromFolder).First.Code);

            var alreadyRoot = Facts();
            alreadyRoot.Destination = root;
            alreadyRoot.CurrentParentGuid = null;
            var verdict = MovePreflight.Evaluate(alreadyRoot);
            Assert.True(verdict.CanApply);
            Assert.True(verdict.AlreadyInDestination);
        }

        [Fact]
        public void Evaluate_AllBlockersAreListed_NotOnlyTheFirst()
        {
            var facts = Facts(baseVersion: "old", currentVersion: "new");
            facts.DestinationInsideObject = true;
            facts.NameConflict = new MoveNameConflict { Guid = Other };
            facts.PersistStrategyAvailable = false;

            var verdict = MovePreflight.Evaluate(facts);

            Assert.Equal(
                new[] { "VersionConflict", "DestinationInsideMovedObject", "DestinationNameConflict", "MoveStrategyUnavailable" },
                verdict.Blockers.Select(b => b.Code).ToArray());
            Assert.Equal(4, verdict.BlockersJson().Count);
        }

        [Fact]
        public void Evaluate_PreviewAndWrite_RefuseTheSameDestinationsWithTheSameCodes()
        {
            Func<MoveFacts, MoveFacts> asWrite = f => { f.DryRun = false; return f; };
            Func<MoveFacts, MoveFacts> asPreview = f => { f.DryRun = true; return f; };
            var scenarios = new Func<MoveFacts>[]
            {
                () => Facts(),
                () => { var f = Facts(); f.DestinationInsideObject = true; return f; },
                () => { var f = Facts(); f.NameConflict = new MoveNameConflict { Guid = Other }; return f; },
                () => { var f = Facts(); f.PersistStrategyAvailable = false; return f; },
                () => Facts(baseVersion: "old", currentVersion: "new"),
                () => { var f = Facts(); f.CurrentParentGuid = StockReturns; return f; }
            };

            foreach (var scenario in scenarios)
            {
                var preview = MovePreflight.Evaluate(asPreview(scenario()));
                var write = MovePreflight.Evaluate(asWrite(scenario()));

                Assert.Equal(write.Blockers.Select(b => b.Code).ToArray(), preview.Blockers.Select(b => b.Code).ToArray());
                Assert.Equal(write.AlreadyInDestination, preview.AlreadyInDestination);
            }
        }
    }
}
