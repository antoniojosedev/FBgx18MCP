using System;
using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The pre-cut certificate of a recovery read (<c>gx-recovery-read/1</c>). Everything
    /// here is synthetic: the certificate is built from plain observations, so no Knowledge
    /// Base and no SDK object is involved. The properties under test are the ones the
    /// Gateway relies on to clear a post-timeout fence: the certificate says "complete"
    /// only when the SDK's whole part was read, its hash and version describe the same
    /// bytes, the identity is whole, nothing moved while it was read - and it never carries
    /// the content, so its size does not depend on the part's.
    /// </summary>
    public class RecoveryReadCertificateTests
    {
        private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";
        // The object-revision half of a version token (last-update ticks and parent).
        private const string RevisionStamp = "638000000000000000:root";

        private static string SyntheticPart(int chars, int lines)
        {
            int width = Math.Max(1, chars / lines - 1);
            var builder = new StringBuilder(chars + 16);
            for (int index = 0; index < lines; index++)
            {
                string text = "    &Counter" + (index % 97) + " += " + index + "; // synthetic";
                builder.Append(text.Length >= width ? text.Substring(0, width) : text.PadRight(width, '.'));
                builder.Append('\n');
            }
            return builder.ToString();
        }

        private static RecoveryReadObservation Observe(
            string text, int? offset = null, int? limit = 0, string part = "Events")
            => new RecoveryReadObservation
            {
                Name = "SyntheticPanel",
                Type = "WebPanel",
                Guid = Guid1,
                EntityKey = "d2b4a0c0-0000-0000-0000-000000000000:7",
                Path = "SyntheticPanel",
                RequestedPart = part,
                ResolvedPart = part,
                Text = text,
                VersionToken = text == null ? null : "638000000000000000:root:" + WriteService.ComputeContentFingerprint(text),
                RequestOffset = offset,
                RequestLimit = limit,
                WriteStampBefore = "w100",
                WriteStampAfter = "w100",
                RevisionBefore = "638000000000000000:root",
                RevisionAfter = "638000000000000000:root"
            };

        // ---- what is certified ---------------------------------------------------------

        [Fact]
        public void WholePartReadIsCompleteAndCarriesIdentityHashVersionAndSize()
        {
            string text = SyntheticPart(3000, 60);
            var certificate = RecoveryReadCertificate.Build(Observe(text));

            Assert.Equal("gx-recovery-read/1", certificate["schema"]!.ToString());
            Assert.True(certificate["complete"]!.Value<bool>());
            Assert.Equal("sdk-complete", certificate["coverage"]!.ToString());
            Assert.True(certificate["authoritative"]!.Value<bool>());
            Assert.Null(certificate["reason"]);
            Assert.Equal(60, certificate["totalLines"]!.Value<int>());
            Assert.Equal(text.Length, certificate["totalChars"]!.Value<int>());
            Assert.Equal(Encoding.UTF8.GetByteCount(text), certificate["utf8Bytes"]!.Value<long>());
            Assert.Null(certificate["totalBytes"]); // the read payload's field counts characters
            Assert.True(certificate["stable"]!.Value<bool>());
            Assert.Equal(WriteService.ComputeContentFingerprint(text), certificate["sha256"]!.ToString());
            Assert.EndsWith(":" + certificate["sha256"], certificate["versionToken"]!.ToString());
            Assert.Equal("w100", certificate["targetWriteStamp"]!.ToString());

            var identity = (JObject)certificate["identity"]!;
            Assert.Equal("SyntheticPanel", identity["name"]!.ToString());
            Assert.Equal("WebPanel", identity["type"]!.ToString());
            Assert.Equal(Guid1, identity["guid"]!.ToString());
            Assert.Equal("Events", identity["part"]!.ToString());
            Assert.Equal("Events", identity["requestedPart"]!.ToString());
        }

        [Theory]
        [InlineData(81500, 2306)]
        [InlineData(250000, 5000)]
        [InlineData(900000, 12000)]
        public void LargePartIsCertifiedWithoutCarryingTheContent(int chars, int lines)
        {
            // 81.5k fits the Gateway's explicit-full-read budget; the others do not. The
            // certificate is computed on the SDK's text before any such budget, so all of
            // them are complete, and it stays small and holds no part of the text.
            string text = SyntheticPart(chars, lines);
            var certificate = RecoveryReadCertificate.Build(Observe(text));

            Assert.True(certificate["complete"]!.Value<bool>());
            Assert.Equal(lines, certificate["totalLines"]!.Value<int>());
            string serialized = certificate.ToString(Newtonsoft.Json.Formatting.None);
            Assert.True(serialized.Length < 1500, "certificate is " + serialized.Length + " characters");
            Assert.DoesNotContain("&Counter", serialized);
            foreach (string contentField in new[] { "source", "content", "text", "code" })
                Assert.Null(certificate[contentField]);
        }

        [Fact]
        public void EmptyPartIsStillAWholeRead()
        {
            var certificate = RecoveryReadCertificate.Build(Observe(string.Empty));

            Assert.True(certificate["complete"]!.Value<bool>());
            Assert.Equal(0, certificate["totalLines"]!.Value<int>());
            Assert.Equal(WriteService.ComputeContentFingerprint(string.Empty), certificate["sha256"]!.ToString());
        }

        // ---- coverage: the caller's window must be the whole part ---------------------

        [Theory]
        [InlineData(null, 0, true)]      // limit=0: everything from the start
        [InlineData(0, 0, true)]
        [InlineData(0, 60, true)]        // an explicit window that covers every line
        [InlineData(0, 500, true)]
        [InlineData(0, 59, false)]       // one line short
        [InlineData(0, 10, false)]
        [InlineData(10, 0, false)]       // offset != 0 even with limit=0
        [InlineData(1, 500, false)]
        public void OnlyAWindowThatCoversEveryLineIsComplete(int? offset, int? limit, bool expectedComplete)
        {
            string text = SyntheticPart(3000, 60);
            var certificate = RecoveryReadCertificate.Build(Observe(text, offset, limit));

            Assert.Equal(expectedComplete, certificate["complete"]!.Value<bool>());
            if (!expectedComplete)
            {
                Assert.Equal("sdk-partial", certificate["coverage"]!.ToString());
                Assert.Equal("window-does-not-cover-part", certificate["reason"]!.ToString());
            }
        }

        [Fact]
        public void DefaultMcpPageOfALargePartIsNotComplete()
        {
            // No offset and no limit: the Worker would page at 200 lines / 16 KB.
            string text = SyntheticPart(81500, 2306);
            var certificate = RecoveryReadCertificate.Build(Observe(text, offset: null, limit: null));

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal("window-does-not-cover-part", certificate["reason"]!.ToString());
            Assert.Equal(2306, certificate["totalLines"]!.Value<int>());
        }

        [Fact]
        public void DefaultMcpPageOfASmallPartIsComplete()
        {
            var certificate = RecoveryReadCertificate.Build(Observe(SyntheticPart(3000, 60), offset: null, limit: null));

            Assert.True(certificate["complete"]!.Value<bool>());
        }

        // ---- every other clause refuses by default ------------------------------------

        [Fact]
        public void PartWithoutTextIsNotComplete()
        {
            var observation = Observe(null);
            observation.VersionToken = RevisionStamp + ":abc";

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal("sdk-unavailable", certificate["coverage"]!.ToString());
            Assert.Equal("part-has-no-text", certificate["reason"]!.ToString());
            Assert.Null(certificate["sha256"]!.Value<string>());
        }

        [Fact]
        public void ReadThatCouldNotBePerformedIsNotCompleteAndNotAuthoritative()
        {
            var observation = Observe(SyntheticPart(3000, 60));
            observation.UnavailableReason = "fresh-read-unavailable";

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.False(certificate["authoritative"]!.Value<bool>());
            Assert.Equal("sdk-unavailable", certificate["coverage"]!.ToString());
            Assert.Equal("fresh-read-unavailable", certificate["reason"]!.ToString());
        }

        [Theory]
        [InlineData(null, "version-token-unavailable")]
        [InlineData("", "version-token-unavailable")]
        [InlineData("638000000000000000:root:not-the-hash-of-this-text", "version-token-not-derived-from-text")]
        public void VersionTokenMustDescribeTheSameBytesThatWereHashed(string token, string reason)
        {
            var observation = Observe(SyntheticPart(3000, 60));
            observation.VersionToken = token;

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal(reason, certificate["reason"]!.ToString());
        }

        [Theory]
        [InlineData("name")]
        [InlineData("guid")]
        [InlineData("part")]
        public void IdentityMustBeWhole(string missing)
        {
            var observation = Observe(SyntheticPart(3000, 60));
            if (missing == "name") observation.Name = null;
            if (missing == "guid") observation.Guid = " ";
            if (missing == "part") observation.ResolvedPart = null;

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal("identity-incomplete", certificate["reason"]!.ToString());
        }

        [Theory]
        [InlineData("write-stamp")]
        [InlineData("revision")]
        [InlineData("no-write-stamp")]
        [InlineData("no-revision")]
        public void ObjectOrWriteStampMovingDuringTheReadInvalidatesIt(string movement)
        {
            var observation = Observe(SyntheticPart(3000, 60));
            if (movement == "write-stamp") observation.WriteStampAfter = "w200";
            if (movement == "revision") observation.RevisionAfter = "638000000000000001:root";
            if (movement == "no-write-stamp") { observation.WriteStampBefore = null; observation.WriteStampAfter = null; }
            if (movement == "no-revision") { observation.RevisionBefore = null; observation.RevisionAfter = null; }

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal("changed-during-read", certificate["reason"]!.ToString());
        }

        [Fact]
        public void AccentedTextReportsItsUtf8SizeApartFromItsCharacterCount()
        {
            string text = "// ação não é ASCII\n";

            var certificate = RecoveryReadCertificate.Build(Observe(text));

            Assert.Equal(text.Length, certificate["totalChars"]!.Value<int>());
            Assert.True(certificate["utf8Bytes"]!.Value<int>() > text.Length);
        }

        [Fact]
        public void RaceIsFlaggedIndependentlyOfWhichRefusalComesFirst()
        {
            // The part also fails the token/hash check; the race must still be visible so the
            // Gateway can veto every route that would clear a fence from this read.
            var observation = Observe(SyntheticPart(3000, 60));
            observation.VersionToken = RevisionStamp + ":not-the-hash-of-this-text";
            observation.WriteStampAfter = "w200";

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal("version-token-not-derived-from-text", certificate["reason"]!.ToString());
            Assert.False(certificate["stable"]!.Value<bool>());
        }

        [Fact]
        public void ReadThatNeverRanIsNotReportedAsARace()
        {
            var observation = Observe(SyntheticPart(3000, 60));
            observation.UnavailableReason = "fresh-read-unavailable";
            observation.WriteStampBefore = null;
            observation.WriteStampAfter = null;

            Assert.True(RecoveryReadCertificate.Build(observation)["stable"]!.Value<bool>());
        }

        [Fact]
        public void CertificateRecordsTheStampSampledBeforeTheRead()
        {
            // A write that lands after the read must show up as a newer stamp than the one
            // the certificate carries, so the recorded value is the first sample.
            var observation = Observe(SyntheticPart(3000, 60));
            observation.WriteStampBefore = "w100";
            observation.WriteStampAfter = "w200"; // a write landed during the read

            var certificate = RecoveryReadCertificate.Build(observation);

            Assert.Equal("w100", certificate["targetWriteStamp"]!.ToString());
            Assert.False(certificate["complete"]!.Value<bool>());
        }

        // ---- the per-target write stamp ----------------------------------------------

        [Fact]
        public void WriteStampAdvancesWhenAnyOfTheTargetsIsWritten()
        {
            string unique = "StampProbe" + Guid.NewGuid().ToString("N");
            string before = WriteService.TargetWriteStamp(unique, "Resolved" + unique);
            Assert.Equal("w0", before);

            WriteService.StampPerTargetWrite("Resolved" + unique);
            string after = WriteService.TargetWriteStamp(unique, "Resolved" + unique);

            Assert.NotEqual(before, after);
            Assert.Equal(after, WriteService.TargetWriteStamp(unique.ToUpperInvariant(), "resolved" + unique));
            Assert.Equal(before, WriteService.TargetWriteStamp(unique));
        }

        [Fact]
        public void WriteStampOfBlankTargetsIsTheEmptyStamp()
            => Assert.Equal("w0", WriteService.TargetWriteStamp(null, "", "  "));

        // ---- the dispatcher's transport flags ----------------------------------------

        [Fact]
        public void DispatcherReadsTheCertifyAndAuthoritativeFlagsByTheirExactNames()
        {
            Assert.True(CommandDispatcher.RequestsRecoveryCertificate(new JObject { ["certifyRecovery"] = true }));
            Assert.False(CommandDispatcher.RequestsRecoveryCertificate(new JObject { ["certifyRecovery"] = false }));
            Assert.False(CommandDispatcher.RequestsRecoveryCertificate(new JObject()));
            Assert.False(CommandDispatcher.RequestsRecoveryCertificate(new JObject { ["_certifyRecovery"] = true }));

            var authoritative = CommandDispatcher.BuildConditionalReadRequest(
                new JObject { ["ifUnchangedSince"] = "grc1.abc", ["requireAuthoritativeRead"] = true });
            Assert.True(authoritative.RequireAuthoritativeRead);
            var plain = CommandDispatcher.BuildConditionalReadRequest(
                new JObject { ["ifUnchangedSince"] = "grc1.abc" });
            Assert.False(plain.RequireAuthoritativeRead);
            Assert.Null(CommandDispatcher.BuildConditionalReadRequest(new JObject()));
        }

        [Fact]
        public void CertificationIsAnObservationForTheWriteDestinationGuard()
            => Assert.True(WriteDestinationGuard.IsObservation("read", "CertifyRecovery", new JObject()));
    }
}
