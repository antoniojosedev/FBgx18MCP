using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3: KbExplorerService — "Locate in KB Explorer" parity.
    // Tests focus on the public Locate() error envelopes. End-to-end
    // breadcrumb assembly is covered by the live worker integration suite.
    public class KbExplorerServiceTests
    {
        [Fact]
        public void Locate_MissingName_ReturnsError()
        {
            var svc = new KbExplorerService(null, null);
            var j = JObject.Parse(svc.Locate(""));
            Assert.NotNull(j["error"]);
        }

        [Fact]
        public void Locate_ObjectServiceNull_ReturnsNotFound()
        {
            var svc = new KbExplorerService(null, null);
            var j = JObject.Parse(svc.Locate("DoesNotExist"));
            Assert.Equal("NotFound", (string)j["code"]);
            Assert.Equal("DoesNotExist", (string)j["name"]);
        }

        [Fact]
        public void SiblingCap_IsTwenty()
        {
            // Cross-check the documented cap so a future bump can't slip
            // past the schema/docs without updating both.
            Assert.Equal(20, KbExplorerService.SiblingCap);
        }
    }
}
