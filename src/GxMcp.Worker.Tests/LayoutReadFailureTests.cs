using System;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #692: get_tree right after KB open answered LayoutReadException with only the
    // reflection wrapper's "target of an invocation" message, so the SDK cause was lost.
    public class LayoutReadFailureTests
    {
        private static Exception ThrownInner()
        {
            try { throw new InvalidOperationException("Part data is not loaded yet"); }
            catch (Exception e) { return e; }
        }

        [Fact]
        public void LayoutReadFailure_ReportsTheRootSdkExceptionInsteadOfTheReflectionWrapper()
        {
            var wrapped = new TargetInvocationException(ThrownInner());

            JObject envelope = JObject.Parse(LayoutService.LayoutReadFailure("Mcp692", wrapped));

            string text = envelope.ToString();
            Assert.Contains("LayoutReadException", text);
            Assert.Contains("Part data is not loaded yet", text);
            Assert.Equal("InvalidOperationException", (string)envelope["exceptionType"]);
            Assert.Contains("LayoutReadFailureTests.ThrownInner", (string)envelope["failureTrace"]);
        }
    }
}
