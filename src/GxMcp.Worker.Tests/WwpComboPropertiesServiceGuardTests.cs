using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // set_combo_properties talks to the SDK and the KB, so its sequence is held by source guards the
    // way the other typed WWP writes are: the order of the steps is the contract, and a reordering
    // is what these fail on. The live behaviour was proved against a disposable KB.
    public class WwpComboPropertiesServiceGuardTests
    {
        private static string Source(params string[] relative) =>
            File.ReadAllText(Path.Combine(TestFixtures.FindRepoRoot(), Path.Combine(relative)));

        private static string Combo => Source("src", "GxMcp.Worker", "Services", "WwpActionService.ComboProperties.cs");

        private static int After(string source, string marker, int from)
        {
            int index = source.IndexOf(marker, from, StringComparison.Ordinal);
            Assert.True(index >= 0, "missing after offset " + from + ": " + marker);
            return index;
        }

        [Fact]
        public void TheWrite_ChangesThroughTheSdkCommand_SavesThenRereadsFreshly_ThenProjectsThenRereadsAgain()
        {
            string source = Combo;
            int write = After(source, "private string WriteComboProperties", 0);
            int update = After(source, "ExecuteNativeUpdate(currentPart", write);
            int apply = After(source, "WwpComboPlanner.Apply(lockedElement.Element, plan)", update);
            int save = After(source, "SaveNativePattern(currentInstance, currentPart)", apply);
            int fresh = After(source, "ReadFreshInstance(currentInstance, out KBObject persistedInstance)", save);
            int verify = After(source, "VerifyPersistedComboProperties(currentXml, persistedXml", fresh);
            int project = After(source, "WwpProjectionHelper.TryProjectHostOntoParent(parent", verify);
            int projection = After(source, "VerifyComboProjection(parentWebFormBefore, projectedWebForm)", project);
            int final = After(source, "ReadFreshInstance(currentInstance, out KBObject finalInstance)", projection);
            int success = After(source, "code: \"WwpComboPropertiesUpdated\"", final);

            Assert.True(success > final);
        }

        [Fact]
        public void TheSavedInstanceIsTheOwnerOfTheChangedPart_NotAnotherCopyOfTheObject()
        {
            string source = Combo;
            int write = After(source, "private string WriteComboProperties", 0);

            // For a Transaction/WebPanel target the read and the envelope resolve different copies of
            // the WorkWithPlus object; the change lives in the envelope's part, so that owner is saved.
            Assert.Matches(new Regex(@"BuildPatternPartEnvelope\(lockedTarget, ""PatternInstance"", readXml, PatternRegistry\.WorkWithPlusPatternId,\s+out KBObject currentInstance, out KBObjectPart currentPart\)"),
                source.Substring(write));
            Assert.DoesNotContain("out KBObject currentInstance, out _", source);
        }

        [Fact]
        public void AWrite_NeedsTheVersionToken_AndComparesItUnderTheTargetLock()
        {
            string source = Combo;
            int required = After(source, "code: \"ExpectedVersionRequired\"", 0);
            int lockAt = After(source, "lock (WriteService.AcquirePerTargetLock(target))", required);
            int stale = After(source, "BuildWwpStaleObject(target, expectedVersion, currentVersion", lockAt);
            int replan = After(source, "WwpComboPlanner.Plan(lockedElement.Element", stale);

            Assert.True(replan > stale, "the plan is recomputed under the lock, after the token check");
            Assert.Matches(new Regex(@"if \(!string\.Equals\(expectedVersion, currentVersion, StringComparison\.Ordinal\)\)\s+return BuildWwpStaleObject\(target, expectedVersion, currentVersion"), source);
            Assert.Contains("!dryRun && string.IsNullOrWhiteSpace(expectedVersion)", source);
        }

        [Fact]
        public void TheSnapshotsAreCaptured_BeforeAnythingIsChanged_AndIncompleteOnesRefuse()
        {
            string source = Combo;
            int snapshots = After(source, "CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore)", 0);
            int guard = After(source, "WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots)", snapshots);
            int update = After(source, "ExecuteNativeUpdate(currentPart", guard);

            Assert.True(update > guard);
            Assert.Contains("code: \"WwpSnapshotRequired\"", source);
        }

        [Fact]
        public void TheRollbackIsOnlyCalledVerified_WhenAFreshRereadMatchesTheSnapshotHashes()
        {
            string source = Combo;
            int restore = After(source, "private JObject RestoreAfterComboFailure", 0);
            string body = source.Substring(restore);

            Assert.Contains("RestoreSnapshots(instance, part, nativeBytes, patternXml, parent, webForm, applyOnSaveBefore)", body);
            Assert.Contains("ReadPatternPartXmlFresh(instance, \"PatternInstance\"", body);
            Assert.Contains("ContentMatches(freshPattern, patternXml)", body);
            Assert.Contains("ContentMatches(freshWebForm, webForm)", body);
            Assert.Contains("rollback[\"rollbackVerified\"] = patternMatches && webFormMatches", body);
            // The failure envelope derives persisted/partialPersistence from that verified flag, not from the attempt.
            Assert.Contains("[\"persisted\"] = saveAttempted && !restored", source);
            Assert.Contains("bool restored = (bool?)rollback[\"rollbackVerified\"] == true;", source);
        }

        [Fact]
        public void NoKbLifecycleOperationIsStartedByTheAction()
        {
            string source = Combo;

            foreach (string forbidden in new[] { "ISpecifierService", "Specify(", "Generate(", "BuildAll", "Rebuild", "Reorg", "RunObject", "genexus_lifecycle", "Execute(" })
                Assert.DoesNotContain(forbidden, source);
            Assert.Contains("[\"lifecycleExecuted\"] = false", source);
        }

        [Fact]
        public void AProjectionThatDidNotRun_FailsTheWriteAndRestores()
        {
            string source = Combo;

            Assert.Matches(new Regex(@"if \(!projected\)\s+throw new WwpComboException\(""WwpComboProjectionFailed"""), source);
            Assert.Matches(new Regex(@"if \(!\(bool\)projection\[""confirmed""\]\)\s+throw new WwpComboException\(""WwpComboProjectionNotConfirmed"""), source);
        }

        [Fact]
        public void ThePreviewAndTheNoChangeAnswer_ReturnBeforeAnythingIsWritten()
        {
            string source = Combo;

            Assert.Matches(new Regex(@"if \(dryRun\)\s+return McpResponse\.Ok\(target: target, code: ""WwpComboPropertiesDryRun"""), source);
            Assert.Matches(new Regex(@"if \(!plan\.AnyChange\)\s+return McpResponse\.Ok\(target: target, code: ""WwpComboPropertiesNoChange"""), source);
        }

        [Fact]
        public void TheOperationIsReachableFromRun_AndIsDispatchedBeforeTheGenericXmlPath()
        {
            string run = Source("src", "GxMcp.Worker", "Services", "WwpActionService.cs");
            int dispatch = run.IndexOf("if (IsComboPropertyOperation(operation))", StringComparison.Ordinal);
            int generic = run.IndexOf("JObject mutation = Apply(afterDocument, operation, args, ResolveProcedure);", StringComparison.Ordinal);

            Assert.True(dispatch > 0 && dispatch < generic);
            Assert.Contains("RunComboPropertyOperation(target, requestedObject, instance, instancePart, xml, args)", run);
        }

        [Fact]
        public void TheTargetIsAnElementWhoseSpecificationCarriesTheComboProperties_AmbiguityIsRefusedNotGuessed()
        {
            string source = Combo;
            int locate = After(source, "private static JObject LocateComboTarget", 0);
            int filter = After(source, ".Where(i => nativeMatches[i].HasProperty(WwpComboProperties.All[0].SdkName))", locate);
            int notAControl = After(source, "code\"] = \"ComboElementNotAControl\"", filter);
            int ambiguous = After(source, "\"ComboAttributeAmbiguous\"", notAControl);
            int found = After(source, "found = new ComboTarget", ambiguous);

            Assert.True(found > ambiguous, "an attribute that appears twice is refused with the element paths, never picked");
            Assert.Contains("if (indexes.Count != 1)", source);
            Assert.Contains("Ordinal = indexes[0]", source);
            Assert.Contains("nativeMatches.Count != xmlMatches.Count", source);
        }

        [Fact]
        public void TheDryRunNeverWrites_AndReportsTheSdkPathItUsed()
        {
            string source = Combo;
            int dry = After(source, "code: \"WwpComboPropertiesDryRun\"", 0);
            int writeCall = After(source, "return WriteComboProperties(", dry);
            string dryBlock = source.Substring(dry, writeCall - dry);

            Assert.Contains("[\"savePathExercised\"] = false", dryBlock);
            Assert.Contains("[\"persisted\"] = false", dryBlock);
            Assert.DoesNotContain("SaveNativePattern", dryBlock);
            Assert.DoesNotContain("ExecuteNativeUpdate", dryBlock);
        }
    }
}
