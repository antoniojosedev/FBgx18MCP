using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction-report item #2: full-mode write returned bare {"error":"Erro"} when the SDK
    // threw without diagnostics, while patch-mode surfaced the real "src0059: Esperando..." text.
    // The fix centralizes the "is this bare?" detection and the "use diagnostics if we have
    // anything better" enrichment in WritePolicy so both write paths share one rule.
    public class WritePolicyErrorEnrichmentTests
    {
        [Theory]
        [InlineData("Erro")]
        [InlineData("erro")]
        [InlineData(" Error ")]
        [InlineData("Erro, line: 1")]
        [InlineData("error,line:42")]
        [InlineData("")]
        [InlineData(null)]
        public void IsBareGenericError_RecognizesUninformativeMessages(string message)
        {
            Assert.True(WritePolicy.IsBareGenericError(message));
        }

        [Theory]
        [InlineData("src0059: Esperando 'EndFor' para fechar 'For Each'")]
        [InlineData("Part save failed: src0216: 'Visible' propriedade invalida")]
        [InlineData("KB not opened")]
        public void IsBareGenericError_RejectsInformativeMessages(string message)
        {
            Assert.False(WritePolicy.IsBareGenericError(message));
        }

        [Fact]
        public void PreferDetailedMessage_BareException_PicksSdkMessages()
        {
            string better = WritePolicy.PreferDetailedMessage(
                exceptionMessage: "Erro",
                sdkMessages: "src0059: Esperando 'EndFor' para fechar 'For Each'",
                issues: null);
            Assert.Equal("src0059: Esperando 'EndFor' para fechar 'For Each'", better);
        }

        [Fact]
        public void PreferDetailedMessage_BareException_FallsThroughBareSdkMessagesToIssues()
        {
            // When SDK messages are also bare "Erro", search the issues[] array for a description
            // with the real diagnostic. This is the common shape from SdkDiagnosticsHelper.GetDiagnostics.
            var issues = new JArray(
                new JObject { ["description"] = "Erro", ["severity"] = "Error" },
                new JObject { ["description"] = "src0216: 'Visible' propriedade invalida", ["severity"] = "Error" });
            string better = WritePolicy.PreferDetailedMessage("Erro", "Erro", issues);
            Assert.Equal("src0216: 'Visible' propriedade invalida", better);
        }

        [Fact]
        public void PreferDetailedMessage_InformativeException_LeavesItUntouched()
        {
            // We never overwrite a non-bare exception, even if diagnostics also have content.
            var issues = new JArray(new JObject { ["description"] = "something else" });
            string result = WritePolicy.PreferDetailedMessage(
                "Part save failed: src0301: invalid attribute",
                "noise",
                issues);
            Assert.Equal("Part save failed: src0301: invalid attribute", result);
        }

        [Fact]
        public void PreferDetailedMessage_NothingBetterAvailable_KeepsBareMessage()
        {
            string result = WritePolicy.PreferDetailedMessage("Erro", null, null);
            Assert.Equal("Erro", result);
        }
    }
}
