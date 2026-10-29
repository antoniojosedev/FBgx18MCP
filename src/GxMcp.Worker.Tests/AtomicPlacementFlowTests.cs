using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // genexus_create action=object_atomic with parentPath="Root Module/Stock/Returns" saved the object,
    // discarded the answer of the move into that path, and reported ObjectCreatedAtomic although a fresh read
    // found the object in the Root Module. AtomicPlacementFlow is the proof that replaced it: a fresh read must
    // find the object in the requested Folder, else it is moved with the move pipeline, else it is deleted again
    // and its absence is confirmed. These tests drive every branch through fake SDK ports.
    public class AtomicPlacementFlowTests
    {
        private static readonly Guid StockModule = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid ReturnsFolder = new Guid("22222222-2222-2222-2222-222222222222");
        private static readonly Guid ObjectGuid = new Guid("99999999-9999-9999-9999-999999999999");
        private const string ReturnsPath = "Root Module/Stock/Returns";

        private static PlacementResolution Destination() => new PlacementResolution
        {
            Status = PlacementStatus.Resolved,
            Requested = ReturnsPath,
            Container = new PlacementContainer(ReturnsFolder, "Returns", "Folder", StockModule, ReturnsPath)
        };

        private static AtomicPlacementFlow.Probe InFolder() => new AtomicPlacementFlow.Probe
        {
            Readable = true, ObjectGuid = ObjectGuid, ParentGuid = ReturnsFolder, ParentPath = ReturnsPath
        };

        private static AtomicPlacementFlow.Probe InRoot() => new AtomicPlacementFlow.Probe
        {
            Readable = true, ObjectGuid = ObjectGuid, ParentGuid = null, ParentPath = "Root Module"
        };

        private sealed class FakeSdk
        {
            public Func<AtomicPlacementFlow.Probe>[] Probes;
            public AtomicPlacementFlow.MoveAttempt Move = new AtomicPlacementFlow.MoveAttempt { Ok = true, Strategy = "EntityManager.UpdateParent" };
            public AtomicPlacementFlow.DeleteAttempt Delete = new AtomicPlacementFlow.DeleteAttempt { Deleted = true, AbsentVerified = true };
            public bool MoveThrows;
            public bool DeleteThrows;
            public int ProbeCalls;
            public int MoveCalls;
            public int DeleteCalls;

            public AtomicPlacementFlow.Ports Ports() => new AtomicPlacementFlow.Ports
            {
                FreshProbe = () => Probes[Math.Min(ProbeCalls++, Probes.Length - 1)](),
                MoveIntoContainer = () =>
                {
                    MoveCalls++;
                    if (MoveThrows) throw new InvalidOperationException("move exploded");
                    return Move;
                },
                DeleteCreated = () =>
                {
                    DeleteCalls++;
                    if (DeleteThrows) throw new InvalidOperationException("delete exploded");
                    return Delete;
                }
            };
        }

        [Fact]
        public void Verify_ParentHonouredAtCreation_NeedsNoMoveAndNoDelete()
        {
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InFolder } };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.True(outcome.Ok);
            Assert.Equal("atCreation", outcome.Strategy);
            Assert.Equal(0, sdk.MoveCalls);
            Assert.Equal(0, sdk.DeleteCalls);
            Assert.Equal(ReturnsFolder, outcome.Persisted.ParentGuid);
        }

        [Fact]
        public void Verify_SdkIgnoredTheParent_ButTheMoveSucceeds_ReportsTheMoveStrategy()
        {
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot, InFolder } };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.True(outcome.Ok);
            Assert.Equal("move", outcome.Strategy);
            Assert.Equal(1, sdk.MoveCalls);
            Assert.Equal(2, sdk.ProbeCalls);
            Assert.Equal(0, sdk.DeleteCalls);
        }

        // The reported defect: the object lands in the Root Module and the move into the requested path fails.
        // The old code answered ObjectCreatedAtomic here and left the object in the root.
        [Fact]
        public void Verify_ObjectLandedInTheRootAndTheMoveFails_IsNeverASuccess_AndTheObjectIsDeletedAgain()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot },
                Move = new AtomicPlacementFlow.MoveAttempt { Ok = false, Code = "MoveFailed", Error = "the SDK did not persist the requested parent" }
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.Equal("PlacementSdkRefused", outcome.ReasonCode);
            Assert.Contains("left the new object in Root Module", outcome.Message);
            Assert.Contains("the SDK did not persist the requested parent", outcome.Message);
            Assert.Equal(1, sdk.DeleteCalls);
            Assert.True((bool)outcome.Compensation["rolledBack"]);
            Assert.Equal("Deleted", (string)outcome.Compensation["status"]);
            Assert.Equal("MoveFailed", (string)outcome.Attempt["move"]["code"]);
        }

        [Fact]
        public void Verify_MoveReportsSuccessButAFreshReadStillFindsTheRoot_IsNotPersisted()
        {
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot, InRoot } };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.Equal("PlacementNotPersisted", outcome.ReasonCode);
            Assert.Equal(1, sdk.DeleteCalls);
            Assert.True((bool)outcome.Compensation["rolledBack"]);
        }

        [Fact]
        public void Verify_FirstFreshReadUnavailable_FailsClosedWithoutMoving()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[]
                {
                    () => new AtomicPlacementFlow.Probe { Readable = false, Error = "FreshReadUnavailable" }
                }
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.Equal("PlacementVerificationFailed", outcome.ReasonCode);
            Assert.Contains("FreshReadUnavailable", outcome.Message);
            Assert.Equal(0, sdk.MoveCalls);
            Assert.Equal(1, sdk.DeleteCalls);
        }

        [Fact]
        public void Verify_SecondFreshReadUnavailable_FailsClosed()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[]
                {
                    InRoot,
                    () => new AtomicPlacementFlow.Probe { Readable = false, Error = "cache busy" }
                }
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.Equal("PlacementVerificationFailed", outcome.ReasonCode);
        }

        [Fact]
        public void Verify_ProbeThatThrows_IsAnUnreadableProbe_NotACrash()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[] { () => throw new InvalidOperationException("sdk is down") }
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.Contains("sdk is down", outcome.Message);
        }

        [Fact]
        public void Verify_MoveThatThrows_IsARefusalWithTheExceptionMessage()
        {
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot }, MoveThrows = true };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.Equal("PlacementSdkRefused", outcome.ReasonCode);
            Assert.Contains("move exploded", outcome.Message);
        }

        [Fact]
        public void Verify_DeleteThatFails_SaysTheObjectMayStillExist()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot },
                Move = new AtomicPlacementFlow.MoveAttempt { Ok = false, Error = "refused" },
                Delete = new AtomicPlacementFlow.DeleteAttempt { Deleted = false, Error = "locked" }
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False(outcome.Ok);
            Assert.False((bool)outcome.Compensation["rolledBack"]);
            Assert.Equal("Failed", (string)outcome.Compensation["status"]);
            Assert.Equal("locked", (string)outcome.Compensation["error"]);
            Assert.Contains("may still exist", (string)outcome.Compensation["note"]);
        }

        [Fact]
        public void Verify_DeleteWhoseAbsenceIsNotConfirmed_IsNotCalledARollback()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot },
                Move = new AtomicPlacementFlow.MoveAttempt { Ok = false, Error = "refused" },
                Delete = new AtomicPlacementFlow.DeleteAttempt { Deleted = true, AbsentVerified = false }
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False((bool)outcome.Compensation["rolledBack"]);
            Assert.Equal("DeletedUnverified", (string)outcome.Compensation["status"]);
        }

        [Fact]
        public void Verify_DeleteThatThrows_IsReportedAsAFailedCompensation()
        {
            var sdk = new FakeSdk
            {
                Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot },
                Move = new AtomicPlacementFlow.MoveAttempt { Ok = false, Error = "refused" },
                DeleteThrows = true
            };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.False((bool)outcome.Compensation["rolledBack"]);
            Assert.Equal("delete exploded", (string)outcome.Compensation["error"]);
        }

        [Fact]
        public void Verify_ExplicitRootDestination_IsMetByAnObjectInTheRoot()
        {
            var root = new PlacementResolution { Status = PlacementStatus.Root, Requested = "Root Module" };
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot } };

            var outcome = AtomicPlacementFlow.Verify(root, sdk.Ports());

            Assert.True(outcome.Ok);
            Assert.Equal(0, sdk.MoveCalls);
        }

        [Fact]
        public void Verify_AnObjectInAnotherFolder_DoesNotSatisfyTheRequestedOne()
        {
            var elsewhere = new AtomicPlacementFlow.Probe
            {
                Readable = true, ObjectGuid = ObjectGuid, ParentGuid = StockModule, ParentPath = "Root Module/Stock"
            };
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { () => elsewhere, InFolder } };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.True(outcome.Ok);
            Assert.Equal("move", outcome.Strategy);
            Assert.Equal(1, sdk.MoveCalls);
        }

        [Fact]
        public void Verify_AttemptRecordsWhatEachFreshReadSaw()
        {
            var sdk = new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InRoot, InFolder } };

            var outcome = AtomicPlacementFlow.Verify(Destination(), sdk.Ports());

            Assert.Equal(ReturnsPath, (string)outcome.Attempt["expectedParent"]["path"]);
            Assert.Equal("Root Module", (string)outcome.Attempt["afterCreate"]["parent"]["path"]);
            Assert.Equal(ReturnsPath, (string)outcome.Attempt["afterMove"]["parent"]["path"]);
        }

        [Fact]
        public void Verify_RequiresAResolvedDestinationAndEveryPort()
        {
            var unresolved = new PlacementResolution { Status = PlacementStatus.NotFound };
            Assert.Throws<ArgumentException>(() => AtomicPlacementFlow.Verify(unresolved, new FakeSdk { Probes = new Func<AtomicPlacementFlow.Probe>[] { InFolder } }.Ports()));
            Assert.Throws<ArgumentException>(() => AtomicPlacementFlow.Verify(Destination(), new AtomicPlacementFlow.Ports()));
        }
    }
}
