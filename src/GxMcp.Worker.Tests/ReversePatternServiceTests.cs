using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReversePatternServiceTests
    {
        private static ObjectService BuildObjectServiceWithoutKb()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            return new ObjectService(kb, build);
        }

        [Fact]
        public void Infer_NullSource_ReturnsErrorEnvelope()
        {
            var svc = new ReversePatternService(objectService: null, uiService: null);
            var j = JObject.Parse(svc.Infer(null));
            Assert.Equal("Error", (string)j["status"]);
            Assert.Contains("at least 2 object names", (string)j["message"]);
        }

        [Fact]
        public void Infer_SingleElementSource_ReturnsErrorEnvelope()
        {
            var svc = new ReversePatternService(objectService: null, uiService: null);
            var j = JObject.Parse(svc.Infer(new JArray("OnlyOne")));
            Assert.Equal("Error", (string)j["status"]);
            Assert.Contains("at least 2 object names", (string)j["message"]);
        }

        [Fact]
        public void Infer_TwoUnresolvedNames_ReturnsInsufficientResolved()
        {
            // ObjectService with no KB → FindObject returns null for everything.
            var obj = BuildObjectServiceWithoutKb();
            var svc = new ReversePatternService(obj, uiService: null);

            var j = JObject.Parse(svc.Infer(new JArray("DoesNotExist1", "DoesNotExist2")));
            Assert.Equal("Error", (string)j["status"]);
            Assert.Equal("InsufficientResolved", (string)j["code"]);
            Assert.Equal(0, (int)j["resolved"]);
            var unresolved = (JArray)j["unresolved"];
            Assert.Equal(2, unresolved.Count);
        }
    }
}
