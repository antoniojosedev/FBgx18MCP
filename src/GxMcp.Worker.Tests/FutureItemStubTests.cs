using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3 doc-flagged long-term / speculative stub. The 18 tools shipped
    // via FutureItemRouter all funnel through FutureItemStub.Deferred — this
    // confirms the typed envelope shape (status / code / itemNumber / hint /
    // docRef) every one of them returns.
    public class FutureItemStubTests
    {
        [Fact]
        public void Deferred_ReturnsTypedFutureEnvelope()
        {
            string json = FutureItemStub.Deferred(86, "Simulate a type/schema change; report breakage without persisting.");

            var obj = JObject.Parse(json);
            Assert.Equal("Future", obj["status"]?.ToString());
            Assert.Equal("ItemDeferred", obj["code"]?.ToString());
            Assert.Equal(86, obj["itemNumber"]?.ToObject<int>());
            Assert.Equal("Simulate a type/schema change; report breakage without persisting.", obj["hint"]?.ToString());
            Assert.Equal(FutureItemStub.DocBase + "86", obj["docRef"]?.ToString());
        }

        [Fact]
        public void Deferred_NullHint_StillProducesValidEnvelope()
        {
            string json = FutureItemStub.Deferred(35, null);
            var obj = JObject.Parse(json);
            Assert.Equal("Future", obj["status"]?.ToString());
            Assert.Equal("ItemDeferred", obj["code"]?.ToString());
            Assert.Equal(35, obj["itemNumber"]?.ToObject<int>());
            Assert.Equal(string.Empty, obj["hint"]?.ToString());
            Assert.Equal(FutureItemStub.DocBase + "35", obj["docRef"]?.ToString());
        }
    }
}
