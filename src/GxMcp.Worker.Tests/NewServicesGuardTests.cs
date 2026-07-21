using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Plan 015 — characterization/guard tests for the ten services added in the
    /// v2.27-2.29 SDK-endpoint expansion. Locks:
    ///   (a) the two destructive actions (DeployService action=deploy,
    ///       CiPipelineService action=pipeline_run/pipeline_abort) fail-fast on
    ///       missing confirm BEFORE any KB/model resolution;
    ///   (b) every other reachable pure-logic guard (NoKbOpen / BadArgs) for the
    ///       remaining eight services, none of which need a live KB to exercise.
    /// All services constructed with a null KbService (and null ObjectService where
    /// applicable) so no live SDK/KB is touched.
    /// </summary>
    public class NewServicesGuardTests
    {
        private static JObject Parse(string json) => JObject.Parse(json);

        // ---------------- DeployService ----------------

        [Fact]
        public void DeployService_Deploy_NoConfirm_ReturnsConfirmRequired_WithoutKb()
        {
            var svc = new DeployService(null);
            var jo = Parse(svc.Run(JObject.Parse("{\"action\":\"deploy\"}")));
            Assert.Equal("ConfirmRequired", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void DeployService_Deploy_WithConfirm_ReturnsNoKbOpen()
        {
            var svc = new DeployService(null);
            var jo = Parse(svc.Run(JObject.Parse("{\"action\":\"deploy\",\"confirm\":true}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void DeployService_ListTargets_ReturnsNoKbOpen()
        {
            var svc = new DeployService(null);
            var jo = Parse(svc.Run(JObject.Parse("{\"action\":\"list_targets\"}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void DeployService_BogusAction_ReturnsNoKbOpen()
        {
            var svc = new DeployService(null);
            var jo = Parse(svc.Run(JObject.Parse("{\"action\":\"bogus\"}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        // ---------------- CiPipelineService ----------------

        [Fact]
        public void CiPipelineService_PipelineRun_NoConfirm_ReturnsConfirmRequired_WithoutKb()
        {
            var svc = new CiPipelineService(null);
            var jo = Parse(svc.Run("pipeline_run", JObject.Parse("{\"project\":\"p\"}")));
            Assert.Equal("ConfirmRequired", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void CiPipelineService_PipelineAbort_NoConfirm_ReturnsConfirmRequired_WithoutKb()
        {
            var svc = new CiPipelineService(null);
            var jo = Parse(svc.Run("pipeline_abort", JObject.Parse("{\"project\":\"p\"}")));
            Assert.Equal("ConfirmRequired", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void CiPipelineService_PipelineList_ReturnsNoKbOpen()
        {
            var svc = new CiPipelineService(null);
            var jo = Parse(svc.Run("pipeline_list", new JObject()));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        // ---------------- Remaining eight services: NoKbOpen (or documented arg-guard) ----------------

        [Fact]
        public void TransferService_Export_ReturnsNoKbOpen()
        {
            var svc = new TransferService(null, null);
            var jo = Parse(svc.Run(JObject.Parse("{\"action\":\"export\"}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void SecurityScanService_ReturnsNoKbOpen()
        {
            var svc = new SecurityScanService(null);
            var jo = Parse(svc.Run(new JObject()));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ReorgImpactService_ReturnsNoKbOpen()
        {
            var svc = new ReorgImpactService(null);
            var jo = Parse(svc.Run(new JObject()));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void KbStatsService_ReturnsNoKbOpen()
        {
            var svc = new KbStatsService(null);
            var jo = Parse(svc.Run(new JObject()));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void TableRelationsService_ReturnsNoKbOpen()
        {
            // "name" required before the KB check (BadArgs guard) — supply it to reach NoKbOpen.
            var svc = new TableRelationsService(null, null);
            var jo = Parse(svc.Run(JObject.Parse("{\"name\":\"SomeTransaction\"}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void CurlProcService_ReturnsNoKbOpen()
        {
            // "name" + "curl" required before the KB check (BadArgs guard) — supply both.
            var svc = new CurlProcService(null, null);
            var jo = Parse(svc.Run(JObject.Parse("{\"name\":\"MyProc\",\"curl\":\"curl https://example.com\"}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void DesignSystemService_ReturnsNoKbOpen()
        {
            var svc = new DesignSystemService(null, null);
            var jo = Parse(svc.Run(new JObject()));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void UserControlsListService_ReturnsNoKbOpen()
        {
            var svc = new UserControlsListService(null);
            var jo = Parse(svc.Run(new JObject()));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }
    }
}
