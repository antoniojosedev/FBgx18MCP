using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class OcrScreenshotServiceTests
    {
        [Fact]
        public void Run_ReturnsUnwiredStubShape()
        {
            var svc = new OcrScreenshotService();
            var json = JObject.Parse(svc.Run(@"C:\nonexistent\shot.png"));

            Assert.Equal("Unwired", (string)json["status"]!);
            Assert.Equal("OcrEngineUnwired", (string)json["code"]!);
            Assert.Contains("Tesseract", (string)json["hint"]!);
            Assert.NotNull(json["engine"]);
            Assert.False((bool)json["pathExists"]!);
        }
    }
}
