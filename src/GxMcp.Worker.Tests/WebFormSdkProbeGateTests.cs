using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The SDK metadata dump held the STA lane for ~3 s on the first layout read of every Worker
    // (seen as a live-smoke latency failure behind a post-open delta refresh). It must stay off
    // unless GXMCP_WEBFORM_SDK_PROBE=1.
    public class WebFormSdkProbeGateTests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("0", false)]
        [InlineData("true", false)]
        [InlineData("1", true)]
        public void Probe_IsOffUnlessExplicitlyEnabled(string setting, bool expected)
        {
            var original = WebFormSdkProbe.EnabledSetting;
            try
            {
                WebFormSdkProbe.EnabledSetting = () => setting;
                Assert.Equal(expected, WebFormSdkProbe.IsEnabled());
            }
            finally
            {
                WebFormSdkProbe.EnabledSetting = original;
            }
        }

        [Fact]
        public void DumpOnce_DoesNothingWhileDisabled()
        {
            var original = WebFormSdkProbe.EnabledSetting;
            try
            {
                WebFormSdkProbe.EnabledSetting = () => null;
                WebFormSdkProbe.DumpOnce(new object());
                Assert.False(WebFormSdkProbe.HasDumped);
            }
            finally
            {
                WebFormSdkProbe.EnabledSetting = original;
            }
        }
    }
}
