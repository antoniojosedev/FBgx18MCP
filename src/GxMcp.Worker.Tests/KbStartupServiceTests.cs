using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3: KbStartupService — set/get the active Environment's StartupObject.
    // Uses the IEnvPropertyStore seam so tests don't touch the GeneXus SDK.
    public class KbStartupServiceTests
    {
        private sealed class FakeStore : KbStartupService.IEnvPropertyStore
        {
            public Dictionary<string, string> Values = new Dictionary<string, string>();
            public bool RefuseWrites { get; set; }
            public int WriteCount { get; private set; }
            public string Get(string n) => Values.TryGetValue(n, out var v) ? v : null;
            public bool Set(string n, string v)
            {
                if (RefuseWrites) return false;
                Values[n] = v;
                WriteCount++;
                return true;
            }
        }

        [Fact]
        public void GetStartup_ReadsBothFields()
        {
            var store = new FakeStore();
            store.Values["StartupObject"] = "WPMain";
            store.Values["DefaultObject"] = "WPHome";
            var svc = new KbStartupService(kbService: null, objectService: null, store: store);
            var j = JObject.Parse(svc.GetStartup());
            Assert.Equal("WPMain", (string)j["startupObject"]);
            Assert.Equal("WPHome", (string)j["defaultObject"]);
        }

        [Fact]
        public void GetStartup_EmptyStartup_SurfacesHint()
        {
            var store = new FakeStore();
            store.Values["DefaultObject"] = "WPHome";
            var svc = new KbStartupService(kbService: null, objectService: null, store: store);
            var j = JObject.Parse(svc.GetStartup());
            Assert.Equal(string.Empty, (string)j["startupObject"]);
            Assert.NotNull(j["hint"]);
        }

        [Fact]
        public void SetStartup_MissingName_ReturnsError()
        {
            var store = new FakeStore();
            var svc = new KbStartupService(kbService: null, objectService: null, store: store);
            var j = JObject.Parse(svc.SetStartup(""));
            Assert.NotNull(j["error"]);
            Assert.Equal(0, store.WriteCount);
        }

        [Fact]
        public void SetStartup_NoObjectService_ReturnsNotFound()
        {
            // With objectService=null the existence check fails — we expect a
            // NotFound envelope rather than a silent write.
            var store = new FakeStore();
            var svc = new KbStartupService(kbService: null, objectService: null, store: store);
            var j = JObject.Parse(svc.SetStartup("AnythingHere"));
            Assert.Equal("NotFound", (string)j["code"]);
            Assert.Equal(0, store.WriteCount);
        }
    }
}
