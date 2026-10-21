using System;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A reflective KBVersionHelper.BranchModel failure surfaced only the generic
    // TargetInvocationException message, hiding the SDK cause from the caller.
    public sealed class KbVersionFailureContractTests
    {
        private static Exception Thrown()
        {
            try { throw new InvalidOperationException("Version TimestampAuthorityBranch already exists"); }
            catch (Exception e) { return e; }
        }

        [Fact]
        public void ReflectiveFailureReportsTheRootCause()
        {
            var wrapped = new TargetInvocationException(Thrown());

            JObject response = JObject.Parse(KbVersionService.VersionFailure(wrapped, "custom hint"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("KbVersionFailed", response["error"]?["code"]?.ToString());
            Assert.Equal("Version TimestampAuthorityBranch already exists", response["error"]?["message"]?.ToString());
            Assert.Equal("custom hint", response["error"]?["hint"]?.ToString());
            Assert.Equal(nameof(InvalidOperationException), response["exceptionType"]?.ToString());
            Assert.Contains(nameof(Thrown), response["failureTrace"]?.ToString());
        }

        [Fact]
        public void DevelopmentBranchParentIsRefusedBeforeTheSdk()
        {
            JObject response = JObject.Parse(KbVersionService.BranchParentError("KBTeste", false));

            Assert.Equal("KbVersionParentNotFrozen", response["error"]?["code"]?.ToString());
            Assert.Contains("'KBTeste'", response["error"]?["message"]?.ToString());
            Assert.Contains("action=freeze", response["error"]?["hint"]?.ToString());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(null)]
        public void FrozenOrUnreadableParentIsLeftToTheSdk(bool? parentFrozen)
        {
            Assert.Null(KbVersionService.BranchParentError("v1", parentFrozen));
        }

        [Fact]
        public void PlainFailureKeepsItsMessageAndDefaultHint()
        {
            JObject response = JObject.Parse(KbVersionService.VersionFailure(new ArgumentException("bad parent")));

            Assert.Equal("bad parent", response["error"]?["message"]?.ToString());
            Assert.Equal("Check the worker log for details.", response["error"]?["hint"]?.ToString());
            Assert.Equal(nameof(ArgumentException), response["exceptionType"]?.ToString());
        }
    }
}
