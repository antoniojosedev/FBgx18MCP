using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// The SDK operations the isolated Events save performs, in the order
    /// <see cref="IsolatedEventsSaveFlow"/> calls them. The production
    /// implementation lives beside <c>WriteService.WriteIsolatedEventsCore</c>;
    /// tests drive the flow with a synthetic implementation, which is the only
    /// way to exercise the failure paths without opening a Knowledge Base.
    /// </summary>
    internal interface IIsolatedEventsSaveSteps : IDisposable
    {
        /// <summary>Version token of the Events part as first read, before any transaction.</summary>
        string OriginalToken { get; }

        /// <summary>True once the SDK event suppression scope is poisoned and the Worker must restart.</summary>
        bool WorkerRestartRequired { get; }

        /// <summary>True when the post-commit verification proved the complete isolated persistence.</summary>
        bool Verified { get; }

        void ReadOriginal();
        void CaptureBaseline();
        void BeginTransaction();

        /// <summary>Fresh read inside the transaction; returns its Events version token.</summary>
        string ReadFreshInTransaction();

        void RecheckTarget();
        void AssignSource();
        void SavePart();
        void SaveObject();

        /// <summary>Always runs after <see cref="SavePart"/>/<see cref="SaveObject"/>, even when one throws.</summary>
        void EndSaveIsolation();

        /// <summary>Throws when the in-transaction object state differs from the requested change.</summary>
        void VerifyPreCommit();

        void Commit();
        void Rollback();
        void EndTransaction();
        void RereadAfterCommit();
        void VerifyAfterCommit(bool committed);
    }

    internal enum IsolatedEventsSaveOutcomeKind
    {
        VersionConflict,
        Verified,
        NotConfirmed,
        Interrupted
    }

    internal sealed class IsolatedEventsSaveOutcome
    {
        private IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind kind, string message, string currentVersion)
        {
            Kind = kind;
            Message = message;
            CurrentVersion = currentVersion;
        }

        internal IsolatedEventsSaveOutcomeKind Kind { get; }

        /// <summary>The human message for every kind except <see cref="IsolatedEventsSaveOutcomeKind.Verified"/>.</summary>
        internal string Message { get; }

        internal string CurrentVersion { get; }

        internal static IsolatedEventsSaveOutcome Conflict(string message, string currentVersion)
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.VersionConflict, message, currentVersion);

        internal static IsolatedEventsSaveOutcome Success()
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.Verified, null, null);

        internal static IsolatedEventsSaveOutcome NotConfirmed(string message)
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.NotConfirmed, message, null);

        internal static IsolatedEventsSaveOutcome Interrupted(string message)
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.Interrupted, message, null);
    }

    /// <summary>
    /// Control flow of the isolated Events save: conflict checks, one
    /// transaction, rollback on any failure, and the mandatory fresh read after
    /// the transaction closes. It holds no SDK type, so the failure handling is
    /// testable without a Knowledge Base. A failed save is never retried and no
    /// compensating write is issued here.
    /// </summary>
    internal static class IsolatedEventsSaveFlow
    {
        internal const string NotConfirmedMessage =
            "Complete isolated object persistence could not be confirmed. Inspect the returned persistence state; do not retry blindly.";

        internal static IsolatedEventsSaveOutcome Run(
            IIsolatedEventsSaveSteps steps, JObject receipt, string baseVersion, Func<IDisposable> acquireWriteGate)
        {
            bool committed = false;
            try
            {
                using (acquireWriteGate())
                {
                    steps.ReadOriginal();
                    if (string.IsNullOrWhiteSpace(baseVersion) || steps.OriginalToken != baseVersion)
                        return IsolatedEventsSaveOutcome.Conflict(
                            "The Events version changed; no save was attempted.", steps.OriginalToken);
                    steps.CaptureBaseline();
                    string failure = null;
                    steps.BeginTransaction();
                    try
                    {
                        string currentToken = steps.ReadFreshInTransaction();
                        if (currentToken != steps.OriginalToken)
                        {
                            steps.Rollback();
                            return IsolatedEventsSaveOutcome.Conflict(
                                "The Events version changed at the transaction boundary; no save was attempted.", currentToken);
                        }
                        steps.RecheckTarget();
                        steps.AssignSource();
                        try
                        {
                            steps.SavePart();
                            steps.SaveObject();
                        }
                        finally
                        {
                            steps.EndSaveIsolation();
                        }
                        steps.VerifyPreCommit();
                        steps.Commit();
                        committed = true;
                    }
                    catch (Exception ex)
                    {
                        failure = ex.Message;
                        try { steps.Rollback(); receipt["transactionRolledBack"] = true; }
                        catch (Exception rollbackEx) { receipt["rollbackError"] = rollbackEx.Message; }
                    }
                    // Mandatory fresh verification occurs after transaction disposal,
                    // while broker suppression remains in force. No compensating Save.
                    steps.EndTransaction();
                    steps.RereadAfterCommit();
                    steps.VerifyAfterCommit(committed);
                    return steps.Verified
                        ? IsolatedEventsSaveOutcome.Success()
                        : IsolatedEventsSaveOutcome.NotConfirmed(failure ?? NotConfirmedMessage);
                }
            }
            catch (Exception ex)
            {
                receipt["objectSaved"] = committed;
                receipt["verificationCompleted"] = false;
                receipt["persistenceUncertain"] = receipt["writeAttempted"]?.Value<bool>() == true;
                receipt["partPersisted"] = JValue.CreateNull();
                receipt["workerRestartRequired"] = steps.WorkerRestartRequired;
                return IsolatedEventsSaveOutcome.Interrupted(
                    "SDK persistence or verification was interrupted: " + ex.Message + ". No compensating write was performed.");
            }
        }
    }
}
