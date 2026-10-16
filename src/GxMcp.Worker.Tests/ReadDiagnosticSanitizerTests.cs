using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReadDiagnosticSanitizerTests
    {
        [Fact]
        public void SanitizeText_RedactsSyntheticSourceCodeInException()
        {
            string rawMessage = "Execution failed while parsing: Event Start\r\nmsg('SYNTHETIC_SOURCE_MARKER_479')\r\nEndEvent with error code 42.";
            string sanitized = ReadDiagnosticSanitizer.SanitizeText(rawMessage, out bool truncated);

            Assert.DoesNotContain("SYNTHETIC_SOURCE_MARKER_479", sanitized);
            Assert.Contains("[source code omitted]", sanitized);
            Assert.Contains("Execution failed while parsing:", sanitized);
            Assert.Contains("with error code 42.", sanitized);
        }

        [Theory]
        [InlineData("Error occurred: Sub 'DoSomething'\r\n&x = 1\r\nEndSub")]
        [InlineData("Failed at: For each Customer\r\nwhere CustomerId = 1\r\nEndFor")]
        [InlineData("Command failed: msg('SecretPayload123')")]
        [InlineData("Procedure crash: call(PRC_SecretOperation, &Token)")]
        public void SanitizeText_RedactsGeneXusStatementsAndCommands(string codePayload)
        {
            string sanitized = ReadDiagnosticSanitizer.SanitizeText(codePayload, out _);

            Assert.DoesNotContain("SecretPayload123", sanitized);
            Assert.DoesNotContain("PRC_SecretOperation", sanitized);
            Assert.Contains("[source code omitted]", sanitized);
        }

        [Fact]
        public void SanitizeException_PreservesDiagnosticMetadataWhileOmittingSource()
        {
            var ex = new InvalidOperationException("Failed at: Event 'Save'\r\nmsg('SECRET_CODE_LEAK')\r\nEndEvent (stage=resolve_object)");
            var sanitized = ReadDiagnosticSanitizer.SanitizeException(ex);

            Assert.NotNull(sanitized);
            Assert.Equal("System.InvalidOperationException", sanitized["type"]?.ToString());
            string message = sanitized["message"]?.ToString();
            Assert.NotNull(message);
            Assert.DoesNotContain("SECRET_CODE_LEAK", message);
            Assert.Contains("[source code omitted]", message);
            Assert.Contains("(stage=resolve_object)", message);
        }

        [Fact]
        public void SanitizeStackTrace_WhenOnlyFrameLimitExceeded_CapsFrames()
        {
            // 20 short frames (total length well under 2000 chars)
            string trace = string.Join(Environment.NewLine, Enumerable.Range(1, 20).Select(i => $"   at App.Step{i}()"));
            string sanitized = ReadDiagnosticSanitizer.SanitizeStackTrace(trace, out bool truncated);

            Assert.True(truncated);
            Assert.Contains("[TRUNCATED: max stack frames exceeded]", sanitized);
            Assert.DoesNotContain("[TRUNCATED: max stack length exceeded]", sanitized);

            var lines = sanitized.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(ReadDiagnosticSanitizer.MaxStackTraceFrames + 1, lines.Length);
        }

        [Fact]
        public void SanitizeStackTrace_WhenOnlyLengthLimitExceeded_CapsLength()
        {
            // 2 very long frames (< 15 frames, but length > 2000)
            string line1 = "   at App.Method1(" + new string('X', 1200) + ")";
            string line2 = "   at App.Method2(" + new string('Y', 1200) + ")";
            string trace = line1 + Environment.NewLine + line2;

            string sanitized = ReadDiagnosticSanitizer.SanitizeStackTrace(trace, out bool truncated);

            Assert.True(truncated);
            Assert.DoesNotContain("[TRUNCATED: max stack frames exceeded]", sanitized);
            Assert.Contains("[TRUNCATED: max stack length exceeded]", sanitized);
            Assert.True(sanitized.Length <= ReadDiagnosticSanitizer.MaxStackTraceLength + " ... [TRUNCATED: max stack length exceeded]".Length);
        }

        [Fact]
        public void SanitizeStackTrace_WhenBothFrameAndLengthLimitExceeded_EnforcesBothBounds()
        {
            // 25 long frames (exceeds 15 frames AND exceeds 2000 chars even after frame truncation)
            string trace = string.Join(Environment.NewLine, Enumerable.Range(1, 25).Select(i => $"   at Heavy.Framework.Module.Subsystem.Class.LongMethodName{i}(" + new string('Z', 200) + ")"));

            string sanitized = ReadDiagnosticSanitizer.SanitizeStackTrace(trace, out bool truncated);

            Assert.True(truncated);
            // Must enforce character length limit after frame truncation (issue #550)
            Assert.Contains("[TRUNCATED: max stack length exceeded]", sanitized);
            Assert.True(sanitized.Length <= ReadDiagnosticSanitizer.MaxStackTraceLength + " ... [TRUNCATED: max stack length exceeded]".Length);
        }
    }
}
