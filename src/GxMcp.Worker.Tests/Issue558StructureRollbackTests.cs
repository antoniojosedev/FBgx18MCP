using System.Collections.Generic;
using GxMcp.Worker.Parsers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #558: the automatic rollback of a `mode=ops` Structure write ended
    // with SDK-API-UNSUPPORTED although `genexus_structure remove_attribute`
    // drops the same attribute through the level's native Items storage, and
    // `rollback.error` said "nothing was persisted" beside `saved:true`.
    //
    // The Items path is reflection over the SDK's private storage, so it is
    // asserted here against fakes that expose the same members the SDK does:
    // a level with an `Items` property whose type carries a private `m_Inner`
    // list and a `Dirty` property, and items with a `Name`.
    public class Issue558StructureRollbackTests
    {
        private sealed class FakeItems
        {
            private readonly List<object> m_Inner = new List<object>();
            public bool Dirty { get; set; }
            public void Add(object item) => m_Inner.Add(item);
            public int Count => m_Inner.Count;
            public bool Contains(object item) => m_Inner.Contains(item);
        }

        private sealed class FakeLevel
        {
            public FakeItems Items { get; } = new FakeItems();
        }

        /// A level without the native storage member: nothing to remove with.
        private sealed class FakeLevelWithoutItems
        {
            public string Name { get; set; }
        }

        private sealed class FakeAttribute
        {
            public string Name { get; set; }
        }

        /// A sub-level sits in the same Items list and also has a Name.
        private sealed class FakeSubLevel
        {
            public string Name { get; set; }
        }

        [Fact]
        public void ItemsPath_RemovesByReference_AndMarksItemsDirty()
        {
            var level = new FakeLevel();
            var keep = new FakeAttribute { Name = "McpTstR03Id" };
            var dead = new FakeAttribute { Name = "McpTstR03A" };
            level.Items.Add(keep);
            level.Items.Add(dead);

            bool removed = TransactionDslParser.TryRemoveAttribute(null, level, dead, dead.Name, out bool apiMissing, out string error);

            Assert.True(removed);
            Assert.False(apiMissing);
            Assert.Null(error);
            Assert.False(level.Items.Contains(dead));
            Assert.True(level.Items.Contains(keep));
            Assert.True(level.Items.Dirty);
        }

        [Fact]
        public void ItemsPath_RemovesByName_WhenTheReferenceDiffers()
        {
            // The Attributes view may hand out a different proxy than the one
            // stored in Items; the name fallback must still find the stored one.
            var level = new FakeLevel();
            var stored = new FakeAttribute { Name = "McpTstR03A" };
            level.Items.Add(stored);
            var proxy = new FakeAttribute { Name = "mcptstr03a" };

            bool removed = TransactionDslParser.TryRemoveAttribute(null, level, proxy, proxy.Name, out bool apiMissing, out _);

            Assert.True(removed);
            Assert.False(apiMissing);
            Assert.Equal(0, level.Items.Count);
            Assert.True(level.Items.Dirty);
        }

        [Fact]
        public void ItemsPath_NeverRemovesAHomonymSubLevel()
        {
            // Items also holds sub-levels. A level named like the dead attribute
            // must survive the name fallback, or the rollback would drop a level
            // instead of the attribute it was asked to remove.
            var level = new FakeLevel();
            var homonymLevel = new FakeSubLevel { Name = "McpTstR03A" };
            level.Items.Add(homonymLevel);
            var dead = new FakeAttribute { Name = "McpTstR03A" };

            bool removed = TransactionDslParser.TryRemoveAttribute(null, level, dead, dead.Name, out bool apiMissing, out string error);

            Assert.False(removed);
            Assert.False(apiMissing);
            Assert.Null(error);
            Assert.True(level.Items.Contains(homonymLevel));
            Assert.False(level.Items.Dirty);
        }

        [Fact]
        public void ItemsPath_FailsClosed_WhenNothingMatches()
        {
            var level = new FakeLevel();
            var other = new FakeAttribute { Name = "McpTstR03Valor" };
            level.Items.Add(other);
            var dead = new FakeAttribute { Name = "McpTstR03A" };

            bool removed = TransactionDslParser.TryRemoveAttribute(null, level, dead, dead.Name, out bool apiMissing, out string error);

            // The storage member exists, so this is not an unsupported API: it
            // is a removal that found nothing, reported as such.
            Assert.False(removed);
            Assert.False(apiMissing);
            Assert.Null(error);
            Assert.True(level.Items.Contains(other));
            Assert.False(level.Items.Dirty);
        }

        [Fact]
        public void NoRemovalMemberAnywhere_ReportsApiMissing()
        {
            var level = new FakeLevelWithoutItems { Name = "Root" };
            var dead = new FakeAttribute { Name = "McpTstR03A" };

            bool removed = TransactionDslParser.TryRemoveAttribute(null, level, dead, dead.Name, out bool apiMissing, out _);

            Assert.False(removed);
            Assert.True(apiMissing);
        }

        [Fact]
        public void OpsNote_AfterASave_DoesNotSayNothingWasPersisted()
        {
            // The reported envelope: the original write was saved, verification
            // failed and the rollback could not remove the attribute.
            var writeJson = JObject.Parse("{code:'WriteNotPersisted',saved:true,persisted:false,partialPersistenceDetected:true,rollback:{rolledBack:false}}");

            string note = WriteService.BuildOpsPersistFailureNote(writeJson);

            Assert.DoesNotContain("nothing was persisted", note);
            Assert.Contains("changes were saved", note);
            Assert.Contains("rollback failed", note);
            Assert.Contains("genexus_structure action=remove_attribute", note);
        }

        [Fact]
        public void OpsNote_BeforeASave_SaysNothingWasPersisted()
        {
            var writeJson = JObject.Parse("{code:'StructureAttributeRemovalUnsupported',saved:false,persisted:false}");

            string note = WriteService.BuildOpsPersistFailureNote(writeJson);

            Assert.Contains("nothing was persisted", note);
            Assert.DoesNotContain("changes were saved", note);
        }

        [Fact]
        public void OpsNote_TreatsSdkSaveCompletedAsSaved()
        {
            var writeJson = JObject.Parse("{code:'WriteNotPersisted',sdkSaveCompleted:true}");

            Assert.DoesNotContain("nothing was persisted", WriteService.BuildOpsPersistFailureNote(writeJson));
        }

        [Fact]
        public void RollbackError_IsTheRollbackWritesOwnFailure()
        {
            // The restore reuses WriteObject; its Structure abort message used to
            // land verbatim in rollback.error and, beside saved:true, read as if
            // the original write had not been saved.
            string abort = WriteService.BuildStructureRemovalAbortMessage(
                "Could not remove attribute(s): McpTstR03A (SDK-API-UNSUPPORTED: this GeneXus SDK build exposes no attribute-removal method on the transaction structure)");
            var restoreEnvelope = JObject.Parse("{status:'error',code:'StructureAttributeRemovalUnsupported'}");
            restoreEnvelope["error"] = new JObject { ["message"] = abort };

            string rollbackError = WriteService.DescribeRollbackWriteFailure(restoreEnvelope);

            Assert.StartsWith("Rollback write failed: Could not remove attribute(s): McpTstR03A", rollbackError);
            Assert.Contains("aborted before its save step", rollbackError);
            Assert.DoesNotContain("nothing was persisted", rollbackError);
        }

        [Fact]
        public void RollbackError_UsesTopLevelMessage_AndAFallback()
        {
            var topLevel = JObject.Parse("{status:'error',code:'StructureAttributeNotRemoved',message:'SDK kept the attribute'}");
            Assert.Equal("Rollback write failed: SDK kept the attribute", WriteService.DescribeRollbackWriteFailure(topLevel));

            var silent = JObject.Parse("{status:'error',code:'StructureAttributeNotRemoved'}");
            Assert.Equal("Rollback write failed.", WriteService.DescribeRollbackWriteFailure(silent));
        }

        [Theory]
        [InlineData("{status:'ok',code:'WriteApplied'}")]
        [InlineData("{status:'Success',code:'WriteApplied'}")]
        [InlineData("{status:'error',code:'WriteNotPersisted'}")]
        [InlineData("{status:'error',code:'WriteVerificationUnavailable'}")]
        public void RollbackError_LeavesTheForcedReReadToDecide(string envelope)
        {
            // A verification outcome may still have performed the physical
            // restore; only the re-read that follows decides those.
            Assert.Null(WriteService.DescribeRollbackWriteFailure(JObject.Parse(envelope)));
        }

        [Fact]
        public void StructureAbortMessage_SpeaksOnlyAboutThisWrite()
        {
            string message = WriteService.BuildStructureRemovalAbortMessage("Could not remove attribute(s): McpTstR03A");

            Assert.StartsWith("Could not remove attribute(s): McpTstR03A. ", message);
            Assert.Contains("aborted before its save step", message);
            Assert.DoesNotContain("nothing was persisted", message);
        }
    }
}
