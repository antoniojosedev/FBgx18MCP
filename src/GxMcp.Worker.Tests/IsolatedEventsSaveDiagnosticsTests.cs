using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using static GxMcp.Worker.Tests.IsolatedEventsSaveFlowTests;

namespace GxMcp.Worker.Tests
{
    public sealed class IsolatedEventsSaveDiagnosticsTests
    {
        private static IEnumerable<KeyValuePair<string, object>> Source(string part, params FakeSdkMessage[] messages)
        {
            var fake = new FakeSdkPart();
            fake.Messages.AddRange(messages);
            return new[] { new KeyValuePair<string, object>(part, fake) };
        }

        private static IsolatedEventsFailure Describe(Exception ex, string phase = "part-save", IEnumerable<KeyValuePair<string, object>> sources = null)
            => IsolatedEventsSaveDiagnostics.Describe(ex, phase, () => sources ?? Array.Empty<KeyValuePair<string, object>>());

        [Fact]
        public void Phases_AreAStableOrderedContract()
        {
            Assert.Equal(new[]
            {
                "preflight", "fresh-read", "version-conflict-check", "baseline-capture", "transaction-fresh-read",
                "target-recheck", "source-assignment", "part-save", "object-save", "pre-commit-verification",
                "commit", "post-commit-reread", "post-commit-verification", "completed"
            }, IsolatedEventsSavePhase.All);
            Assert.Equal(IsolatedEventsSavePhase.All.Length, IsolatedEventsSavePhase.All.Distinct().Count());
        }

        // -- sanitization --------------------------------------------------------

        [Theory]
        [InlineData(@"Cannot open C:\KBs\SampleKb\data.db now", "Cannot open <path> now")]
        [InlineData(@"Cannot open C:\Users\Jane Doe\AppData\Local\gx.log", "Cannot open <path>")]
        [InlineData(@"share \\build-host\kbs\SampleKb\x.xml missing", "share <path> missing")]
        [InlineData("see file:///C:/KBs/SampleKb/x.xml.", "see file:///<path>")]
        [InlineData("path 'D:/work/sample/x.cs' bad", "path '<path>' bad")]
        public void Sanitize_RemovesLocalPaths(string raw, string expected)
            => Assert.Equal(expected, IsolatedEventsSaveDiagnostics.Sanitize(raw));

        [Theory]
        // Placeholders are short and obviously fake; only their shape matters to the patterns.
        [InlineData("Server=db01;Database=KbSample;User Id=sa;Password=pw1", "pw1")]
        [InlineData("connectionstring=Server=db01;Pwd=pw1", "pw1")]
        [InlineData("Data Source=db01;Initial Catalog=KbSample", "db01")]
        [InlineData("failed with token=tk1", "tk1")]
        [InlineData("Authorization: Bearer xxxxxxxxxx", "xxxxxxxxxx")]
        [InlineData("used ghp_xxxxxxxxxxxxxxxxxxxx for push", "ghp_xxxxxxxxxxxxxxxxxxxx")]
        [InlineData("jwt eyJxxxxxxxxxxxx.yyyyyyyyyyyy.zz ok", "eyJxxxxxxxxxxxx")]
        public void Sanitize_RemovesCredentialsTokensAndConnectionParts(string raw, string secret)
        {
            string clean = IsolatedEventsSaveDiagnostics.Sanitize(raw);
            Assert.DoesNotContain(secret, clean);
            Assert.Contains("<redacted>", clean);
        }

        [Fact]
        public void Sanitize_KeepsTheSdkDiagnosticReadable()
        {
            const string sdk = "src0059: Esperando 'EndFor' (line: 12)";
            Assert.Equal(sdk, IsolatedEventsSaveDiagnostics.Sanitize(sdk));
        }

        [Fact]
        public void Sanitize_CollapsesLinesAndBoundsTheLength()
        {
            Assert.Equal("a b", IsolatedEventsSaveDiagnostics.Sanitize("a\r\n  b"));
            string longText = new string('x', 5000);
            Assert.True(IsolatedEventsSaveDiagnostics.Sanitize(longText).Length <= 600);
        }

        [Fact]
        public void Sanitize_HugeOrPathologicalInputIsBoundedAndFast()
        {
            // Drive prefixes without any separator after them are the worst case for the path pattern.
            string pathological = string.Concat(Enumerable.Repeat("C:\\" + new string('a', 60) + " ", 40000));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            string clean = IsolatedEventsSaveDiagnostics.Sanitize("password=pw1 " + pathological);

            Assert.True(watch.ElapsedMilliseconds < 5000, "sanitization took " + watch.ElapsedMilliseconds + " ms");
            Assert.DoesNotContain("pw1", clean);
            Assert.True(clean.Length <= 600);
        }

