using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The published contract of <c>genexus_connection_recover action=recovery_read_complete</c>:
    /// its classification (a preview unless dryRun is explicitly false), its place in the
    /// schema the argument guard enforces, and the fact that it is not a journal-only action.
    /// </summary>
    public class RecoveryReadCompletionContractTests
    {
        private static JObject Args(JToken? dryRun = null)
        {
            var args = new JObject { ["action"] = "recovery_read_complete", ["name"] = "SyntheticPanel", ["part"] = "Events" };
            if (dryRun != null) args["dryRun"] = dryRun;
            return args;
        }

        [Theory]
        [InlineData(null)]
        [InlineData(true)]
        public void DryRunDefaultsToAReadOnlyPreviewThatReadsTheKbThroughTheWorker(bool? dryRun)
        {
            var contract = OperationClassifier.Describe("genexus_connection_recover",
                Args(dryRun.HasValue ? new JValue(dryRun.Value) : null));

            Assert.Equal(OperationClassifier.OperationKind.ReadOnly, contract.Kind);
            Assert.Equal("kb.read", contract.Effects);
            Assert.Equal("gateway", contract.Execution);
            Assert.Equal("safe", contract.Retry);
            Assert.Equal("never", contract.Cache);
            Assert.Empty(contract.Invalidation);
            Assert.True(contract.PreviewSupported);
        }

        [Fact]
        public void NullDryRunIsAlsoAPreview()
            => Assert.Equal(OperationClassifier.OperationKind.ReadOnly,
                OperationClassifier.Describe("genexus_connection_recover", Args(JValue.CreateNull())).Kind);

        [Fact]
        public void ExplicitFalseRewritesTheRecoveryJournalAndNothingElse()
        {
            var contract = OperationClassifier.Describe("genexus_connection_recover", Args(false));

            Assert.Equal(OperationClassifier.OperationKind.Mutating, contract.Kind);
            Assert.Equal("file.write", contract.Effects);
            Assert.Equal("gateway", contract.Execution);
            Assert.Equal("operation_key", contract.Retry);
            Assert.Equal("never", contract.Cache);
            Assert.Equal(new[] { "files" }, contract.Invalidation);
            Assert.True(contract.PreviewSupported);
        }

        [Fact]
        public void ActionIsMutatingAndPreviewCapableInTheActionContract()
        {
            Assert.True(OperationClassifier.IsMutationCandidate("genexus_connection_recover", Args(false)));
            Assert.False(OperationClassifier.IsMutationCandidate("genexus_connection_recover", Args(true)));
            Assert.False(OperationClassifier.IsMutationCandidate("genexus_connection_recover", Args()));
        }

        [Fact]
        public void ActionNeedsAResolvedKbAndAWorkerUnlikeTheJournalOnlyActions()
        {
            Assert.True(OperationClassifier.RequiresSessionLease("genexus_connection_recover", Args(false)));
            Assert.True(OperationClassifier.RequiresSessionLease("genexus_connection_recover", Args()));
            Assert.False(OperationClassifier.RequiresSessionLease("genexus_connection_recover",
                new JObject { ["action"] = "journal_status" }));
        }

        [Fact]
        public void JournalActionHandlerLeavesTheRecoveryReadToTheWorkerBackedPath()
        {
            var registry = new MutationRecoveryRegistry();

            Assert.Null(Program.HandleMutationJournalAction(registry, Args(false)));
            Assert.Null(Program.HandleMutationJournalAction(registry, new JObject()));
        }

        [Fact]
        public void PublishedSchemaDeclaresTheActionAndItsArgumentsSoTheGuardAcceptsThem()
        {
            var accepted = GatewayArgsValidator.Validate("genexus_connection_recover", new JObject
            {
                ["action"] = "recovery_read_complete",
                ["kb"] = "synthetic-kb",
                ["name"] = "SyntheticPanel",
                ["part"] = "Events",
                ["type"] = "WebPanel",
                ["guid"] = "0f8fad5b-d9cb-469f-a165-70867728950e",
                ["entityKey"] = "d2b4a0c0-0000-0000-0000-000000000000:7",
                ["path"] = "SyntheticPanel",
                ["operationId"] = "op-1",
                ["baseVersion"] = "638000000000000000:root:abc",
                ["dryRun"] = false
            });
            Assert.True(accepted.Ok, string.Join("; ", accepted.Violations.Select(v => $"{v.Path} ({v.Actual})")));

            var rejected = GatewayArgsValidator.Validate("genexus_connection_recover", new JObject
            {
                ["action"] = "recovery_read_completed"
            });
            Assert.False(rejected.Ok);
        }

        [Fact]
        public void ActionDoesNotPerformAnyKbOperationBeyondReadingAndHasNoImplicitOnes()
        {
            // The contract says it in one place: the only side effect of a non-preview call is
            // the journal file, never the KB or a process.
            var applied = OperationClassifier.Describe("genexus_connection_recover", Args(false));

            Assert.DoesNotContain("kb", applied.Invalidation);
            Assert.NotEqual("kb.write", applied.Effects);
            Assert.NotEqual("process.write", applied.Effects);
            Assert.NotEqual("external.execute", applied.Effects);
        }
    }
}
