using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class OrientServiceTests
    {
        [Fact]
        public void Welcome_WithoutKb_ReturnsStructuredResponse()
        {
            var svc = new OrientService(kbService: null);
            var raw = svc.Welcome();
            var json = JObject.Parse(raw);

            Assert.Equal("Success", (string)json["status"]!);
            Assert.NotNull(json["kb"]);
            Assert.NotNull(json["recentEdits"]);
            Assert.NotNull(json["gotchas"]);
            Assert.True(((JArray)json["gotchas"]!).Count == 3);
            Assert.True(((JArray)json["topTools"]!).Count == 5);
        }

        [Fact]
        public void Welcome_NoKbPath_RecentEdits_IsEmpty()
        {
            var svc = new OrientService(kbService: null);
            var json = JObject.Parse(svc.Welcome());
            Assert.Empty((JArray)json["recentEdits"]!);
        }
    }
}
