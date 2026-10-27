using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpGridVariableProjectionRefusalTests
    {
        [Fact]
        public void AddGridVariableRefusesRealWritesAfterValidationAndBeforeAnyMutation()
        {
            string writer = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "WwpActionService.GridVariable.cs")));
            int validation = writer.IndexOf("JObject previewMutation = ApplyAddGridVariableXml", StringComparison.Ordinal);
            int stale = writer.IndexOf("if (!IsExpectedVersion(expectedVersion, versionToken))", StringComparison.Ordinal);
            int preview = writer.IndexOf("if (dryRun)", StringComparison.Ordinal);
            int refusal = writer.IndexOf("return McpResponse.Err(code: \"WwpProjectionVerificationUnavailable\"", StringComparison.Ordinal);
            int end = writer.IndexOf("internal static DeclaredVariable FindDeclaredVariable", StringComparison.Ordinal);
            Assert.True(validation >= 0 && stale > validation && preview > stale && refusal > preview && end > refusal);
            string entrypoint = writer.Substring(0, end);
            Assert.EndsWith("});\n\n            ", writer.Substring(preview, refusal - preview));
            string refusalBlock = writer.Substring(refusal, end - refusal);
            Assert.Contains("[\"persisted\"] = false", refusalBlock);
            Assert.Contains("[\"saved\"] = false", refusalBlock);
            Assert.DoesNotContain("if (", refusalBlock);
            Assert.DoesNotContain("AcquirePerTargetLock", entrypoint);
            Assert.DoesNotContain("CaptureSnapshots", entrypoint);
            Assert.DoesNotContain("ApplyNativeAddGridVariable", entrypoint);
            Assert.DoesNotContain("SaveNativePattern", entrypoint);
            Assert.DoesNotContain("TryProjectHostOntoParent", entrypoint);
        }
    }
}
