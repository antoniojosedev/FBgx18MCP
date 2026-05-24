using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class CrossBrowserServiceTests
    {
        [Fact]
        public void Run_EmptyTarget_ReturnsTopLevelError()
        {
            var svc = new CrossBrowserService(runObject: null);
            var j = JObject.Parse(svc.Run("", null, null));
            Assert.Equal("Error", (string)j["status"]);
            Assert.Equal("target is required.", (string)j["message"]);
        }

        [Fact]
        public void Run_UnknownBrowser_ReturnsPerBrowserUnknownBrowserError()
        {
            // RunObjectService with null deps still resolves a URL (PreviewService?.LoadConfig() is null-safe).
            var runObj = new RunObjectService(objectService: null, kbService: null, previewService: null);
            var svc = new CrossBrowserService(runObj);

            var j = JObject.Parse(svc.Run("MyPanel", new JArray("explorer42"), null));
            Assert.Equal("Success", (string)j["status"]);
            var results = (JArray)j["results"];
            Assert.Single(results);
            var entry = results[0];
            Assert.Equal("explorer42", (string)entry["browser"]);
            Assert.False((bool)entry["ok"]);
            Assert.Equal("UnknownBrowser", (string)entry["code"]);
            Assert.True((bool)j["anyFailed"]);
        }
    }
}
