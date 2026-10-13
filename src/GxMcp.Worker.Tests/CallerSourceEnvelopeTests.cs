using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #491, follow-up to #287: `mode=callers` returned
    // `CallerSitesUnconfirmed` with `callSiteCount: 0` for every target while
    // `sdkCallers` was fully populated. #287 fixed the call *syntax*; the scan
    // was never reaching the syntax.
    //
    // Two layers had to come off before it was reachable, and both were silent:
    //
    // 1. `ReadObjectSource` answers with a JSON envelope, not the part text. The
    //    scan searched `{"part":"Source","source":"...","readFreshness":{...}}`
    //    for call syntax.
    // 2. `ScanSdkCallerSources` reads with the default `client: "ide"`, and
    //    `ObjectService.ProcessTextResponse` only returns plain text for
    //    `client == "mcp"` - every other client gets a base64 body. So the
    //    envelope also wrapped an encoded blob.
    //
    // Live reproduction on C:\KBs\KBTeste (GeneXus 18.0.10, rebuilt Worker
    // hot-swapped):
    //   callers of PCalculaDesconto -> callSiteCount: 0, code
    //   `CallerSitesUnconfirmed`, sdkCallers: ["Test_PCalculaDesonto"] - while
    //   Test_PCalculaDesconto's Source plainly reads
    //   `PCalculaDesconto.Call(&ValorTotal, &TipoCliente)`.
    public class CallerSourceEnvelopeTests
    {
        /// The `client: "ide"` read: an envelope wrapping a base64 body. This is
        /// the shape the scan actually receives in production.
        private const string PartText =
            "// call the target\nCall(McpTst492Valor)\n";

        private static string Envelope(bool base64)
        {
            string body = base64
                ? Convert.ToBase64String(new UTF8Encoding(false).GetBytes(PartText))
                : PartText;

            return new JObject
            {
                ["part"] = "Source",
                ["isTruncatedByWorker"] = false,
                ["truncated"] = false,
                ["isBase64"] = base64,
                ["source"] = body,
                ["readFreshness"] = new JObject { ["origin"] = "sdk-object-state" }
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// A read that answers with plain text, as an injected test reader does.
        private const string EnvelopeWithSource =
            "{\"part\":\"Source\",\"source\":\"// call the target\\nCall(McpTst492Valor)\\n\",\"isBase64\":false,\"readFreshness\":{\"origin\":\"sdk-refresh\"}}";

        [Fact]
        public void Envelope_IsUnwrappedToItsSourceText()
        {
            var source = AnalyzeService.ExtractReadEnvelopeSource(EnvelopeWithSource);

            Assert.NotNull(source);
            Assert.Contains("Call(McpTst492Valor)", source);
            Assert.False(source.Contains("readFreshness"), "the envelope must not reach the call parser");
            Assert.False(source.Contains("isBase64"), "the envelope must not reach the call parser");
        }

        [Fact]
        public void PlainText_IsReturnedUnchanged()
        {
            // A caller-source reader injected for tests answers with the bare part
            // text, and so must keep working.
            Assert.Equal("Call(McpTst492Valor)", AnalyzeService.ExtractReadEnvelopeSource("Call(McpTst492Valor)"));
        }

        [Fact]
        public void EnvelopeCarryingTheCall_ProducesACallSite()
        {
            // The end-to-end shape of the bug: the scan must now find the call.
            // The call sits on line 2, after a comment line, so this also pins
            // that the comment does not swallow it.
            var source = AnalyzeService.ExtractReadEnvelopeSource(EnvelopeWithSource);
            Assert.NotNull(source);

            // `Call(Target)` parses as Callee="Call" with the target as its first
            // argument, and CallSiteMatcher is what resolves that legacy
            // invocation form. Asserting on `Callee` alone would have tested a
            // property the scan never reads.
            var calls = SourceParser.ParseCalls(source, false);
            Assert.Single(calls);
            Assert.Equal(2, calls[0].LineNumber);
            Assert.True(
                CallSiteMatcher.Matches(calls[0], "McpTst492Valor", "McpTst492Valor"),
                "the call inside the envelope must match the target");
        }

        [Theory]
        [InlineData("{\"error\":{\"code\":\"ObjectNotFound\"}}")]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"X\"}}")]
        [InlineData("{\"part\":\"Source\",\"source\":\"\"}")]
        [InlineData("{\"part\":\"Source\",\"source\":null}")]
        [InlineData("{\"part\":\"Source\"}")]
        [InlineData("{\"part\":\"Source\",\"source\":\"Call(A)\",\"isBase64\":true}")]
        [InlineData("{\"part\":\"Source\",\"source\":\"not base64 at all!!\",\"isBase64\":true}")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void UnusablePayloads_AreRejectedRatherThanScanned(string payload)
        {
            // Each of these must be "nothing to scan". Scanning the JSON itself is
            // what produced callSiteCount 0 for every target in the KB.
            Assert.Null(AnalyzeService.ExtractReadEnvelopeSource(payload));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ABase64Body_IsDecodedToTheSameTextAsAPlainOne(bool base64)
        {
            // Both representations must reach the parser as the same text, or
            // the scan's result would depend on which client the read used.
            var source = AnalyzeService.ExtractReadEnvelopeSource(Envelope(base64));

            Assert.NotNull(source);
            Assert.Equal(PartText, source);
        }

        [Fact]
        public void ABase64BodyCarryingTheCall_ProducesACallSite()
        {
            // The production shape end to end: envelope, encoded body, call
            // syntax only reachable after both layers come off.
            var source = AnalyzeService.ExtractReadEnvelopeSource(Envelope(true));
            Assert.NotNull(source);

            var calls = SourceParser.ParseCalls(source, false);
            Assert.Single(calls);
            Assert.True(CallSiteMatcher.Matches(calls[0], "McpTst492Valor", "McpTst492Valor"));
        }

        [Fact]
        public void TheMemberCallForm_IsMatchedThroughTheBase64Body()
        {
            // What the live reproduction actually contained:
            //   PCalculaDesconto.Call(&ValorTotal, &TipoCliente)
            // which ParseCalls reports as Callee="PCalculaDesconto.Call" and
            // CallSiteMatcher resolves to the object.
            const string text = "Sub 'Test_HappyPath'\n    &ValorTotal = 100\n    PCalculaDesconto.Call(&ValorTotal, &TipoCliente)\n";
            var payload = "{\"part\":\"Source\",\"isBase64\":true,\"source\":\""
                        + Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text))
                        + "\"}";

            var source = AnalyzeService.ExtractReadEnvelopeSource(payload);
            Assert.NotNull(source);

            var calls = SourceParser.ParseCalls(source, false);
            Assert.Contains(calls, c => CallSiteMatcher.Matches(c, "PCalculaDesconto", "PCalculaDesconto"));
        }

        [Fact]
        public void MalformedJson_IsRejected()
        {
            Assert.Null(AnalyzeService.ExtractReadEnvelopeSource("{not json"));
        }

        [Fact]
        public void ScanSdkCallerSources_FindsTheCallThroughAnEnvelope()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Caller",
                    Type = "Procedure",
                    Calls = new List<string> { "McpTst492Valor" },
                    CalledBy = new List<string> { "Caller" }
                }
            });
            index.MarkIndexComplete(1);

            var svc = new AnalyzeService(index, null, null, (name, part) => Envelope(true));

            var sites = svc.ScanSdkCallerSources(
                new[] { "Caller" }, "McpTst492Valor", "McpTst492Valor");

            Assert.True(
                sites.Count > 0,
                "the scan must find the call inside the read envelope");
            Assert.Equal("Caller", sites[0]["object"]?.ToString());
            Assert.Equal(2, sites[0]["line"]?.Value<int>());
        }

        [Fact]
        public void ScanSdkCallerSources_ReturnsNothingForAnErrorEnvelope()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Caller",
                    Type = "Procedure",
                    Calls = new List<string> { "Target" },
                    CalledBy = new List<string> { "Caller" }
                }
            });
            index.MarkIndexComplete(1);

            var svc = new AnalyzeService(index, null, null,
                (name, part) => "{\"error\":{\"code\":\"ObjectNotFound\"}}");

            var sites = svc.ScanSdkCallerSources(new[] { "Caller" }, "Target", "Target");

            Assert.Empty(sites);
        }
    }
}
