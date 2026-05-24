using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TutorialServiceTests
    {
        [Fact]
        public void GetStep_One_ReturnsOrientStep()
        {
            var svc = new TutorialService();
            var j = JObject.Parse(svc.GetStep(1));

            Assert.Equal("Success", (string)j["status"]);
            Assert.Equal(1, (int)j["stepNumber"]);
            Assert.Equal(6, (int)j["totalSteps"]);
            Assert.Equal("Orient", (string)j["title"]);
            Assert.NotNull(j["suggestedCall"]);
            Assert.Equal("genexus_whoami", (string)j["suggestedCall"]["tool"]);
            Assert.Equal(2, (int)j["next"]);
        }

        [Fact]
        public void GetStep_Six_HasNullNext()
        {
            var svc = new TutorialService();
            var j = JObject.Parse(svc.GetStep(6));

            Assert.Equal("Success", (string)j["status"]);
            Assert.Equal(6, (int)j["stepNumber"]);
            Assert.Equal(6, (int)j["totalSteps"]);
            // Final step → next is JSON null (no further step).
            Assert.True(j["next"] == null || j["next"].Type == JTokenType.Null);
        }

        [Fact]
        public void GetStep_Zero_ReturnsOutOfRangeError()
        {
            var svc = new TutorialService();
            var j = JObject.Parse(svc.GetStep(0));

            Assert.Equal("Error", (string)j["status"]);
            Assert.Equal("StepOutOfRange", (string)j["code"]);
            Assert.Equal(6, (int)j["totalSteps"]);
        }

        [Fact]
        public void GetStep_Seven_ReturnsOutOfRangeError()
        {
            var svc = new TutorialService();
            var j = JObject.Parse(svc.GetStep(7));

            Assert.Equal("Error", (string)j["status"]);
            Assert.Equal("StepOutOfRange", (string)j["code"]);
        }
    }
}
