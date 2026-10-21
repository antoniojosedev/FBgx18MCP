using System;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class LogRedactionBoundedWorkTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NonCredentialRunsDoNotRestartThePrefixAtEveryCharacter(bool whitespace)
        {
            string input = whitespace ? "password" + new string(' ', 65536) + "not-a-separator" : new string('x', 65536);
            // Pin the pattern's work as well as the production timeout fallback: returning
            // a placeholder after a timeout must not hide quadratic scanning of ordinary logs.
            string result = Regex.Replace(input, LogRedaction.Pattern, _ => "<redacted>",
                RegexOptions.None, TimeSpan.FromSeconds(1));
            Assert.Equal(input, result);
        }

        [Theory]
        [InlineData("\"DbPassword\" : \"SYNTHETIC-CREDENTIAL\"", "\"DbPassword : <redacted>")]
        [InlineData("DbPassword  '  = 'SYNTHETIC-CREDENTIAL'", "DbPassword  = <redacted>")]
        [InlineData(".DbPassword=SYNTHETIC-CREDENTIAL", ".DbPassword=<redacted>")]
        [InlineData("-pass=SYNTHETIC-CREDENTIAL", "-pass=<redacted>")]
        public void LargeDiagnosticStillMasksCredentialsWithoutDamagingItsPrefix(string assignment, string expected)
        {
            string prefix = new string('x', 65536) + " ";
            Assert.Equal(prefix + expected, LogRedaction.Redact(prefix + assignment));
        }
    }
}
