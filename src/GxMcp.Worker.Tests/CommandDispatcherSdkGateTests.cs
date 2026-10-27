using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #692: get_tree dispatched in the same millisecond as the post-open delta refresh
    // threw inside the SDK. Layout part reads now hold SdkGate like source reads.
    public class CommandDispatcherSdkGateTests
    {
        [Theory]
        [InlineData("Layout", "GetTree")]
        [InlineData("layout", "FindControls")]
        [InlineData("Layout", "InspectSurface")]
        [InlineData("read", null)]
        [InlineData("Read", "Source")]
        public void LayoutAndSourceReads_HoldTheSdkGate(string method, string action)
        {
            Assert.True(CommandDispatcher.RequiresSdkGateForRead(method, action));
        }

        [Theory]
        [InlineData("Layout", "SetProperty")]
        [InlineData("Layout", "RenamePrintBlock")]
        [InlineData("Search", "GetTree")]
        [InlineData(null, null)]
        public void WritesAndOtherModules_KeepTheirOwnScopes(string method, string action)
        {
            Assert.False(CommandDispatcher.RequiresSdkGateForRead(method, action));
        }
    }
}