        [Fact]
        public void Sanitize_RemovesRawSourceCodeFromAMultilineMessage()
        {
            string message = "src0059 near\nFor each &Customer\n    msg(&Customer.Name)\nEndFor";
            string clean = IsolatedEventsSaveDiagnostics.Sanitize(message);
            Assert.DoesNotContain("&Customer", clean);
            Assert.DoesNotContain("msg(", clean);
        }

        [Fact]
        public void Sanitize_NeverThrowsOnNullOrEmpty()
        {
            Assert.Equal(string.Empty, IsolatedEventsSaveDiagnostics.Sanitize(null));
            Assert.Equal(string.Empty, IsolatedEventsSaveDiagnostics.Sanitize(string.Empty));
        }

        // -- failure description -------------------------------------------------

        [Fact]
        public void Describe_SanitizesTheMessageTheInnerMessagesAndTheSdkText()
        {
            var ex = new InvalidOperationException(@"load failed C:\Users\Jane Doe\kb\x.dll",
                new Exception("Server=db01;Password=pw1"));
            var failure = Describe(ex, sources: Source("Events", new FakeSdkMessage { Text = @"src0001: bad ref in C:\KBs\Sample\a.txt" }));

            string all = failure.Detail + string.Join("|", failure.InnerMessages) + failure.SdkMessages + failure.OriginalError;
            Assert.DoesNotContain("Jane", all);
            Assert.DoesNotContain("pw1", all);
            Assert.DoesNotContain("db01", all);
            Assert.DoesNotContain(@"C:\", all);
        }

        [Fact]
        public void Describe_BoundsInnerMessagesAndSdkMessages()
        {
            Exception ex = new Exception("leaf");
            for (int i = 0; i < 12; i++) ex = new Exception("level " + i, ex);
            var messages = Enumerable.Range(0, 40)
                .Select(i => new FakeSdkMessage { Text = "src" + (1000 + i) + ": problem " + i }).ToArray();

            var failure = Describe(ex, sources: Source("Events", messages));

            Assert.Equal(IsolatedEventsSaveDiagnostics.MaxInnerMessages, failure.InnerMessages.Count);
            Assert.Equal(IsolatedEventsSaveDiagnostics.MaxSdkMessages, failure.SdkMessages.Count);
            Assert.True(failure.SdkMessagesTruncated);
        }

        [Fact]
        public void Describe_FlattensAnAggregateAndPrefersAnInformativeChild()
        {
            var ex = new AggregateException("One or more errors occurred.",
                new Exception("Erro"), new InvalidOperationException("part rejected by validator"));

            var failure = Describe(ex);

            Assert.Equal(new[] { "Erro", "part rejected by validator" }, failure.InnerMessages);
            Assert.Equal("part rejected by validator", failure.Detail);
        }

        [Fact]
        public void Describe_AWarningNeverReplacesTheBareMessageNorFillsSourceErrors()
        {
            var failure = Describe(new Exception("Erro"),
                sources: Source("Events", new FakeSdkMessage { Text = "src0100: unused variable", Level = "Warning" }));

            Assert.Contains("generic error", failure.Detail);
            Assert.Single(failure.SdkMessages);
            Assert.Equal("Warning", failure.SdkMessages[0]["severity"]?.ToString());
            Assert.Empty(failure.SourceValidationErrors);
        }

        [Fact]
        public void Describe_AnErrorOnAnotherPartInformsTheMessageButNotTheSourceErrors()
        {
            var failure = Describe(new Exception("Erro"),
                sources: Source("Rules", new FakeSdkMessage { Text = "rul0001: rule broken" }));

            Assert.Equal("rul0001: rule broken", failure.Detail);
            Assert.Single(failure.SdkMessages);
            Assert.Equal("Rules", failure.SdkMessages[0]["part"]?.ToString());
            Assert.Empty(failure.SourceValidationErrors);
        }

        [Fact]
        public void Describe_TakesCodeLineAndColumnFromThePositionOrFromTheText()
        {
            var positioned = new FakeSdkMessage { Text = "src0059: Esperando 'EndFor'", Position = new FakeTextPosition { Line = 7, Char = 3 } };
            var textual = new FakeSdkMessage { Text = "src0216: 'Visible' propriedade invalida, line: 30" };
            var bare = new FakeSdkMessage { Text = "something unrelated" };

            var failure = Describe(new Exception("Erro"), sources: Source("Events", positioned, textual, bare));

            var entries = failure.SdkMessages.Cast<JObject>().ToArray();
            Assert.Equal(7, entries[0]["line"]?.Value<int>());
            Assert.Equal(3, entries[0]["column"]?.Value<int>());
            Assert.Equal("src0216", entries[1]["code"]?.ToString());
            Assert.Equal(30, entries[1]["line"]?.Value<int>());
            Assert.Null(entries[1]["column"]);
            Assert.Null(entries[2]["code"]);
            Assert.Null(entries[2]["line"]);
        }

        [Fact]
        public void Describe_DeduplicatesTheSameMessageSeenOnTheObjectAndItsPart()
        {
            var message = new FakeSdkMessage { Text = "src0059: Esperando 'EndFor'" };
            var failure = Describe(new Exception("Erro"), sources: Source("Events", message).Concat(Source("Events", message)));
            Assert.Single(failure.SdkMessages);
        }

        [Fact]
        public void Describe_ToleratesObjectsWithoutMessagesAndNullSources()
        {
            var sources = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("Events", new object()),
                new KeyValuePair<string, object>("Rules", null)
            };

            var failure = Describe(new Exception("boom"), sources: sources);

            Assert.Equal("boom", failure.Detail);
            Assert.Empty(failure.SdkMessages);
        }

