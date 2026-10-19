using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using static GxMcp.Gateway.Tests.RecoveryReadFixtures;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// <c>genexus_connection_recover action=recovery_read_complete</c>: the typed way to
    /// clear a post-timeout fence from a certified SDK read that is never returned. The
    /// Worker is replaced by a script of certificates, so every scenario - a part larger
    /// than the response budget, a version that moved, a fence re-armed meanwhile, a client
    /// claiming completeness - is exercised without a Knowledge Base.
    /// </summary>
    public class RecoveryReadCompletionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-recovery-cert-" + Guid.NewGuid().ToString("N"));
        private readonly string _journal;

        public RecoveryReadCompletionTests()
        {
            Directory.CreateDirectory(_root);
            _journal = Path.Combine(_root, "journal.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        /// <summary>Answers each certification request from a script; the last answer repeats.</summary>
        private sealed class ScriptedWorker
        {
            private readonly Queue<Func<JObject?>> _answers = new Queue<Func<JObject?>>();
            private Func<JObject?>? _last;
            internal List<JObject> Requests { get; } = new List<JObject>();

            internal ScriptedWorker Answer(JObject? envelope) => Answer(() => envelope);

            internal ScriptedWorker Answer(Func<JObject?> answer)
            {
                _answers.Enqueue(answer);
                return this;
            }

            internal Task<JObject?> CertifyAsync(JObject selector)
            {
                Requests.Add((JObject)selector.DeepClone());
                if (_answers.Count > 0) _last = _answers.Dequeue();
                return Task.FromResult(_last?.Invoke());
            }
        }

        private MutationRecoveryRegistry Registry(string? journal = null) =>
            journal == null ? new MutationRecoveryRegistry() : new MutationRecoveryRegistry(journal);

        private static JObject Args(bool? dryRun = null, string? part = PartName, string? name = ObjectName, string? operationId = null)
        {
            var args = new JObject { ["action"] = "recovery_read_complete" };
            if (name != null) args["name"] = name;
            if (part != null) args["part"] = part;
            if (operationId != null) args["operationId"] = operationId;
            if (dryRun.HasValue) args["dryRun"] = dryRun.Value;
            return args;
        }

        private static JObject Whole(string text, int lines, string stamp = "w100")
            => Certificate(text, lines, stamp: stamp);

        private static Task<JObject> Run(MutationRecoveryRegistry registry, JObject args, ScriptedWorker worker, string? kb = Kb)
            => RecoveryReadCompletion.RunAsync(registry, kb, args, worker.CertifyAsync);

        // ---- the typed response ------------------------------------------------------

        [Fact]
        public async Task ApplyClearsTheFenceAndAnswersWithTheTypedFields()
        {
            var registry = Registry(_journal);
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(81500, 2306);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 2306)));

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("ok", result["status"]!.ToString());
            Assert.True(result["complete"]!.Value<bool>());
            Assert.Equal(2306, result["totalLines"]!.Value<int>());
            Assert.Equal(Sha256(text), result["hash"]!.ToString());
            Assert.EndsWith(":" + Sha256(text), result["versionToken"]!.ToString());
            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.True(result["persisted"]!.Value<bool>());
            Assert.Empty((JArray)result["implicitOperations"]!);
            Assert.False(result["dryRun"]!.Value<bool>());
            Assert.Equal("op-1", result["fence"]!["operationId"]!.ToString());
            Assert.Equal(0, registry.Count);
            // The journal on disk no longer holds it either: a restart does not bring it back.
            Assert.Equal(0, new MutationRecoveryRegistry(_journal).Count);
        }

        [Theory]
        [InlineData(81500, 2306)]
        [InlineData(250000, 5000)]
        [InlineData(900000, 12000)]
        public async Task PartLargerThanTheResponseBudgetIsClearedWithoutReturningIt(int chars, int lines)
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-large");
            string text = SyntheticPart(chars, lines);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, lines)));

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.Equal(lines, result["totalLines"]!.Value<int>());
            string serialized = result.ToString(Newtonsoft.Json.Formatting.None);
            Assert.True(serialized.Length < 4000, "response is " + serialized.Length + " characters");
            Assert.DoesNotContain("&Counter", serialized);
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public async Task InMemoryRegistryReportsClearedButNotPersisted()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);

            var result = await Run(registry, Args(dryRun: false), new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60))));

            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.False(result["persisted"]!.Value<bool>());
        }

        // ---- dryRun only inspects ----------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData(true)]
        public async Task DryRunIsTheDefaultAndNeverTouchesTheJournal(bool? dryRun)
        {
            var registry = Registry(_journal);
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string before = File.ReadAllText(_journal);
            string text = SyntheticPart(3000, 60);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60)));

            var result = await Run(registry, Args(dryRun), worker);

            Assert.Equal("ok", result["status"]!.ToString());
            Assert.True(result["dryRun"]!.Value<bool>());
            Assert.True(result["complete"]!.Value<bool>());
            Assert.True(result["wouldClear"]!.Value<bool>());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.False(result["persisted"]!.Value<bool>());
            Assert.Empty((JArray)result["implicitOperations"]!);
            Assert.Equal(before, File.ReadAllText(_journal));
            Assert.Equal(1, registry.Count);
            Assert.Single(worker.Requests);
        }

        [Fact]
        public async Task DryRunStillReReadsAndReportsWhyTheFenceWouldNotClear()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            var partial = Whole(SyntheticPart(3000, 60), 60).With("complete", false);
            partial["reason"] = "window-does-not-cover-part";
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(partial));

            var result = await Run(registry, Args(), worker);

            Assert.Equal("ok", result["status"]!.ToString());
            Assert.False(result["complete"]!.Value<bool>());
            Assert.False(result["wouldClear"]!.Value<bool>());
            Assert.Equal("certificate-incomplete", result["reason"]!.ToString());
            Assert.Equal("window-does-not-cover-part", result["reasonDetail"]!.ToString());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task DryRunWithAPinnedVersionThatMovedWouldNotClear()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            var args = Args();
            args["baseVersion"] = "638000000000000000:root:" + Sha256("what I inspected");

            var result = await Run(registry, args,
                new ScriptedWorker().Answer(WorkerEnvelope(Whole(SyntheticPart(3000, 60), 60))));

            Assert.False(result["wouldClear"]!.Value<bool>());
            Assert.Equal("base-version-mismatch", result["reason"]!.ToString());
        }

        // ---- the certificate decides, never the caller -------------------------------

        [Fact]
        public async Task ClientDeclarationsOfCompletenessAreIgnored()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            var args = Args(dryRun: false);
            args["complete"] = true;
            args["fenceCleared"] = true;
            args["recoveryCertificate"] = Whole(text, 60);
            args["versionToken"] = "638000000000000000:root:" + Sha256(text);
            args["sha256"] = Sha256(text);
            args["totalLines"] = 60;
            var incomplete = Whole(text, 60).With("complete", false);
            incomplete["reason"] = "window-does-not-cover-part";
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(incomplete));

            var result = await Run(registry, args, worker);

            Assert.Equal("error", result["status"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Equal("RecoveryReadNotCertified", result["error"]!["code"]!.ToString());
            Assert.Equal(1, registry.Count);
            // None of those claims reaches the Worker: it is asked about the fence, not told.
            var request = Assert.Single(worker.Requests);
            foreach (string claim in new[] { "complete", "fenceCleared", "recoveryCertificate", "versionToken", "sha256", "totalLines" })
                Assert.Null(request[claim]);
        }

        [Fact]
        public async Task ForgedCertificateInsideTheWorkerEnvelopeStillHasToMatchTheFence()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            // Self-consistent and "complete", but about a different object.
            var otherObject = Certificate(text, 60, name: "OtherPanel", guid: "11111111-2222-3333-4444-555555555555");

            var result = await Run(registry, Args(dryRun: false), new ScriptedWorker().Answer(WorkerEnvelope(otherObject)));

            Assert.Equal("error", result["status"]!.ToString());
            Assert.Equal("name-mismatch", result["reason"]!.ToString());
            Assert.Equal(1, registry.Count);
        }

        [Theory]
        [InlineData("coverage", "sdk-partial")]
        [InlineData("complete", false)]
        [InlineData("authoritative", false)]
        public async Task CertificateThatIsNotWholeAndAuthoritativeNeverClears(string field, object value)
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);

            var result = await Run(registry, Args(dryRun: false),
                new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60).With(field, value))));

            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Equal(1, registry.Count);
        }

        // ---- a version that moved invalidates the certificate ------------------------

        [Fact]
        public async Task VersionTokenThatChangedBetweenTheTwoReadsKeepsTheFence()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            string newer = SyntheticPart(3100, 60);
            var worker = new ScriptedWorker()
                .Answer(WorkerEnvelope(Whole(text, 60)))
                .Answer(WorkerEnvelope(Whole(newer, 60)));

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("error", result["status"]!.ToString());
            Assert.Equal("RecoveryReadStale", result["error"]!["code"]!.ToString());
            Assert.Equal("version-changed-during-recovery", result["reason"]!.ToString());
            Assert.Equal("versionToken", result["reasonDetail"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task WriteStampNewerThanTheCertificateKeepsTheFence()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            var worker = new ScriptedWorker()
                .Answer(WorkerEnvelope(Whole(text, 60, stamp: "w100")))
                .Answer(WorkerEnvelope(Whole(text, 60, stamp: "w200")));

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("version-changed-during-recovery", result["reason"]!.ToString());
            Assert.Equal("targetWriteStamp", result["reasonDetail"]!.ToString());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task ConfirmationReadThatIsNoLongerCompleteKeepsTheFence()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            var racing = Whole(text, 60).With("complete", false);
            racing["reason"] = "changed-during-read";
            var worker = new ScriptedWorker()
                .Answer(WorkerEnvelope(Whole(text, 60)))
                .Answer(WorkerEnvelope(racing));

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("RecoveryReadStale", result["error"]!["code"]!.ToString());
            Assert.Equal("confirmation-certificate-incomplete", result["reason"]!.ToString());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task FenceRearmedWhileTheConfirmationReadRanIsNotCleared()
        {
            var registry = Registry(_journal);
            registry.RequireRead(Kb, ObjectName, PartName, "op-old");
            string text = SyntheticPart(3000, 60);
            int calls = 0;
            var worker = new ScriptedWorker().Answer(() =>
            {
                // A second write times out while the part is being certified.
                if (++calls == 2) registry.RequireRead(Kb, ObjectName, PartName, "op-new");
                return WorkerEnvelope(Whole(text, 60));
            });

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("RecoveryReadStale", result["error"]!["code"]!.ToString());
            Assert.Equal("fence-rearmed", result["reason"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var survivor));
            Assert.Equal("op-new", survivor.OperationId);
        }

        [Fact]
        public async Task PinnedVersionMustBeTheVersionThatIsCurrent()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            var certificate = Whole(text, 60);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(certificate));

            var stale = Args(dryRun: false);
            stale["baseVersion"] = "638000000000000000:root:" + Sha256("an older read");
            var refused = await Run(registry, stale, worker);

            Assert.Equal("RecoveryReadStale", refused["error"]!["code"]!.ToString());
            Assert.Equal("base-version-mismatch", refused["reason"]!.ToString());
            Assert.Equal(1, registry.Count);
            Assert.Single(worker.Requests); // refused before the confirmation read

            var current = Args(dryRun: false);
            current["baseVersion"] = certificate["versionToken"]!.ToString();
            var cleared = await Run(registry, current, worker);

            Assert.True(cleared["fenceCleared"]!.Value<bool>());
            Assert.Equal(0, registry.Count);
        }

        // ---- only the matching fence -------------------------------------------------

        [Fact]
        public async Task FencesOfOtherObjectsPartsTypesAndGuidsSurvive()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-target");
            registry.RequireRead(Kb, "OtherPanel", PartName, "op-other-object");
            registry.RequireRead(Kb, ObjectName, "Rules", "op-other-part");
            registry.RequireRead(Kb, ObjectName, PartName, "op-other-type",
                null, null, "Procedure", null, null);
            registry.RequireRead(Kb, ObjectName, PartName, "op-other-guid",
                "11111111-2222-3333-4444-555555555555", null, null, null, null);
            string text = SyntheticPart(3000, 60);
            // The target fence is name-only; the typed and guid-bound ones are not found by
            // a name-only selector at all, which is what keeps them out of the way.
            var result = await Run(registry, Args(dryRun: false),
                new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60))));

            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.Equal("op-target", result["fence"]!["operationId"]!.ToString());
            Assert.Equal(4, registry.Count);
            Assert.True(registry.TryGet(Kb, "OtherPanel", PartName, out _));
            Assert.True(registry.TryGet(Kb, ObjectName, "Rules", out _));
            var remaining = registry.Pending.Select(f => f.OperationId).OrderBy(o => o).ToArray();
            Assert.Equal(new[] { "op-other-guid", "op-other-object", "op-other-part", "op-other-type" }, remaining);
        }

        [Fact]
        public async Task SeveralMatchingFencesAreRefusedAsAmbiguousAndNoneIsCleared()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, "Events", "op-events");
            registry.RequireRead(Kb, ObjectName, "Rules", "op-rules");
            var worker = new ScriptedWorker();

            var result = await Run(registry, Args(dryRun: false, part: null), worker);

            Assert.Equal("RecoveryFenceAmbiguous", result["error"]!["code"]!.ToString());
            Assert.Equal(2, ((JArray)result["fences"]!).Count);
            Assert.Equal(2, registry.Count);
            Assert.Empty(worker.Requests);
        }

        [Fact]
        public async Task OperationIdSelectsOneOfSeveralFences()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, "Events", "op-events");
            registry.RequireRead(Kb, ObjectName, "Rules", "op-rules");
            string text = SyntheticPart(3000, 60);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(
                Certificate(text, 60, part: "Rules")));

            var result = await Run(registry, Args(dryRun: false, part: null, operationId: "op-rules"), worker);

            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.False(registry.TryGet(Kb, ObjectName, "Rules", out _));
            Assert.True(registry.TryGet(Kb, ObjectName, "Events", out _));
        }

        [Fact]
        public async Task IdentityBoundFenceIsReadByItsRecordedIdentityNotTheCallersWords()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-typed",
                ObjectGuid, ObjectEntityKey, "WebPanel", null, null);
            string text = SyntheticPart(3000, 60);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60)));
            var args = Args(dryRun: false);
            args["guid"] = ObjectGuid;
            args["entityKey"] = ObjectEntityKey;
            args["type"] = "WebPanel";

            var result = await Run(registry, args, worker);

            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.Equal(2, worker.Requests.Count); // the certification and its confirmation read
            var request = worker.Requests[0];
            Assert.Equal(request.ToString(), worker.Requests[1].ToString());
            Assert.Equal("Read", request["module"]!.ToString());
            Assert.Equal("CertifyRecovery", request["action"]!.ToString());
            Assert.Equal(ObjectName, request["target"]!.ToString());
            Assert.Equal(PartName, request["part"]!.ToString());
            Assert.Equal(ObjectGuid, request["guid"]!.ToString());
            Assert.Equal(ObjectEntityKey, request["entityKey"]!.ToString());
            Assert.Equal("WebPanel", request["type"]!.ToString());
            Assert.Equal(0, request["limit"]!.Value<int>());
            Assert.Null(request["offset"]);
            Assert.Null(request["path"]); // absent, not null: the Worker reads null as ""
        }

        // ---- finding the fence: aliases and identity --------------------------------

        [Fact]
        public async Task FenceRecordedUnderAnotherAliasIsReportedNotSilentlyMissed()
        {
            var registry = Registry();
            registry.RequireRead("handle-alias", ObjectName, PartName, "op-1");
            var worker = new ScriptedWorker();

            var result = await Run(registry, Args(dryRun: false), worker, kb: "folder-alias");

            Assert.Equal("error", result["status"]!.ToString());
            Assert.Equal("RecoveryFenceNotFound", result["error"]!["code"]!.ToString());
            Assert.Equal("no-matching-fence", result["reason"]!.ToString());
            var lookup = (JObject)result["lookup"]!;
            Assert.Equal("folder-alias", lookup["kbAlias"]!.ToString());
            var near = Assert.Single((JArray)lookup["nearMisses"]!);
            Assert.Equal("handle-alias", near["kbAlias"]!.ToString());
            Assert.False(near["sameKb"]!.Value<bool>());
            Assert.Contains("kb=<alias", lookup["hint"]!.ToString());
            Assert.Equal(1, registry.Count);
            Assert.Empty(worker.Requests);
        }

        [Fact]
        public async Task FenceBoundToATypeIsNotFoundByANameOnlySelectorAndTheAnswerSaysSo()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-typed", null, null, "WebPanel", null, null);

            var result = await Run(registry, Args(dryRun: false), new ScriptedWorker());

            Assert.Equal("RecoveryFenceNotFound", result["error"]!["code"]!.ToString());
            var near = Assert.Single((JArray)result["lookup"]!["nearMisses"]!);
            Assert.Equal("WebPanel", near["targetType"]!.ToString());
            Assert.True(near["sameKb"]!.Value<bool>());
        }

        [Fact]
        public async Task NothingPendingForTheObjectIsAnOkAnswerNotAFailure()
        {
            var registry = Registry();
            registry.RequireRead(Kb, "UnrelatedPanel", PartName, "op-other");
            var worker = new ScriptedWorker();

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("ok", result["status"]!.ToString());
            Assert.Equal("no-fence-pending", result["reason"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Empty((JArray)result["lookup"]!["nearMisses"]!);
            Assert.Equal(1, registry.Count);
            Assert.Empty(worker.Requests);
        }

        // ---- when the evidence cannot be had -----------------------------------------

        [Theory]
        [InlineData("timeout", "worker-timeout")]
        [InlineData("null", "worker-unavailable")]
        [InlineData("error", "worker-error:ObjectNotFound")]
        [InlineData("nested-error", "worker-error:FreshReadUnavailable")]
        [InlineData("empty", "certificate-absent")]
        public async Task WorkerThatCannotCertifyLeavesTheFence(string failure, string expectedReason)
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            JObject? answer = failure switch
            {
                "timeout" => new JObject { ["__timeout"] = true },
                "null" => null,
                "error" => new JObject { ["error"] = new JObject { ["code"] = "ObjectNotFound" } },
                "nested-error" => new JObject { ["result"] = new JObject { ["status"] = "error", ["error"] = new JObject { ["code"] = "FreshReadUnavailable" } } },
                _ => new JObject { ["result"] = new JObject { ["status"] = "ok", ["result"] = new JObject() } }
            };

            var result = await Run(registry, Args(dryRun: false), new ScriptedWorker().Answer(answer));

            Assert.Equal("error", result["status"]!.ToString());
            Assert.Equal("RecoveryReadNotCertified", result["error"]!["code"]!.ToString());
            Assert.Equal(expectedReason, result["reason"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task ConfirmationReadThatFailsLeavesTheFence()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            string text = SyntheticPart(3000, 60);
            var worker = new ScriptedWorker()
                .Answer(WorkerEnvelope(Whole(text, 60)))
                .Answer(new JObject { ["__timeout"] = true });

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("confirmation-read-failed", result["reason"]!.ToString());
            Assert.Equal("worker-timeout", result["reasonDetail"]!.ToString());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task UnhealthyJournalRefusesBeforeAnyWorkerRead()
        {
            string brokenJournal = Path.Combine(_root, "broken.json");
            File.WriteAllText(brokenJournal, "{ not json");
            var registry = new MutationRecoveryRegistry(brokenJournal);
            var worker = new ScriptedWorker();

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("MutationRecoveryJournalUnavailable", result["error"]!["code"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Empty(worker.Requests);
        }

        // ---- the request ------------------------------------------------------------

        [Fact]
        public async Task RequestWithoutAnObjectSelectorIsRefused()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");

            var result = await Run(registry, new JObject { ["action"] = "recovery_read_complete", ["part"] = PartName },
                new ScriptedWorker());

            Assert.Equal("InvalidRecoveryRead", result["error"]!["code"]!.ToString());
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task ForceCannotBeCombinedWithTheRecoveryRead()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            var args = Args(dryRun: false);
            args["force"] = true;
            var worker = new ScriptedWorker();

            var result = await Run(registry, args, worker);

            Assert.Equal("InvalidRecoveryRead", result["error"]!["code"]!.ToString());
            Assert.Empty(worker.Requests);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task NoSelectedKnowledgeBaseIsRefusedWithoutReadingAnything()
        {
            var registry = Registry();
            var worker = new ScriptedWorker();

            var result = await Run(registry, Args(dryRun: false), worker, kb: " ");

            Assert.Equal("KB_CONTEXT_REQUIRED", result["error"]!["code"]!.ToString());
            Assert.Empty(worker.Requests);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void OnlyAnExplicitFalseApplies(bool? dryRun, bool expectedPreview)
            => Assert.Equal(expectedPreview, RecoveryReadCompletion.IsPreview(Args(dryRun)));

        [Fact]
        public void DryRunAsNullOrAStringStaysSafe()
        {
            Assert.True(RecoveryReadCompletion.IsPreview(new JObject { ["dryRun"] = JValue.CreateNull() }));
            Assert.False(RecoveryReadCompletion.IsPreview(new JObject { ["dryRun"] = "false" }));
            Assert.True(RecoveryReadCompletion.IsPreview(new JObject { ["dryRun"] = "maybe" }));
            Assert.True(RecoveryReadCompletion.IsPreview(new JObject { ["dryRun"] = 0 }));
        }

        // ---- clearing the fence does not say the timed-out write landed ------------------

        [Theory]
        [InlineData("same", "notApplied")]
        [InlineData("other", "changed")]
        [InlineData("none", "unknown")]
        public async Task ResponseSaysWhetherThePartIsStillAtTheVersionTheTimedOutWriteExpected(string expected, string state)
        {
            string text = SyntheticPart(3000, 60);
            var certificate = Whole(text, 60);
            string? expectedVersion = expected switch
            {
                "same" => certificate["versionToken"]!.ToString(),
                "other" => "638000000000000001:root:" + Sha256("before the write"),
                _ => null
            };
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1", null, null, null, null, expectedVersion);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(certificate));

            var inspection = await Run(registry, Args(dryRun: true), worker);
            var applied = await Run(registry, Args(dryRun: false), worker);

            foreach (var result in new[] { inspection, applied })
            {
                Assert.Equal(state, result["timedOutWrite"]!.ToString());
                if (expectedVersion == null) Assert.Null(result["expectedVersion"]!.Value<string>());
                else Assert.Equal(expectedVersion, result["expectedVersion"]!.ToString());
            }
            // Clearing the fence is a statement about the journal, whatever the write did.
            Assert.True(applied["fenceCleared"]!.Value<bool>());
        }

        [Fact]
        public void WriteStateIsUnknownWithoutBothVersions()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1", null, null, null, null, "token");
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var fence));

            Assert.Equal("unknown", RecoveryReadCompletion.TimedOutWriteState(fence, null));
            Assert.Equal("unknown", RecoveryReadCompletion.TimedOutWriteState(fence, " "));
            Assert.Equal("notApplied", RecoveryReadCompletion.TimedOutWriteState(fence, "token"));
            Assert.Equal("changed", RecoveryReadCompletion.TimedOutWriteState(fence, "token2"));
        }

        // ---- a fence that settled meanwhile is not a failure --------------------------

        [Fact]
        public async Task FenceClearedByAnotherRouteWhileCertifyingIsReportedAsAlreadyCleared()
        {
            var registry = Registry(_journal);
            registry.RequireRead(Kb, ObjectName, PartName, "op-1");
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var observed));
            string text = SyntheticPart(3000, 60);
            int calls = 0;
            var worker = new ScriptedWorker().Answer(() =>
            {
                // A plain whole-part read (or a second recovery call) settles the fence first.
                if (++calls == 2) Assert.True(registry.ConfirmRead(Kb, ObjectName, PartName, observed));
                return WorkerEnvelope(Whole(text, 60));
            });

            var result = await Run(registry, Args(dryRun: false), worker);

            Assert.Equal("ok", result["status"]!.ToString());
            Assert.Equal("already-cleared", result["reason"]!.ToString());
            Assert.False(result["fenceCleared"]!.Value<bool>());
            Assert.Null(result["error"]);
            Assert.Equal(0, registry.Count);
        }

        // ---- the caller cannot pick which homonym is certified ----------------------------

        [Fact]
        public async Task FenceWithNoRecordedIdentityIsReadByNameAloneWhateverTypeTheCallerGives()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-untyped");
            string text = SyntheticPart(3000, 60);
            var args = Args(dryRun: true);
            args["type"] = "Procedure";   // a homonym of another type, chosen by the caller
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60)));

            await Run(registry, args, worker);

            var request = Assert.Single(worker.Requests);
            Assert.Null(request["type"]);
            Assert.Null(request["guid"]);
            Assert.Equal(ObjectName, request["target"]!.ToString());
        }

        [Fact]
        public void SelectorCarriesExactlyWhatTheFenceRecorded()
        {
            var registry = Registry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1", ObjectGuid, ObjectEntityKey, "WebPanel", "Mod.SyntheticPanel", null);
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var fence));

            var selector = RecoveryReadCompletion.BuildSelector(fence);

            Assert.Equal("WebPanel", selector["type"]!.ToString());
            Assert.Equal(ObjectGuid, selector["guid"]!.ToString());
            Assert.Equal(ObjectEntityKey, selector["entityKey"]!.ToString());
            Assert.Equal("Mod.SyntheticPanel", selector["path"]!.ToString());
            Assert.Equal(0, selector["limit"]!.Value<int>());
        }

        // ---- fences recorded under the alias an earlier build minted from the path --------

        [Fact]
        public async Task FenceRecordedUnderTheFolderAliasIsFoundAndClearedThroughTheEquivalentScope()
        {
            var registry = Registry();
            registry.RequireRead("folder-alias", ObjectName, PartName, "op-legacy");
            string text = SyntheticPart(3000, 60);
            var worker = new ScriptedWorker().Answer(WorkerEnvelope(Whole(text, 60)));

            var result = await RecoveryReadCompletion.RunAsync(
                registry, new[] { "handle-alias", "folder-alias" }, Args(dryRun: false), worker.CertifyAsync);

            Assert.True(result["fenceCleared"]!.Value<bool>());
            Assert.Equal("folder-alias", result["fence"]!["kbAlias"]!.ToString());
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public async Task NearMissesCountEveryEquivalentScopeAsTheSameKb()
        {
            var registry = Registry();
            registry.RequireRead("folder-alias", ObjectName, "Rules", "op-rules");

            var result = await RecoveryReadCompletion.RunAsync(
                registry, new[] { "handle-alias", "folder-alias" }, Args(dryRun: false, part: "Events"),
                new ScriptedWorker().CertifyAsync);

            var near = Assert.Single((JArray)result["lookup"]!["nearMisses"]!);
            Assert.True(near["sameKb"]!.Value<bool>());
            Assert.Equal(1, result["lookup"]!["pendingInKb"]!.Value<int>());
        }

        // ---- reading the Worker's envelope ------------------------------------------

        [Fact]
        public void CertificateIsFoundAtAnyDepthTheWorkerEnvelopeUses()
        {
            var certificate = Certificate(SyntheticPart(3000, 60), 60);

            Assert.NotNull(RecoveryReadCompletion.ExtractCertificate(WorkerEnvelope(certificate), out _));
            Assert.NotNull(RecoveryReadCompletion.ExtractCertificate(
                new JObject { ["recoveryCertificate"] = certificate }, out _));
            Assert.NotNull(RecoveryReadCompletion.ExtractCertificate(
                new JObject { ["result"] = new JObject { ["recoveryCertificate"] = certificate } }, out _));
        }
    }
}
