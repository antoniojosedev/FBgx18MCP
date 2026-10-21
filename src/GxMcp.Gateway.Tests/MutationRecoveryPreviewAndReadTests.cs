using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A slow dryRun preview must not leave an object fenced by a write that never
    /// happened, and a complete recovery read must clear the fence it observed even when
    /// the object is larger than the Gateway's response envelope budget. Everything here is
    /// synthetic: no KB is opened and no Worker is started.
    /// </summary>
    public class MutationRecoveryPreviewAndReadTests
    {
        private const string Kb = "synthetic-kb";

        // Source of ~81.5k characters over 2306 lines: bigger than the 80000-character
        // envelope limit, smaller than the 200000-character explicit-full-read budget.
        private static string SyntheticEventsSource(out int lines, int targetChars = 81500, int lineCount = 2306)
        {
            lines = lineCount;
            int baseWidth = Math.Max(1, targetChars / lineCount - 1);
            int extra = Math.Max(0, targetChars - lineCount * (baseWidth + 1));
            var builder = new StringBuilder(targetChars + 16);
            for (int index = 0; index < lineCount; index++)
            {
                string text = "    &Counter" + (index % 97) + " += " + index + "; // synthetic";
                int width = baseWidth + (index < extra ? 1 : 0);
                builder.Append(text.Length >= width ? text.Substring(0, width) : text.PadRight(width, '.'));
                builder.Append('\n');
            }
            return builder.ToString();
        }

        private static JObject WorkerPartRead(
            string source, int lines, string name = "SyntheticPanel", string part = "Events",
            string version = "v1", bool explicitFull = true, int offset = 0, bool paginated = false)
            => new JObject
            {
                ["name"] = name,
                ["part"] = part,
                ["source"] = source,
                ["offset"] = offset,
                ["limit"] = lines,
                ["totalLines"] = paginated ? lines * 3 : lines,
                ["totalBytes"] = Encoding.UTF8.GetByteCount(source),
                ["truncated"] = paginated,
                ["isTruncatedByWorker"] = paginated,
                ["explicitFullRead"] = explicitFull,
                ["versionToken"] = version
            };

        /// <summary>The wire guards a successful genexus_read passes before the fence decision.</summary>
        private static JToken ThroughWireGuards(JObject workerPayload, JObject readArgs)
        {
            var truncate = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(truncate);
            var shaped = (JToken)truncate!.Invoke(null, new object?[] { workerPayload.DeepClone(), "genexus_read" })!;
            return shaped is JObject shapedObject
                ? new ResponseSizeGuard().Apply(shapedObject, "genexus_read", readArgs).result
                : shaped;
        }

        private static JObject ReadArgs(string name = "SyntheticPanel", string? part = "Events", string? type = null)
        {
            var args = new JObject { ["name"] = name, ["limit"] = 0 };
            if (part != null) args["part"] = part;
            if (type != null) args["type"] = type;
            return args;
        }

        private static int ClearWithRead(MutationRecoveryRegistry registry, JObject readArgs, JToken finalResult)
        {
            var covered = Program.FindCoveredRecoveryReads(registry, Kb, readArgs);
            return Program.ConfirmMutationRecoveryRead(registry, Kb, finalResult, covered);
        }

        private static JObject Edit(bool? dryRun, string mode = "patch", string name = "SyntheticPanel", string part = "Events")
        {
            var args = new JObject
            {
                ["name"] = name, ["part"] = part, ["mode"] = mode,
                ["patch"] = new JObject { ["find"] = "a", ["replace"] = "b" }
            };
            if (dryRun.HasValue) args["dryRun"] = dryRun.Value;
            return args;
        }

        // ---- characterization: what already holds and must keep holding ----------------

        [Theory]
        [InlineData(null)]
        [InlineData(false)]
        public void WriteThatTimesOutArmsTheFence(bool? dryRun)
        {
            var registry = new MutationRecoveryRegistry();
            var timeout = new JObject { ["error"] = new JObject { ["retryable"] = true } };

            Assert.True(Program.RegisterTimeoutRecoveryFence(
                registry, Kb, "genexus_edit", Edit(dryRun), "op-write", timeout));

            Assert.True(registry.TryGet(Kb, "SyntheticPanel", "Events", out var fence));
            Assert.Equal("op-write", fence.OperationId);
            Assert.True(timeout["reReadRequired"]?.Value<bool>());
            Assert.False(timeout["error"]?["retryable"]?.Value<bool>());
        }

        [Fact]
        public void ActionlessToolOutsideTheExplicitPreviewListKeepsTheFenceOnDryRun()
        {
            // An arbitrary dryRun flag must not hide a write: only tools whose preview was
            // shown not to persist are exempt.
            var registry = new MutationRecoveryRegistry();
            var args = new JObject { ["name"] = "SyntheticPanel", ["part"] = "Events", ["dryRun"] = true };

            Assert.True(Program.RegisterTimeoutRecoveryFence(
                registry, Kb, "genexus_edit_and_build", args, "op-unproven", new JObject()));
            Assert.True(registry.TryGet(Kb, "SyntheticPanel", "Events", out _));
        }

        [Fact]
        public void CompleteReadOfASmallPartClearsTheMatchingFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-1");
            string source = SyntheticEventsSource(out int lines, targetChars: 3000, lineCount: 60);
            var args = ReadArgs();

            Assert.Equal(1, ClearWithRead(registry, args, ThroughWireGuards(WorkerPartRead(source, lines), args)));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void FenceOfAnotherObjectOrPartIsNotClearedByTheRead()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-target");
            registry.RequireRead(Kb, "OtherPanel", "Events", "op-other-object");
            registry.RequireRead(Kb, "SyntheticPanel", "Rules", "op-other-part");
            string source = SyntheticEventsSource(out int lines, targetChars: 3000, lineCount: 60);
            var args = ReadArgs();

            Assert.Equal(1, ClearWithRead(registry, args, ThroughWireGuards(WorkerPartRead(source, lines), args)));

            Assert.False(registry.TryGet(Kb, "SyntheticPanel", "Events", out _));
            Assert.True(registry.TryGet(Kb, "OtherPanel", "Events", out var otherObject));
            Assert.Equal("op-other-object", otherObject.OperationId);
            Assert.True(registry.TryGet(Kb, "SyntheticPanel", "Rules", out var otherPart));
            Assert.Equal("op-other-part", otherPart.OperationId);
            Assert.Equal(2, registry.Count);
        }

        [Fact]
        public void IdentityBoundFenceIsNotClearedByANameOnlyRead()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-typed",
                targetGuid: null, targetEntityKey: null, targetType: "WebPanel",
                targetPath: null, expectedVersion: null);
            string source = SyntheticEventsSource(out int lines, targetChars: 3000, lineCount: 60);
            var nameOnly = ReadArgs();

            Assert.Equal(0, ClearWithRead(registry, nameOnly, ThroughWireGuards(WorkerPartRead(source, lines), nameOnly)));
            Assert.Equal(1, registry.Count);

            var typed = ReadArgs(type: "WebPanel");
            Assert.Equal(1, ClearWithRead(registry, typed, ThroughWireGuards(WorkerPartRead(source, lines), typed)));
        }

        [Fact]
        public void ReadObservedBeforeTheFenceWasRearmedCannotClearTheNewerFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-old");
            string source = SyntheticEventsSource(out int lines, targetChars: 3000, lineCount: 60);
            var args = ReadArgs();
            var covered = Program.FindCoveredRecoveryReads(registry, Kb, args);
            Assert.Single(covered);

            // A second timeout re-arms the same part while the read is still in flight.
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-new");

            Assert.Equal(0, Program.ConfirmMutationRecoveryRead(
                registry, Kb, ThroughWireGuards(WorkerPartRead(source, lines), args), covered));
            Assert.True(registry.TryGet(Kb, "SyntheticPanel", "Events", out var survivor));
            Assert.Equal("op-new", survivor.OperationId);

            Assert.Equal(1, ClearWithRead(registry, args, ThroughWireGuards(WorkerPartRead(source, lines), args)));
        }

        [Fact]
        public void WorkerPaginatedPageDoesNotClearTheFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-1");
            string page = SyntheticEventsSource(out int lines, targetChars: 6000, lineCount: 150);
            var args = ReadArgs();
            args["limit"] = 150;

            Assert.Equal(0, ClearWithRead(registry, args,
                ThroughWireGuards(WorkerPartRead(page, lines, explicitFull: false, paginated: true), args)));
            Assert.Equal(0, ClearWithRead(registry, args,
                ThroughWireGuards(WorkerPartRead(page, lines, explicitFull: false, offset: 150), args)));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void ReadTheGatewayHadToCutDoesNotClearTheFence()
        {
            // Beyond the 200000-character explicit-full-read budget the Gateway really drops
            // the tail, so the caller did not observe the whole part.
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-1");
            string huge = SyntheticEventsSource(out int lines, targetChars: 260000, lineCount: 6000);
            var args = ReadArgs();

            JToken final = ThroughWireGuards(WorkerPartRead(huge, lines), args);

            Assert.True(final["truncatedByGateway"]?.Value<bool>());
            Assert.True(final["source"]!.Value<string>()!.Length < huge.Length);
            Assert.Equal(0, ClearWithRead(registry, args, final));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void FullObjectReadClearsOnlyThePartsItCarriedOrEnumerated()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-events");
            registry.RequireRead(Kb, "SyntheticPanel", "Rules", "op-rules-empty");
            registry.RequireRead(Kb, "SyntheticPanel", "Variables", "op-not-read");
            var args = new JObject { ["name"] = "SyntheticPanel" };
            var fullObject = new JObject
            {
                ["code"] = "FullObjectRead",
                ["result"] = new JObject
                {
                    ["versionToken"] = "v1",
                    ["parts"] = new JObject { ["Events"] = "Event 'Synthetic'\nEndEvent\n" },
                    ["availableParts"] = new JArray("Events", "Rules")
                }
            };

            Assert.Equal(2, ClearWithRead(registry, args, ThroughWireGuards(fullObject, args)));

            Assert.False(registry.TryGet(Kb, "SyntheticPanel", "Events", out _));
            Assert.False(registry.TryGet(Kb, "SyntheticPanel", "Rules", out _));
            Assert.True(registry.TryGet(Kb, "SyntheticPanel", "Variables", out var untouched));
            Assert.Equal("op-not-read", untouched.OperationId);
        }

        [Fact]
        public void SizeGuardSentinelDoesNotClearTheFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-1");
            var args = ReadArgs();
            var oversize = new JObject
            {
                ["code"] = "FullObjectRead",
                ["result"] = new JObject
                {
                    ["versionToken"] = "v1",
                    ["parts"] = new JObject { ["Events"] = new string('E', 300000) }
                }
            };

            JToken final = ThroughWireGuards(oversize, args);

            Assert.NotNull(final["_meta"]?["truncated"]);
            Assert.Equal(0, ClearWithRead(registry, args, final));
            Assert.Equal(1, registry.Count);
        }

        // ---- reproduction of the field report ------------------------------------------

        [Fact]
        public void SlowDryRunPreviewDoesNotArmTheFence()
        {
            var registry = new MutationRecoveryRegistry();
            var timeout = new JObject { ["error"] = new JObject { ["retryable"] = true } };

            bool registered = Program.RegisterTimeoutRecoveryFence(
                registry, Kb, "genexus_edit", Edit(dryRun: true), "op-preview", timeout);

            Assert.False(registered);
            Assert.Equal(0, registry.Count);
            Assert.Null(timeout["reReadRequired"]);
            Assert.True(timeout["error"]?["retryable"]?.Value<bool>());
        }

        [Theory]
        [InlineData("patch")]
        [InlineData("ops")]
        [InlineData("full")]
        public void DryRunPreviewIsExemptForEveryEditMode(string mode)
        {
            var registry = new MutationRecoveryRegistry();

            Assert.False(Program.RegisterTimeoutRecoveryFence(
                registry, Kb, "genexus_edit", Edit(dryRun: true, mode: mode), "op-preview", new JObject()));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void PreviewOfAnotherObjectDoesNotRemoveAnExistingWriteFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-write");

            Assert.False(Program.RegisterTimeoutRecoveryFence(
                registry, Kb, "genexus_edit", Edit(dryRun: true), "op-preview", new JObject()));

            Assert.True(registry.TryGet(Kb, "SyntheticPanel", "Events", out var fence));
            Assert.Equal("op-write", fence.OperationId);
        }

        [Fact]
        public void ChangeSetCarryingDryRunIsNotTreatedAsEffectFree()
        {
            // changeSet.action=apply persists regardless of a top-level dryRun, so the explicit
            // preview list does not cover that route.
            var registry = new MutationRecoveryRegistry();
            var args = new JObject
            {
                ["name"] = "SyntheticPanel", ["part"] = "Events", ["dryRun"] = true,
                ["changeSet"] = new JObject { ["action"] = "apply" }
            };

            Assert.True(Program.RegisterTimeoutRecoveryFence(
                registry, Kb, "genexus_edit", args, "op-changeset", new JObject()));
            Assert.Equal(1, registry.Count);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("false", false)]
        [InlineData("yes", false)]
        [InlineData("1", false)]
        [InlineData("", false)]
        public void OnlyAnUnambiguousDryRunFlagWaivesTheFence(string spelling, bool exempt)
        {
            var args = Edit(dryRun: null);
            args["dryRun"] = spelling;

            Assert.Equal(exempt, OperationClassifier.IsEffectFreeDryRunPreview("genexus_edit", args));
            Assert.Equal(!exempt, Program.RegisterTimeoutRecoveryFence(
                new MutationRecoveryRegistry(), Kb, "genexus_edit", args, "op", new JObject()));
        }

        [Fact]
        public void DryRunAcceptedByAToolDoesNotMakeItEffectFree()
        {
            foreach (var tool in new[] { "genexus_edit_and_build", "genexus_variable", "genexus_io", "genexus_rename_across_kb" })
            {
                var args = new JObject { ["name"] = "SyntheticPanel", ["dryRun"] = true };
                Assert.False(OperationClassifier.IsEffectFreeDryRunPreview(tool, args), tool);
            }
            Assert.False(OperationClassifier.IsEffectFreeDryRunPreview("genexus_edit", null));
            Assert.False(OperationClassifier.IsEffectFreeDryRunPreview(null, Edit(dryRun: true)));
        }

        [Fact]
        public void WaivingTheFenceDoesNotChangeHowOtherConsumersClassifyTheCall()
        {
            // Cache and shaping decisions keep reading the classifier: a genexus_edit preview
            // is still a mutating call there and is never served from the semantic cache.
            JObject preview = Edit(dryRun: true);

            Assert.True(OperationClassifier.IsMutationCandidate("genexus_edit", preview));
            Assert.False(OperationClassifier.IsReadOnly("genexus_edit", preview));
            Assert.True(Program.IsMutatingTool("genexus_edit", preview));
            Assert.True(Program.IsMutationPreview(preview));
        }

        [Fact]
        public void CancelledPreviewIsNotFencedButCancelledWriteIs()
        {
            Assert.False(Program.ShouldFenceCancelledOperation("genexus_edit", Edit(dryRun: true)));
            Assert.True(Program.ShouldFenceCancelledOperation("genexus_edit", Edit(dryRun: false)));
            Assert.True(Program.ShouldFenceCancelledOperation("genexus_edit", Edit(dryRun: null)));
            Assert.True(Program.ShouldFenceCancelledOperation("genexus_io",
                new JObject { ["action"] = "import_part", ["name"] = "SyntheticPanel", ["dryRun"] = true }));
            Assert.False(Program.ShouldFenceCancelledOperation("genexus_query", new JObject()));
        }

        [Fact]
        public void CancelReplyAsksForARereadOnlyWhenTheOperationCouldHaveWritten()
        {
            var write = Program.BuildTrackerCancelPayload("op-1", "req-1", true, true, "genexus_edit", Edit(dryRun: false));
            Assert.Equal("Cancelled", write["status"]!.Value<string>());
            Assert.True(write["reReadRequired"]!.Value<bool>());

            var preview = Program.BuildTrackerCancelPayload("op-2", null, true, true, "genexus_edit", Edit(dryRun: true));
            Assert.Equal("Cancelled", preview["status"]!.Value<string>());
            Assert.False(preview["reReadRequired"]!.Value<bool>());
            Assert.Equal("op-2", preview["operationId"]!.Value<string>());
            Assert.True(preview["recycledWorker"]!.Value<bool>());

            var missing = Program.BuildTrackerCancelPayload("op-3", null, false, false, "genexus_edit", Edit(dryRun: false));
            Assert.Equal("NotFound", missing["status"]!.Value<string>());
            Assert.False(missing["reReadRequired"]!.Value<bool>());
        }

        [Fact]
        public void FullReadOfAnEventsPartOverTheEnvelopeLimitIsNotMarkedTruncated()
        {
            string source = SyntheticEventsSource(out int lines);
            var args = ReadArgs();

            JObject final = Assert.IsType<JObject>(ThroughWireGuards(WorkerPartRead(source, lines), args));

            Assert.True(source.Length > 81000);
            Assert.Equal(source, final["source"]!.Value<string>());
            Assert.Null(final["truncatedByGateway"]);
            Assert.Null(final["truncatedBy"]);
            Assert.NotEqual(true, final["isTruncated"]?.Value<bool>());
            Assert.Equal(lines, final["totalLines"]!.Value<int>());
        }

        [Fact]
        public void FullReadOverTheEnvelopeLimitClearsOnlyTheMatchingFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-preview-or-write");
            registry.RequireRead(Kb, "OtherPanel", "Events", "op-other-object");
            string source = SyntheticEventsSource(out int lines);
            var args = ReadArgs();

            Assert.Equal(1, ClearWithRead(registry, args, ThroughWireGuards(WorkerPartRead(source, lines), args)));

            Assert.False(registry.TryGet(Kb, "SyntheticPanel", "Events", out _));
            Assert.True(registry.TryGet(Kb, "OtherPanel", "Events", out _));
        }

        [Fact]
        public void LargeXmlMetadataPartReadInFullIsNotMarkedTruncated()
        {
            // Layout/WebForm/PatternInstance reads have a 180000-character field budget but
            // shared the 80000-character envelope limit, so a part that was returned whole
            // was still reported as cut.
            string xml = SyntheticEventsSource(out int lines, targetChars: 120000, lineCount: 2400);
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "WebForm", "op-1");
            var args = ReadArgs(part: "WebForm");

            JObject final = Assert.IsType<JObject>(ThroughWireGuards(
                WorkerPartRead(xml, lines, part: "WebForm"), args));

            Assert.Null(final["truncatedByGateway"]);
            Assert.Equal(xml.Length, final["source"]!.Value<string>()!.Length);
            Assert.Equal(1, ClearWithRead(registry, args, final));
        }

        [Fact]
        public void ReadWithoutVersionTokenDoesNotClearTheFence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, "SyntheticPanel", "Events", "op-1");
            string source = SyntheticEventsSource(out int lines);
            var args = ReadArgs();
            var unversioned = WorkerPartRead(source, lines, version: "");

            Assert.Equal(0, ClearWithRead(registry, args, ThroughWireGuards(unversioned, args)));
            Assert.Equal(1, registry.Count);
        }
    }
}
