using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #692: a LayoutReadException envelope was logged as TOOL-LATENCY result=success.
    public class ToolLatencyResultClassTests
    {
        [Fact]
        public void WorkerErrorEnvelopeInsideResult_IsClassifiedAsError()
        {
            var response = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"status\":\"error\",\"error\":{\"code\":\"LayoutReadException\",\"message\":\"x\"}}}");
            Assert.Equal("error", Program.ClassifyToolLatencyResult(response));
        }

        [Fact]
        public void McpIsErrorResult_IsClassifiedAsError()
        {
            var response = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"isError\":true,\"content\":[]}}");
            Assert.Equal("error", Program.ClassifyToolLatencyResult(response));
        }

        [Fact]
        public void JsonRpcError_IsClassifiedAsError()
        {
            var response = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"error\":{\"code\":-32603,\"message\":\"x\"}}");
            Assert.Equal("error", Program.ClassifyToolLatencyResult(response));
        }

        [Fact]
        public void OkEnvelope_IsClassifiedAsSuccess()
        {
            var response = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"status\":\"ok\",\"code\":\"LayoutRead\"}}");
            Assert.Equal("success", Program.ClassifyToolLatencyResult(response));
            Assert.Equal("success", Program.ClassifyToolLatencyResult(null));
        }
    }
}
