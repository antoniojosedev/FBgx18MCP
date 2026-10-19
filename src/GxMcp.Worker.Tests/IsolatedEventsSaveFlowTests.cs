using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Characterization of the isolated Events save control flow with a synthetic
    // step implementation. No SDK object, Knowledge Base or transaction exists
    // here: the flow owns ordering, rollback and what is reported, and that is
    // exactly the part these tests pin.
    public sealed class IsolatedEventsSaveFlowTests
    {
        internal sealed class RecordingSteps : IIsolatedEventsSaveSteps
        {
            private readonly JObject _receipt;
            internal readonly List<string> Calls = new List<string>();
            internal readonly Dictionary<string, Exception> Faults = new Dictionary<string, Exception>();

            internal RecordingSteps(JObject receipt) { _receipt = receipt; }

            public string OriginalToken { get; set; } = "token-1";
            internal string FreshToken { get; set; } = "token-1";
            public bool WorkerRestartRequired { get; set; }
            public bool Verified { get; set; } = true;
            internal bool? VerifiedCommitted;

            private void Step(string name)
            {
                Calls.Add(name);
                if (Faults.TryGetValue(name, out var fault)) throw fault;
            }

            public void ReadOriginal() => Step("ReadOriginal");
            public void CaptureBaseline() => Step("CaptureBaseline");
            public void BeginTransaction() => Step("BeginTransaction");
            public string ReadFreshInTransaction() { Step("ReadFreshInTransaction"); return FreshToken; }
            public void RecheckTarget() => Step("RecheckTarget");
            public void AssignSource() { Step("AssignSource"); _receipt["writeAttempted"] = true; }
            public void SavePart() => Step("SavePart");
            public void SaveObject() => Step("SaveObject");
            public void EndSaveIsolation() => Step("EndSaveIsolation");
            public void VerifyPreCommit() => Step("VerifyPreCommit");
            public void Commit() => Step("Commit");
            public void Rollback() => Step("Rollback");
            public void EndTransaction() => Step("EndTransaction");
            public void RereadAfterCommit() => Step("RereadAfterCommit");
            public void VerifyAfterCommit(bool committed) { VerifiedCommitted = committed; Step("VerifyAfterCommit"); }
            public void Dispose() { }
        }

        internal sealed class Gate
        {
            internal int Acquired;
            internal int Released;
            internal IDisposable Acquire() { Acquired++; return new Releaser(this); }
            private sealed class Releaser : IDisposable
            {
                private readonly Gate _gate;
                internal Releaser(Gate gate) { _gate = gate; }
                public void Dispose() { _gate.Released++; }
            }
        }

        private static readonly string[] HappyPath =
        {
            "ReadOriginal", "CaptureBaseline", "BeginTransaction", "ReadFreshInTransaction", "RecheckTarget",
            "AssignSource", "SavePart", "SaveObject", "EndSaveIsolation", "VerifyPreCommit", "Commit",
            "EndTransaction", "RereadAfterCommit", "VerifyAfterCommit"
        };

        private static IsolatedEventsSaveOutcome Run(RecordingSteps steps, JObject receipt, Gate gate = null, string baseVersion = "token-1")
        {
            gate = gate ?? new Gate();
            return IsolatedEventsSaveFlow.Run(steps, receipt, baseVersion, gate.Acquire);
        }

        [Fact]
        public void HappyPath_RunsEveryStepOnceInOrder_AndCommits()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt);
            var gate = new Gate();

            var outcome = Run(steps, receipt, gate);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Verified, outcome.Kind);
            Assert.Equal(HappyPath, steps.Calls);
            Assert.True(steps.VerifiedCommitted);
            Assert.Equal(1, gate.Acquired);
            Assert.Equal(1, gate.Released);
            Assert.Null(receipt["transactionRolledBack"]);
        }

        [Theory]
        [InlineData("SavePart")]
        [InlineData("SaveObject")]
        public void SdkSaveThrowingBareErro_RollsBackWithoutCommitOrSecondWrite_AndKeepsOnlyTheBareMessage(string failingStep)
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };
            steps.Faults[failingStep] = new Exception("Erro");

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            // Characterization of the field report: the actionable cause is lost.
            Assert.Equal("Erro", outcome.Message);
            Assert.True(receipt["transactionRolledBack"]?.Value<bool>());
            Assert.DoesNotContain("Commit", steps.Calls);
            Assert.Equal(1, steps.Calls.Count(c => c == failingStep));
            Assert.Equal(1, steps.Calls.Count(c => c == "SavePart"));
            Assert.Equal(failingStep == "SaveObject" ? 1 : 0, steps.Calls.Count(c => c == "SaveObject"));
            Assert.Contains("EndSaveIsolation", steps.Calls);
            Assert.Equal(new[] { "EndTransaction", "RereadAfterCommit", "VerifyAfterCommit" },
                steps.Calls.Skip(steps.Calls.IndexOf("Rollback") + 1));
            Assert.False(steps.VerifiedCommitted);
        }

        [Fact]
        public void CurrentReceipt_CarriesNoPhaseExceptionTypeOrSdkMessageEvidence()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };
            steps.Faults["SaveObject"] = new InvalidOperationException("Erro", new Exception("inner cause"));

            Run(steps, receipt);

            foreach (string absent in new[] { "savePhase", "exceptionType", "innerMessages", "sdkMessages", "sourceValidationErrors", "originalError" })
                Assert.Null(receipt[absent]);
        }

        [Fact]
        public void PreCommitMismatch_RollsBackAndReportsTheVerificationText()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };
            steps.Faults["VerifyPreCommit"] = new InvalidOperationException("The pre-commit SDK object state did not match the requested isolated change.");

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal("The pre-commit SDK object state did not match the requested isolated change.", outcome.Message);
            Assert.DoesNotContain("Commit", steps.Calls);
            Assert.True(receipt["transactionRolledBack"]?.Value<bool>());
        }

        [Fact]
        public void FailureInTheFinallyOfTheSaveIsolation_RollsBackBeforeCommit()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };
            steps.Faults["EndSaveIsolation"] = new InvalidOperationException("SDK did not restore SkipApplyPattern.");

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal("SDK did not restore SkipApplyPattern.", outcome.Message);
            Assert.DoesNotContain("VerifyPreCommit", steps.Calls);
            Assert.DoesNotContain("Commit", steps.Calls);
            Assert.True(receipt["transactionRolledBack"]?.Value<bool>());
        }

        [Fact]
        public void RollbackFailure_IsRecordedAndVerificationStillRuns()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };
            steps.Faults["SaveObject"] = new Exception("Erro");
            steps.Faults["Rollback"] = new InvalidOperationException("rollback refused");

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal("rollback refused", receipt["rollbackError"]?.ToString());
            Assert.Null(receipt["transactionRolledBack"]);
            Assert.Contains("VerifyAfterCommit", steps.Calls);
        }

        [Fact]
        public void UnconfirmedPersistenceWithoutAFailure_ReportsTheDefaultInstruction()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal(IsolatedEventsSaveFlow.NotConfirmedMessage, outcome.Message);
            Assert.True(steps.VerifiedCommitted);
            Assert.Contains("Commit", steps.Calls);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("token-2")]
        public void BaseVersionMismatchBeforeTheTransaction_ConflictsWithoutBaselineOrWrite(string baseVersion)
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt);
            var gate = new Gate();

            var outcome = Run(steps, receipt, gate, baseVersion);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.VersionConflict, outcome.Kind);
            Assert.Equal("The Events version changed; no save was attempted.", outcome.Message);
            Assert.Equal("token-1", outcome.CurrentVersion);
            Assert.Equal(new[] { "ReadOriginal" }, steps.Calls);
            Assert.Equal(1, gate.Released);
        }

        [Fact]
        public void TokenChangedAtTheTransactionBoundary_RollsBackAndNeverAssignsTheSource()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { FreshToken = "token-2" };

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.VersionConflict, outcome.Kind);
            Assert.Equal("The Events version changed at the transaction boundary; no save was attempted.", outcome.Message);
            Assert.Equal("token-2", outcome.CurrentVersion);
            Assert.Equal(new[] { "ReadOriginal", "CaptureBaseline", "BeginTransaction", "ReadFreshInTransaction", "Rollback" }, steps.Calls);
        }

        [Fact]
        public void InterruptionAfterTheCommit_ReportsUncertainPersistenceWithoutCompensation()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { WorkerRestartRequired = true };
            steps.Faults["RereadAfterCommit"] = new InvalidOperationException("fresh read failed");

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Equal("SDK persistence or verification was interrupted: fresh read failed. No compensating write was performed.", outcome.Message);
            Assert.True(receipt["objectSaved"]?.Value<bool>());
            Assert.False(receipt["verificationCompleted"]?.Value<bool>());
            Assert.True(receipt["persistenceUncertain"]?.Value<bool>());
            Assert.Equal(JTokenType.Null, receipt["partPersisted"].Type);
            Assert.True(receipt["workerRestartRequired"]?.Value<bool>());
            Assert.DoesNotContain("VerifyAfterCommit", steps.Calls);
        }

        [Fact]
        public void InterruptionBeforeAnyWrite_DoesNotClaimUncertainPersistence()
        {
            var receipt = new JObject { ["writeAttempted"] = false };
            var steps = new RecordingSteps(receipt);
            steps.Faults["BeginTransaction"] = new Exception("no transaction");

            var outcome = Run(steps, receipt);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.False(receipt["objectSaved"]?.Value<bool>());
            Assert.False(receipt["persistenceUncertain"]?.Value<bool>());
            Assert.DoesNotContain("AssignSource", steps.Calls);
        }

        [Fact]
        public void WriteGateFailure_IsReportedAsAnInterruption()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt);

            var outcome = IsolatedEventsSaveFlow.Run(steps, receipt, "token-1",
                () => throw new InvalidOperationException("gate unavailable"));

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Contains("gate unavailable", outcome.Message);
            Assert.Empty(steps.Calls);
        }

        [Fact]
        public void WriteGate_IsReleasedWhenTheSaveFails()
        {
            var receipt = new JObject();
            var steps = new RecordingSteps(receipt) { Verified = false };
            steps.Faults["SavePart"] = new Exception("Erro");
            var gate = new Gate();

            Run(steps, receipt, gate);

            Assert.Equal(1, gate.Acquired);
            Assert.Equal(1, gate.Released);
        }
    }
}
