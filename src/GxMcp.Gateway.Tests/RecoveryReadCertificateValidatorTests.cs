using System;
using Newtonsoft.Json.Linq;
using Xunit;
using static GxMcp.Gateway.Tests.RecoveryReadFixtures;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Whether a Worker-built recovery certificate is evidence for one fence. The validator
    /// refuses by default: each clause below has a case that breaks only that clause, so a
    /// guard that stops refusing turns exactly one test red.
    /// </summary>
    public class RecoveryReadCertificateValidatorTests
    {
        private static readonly string Text = SyntheticPart(3000, 60);

        private static RecoveryRequirement Fence(
            string target = ObjectName, string part = PartName,
            string? guid = null, string? entityKey = null, string? type = null, string? path = null)
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, target, part, "op-fence", guid, entityKey, type, path, null);
            Assert.True(registry.TryGet(Kb, target, part, out var fence));
            return fence;
        }

        private static JObject Valid() => Certificate(Text, 60);

        private static void AssertRefused(string reason, JToken? certificate, RecoveryRequirement fence)
        {
            var verdict = RecoveryReadCertificateValidator.Evaluate(certificate, fence);
            Assert.False(verdict.Accepted);
            Assert.Equal(reason, verdict.Reason);
        }

        [Fact]
        public void CompleteCertificateOfTheFencedPartIsAccepted()
        {
            var verdict = RecoveryReadCertificateValidator.Evaluate(Valid(), Fence());

            Assert.True(verdict.Accepted);
            Assert.Equal("accepted", verdict.Reason);
        }

        // ---- the certificate itself ---------------------------------------------------

        [Fact]
        public void MissingOrForeignSchemaIsRefused()
        {
            AssertRefused("certificate-missing", null, Fence());
            AssertRefused("certificate-missing", new JValue("complete"), Fence());
            AssertRefused("schema-unsupported", Valid().With("schema", "gx-recovery-read/2"), Fence());
            AssertRefused("schema-unsupported", Valid().With("schema", null), Fence());
        }

        [Theory]
        [InlineData("complete", false, "certificate-incomplete")]
        [InlineData("complete", "true", "certificate-incomplete")]   // a string is not a verdict
        [InlineData("coverage", "sdk-partial", "coverage-not-sdk-complete")]
        [InlineData("coverage", "sdk-unavailable", "coverage-not-sdk-complete")]
        [InlineData("authoritative", false, "not-authoritative")]
        [InlineData("authoritative", "yes", "not-authoritative")]
        public void CertificateThatDoesNotClaimWholeAuthoritativeCoverageIsRefused(string field, object value, string reason)
            => AssertRefused(reason, Valid().With(field, value), Fence());

        [Fact]
        public void IncompleteCertificateCarriesTheWorkersOwnReason()
        {
            var incomplete = Valid().With("complete", false);
            incomplete["reason"] = "window-does-not-cover-part";

            var verdict = RecoveryReadCertificateValidator.Evaluate(incomplete, Fence());

            Assert.Equal("certificate-incomplete", verdict.Reason);
            Assert.Equal("window-does-not-cover-part", verdict.Detail);
        }

        [Theory]
        [InlineData("versionToken", null, "version-token-missing")]
        [InlineData("versionToken", "", "version-token-missing")]
        [InlineData("totalLines", null, "total-lines-invalid")]
        [InlineData("totalLines", -1, "total-lines-invalid")]
        [InlineData("totalLines", "60", "total-lines-invalid")]
        public void VersionAndSizeMustBePresentAndWellFormed(string field, object? value, string reason)
            => AssertRefused(reason, Valid().With(field, value), Fence());

        [Theory]
        [InlineData(null)]
        [InlineData("abc")]
        [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD")] // upper case
        [InlineData("0123456789012345678901234567890123456789012345678901234567890123456")] // 67 characters
        public void HashMustBeAWellFormedLowerCaseSha256EvenWhenTheTokenEndsInIt(string? hash)
        {
            // The token is made to agree with the malformed hash, so only the hash check can refuse.
            var certificate = Valid().With("sha256", hash);
            certificate["versionToken"] = "638000000000000000:root:" + hash;

            AssertRefused("hash-invalid", certificate, Fence());
        }

        [Fact]
        public void VersionTokenThatDoesNotEndInTheHashIsRefused()
        {
            // A client cannot make a token and a hash agree by editing one of them.
            var forged = Valid().With("versionToken", "638000000000000000:root:" + Sha256("other bytes"));

            AssertRefused("version-token-hash-mismatch", forged, Fence());
        }

        [Theory]
        [InlineData("identity", null, "identity-missing")]
        [InlineData("identity.guid", null, "identity-guid-missing")]
        [InlineData("identity.guid", " ", "identity-guid-missing")]
        public void IdentityMustBeWholeEvenForANameOnlyFence(string field, object? value, string reason)
            => AssertRefused(reason, Valid().With(field, value), Fence());

        // ---- the fence it may clear --------------------------------------------------

        [Fact]
        public void CertificateOfAnotherPartDoesNotClearTheFence()
        {
            var otherPart = Valid().With("identity.part", "Rules").With("identity.requestedPart", "Rules");

            AssertRefused("part-mismatch", otherPart, Fence(part: PartName));
        }

        [Fact]
        public void FenceNamedBySourceIsClearedWhenTheWorkerResolvedThatAliasToAnotherPart()
        {
            // The fence recorded the part as the write named it ("Source"); the Worker
            // resolved the alias. The requested part is what ties the two together.
            var resolved = Valid().With("identity.part", "Methods").With("identity.requestedPart", "Source");

            Assert.True(RecoveryReadCertificateValidator.Evaluate(resolved, Fence(part: "Source")).Accepted);
        }

        [Fact]
        public void CertificateOfAnotherObjectDoesNotClearANameOnlyFence()
        {
            AssertRefused("name-mismatch", Valid().With("identity.name", "OtherPanel"), Fence());
        }

        [Fact]
        public void NameMatchIsCaseInsensitive()
            => Assert.True(RecoveryReadCertificateValidator.Evaluate(
                Valid().With("identity.name", ObjectName.ToUpperInvariant()), Fence()).Accepted);

        [Fact]
        public void GuidBoundFenceNeedsTheSameGuidButSurvivesARename()
        {
            var fence = Fence(guid: ObjectGuid);

            AssertRefused("guid-mismatch",
                Valid().With("identity.guid", "11111111-2222-3333-4444-555555555555"), fence);

            // Same guid in another spelling, and the object was renamed since the fence.
            var renamed = Valid()
                .With("identity.guid", "{" + ObjectGuid.ToUpperInvariant() + "}")
                .With("identity.name", "RenamedPanel");
            Assert.True(RecoveryReadCertificateValidator.Evaluate(renamed, fence).Accepted);
        }

        [Fact]
        public void EntityKeyBoundFenceNeedsTheSameEntityKey()
        {
            var fence = Fence(entityKey: ObjectEntityKey);

            AssertRefused("entity-key-mismatch", Valid().With("identity.entityKey", "d2b4a0c0-0000-0000-0000-000000000000:8"), fence);
            AssertRefused("entity-key-mismatch", Valid().With("identity.entityKey", null), fence);
            Assert.True(RecoveryReadCertificateValidator.Evaluate(Valid(), fence).Accepted);
        }

        [Fact]
        public void TypedFenceIsNotClearedByAHomonymOfAnotherType()
        {
            var fence = Fence(type: "WebPanel");

            AssertRefused("type-mismatch", Valid().With("identity.type", "Procedure"), fence);
            AssertRefused("type-mismatch", Valid().With("identity.type", null), fence);
            Assert.True(RecoveryReadCertificateValidator.Evaluate(
                Valid().With("identity.type", "webpanel"), fence).Accepted);
        }

        [Fact]
        public void TypeIsEnforcedEvenWhenTheGuidMatches()
        {
            var fence = Fence(guid: ObjectGuid, type: "WebPanel");

            AssertRefused("type-mismatch", Valid().With("identity.type", "Procedure"), fence);
        }

        [Fact]
        public void PathOnlyFenceNeedsThePathButAGuidProvesIdentityWithoutIt()
        {
            var pathOnly = Fence(path: "Module.SyntheticPanel");

            AssertRefused("path-mismatch", Valid().With("identity.path", "Other.SyntheticPanel"), pathOnly);
            AssertRefused("path-mismatch", Valid().With("identity.path", null), pathOnly);
            Assert.True(RecoveryReadCertificateValidator.Evaluate(
                Valid().With("identity.path", "module.syntheticpanel"), pathOnly).Accepted);

            var guidAndPath = Fence(guid: ObjectGuid, path: "Module.SyntheticPanel");
            Assert.True(RecoveryReadCertificateValidator.Evaluate(
                Valid().With("identity.path", "Moved.SyntheticPanel"), guidAndPath).Accepted);
        }

        // ---- fences a text certificate must never clear ------------------------------

        [Fact]
        public void KbLevelFenceIsNeverClearedByAPartCertificate()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, MutationRecoveryRegistry.KbRecoveryTarget,
                MutationRecoveryRegistry.KbRecoveryPart, "op-manifest");
            Assert.True(registry.TryGet(Kb, MutationRecoveryRegistry.KbRecoveryTarget,
                MutationRecoveryRegistry.KbRecoveryPart, out var kbFence));

            AssertRefused("kb-level-fence", Valid(), kbFence);
        }

        [Fact]
        public void PropertiesFenceIsNeverClearedByATextCertificate()
        {
            var fence = Fence(part: "Properties");

            AssertRefused("properties-fence", Valid().With("identity.part", "Properties").With("identity.requestedPart", "Properties"), fence);
        }

        // ---- concurrency evidence ----------------------------------------------------

        [Fact]
        public void ConcurrentChangeIsReportedForTheChangedDuringReadReasonOrAnUnstableRead()
        {
            var raced = Valid().With("complete", false);
            raced["reason"] = "changed-during-read";
            var paginated = Valid().With("complete", false);
            paginated["reason"] = "window-does-not-cover-part";
            // An earlier refusal (here the window) hides the reason, never the race itself.
            var racedBehindAnotherReason = Valid().With("complete", false).With("stable", false);
            racedBehindAnotherReason["reason"] = "window-does-not-cover-part";

            Assert.True(RecoveryReadCertificateValidator.ReportsConcurrentChange(raced));
            Assert.True(RecoveryReadCertificateValidator.ReportsConcurrentChange(racedBehindAnotherReason));
            Assert.False(RecoveryReadCertificateValidator.ReportsConcurrentChange(paginated));
            Assert.False(RecoveryReadCertificateValidator.ReportsConcurrentChange(Valid()));
            Assert.False(RecoveryReadCertificateValidator.ReportsConcurrentChange(null));
        }

        [Fact]
        public void CertificateThatDoesNotStateStabilityIsRefused()
        {
            AssertRefused("changed-during-read", Valid().With("stable", false), Fence());
            AssertRefused("stability-not-certified", Valid().With("stable", null), Fence());
            AssertRefused("stability-not-certified", Valid().With("stable", "true"), Fence());
        }

        [Theory]
        [InlineData("versionToken", "638000000000000001:root:", "versionToken")]
        [InlineData("targetWriteStamp", "w200", "targetWriteStamp")]
        [InlineData("totalLines", 61, "totalLines")]
        [InlineData("identity.guid", "11111111-2222-3333-4444-555555555555", "identity.guid")]
        public void TwoCertificatesAreTheSameStateOnlyIfNothingMovedBetweenThem(string field, object value, string expected)
        {
            var first = Valid();
            var second = field == "versionToken"
                ? first.With("versionToken", "638000000000000001:root:" + first["sha256"])
                : first.With(field, value);

            Assert.False(RecoveryReadCertificateValidator.SameObservedState(first, second, out string difference));
            Assert.Equal(expected, difference);
        }

        [Fact]
        public void IdenticalCertificatesAreTheSameStateAndABrokenHashIsNot()
        {
            var first = Valid();

            Assert.True(RecoveryReadCertificateValidator.SameObservedState(first, Valid(), out _));
            Assert.False(RecoveryReadCertificateValidator.SameObservedState(
                first, Valid().With("sha256", Sha256("other bytes")), out string difference));
            Assert.Equal("sha256", difference);
            Assert.False(RecoveryReadCertificateValidator.SameObservedState(first, null, out difference));
            Assert.Equal("certificate-missing", difference);
        }
    }
}
