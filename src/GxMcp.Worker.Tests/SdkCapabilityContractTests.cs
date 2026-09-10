using System;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Compatibility;

namespace GxMcp.Worker.Tests
{
    public class SdkCapabilityContractTests
    {
        [Fact]
        public void CapabilityProbeIsHonestAboutPersistenceEvidence()
        {
            var response = JObject.Parse(new SdkProbeService().Capabilities());

            Assert.Equal("genexus-sdk-capabilities/1", response["schemaVersion"]?.ToString());
            Assert.NotNull(response["sdk"]?["major"]);
            Assert.Equal("signature_probe", response["contract"]?["evidenceLevel"]?.ToString());
            Assert.False(response["contract"]?["persistenceVerified"]?.Value<bool>() ?? true);
            Assert.Contains(response["compatibility"]?["designSystem"]?["status"]?.ToString(), new[] { "native", "source_parts_fallback", "unsupported_catalog" });
            Assert.True(response["compatibility"]?["designSystem"]?["sourcePartsFallback"]?.Value<bool>());
            var capabilities = response["capabilities"] as JArray;
            Assert.NotNull(capabilities);
            Assert.Contains(capabilities.Values<JObject>(), item => item["capability"]?.ToString() == "authoring.transaction");
            Assert.Contains(capabilities.Values<JObject>(), item => item["status"]?.ToString() == "deferred");
            foreach (var capability in capabilities.Values<JObject>())
            {
                Assert.Contains(capability["status"]?.ToString(), new[] { "available_unverified", "unavailable", "unsupported_catalog", "deferred" });
                Assert.False(capability["evidence"]?["persistenceVerified"]?.Value<bool>() ?? true);
            }
        }

        [Fact]
        public void SdkIdentity_RecognizesSupportedAndUnknownMajorsWithoutLiveSdk()
        {
            var gx17 = SdkIdentity.FromVersion("17.0.11.163677", "test");
            Assert.Equal("17", gx17.ToJson()["major"]?.ToString());
            Assert.True(gx17.ToJson()["catalogSupported"]?.Value<bool>());

            var gx19 = SdkIdentity.FromVersion("19.0.0", "test");
            Assert.Equal("19", gx19.ToJson()["major"]?.ToString());
            Assert.False(gx19.ToJson()["catalogSupported"]?.Value<bool>());
        }
    }
}
