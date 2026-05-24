using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class VoiceIntentServiceTests
    {
        [Fact]
        public void Map_AddButtonCalledConfirmar_DispatchesEditForm()
        {
            var svc = new VoiceIntentService();
            var j = svc.Map("add button called Confirmar");

            Assert.True((bool)j["matched"]);
            Assert.Equal("genexus_edit_form", (string)j["dispatchedTool"]);
            var args = (JObject)j["dispatchedArgs"];
            Assert.NotNull(args);
            Assert.Equal("add_button", (string)args["action"]);
            Assert.Equal("Confirmar", (string)args["caption"]);
        }

        [Fact]
        public void Map_UnmatchedTranscript_ReturnsMatchedFalse()
        {
            var svc = new VoiceIntentService();
            var j = svc.Map("purple monkey dishwasher xyz");

            Assert.False((bool)j["matched"]);
            Assert.True((bool)j["unrecognised"]);
        }

        [Fact]
        public void Map_EmptyTranscript_ReturnsMatchedFalseWithUnrecognised()
        {
            var svc = new VoiceIntentService();
            var j = svc.Map("");

            Assert.False((bool)j["matched"]);
            Assert.True((bool)j["unrecognised"]);
            Assert.Contains("Empty transcript", (string)j["hint"]);
        }

        [Fact]
        public void Map_NullTranscript_ReturnsMatchedFalseWithUnrecognised()
        {
            var svc = new VoiceIntentService();
            var j = svc.Map(null);

            Assert.False((bool)j["matched"]);
            Assert.True((bool)j["unrecognised"]);
        }
    }
}
