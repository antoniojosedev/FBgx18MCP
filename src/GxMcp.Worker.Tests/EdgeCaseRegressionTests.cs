using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.6.6 edge-case sweep — fills gaps left by per-stream test suites.
    // Each region covers a single feature added during waves 1-3; cases here are
    // the boundary / fallback / retention paths that the original streams either
    // documented in code comments but didn't exercise, or that surfaced as bugs
    // during integration.
    [Collection("InProcessSdkReflection")]
    public class EdgeCaseRegressionTests
    {
        // ── Stream E (FR#9) — IsBcOrphanError predicate ─────────────────────

        private static IndexCacheService NewIndexWith(params (string Name, string Type)[] entries)
        {
            var svc = new IndexCacheService();
            svc.ReplaceAll(entries.Select(e => new SearchIndex.IndexEntry
            {
                Guid = Guid.NewGuid().ToString(),
                Name = e.Name,
                Type = e.Type,
                IsEnriched = true
            }));
            return svc;
        }

        [Fact]
        public void IsBcOrphanError_NotCs2001_ReturnsFalse()
        {
            var idx = NewIndexWith(("Foo", "Transaction"));
            Assert.False(BuildService.IsBcOrphanError(
                "error CS0246: tipo 'Foo' não encontrado", idx));
        }

        [Fact]
        public void IsBcOrphanError_Cs2001_WithoutBcSuffix_ReturnsFalse()
        {
            var idx = NewIndexWith(("Foo", "Transaction"));
            Assert.False(BuildService.IsBcOrphanError(
                "error CS2001: arquivo 'foo.cs' não encontrado", idx));
        }

        [Fact]
        public void IsBcOrphanError_BcSuffix_TransactionMissing_ReturnsTrue()
        {
            var idx = NewIndexWith(("OtherObj", "Transaction"));
            Assert.True(BuildService.IsBcOrphanError(
                "error CS2001: arquivo 'gone_bc.cs' não encontrado", idx));
        }

        [Fact]
        public void IsBcOrphanError_BcSuffix_NameKnownButNotTransaction_ReturnsTrue()
        {
            var idx = NewIndexWith(("MyObj", "Procedure"));
            Assert.True(BuildService.IsBcOrphanError(
                "error CS2001: arquivo 'myobj_bc.cs' não encontrado", idx));
        }

        [Fact]
        public void IsBcOrphanError_BcSuffix_NameIsTransaction_ReturnsFalse()
        {
            var idx = NewIndexWith(("MyTrn", "Transaction"));
            Assert.False(BuildService.IsBcOrphanError(
                "error CS2001: arquivo 'mytrn_bc.cs' não encontrado", idx));
        }

        [Fact]
        public void IsBcOrphanError_NullLookup_ReturnsFalse()
        {
            // Without the index the predicate can't make a safe call — must
            // return false so the original error stands rather than getting
            // silently demoted to a warning.
            Assert.False(BuildService.IsBcOrphanError(
                "error CS2001: arquivo 'mytrn_bc.cs' não encontrado", null));
        }

        // ── Stream D follow-up — non-Build/RebuildAll actions fall back ─────

        [Theory]
        [InlineData("Reorg")]
        [InlineData("Validate")]
        [InlineData("Check")]
        [InlineData("Sync")]
        public void InProcessBuildRunner_NonBuildAction_FallsBack(string action)
        {
            // Reorg/Validate/Check/Sync emit distinct MSBuild tasks
            // (<CheckAndInstallDatabase>, <CheckKnowledgeBase>, full
            // <IdeWebBuildAndDeploy> Sync) that the in-process runner does NOT
            // own. The runner must refuse so RunBuild's MSBuild.exe fallback
            // takes the requested action's correct task pipeline.
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "edge-" + action,
                Status = "Running",
                Action = action,
                StartedAt = DateTime.UtcNow
            };
            bool ok = InProcessBuildRunner.Run(
                status, action, new List<string>(),
                (s, l, e) => { },
                kbHandle: new object(),
                kbLock: new object());
            Assert.False(ok);
        }

        // ── Stream C (FR#22) — BuildOutputShaper boundary ───────────────────

        [Fact]
        public void Shape_ExactlyAtCap_KeepsEverythingInHead()
        {
            // At exactly HeadBytes+TailBytes there is no middle to elide.
            // The implementation passes the whole content through `head` and
            // leaves `tail` empty + `dropped_lines = 0`.
            int total = BuildOutputShaper.HeadBytes + BuildOutputShaper.TailBytes;
            string content = new string('a', total);
            var shaped = BuildOutputShaper.Shape(content, totalLines: 1, fullLogPath: null);
            Assert.Equal(content, shaped.head);
            Assert.Equal(string.Empty, shaped.tail);
            Assert.Equal(0, shaped.dropped_lines);
        }

        [Fact]
        public void Shape_OneByteOverCap_SplitsAndCountsDropped()
        {
            // One byte over the cap forces a head/tail split with a 1-byte
            // middle. Because the middle has no newline the dropped-line count
            // is 0 even though some bytes were elided — documents the lossy
            // approximation of dropped_lines.
            int total = BuildOutputShaper.HeadBytes + BuildOutputShaper.TailBytes + 1;
            string content = new string('a', total);
            var shaped = BuildOutputShaper.Shape(content, totalLines: 1, fullLogPath: null);
            Assert.Equal(BuildOutputShaper.HeadBytes, shaped.head.Length);
            Assert.Equal(BuildOutputShaper.TailBytes, shaped.tail.Length);
            Assert.Equal(0, shaped.dropped_lines);
        }

        // ── FR#13 follow-up — validate=only honored in mode=patch ────────────

        [Fact]
        public void Dispatcher_PatchApply_ValidateOnly_MapsToDryRun_ViaConvention()
        {
            // The dispatcher's "patch" / "Apply" case maps validate=only|validate-only
            // to dryRun=true before invoking PatchService.ApplyPatch. The PatchService
            // dryRun branch returns "Applied / Write skipped" without persisting.
            // We assert the mapping convention exists in source so a future refactor
            // can't silently regress to the v2.6.6-pre behaviour that persisted under
            // validate=only.
            string dispatcherSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "GxMcp.Worker", "Services",
                    "CommandDispatcher.cs"));
            Assert.Contains("validateMode", dispatcherSrc);
            Assert.Contains("dryRunArg || validateOnly", dispatcherSrc);
        }

        // ── Stream A (FR#10) — patch safety on intentional deletes ─────────

        [Fact]
        public void IsPatchWriteSafe_IntentionalDelete_WhenOpApplied_Passes()
        {
            // An operator deleting half a Events block on purpose: anyOpApplied
            // is true, so the shrink-ratio guard must NOT fire. Only the
            // no-op shrink (anyOpApplied=false) trips suspicious_shrink.
            string original = new string('x', 1000);
            string proposed = new string('x', 100); // 90% shrink, intentional
            bool ok = WriteService.IsPatchWriteSafe(original, proposed,
                                                   anyOpApplied: true,
                                                   out string reason);
            Assert.True(ok);
            Assert.Null(reason);
        }

        [Fact]
        public void IsPatchWriteSafe_NullProposed_AlwaysFails_EvenWithOpApplied()
        {
            // A null proposal can only mean the patch produced no usable
            // result. anyOpApplied being true does NOT override the null
            // check — there's no content to persist.
            bool ok = WriteService.IsPatchWriteSafe("anything", null,
                                                   anyOpApplied: true,
                                                   out string reason);
            Assert.False(ok);
            Assert.Equal("patch_no_match", reason);
        }

        // ── Stream A (FR#11) — snapshot retention pruning ───────────────────

        [Fact]
        public void EditSnapshotStore_RetentionCapsAtMaxSnapshotsPerKey()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "GxMcpEdgeSnap_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string guid = Guid.NewGuid().ToString();
                int saves = EditSnapshotStore.MaxSnapshotsPerKey + 5;
                for (int i = 0; i < saves; i++)
                {
                    var info = EditSnapshotStore.SaveSnapshot(root, guid, "Source",
                        content: "iter-" + i);
                    Assert.NotNull(info);
                    // Force distinct UTC ms-precision timestamps so the pruner
                    // can order strictly newest-first.
                    Thread.Sleep(2);
                }
                var list = EditSnapshotStore.List(root, guid, "Source");
                Assert.Equal(EditSnapshotStore.MaxSnapshotsPerKey, list.Count);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        // ── Stream F — concurrent waiters wake on a single state change ─────

        [Fact]
        public void StatusWait_ConcurrentWaiters_BothWake_OnSinglePhaseChange()
        {
            // Two callers polling the same taskId with wait=10 must both
            // observe the next state change. The signal must NOT auto-reset
            // before the second waiter sees it.
            var svc = new BuildService();
            string taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = "Build",
                Target = "X",
                Status = "Running",
                Phase = "Starting",
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StartedAt = DateTime.UtcNow
            };
            var fld = typeof(BuildService).GetField("_tasks",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            var dict = (ConcurrentDictionary<string, BuildService.BuildTaskStatus>)fld!.GetValue(null)!;
            dict[taskId] = status;

            string baseline = status.ComputeBaseline();
            var waitA = Task.Run(() => svc.GetStatusWait(taskId, 10, baseline, 1, 50));
            var waitB = Task.Run(() => svc.GetStatusWait(taskId, 10, baseline, 1, 50));
            Thread.Sleep(120); // let both threads enter Wait
            status.Phase = "Specifying";
            status.StateChangeSignal.Set();

            bool aDone = waitA.Wait(TimeSpan.FromSeconds(3));
            bool bDone = waitB.Wait(TimeSpan.FromSeconds(3));
            Assert.True(aDone, "waiter A timed out — signal failed to propagate");
            Assert.True(bDone, "waiter B timed out — signal didn't reach second waiter");
        }

        // ── Stream E (FR#7) — Normalizer edges ──────────────────────────────

        [Fact]
        public void Normalizer_Acessoperfil_DoesNotStripLeadingA()
        {
            // The 'a'/'p' prefix collisions noted in friction-list #7 —
            // "acessoperfil" must NOT become "cessoperfil".
            var idx = NewIndexWith(("acessoperfil", "Transaction"));
            var m = typeof(BuildService).GetMethod("NormalizeMissingObjectName",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(m);
            string norm = (string)m!.Invoke(null, new object[] { "acessoperfil", idx })!;
            Assert.Equal("acessoperfil", norm);
        }

        [Fact]
        public void Normalizer_UnknownStripped_UnknownOriginal_ReturnsNull()
        {
            var idx = NewIndexWith(("realObject", "Transaction"));
            var m = typeof(BuildService).GetMethod("NormalizeMissingObjectName",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(m);
            string norm = (string)m!.Invoke(null, new object[] { "ghost_bc", idx })!;
            // Stripped form "ghost" is unknown AND original "ghost_bc" is unknown
            // → suggestion is dropped so the agent doesn't chase a phantom.
            Assert.Null(norm);
        }

        // ── Stream E (FR#8) — BC variant auto-chain in ExpandTargets ────────

        [Fact]
        public void ExpandTargets_TransactionWithBcCompanion_PrependsBcVariant()
        {
            var idx = NewIndexWith(("Customer", "Transaction"), ("Customer_bc", "Transaction"));
            var svc = new BuildService();
            typeof(BuildService).GetMethod("SetIndexCacheService")!.Invoke(svc, new object[] { idx });
            var plan = svc.ExpandTargets(new[] { "Customer" }, includeCallees: "none", cap: 50);
            // includeCallees="none" returns originalList unchanged (no BC injection
            // either — auto-chain runs only under the dependency-graph path).
            // Switch to "direct" which keeps the BC injection enabled.
            plan = svc.ExpandTargets(new[] { "Customer" }, includeCallees: "direct", cap: 50);
            Assert.Contains("Customer_bc", plan.Expanded);
            // BC variant must appear BEFORE the trn itself in the build order.
            Assert.True(plan.Expanded.IndexOf("Customer_bc") < plan.Expanded.IndexOf("Customer"));
        }

        [Fact]
        public void ExpandTargets_TransactionWithoutBcCompanion_DoesNotInvent()
        {
            // Only one Transaction in the index — no <name>_bc companion exists.
            // The expansion must not invent a phantom target.
            var idx = NewIndexWith(("Product", "Transaction"));
            var svc = new BuildService();
            typeof(BuildService).GetMethod("SetIndexCacheService")!.Invoke(svc, new object[] { idx });
            var plan = svc.ExpandTargets(new[] { "Product" }, includeCallees: "direct", cap: 50);
            Assert.DoesNotContain("Product_bc", plan.Expanded);
        }

        [Fact]
        public void ExpandTargets_NonTransactionTarget_SkipsBcInjection()
        {
            // The BC heuristic must NOT fire for non-Transaction objects, even
            // if a "<name>_bc" object happens to exist in the index.
            var idx = NewIndexWith(
                ("Calculator", "Procedure"),
                ("Calculator_bc", "Transaction"));  // unrelated naming collision
            var svc = new BuildService();
            typeof(BuildService).GetMethod("SetIndexCacheService")!.Invoke(svc, new object[] { idx });
            var plan = svc.ExpandTargets(new[] { "Calculator" }, includeCallees: "direct", cap: 50);
            Assert.DoesNotContain("Calculator_bc", plan.Expanded);
        }
    }
}
