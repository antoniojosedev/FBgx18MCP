using System;
using System.IO;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// v2.6.6 Stream C FR#24 — every Logger line carries an ISO-8601 timestamp
    /// with milliseconds + offset and a [phase] tag drawn from Logger.CurrentPhase.
    /// Capturing the synchronous stderr mirror (preserved by Logger for the Gateway
    /// capture path) is the cheapest way to assert formatting without racing the
    /// async file writer.
    /// </summary>
    [Collection("StderrCapture")]
    public class LoggerPhaseTagTests : IDisposable
    {
        private readonly TextWriter _originalErr;
        private readonly StringWriter _capture;
        private readonly string _priorPhase;

        public LoggerPhaseTagTests()
        {
            _priorPhase = Logger.CurrentPhase;
            _originalErr = Console.Error;
            _capture = new StringWriter();
            Console.SetError(_capture);
        }

        public void Dispose()
        {
            Console.SetError(_originalErr);
            Logger.CurrentPhase = _priorPhase;
            _capture.Dispose();
        }

        private string LastLineWithoutMirrorPrefix()
        {
            // Logger writes "[Worker Log] " + the formatted line to stderr; strip
            // the mirror prefix so the regex matches the on-disk format.
            const string Mirror = "[Worker Log] ";
            string buffer = _capture.ToString();
            var lines = buffer.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);
            string line = lines[lines.Length - 1];
            if (line.StartsWith(Mirror)) line = line.Substring(Mirror.Length);
            return line;
        }

        [Fact]
        public void Info_WithPhaseSet_EmitsIsoTimestampAndPhaseTag()
        {
            // Arrange
            Logger.CurrentPhase = "Specifying";

            // Act
            Logger.Info("hello");

            // Assert
            var line = LastLineWithoutMirrorPrefix();
            var rx = new Regex(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}\] \[INFO\] \[Specifying\] hello$");
            Assert.Matches(rx, line);
        }

        [Fact]
        public void Info_WithNullPhase_EmitsEmptyBracketsTag()
        {
            // Arrange
            Logger.CurrentPhase = null;

            // Act
            Logger.Info("plain message");

            // Assert
            var line = LastLineWithoutMirrorPrefix();
            var rx = new Regex(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}\] \[INFO\] \[\] plain message$");
            Assert.Matches(rx, line);
        }

        [Fact]
        public void Warn_And_Error_LevelsAlsoCarryPhaseTag()
        {
            // Arrange
            Logger.CurrentPhase = "Compiling";

            // Act
            Logger.Warn("be careful");
            var warnLine = LastLineWithoutMirrorPrefix();

            Logger.Error("kaboom");
            var errLine = LastLineWithoutMirrorPrefix();

            // Assert
            Assert.Matches(new Regex(@"\[WARN\] \[Compiling\] be careful$"), warnLine);
            Assert.Matches(new Regex(@"\[ERROR\] \[Compiling\] kaboom$"), errLine);
        }

        [Fact]
        public void PhaseTag_IsAsyncLocal_DoesNotLeakAcrossClearedScope()
        {
            // Arrange
            Logger.CurrentPhase = "Generating";
            Logger.Info("inside scope");
            var inScope = LastLineWithoutMirrorPrefix();

            // Act — caller resets phase to null; the next line MUST drop back to "[]".
            Logger.CurrentPhase = null;
            Logger.Info("outside scope");
            var outScope = LastLineWithoutMirrorPrefix();

            // Assert
            Assert.Contains("[Generating] inside scope", inScope);
            Assert.Contains("[] outside scope", outScope);
        }
    }
}
