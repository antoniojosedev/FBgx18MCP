using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 024 — genexus_gam action=define_api/deploy must require confirm=true
    // before touching any KB/service state. The gate sits before KB resolution,
    // so it is exercisable without a live KB.
    public class GamConfirmGateTests
    {
        private static GamService NewSvc() => new GamService(new KbService(new IndexCacheService()));

        [Fact]
        public void Deploy_WithoutConfirm_ReturnsConfirmRequired()
        {
            var svc = NewSvc();
            var result = JObject.Parse(svc.Run(JObject.Parse("{\"action\":\"deploy\"}")));
            Assert.Equal("ConfirmRequired", result["error"]?["code"]?.ToString());
        }

        [Fact]
        public void DefineApi_WithoutConfirm_ReturnsConfirmRequired()
        {
            var svc = NewSvc();
            var result = JObject.Parse(svc.Run(JObject.Parse("{\"action\":\"define_api\"}")));
            Assert.Equal("ConfirmRequired", result["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Status_DoesNotRequireConfirm()
        {
            var svc = NewSvc();
            var result = JObject.Parse(svc.Run(JObject.Parse("{\"action\":\"status\"}")));
            Assert.NotEqual("ConfirmRequired", result["error"]?["code"]?.ToString());
        }
    }
}