        [Fact]
        public void Describe_SourcesThatThrow_AreReportedAndDoNotHideTheFailure()
        {
            var failure = IsolatedEventsSaveDiagnostics.Describe(new Exception("boom"), "part-save",
                () => throw new InvalidOperationException("no messages"));

            Assert.Equal("boom", failure.Detail);
            Assert.Equal("no messages", failure.SdkMessagesError);
        }

        [Fact]
        public void Describe_WithoutAnyExceptionDetail_StillNamesTheExceptionType()
        {
            var failure = Describe(new InvalidOperationException(""));
            Assert.Contains("System.InvalidOperationException", failure.Detail);
            Assert.Null(failure.OriginalError);
        }

        [Theory]
        [InlineData("Erro")]
        [InlineData("Error")]
        [InlineData("Erro, line: 1")]
        [InlineData("  erro  ")]
        public void Describe_TreatsEveryBareGenericFormAsUninformative(string message)
        {
            var failure = Describe(new Exception(message));
            Assert.Contains("generic error", failure.Detail);
            Assert.Equal(message.Trim(), failure.OriginalError);
        }

        // -- trace ---------------------------------------------------------------

        [Fact]
        public void Trace_StartsAtPreflight_AndUpdatesTheReceiptBeforeEachStage()
        {
            var receipt = new JObject();
            var trace = new IsolatedEventsSaveTrace(receipt);
            Assert.Equal("preflight", trace.Phase);

            trace.Enter("part-save");

            Assert.Equal("part-save", receipt["savePhase"]?.ToString());
            Assert.False(trace.Failed);
        }

        [Fact]
        public void Trace_AfterAFailure_KeepsSavePhaseAndMovesLaterStagesToVerificationPhase()
        {
            var receipt = new JObject();
            var trace = new IsolatedEventsSaveTrace(receipt);
            trace.Enter("object-save");
            trace.RecordFailure(new Exception("Erro"), null);

            trace.Enter("post-commit-reread");

            Assert.True(trace.Failed);
            Assert.Equal("object-save", trace.FailedPhase);
            Assert.Equal("object-save", receipt["savePhase"]?.ToString());
            Assert.Equal("post-commit-reread", receipt["verificationPhase"]?.ToString());
        }

        [Fact]
        public void Trace_SecondFailure_GoesToVerificationFailureWithoutTouchingTheFirst()
        {
            var receipt = new JObject();
            var trace = new IsolatedEventsSaveTrace(receipt);
            trace.Enter("part-save");
            trace.RecordFailure(new InvalidOperationException("first"), null);
            trace.Enter("post-commit-verification");

            trace.RecordFailure(new ArgumentException("second"), null);

            Assert.Equal(typeof(InvalidOperationException).FullName, receipt["exceptionType"]?.ToString());
            Assert.Equal("part-save", receipt["savePhase"]?.ToString());
            Assert.Equal(typeof(ArgumentException).FullName, receipt["verificationFailure"]["exceptionType"]?.ToString());
            Assert.Equal("post-commit-verification", receipt["verificationFailure"]["savePhase"]?.ToString());
        }

        [Fact]
        public void Trace_RequiresAReceipt()
            => Assert.Throws<ArgumentNullException>(() => new IsolatedEventsSaveTrace(null));
    }
}
