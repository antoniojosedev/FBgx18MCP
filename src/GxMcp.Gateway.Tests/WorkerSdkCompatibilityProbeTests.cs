using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    public class WorkerSdkCompatibilityProbeTests
    {
        [Fact]
        public void Evaluate_AllowsEveryMajorInTheExplicitCatalog()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXus17", "17.0.4.153047");

            Assert.True(result.IsCompatible);
            Assert.False(result.IsRejected);
            Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
            Assert.Equal("17", result.Major);
            Assert.Contains("17", result.ToDiagnosticObject()["supportedMajors"]!.ToString());
        }

        [Fact]
        public void Evaluate_RejectsMajorOutsideTheExplicitCatalog()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXus19", "19.0.1.0");

            Assert.False(result.IsCompatible);
            Assert.True(result.IsRejected);
            Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", result.Code);
            Assert.Equal("19", result.Major);
            Assert.Contains("expectedMajors=", result.Diagnostic);
            Assert.Contains("actualVersion=19.0.1.0", result.Diagnostic);
        }
    }
}
