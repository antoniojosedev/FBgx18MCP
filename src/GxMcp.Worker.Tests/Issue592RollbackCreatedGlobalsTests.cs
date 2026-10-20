using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Parsers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #592: the automatic rollback of a `mode=ops` Structure write restored
    // the Transaction and reported rolledBack:true, verified:true, but the global
    // Attribute the reverted write had created stayed in the Knowledge Base and
    // nothing in the response said so.
    //
    // The cleanup is asserted here against a fake SDK store that answers the
    // same questions the Worker's object resolution, incoming-reference walk and
    // object-delete path answer, so every guard runs without a Knowledge Base.
    public class Issue592RollbackCreatedGlobalsTests
    {
        private static readonly Guid NewGlobalGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly Guid TransactionGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        private static readonly Guid OtherObjectGuid = Guid.Parse("99999999-8888-7777-6666-555555555555");
        private const string NewGlobalName = "OrderExtraCode";

        private sealed class FakeStore : ICreatedGlobalStore
        {
            public Guid? TargetGuid { get; set; } = TransactionGuid;
            public readonly Dictionary<Guid, ResolvedGlobal> Objects = new Dictionary<Guid, ResolvedGlobal>();
            public Func<Guid, ReferenceProbe> References = _ => new ReferenceProbe();
            public Func<ResolvedGlobal, JObject> DeleteBehaviour;
            public readonly List<Guid> Deleted = new List<Guid>();
            public int ReferenceChecks;

            public ResolvedGlobal Resolve(Guid guid)
            {
                ResolvedGlobal resolved;
                return Objects.TryGetValue(guid, out resolved) ? resolved : null;
            }

            public ReferenceProbe FindReferences(Guid guid)
            {
                ReferenceChecks++;
                return References(guid);
            }

            public JObject Delete(ResolvedGlobal global)
            {
                if (DeleteBehaviour != null) return DeleteBehaviour(global);
                Deleted.Add(global.Guid);
                Objects.Remove(global.Guid);
                return JObject.Parse(GxMcp.Worker.Models.McpResponse.Ok(
                    target: global.Name, code: "ObjectDeleted",
                    result: new JObject { ["rereadConfirmed"] = true }));
            }
        }

        private static FakeStore StoreWithTheNewGlobal()
        {
            var store = new FakeStore();
            store.Objects[NewGlobalGuid] = new ResolvedGlobal { Guid = NewGlobalGuid, Name = NewGlobalName, TypeName = "Attribute" };
            return store;
        }

        /// The envelope RollbackFullWriteFailure has just written for a Structure
        /// rollback: the original write was saved, verification failed, the prior
        /// DSL was restored and re-read.
        private static JObject RollbackResponse(bool withCreatedGlobal = true, bool verified = true)
        {
            var response = JObject.Parse(
                "{status:'error',code:'WriteNotPersisted',saved:true,persisted:false,stateRestored:true,"
                + "postSaveVerification:{reReadConfirmed:true,matches:true,reason:'rollbackVerified'}}");
            response["rolledBack"] = verified;
            response["rollback"] = new JObject
            {
                ["requested"] = true, ["attempted"] = true, ["rolledBack"] = verified, ["verified"] = verified,
                ["atomic"] = true, ["baseVersion"] = "35", ["saveRequired"] = true, ["reReadConfirmed"] = true,
                ["error"] = verified ? null : "Rollback re-read was incomplete."
            };
            if (withCreatedGlobal)
                response["createdGlobals"] = new JArray(new JObject { ["name"] = NewGlobalName, ["guid"] = NewGlobalGuid.ToString() });
            return response;
        }

        private static ReferenceProbe ReferenceFrom(string name, string type, Guid guid)
        {
            var probe = new ReferenceProbe();
            probe.References.Add(new ReferencingObject { Name = name, TypeName = type, Guid = guid });
            return probe;
        }

        // ---- the reported case ------------------------------------------------

        [Fact]
        public void NewGlobal_VerifiedRollback_RemovesTheGlobalAndReportsIt()
        {
            var store = StoreWithTheNewGlobal();
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Equal(new[] { NewGlobalGuid }, store.Deleted);
            var receipt = response["rollback"]["createdGlobals"];
            var removed = Assert.Single((JArray)receipt["removed"]);
            Assert.Equal(NewGlobalName, removed["name"]);
            Assert.Equal(NewGlobalGuid.ToString(), removed["guid"]);
            Assert.True(removed["removed"].Value<bool>());
            Assert.True(removed["reReadConfirmed"].Value<bool>());
            Assert.Empty((JArray)receipt["retained"]);
            Assert.True(receipt["complete"].Value<bool>());
            Assert.Null(receipt["hint"]);
            // The rollback verdict still describes the Structure restore.
            Assert.True(response["rollback"]["rolledBack"].Value<bool>());
            Assert.True(response["rollback"]["verified"].Value<bool>());
            Assert.True(response["rolledBack"].Value<bool>());
            Assert.Equal("rollbackVerified", response["postSaveVerification"]["reason"]);
        }

        // ---- guards: keep the global and say why --------------------------------

        [Fact]
        public void GlobalReferencedElsewhere_IsRetainedAndListed()
        {
            var store = StoreWithTheNewGlobal();
            store.References = _ => ReferenceFrom("OrderReport", "Procedure", OtherObjectGuid);
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            Assert.True(store.Objects.ContainsKey(NewGlobalGuid));
            var receipt = response["rollback"]["createdGlobals"];
            Assert.Empty((JArray)receipt["removed"]);
            var retained = Assert.Single((JArray)receipt["retained"]);
            Assert.Equal(NewGlobalName, retained["name"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonReferenced, retained["reason"]);
            Assert.False(retained["removed"].Value<bool>());
            var reference = Assert.Single((JArray)retained["references"]);
            Assert.Equal("OrderReport", reference["name"]);
            Assert.Equal("Procedure", reference["type"]);
            Assert.Equal(1, retained["referenceCount"].Value<int>());
            Assert.False(receipt["complete"].Value<bool>());
            Assert.Contains("'OrderExtraCode' (referenced)", receipt["hint"].ToString());
            Assert.Contains("genexus_delete_object", receipt["hint"].ToString());
        }

        [Fact]
        public void TruncatedReferenceWalk_IsRetained()
        {
            var store = StoreWithTheNewGlobal();
            store.References = _ => new ReferenceProbe { Truncated = true };
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonReferenced, retained["reason"]);
            Assert.True(retained["referencesTruncated"].Value<bool>());
        }

        [Fact]
        public void ReferenceCheckUnavailable_RetainsAndSaysSo()
        {
            var store = StoreWithTheNewGlobal();
            store.References = _ => throw new InvalidOperationException("reference graph offline");
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            Assert.True(store.Objects.ContainsKey(NewGlobalGuid));
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonReferenceCheckUnavailable, retained["reason"]);
            Assert.Equal("reference graph offline", retained["error"]);
            Assert.False(response["rollback"]["createdGlobals"]["complete"].Value<bool>());
        }

        [Fact]
        public void ReferenceProbeWithoutAnAnswer_IsTreatedAsUnavailable()
        {
            var store = StoreWithTheNewGlobal();
            store.References = _ => null;
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonReferenceCheckUnavailable, retained["reason"]);
        }

        [Fact]
        public void UnverifiedRollback_RetainsWithoutTouchingTheStore()
        {
            var store = StoreWithTheNewGlobal();
            var response = RollbackResponse(verified: false);

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            Assert.Equal(0, store.ReferenceChecks);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonRollbackUnverified, retained["reason"]);
            Assert.False(response["rollback"]["rolledBack"].Value<bool>());
        }

        [Fact]
        public void RolledBackButUnverified_StillRetains()
        {
            // rolledBack:true with verified:false is a restore the re-read could
            // not confirm; it must not authorize a delete either.
            var store = StoreWithTheNewGlobal();
            var response = RollbackResponse();
            response["rollback"]["verified"] = false;

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonRollbackUnverified, retained["reason"]);
        }

        [Fact]
        public void GuidResolvingToAnotherObject_IsRetainedAsIdentityMismatch()
        {
            var store = new FakeStore();
            store.Objects[NewGlobalGuid] = new ResolvedGlobal { Guid = NewGlobalGuid, Name = "CustomerCode", TypeName = "Attribute" };
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            Assert.Equal(0, store.ReferenceChecks);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonIdentityMismatch, retained["reason"]);
            Assert.Equal("CustomerCode", retained["resolvedName"]);
        }

        [Fact]
        public void GuidResolvingToANonAttribute_IsRetainedAsIdentityMismatch()
        {
            var store = new FakeStore();
            store.Objects[NewGlobalGuid] = new ResolvedGlobal { Guid = NewGlobalGuid, Name = NewGlobalName, TypeName = "Procedure" };
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonIdentityMismatch, retained["reason"]);
            Assert.Equal("Procedure", retained["resolvedType"]);
        }

        [Fact]
        public void LedgerEntryWithoutAGuid_IsRetainedAsIdentityUnknown()
        {
            var store = StoreWithTheNewGlobal();
            var response = RollbackResponse();
            response["createdGlobals"] = new JArray(new JObject { ["name"] = NewGlobalName, ["guid"] = null });

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonIdentityUnknown, retained["reason"]);
        }

        [Fact]
        public void DeleteRefusedByTheDeletePath_IsRetainedWithItsError()
        {
            var store = StoreWithTheNewGlobal();
            store.DeleteBehaviour = global => JObject.Parse(GxMcp.Worker.Models.McpResponse.Err(
                code: "DeleteNotPersisted", message: "The SDK still resolves the object after Delete().", target: global.Name));
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonDeleteFailed, retained["reason"]);
            Assert.Equal("DeleteNotPersisted", retained["code"]);
            Assert.Contains("still resolves", retained["error"].ToString());
        }

        [Fact]
        public void DeleteThrowing_IsRetainedWithTheException()
        {
            var store = StoreWithTheNewGlobal();
            store.DeleteBehaviour = _ => throw new InvalidOperationException("sdk refused the delete");
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonDeleteFailed, retained["reason"]);
            Assert.Equal("sdk refused the delete", retained["error"]);
        }

        [Fact]
        public void StoreUnavailable_RetainsEveryGlobal()
        {
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, null);

            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonResolveUnavailable, retained["reason"]);
        }

        // ---- what does not block ----------------------------------------------------

        [Fact]
        public void ReferenceFromTheRolledBackTransactionItself_DoesNotBlockRemoval()
        {
            // The verified restore already proved the Transaction's Structure no
            // longer carries the attribute; a lingering self-reference is bookkeeping.
            var store = StoreWithTheNewGlobal();
            store.References = _ => ReferenceFrom("OrderHeader", "Transaction", TransactionGuid);
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Equal(new[] { NewGlobalGuid }, store.Deleted);
            Assert.Single((JArray)response["rollback"]["createdGlobals"]["removed"]);
        }

        [Fact]
        public void UnresolvedReference_BlocksEvenWhenTheTargetIsUnknown()
        {
            // A reference the model could not resolve to an object is still a
            // reference; without a target GUID nothing can be excluded either.
            var store = StoreWithTheNewGlobal();
            store.TargetGuid = null;
            store.References = _ => ReferenceFrom("native-key-only", null, Guid.Empty);
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var retained = Assert.Single((JArray)response["rollback"]["createdGlobals"]["retained"]);
            Assert.Equal(RollbackGlobalCleanup.ReasonReferenced, retained["reason"]);
        }

        [Fact]
        public void GlobalAlreadyGone_IsReportedRemovedWithoutADelete()
        {
            var store = new FakeStore();
            var response = RollbackResponse();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            var removed = Assert.Single((JArray)response["rollback"]["createdGlobals"]["removed"]);
            Assert.True(removed["alreadyAbsent"].Value<bool>());
            Assert.True(response["rollback"]["createdGlobals"]["complete"].Value<bool>());
        }

        // ---- existing global: nothing was created, nothing is removed ------------------

        [Fact]
        public void ExistingGlobal_AddAttribute_RemovesNothingAndReportsNothing()
        {
            // The parser only registers globals it created; an add_attribute of an
            // existing global carries no createdGlobals, so the rollback has nothing
            // to undo and the receipt stays as it was.
            var store = StoreWithTheNewGlobal();
            var response = RollbackResponse(withCreatedGlobal: false);
            string before = response.ToString(Newtonsoft.Json.Formatting.None);

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            Assert.Equal(0, store.ReferenceChecks);
            Assert.Null(response["rollback"]["createdGlobals"]);
            Assert.Equal(before, response.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void EmptyCreatedGlobalsList_RemovesNothing()
        {
            var store = StoreWithTheNewGlobal();
            var response = RollbackResponse(withCreatedGlobal: false);
            response["createdGlobals"] = new JArray();

            WriteService.ApplyCreatedGlobalRollback(response, store);

            Assert.Empty(store.Deleted);
            Assert.Null(response["rollback"]["createdGlobals"]);
        }

        // ---- ledger plumbing: parser -> receipt -> rollback -----------------------------

        [Fact]
        public void ParserHandOff_IsTakenOnceAndResetByTheNextParse()
        {
            TransactionDslParser.PublishCommittedGlobals(new[]
            {
                new TransactionDslParser.CreatedGlobalIdentity(NewGlobalName, NewGlobalGuid)
            });

            var taken = TransactionDslParser.TakeCommittedGlobals();
            Assert.Single(taken);
            Assert.Equal(NewGlobalName, taken[0].Name);
            Assert.Equal(NewGlobalGuid, taken[0].Guid);
            Assert.Empty(TransactionDslParser.TakeCommittedGlobals());

            // Parse() resets the hand-off before it starts; a stale ledger can
            // never describe another write.
            TransactionDslParser.PublishCommittedGlobals(new[]
            {
                new TransactionDslParser.CreatedGlobalIdentity(NewGlobalName, NewGlobalGuid)
            });
            TransactionDslParser.PublishCommittedGlobals(null);
            Assert.Empty(TransactionDslParser.TakeCommittedGlobals());
        }

        [Fact]
        public void ReceiptHoistsCreatedGlobalsFromTheOkPayload()
        {
            // The Structure writer puts the ledger on the ok payload (result), the
            // receipt hoists it so the rollback finds it beside the other evidence.
            var response = JObject.Parse(GxMcp.Worker.Models.McpResponse.Ok(target: "OrderHeader", code: "WriteApplied",
                result: new JObject { ["createdGlobals"] = new JArray(new JObject { ["name"] = NewGlobalName, ["guid"] = NewGlobalGuid.ToString() }) }));

            var receipt = WriteService.ApplyTextVerificationReceipt(
                response, "OrderHeader", "Structure", "before", "requested", "different", "35", false, null, "exact");

            Assert.Equal("WriteNotPersisted", receipt["code"]);
            var created = Assert.Single((JArray)receipt["createdGlobals"]);
            Assert.Equal(NewGlobalName, created["name"]);
            Assert.Equal(NewGlobalGuid.ToString(), created["guid"]);
        }

        [Fact]
        public void CreatedGlobalsAreReadFromEitherLocation()
        {
            var topLevel = JObject.Parse("{createdGlobals:[{name:'A',guid:'" + NewGlobalGuid + "'}]}");
            var nested = JObject.Parse("{result:{createdGlobals:[{name:'B',guid:'not-a-guid'},{name:'',guid:''}]}}");

            var fromTop = WriteService.ReadCreatedGlobals(topLevel);
            var fromResult = WriteService.ReadCreatedGlobals(nested);

            Assert.Equal(NewGlobalGuid, Assert.Single(fromTop).Guid);
            var b = Assert.Single(fromResult);
            Assert.Equal("B", b.Name);
            Assert.Equal(Guid.Empty, b.Guid);
            Assert.Empty(WriteService.ReadCreatedGlobals(new JObject()));
        }

        [Fact]
        public void AttachCreatedGlobals_WritesNothingWhenNothingWasCreated()
        {
            var payload = new JObject();

            WriteService.AttachCreatedGlobals(payload, null);
            WriteService.AttachCreatedGlobals(payload, new TransactionDslParser.CreatedGlobalIdentity[0]);
            Assert.False(payload.HasValues);

            WriteService.AttachCreatedGlobals(payload, new[] { new TransactionDslParser.CreatedGlobalIdentity(NewGlobalName, NewGlobalGuid) });
            var created = Assert.Single((JArray)payload["createdGlobals"]);
            Assert.Equal(NewGlobalGuid.ToString(), created["guid"]);
        }

        [Fact]
        public void SourceRollbackPath_IsUntouchedByTheLedger()
        {
            // Regression guard for the existing Source branch: it returns before
            // any rollback write and must not start resolving globals either.
            var writer = (WriteService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(WriteService));
            var method = typeof(WriteService).GetMethod("RollbackFullWriteFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var response = JObject.Parse("{status:'error',code:'WriteNotPersisted',source:'newer content',versionToken:'35',postSaveVerification:{reReadConfirmed:true}}");
            response["createdGlobals"] = new JArray(new JObject { ["name"] = NewGlobalName, ["guid"] = NewGlobalGuid.ToString() });

            var result = JObject.Parse((string)method.Invoke(writer, new object[] { response.ToString(), "Sample", "Source", "Procedure", "original" }));

            Assert.Equal("AtomicRollbackUnavailable", result["rollback"]["reason"]);
            Assert.Null(result["rollback"]["createdGlobals"]);
        }
    }
}
