using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Doctor is a triage probe — its contract is "never throw, always return an
    /// envelope". We exercise the all-null path (no KB, no cache, no tracker),
    /// the envelope shape contract, the KB-not-open warning path, and the
    /// telemetry-sort path via a tiny stub tracker.
    /// </summary>
    public class DoctorServiceTests
    {
        [Fact]
        public void Diagnose_AllNullDependencies_DoesNotThrowAndReturnsSuccess()
        {
            var svc = new DoctorService(null, null, null);
            string raw = svc.Diagnose();
            Assert.False(string.IsNullOrWhiteSpace(raw));
            var json = JObject.Parse(raw);
            Assert.Equal("Success", (string)json["status"]);
        }

        [Fact]
        public void Diagnose_ReturnsEnvelopeWithAllRequiredBlocks()
        {
            var svc = new DoctorService(null, null, null);
            var json = JObject.Parse(svc.Diagnose());

            Assert.NotNull(json["status"]);
            Assert.NotNull(json["checkedAt"]);
            Assert.NotNull(json["version"]);
            Assert.NotNull(json["geneXus"]);
            Assert.NotNull(json["kb"]);
            Assert.NotNull(json["worker"]);
            Assert.NotNull(json["cache"]);
            Assert.NotNull(json["telemetry"]);
            Assert.NotNull(json["warnings"]);
            // 'hint' is either null or a string — must be present.
            Assert.True(json.ContainsKey("hint"));

            // Worker block must always have a real pid (the running test host).
            Assert.True((int)json["worker"]["pid"] > 0);

            // Telemetry shape contract: zero-state but well-formed.
            Assert.Equal(0L, (long)json["telemetry"]["totalToolCalls"]);
            Assert.IsType<JArray>(json["telemetry"]["slowestTools"]);
            Assert.IsType<JArray>(json["telemetry"]["mostCalled"]);
        }

        [Fact]
        public void Diagnose_NullKbService_EmitsCallOpenKbWarning()
        {
            var svc = new DoctorService(null, null, null);
            var json = JObject.Parse(svc.Diagnose());
            var warnings = (JArray)json["warnings"];
            bool found = false;
            foreach (var w in warnings)
            {
                if (((string)w).IndexOf("genexus_open_kb", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found, "Expected a 'Call genexus_open_kb first.' warning when KB is unopened. Got: " + warnings.ToString());
            // Hint should mirror first warning when any present.
            Assert.NotEqual(JTokenType.Null, json["hint"].Type);
        }

        [Fact]
        public void Diagnose_TelemetryFromTracker_SortsSlowestByP95Desc()
        {
            // Tracker stub exposing BuildMetricsPayload(): { tools: [...] } — reflection
            // discovers the method just as the Gateway's OperationTracker exposes.
            var stub = new FakeTracker(new JObject
            {
                ["tools"] = new JArray
                {
                    new JObject { ["toolName"] = "fast",   ["count"] = 50, ["errors"] = 0,  ["p95Ms"] = 12 },
                    new JObject { ["toolName"] = "slow",   ["count"] = 10, ["errors"] = 0,  ["p95Ms"] = 9000 },
                    new JObject { ["toolName"] = "medium", ["count"] = 20, ["errors"] = 3,  ["p95Ms"] = 400 },
                }
            });
            var svc = new DoctorService(null, null, stub);
            var json = JObject.Parse(svc.Diagnose());

            var slowest = (JArray)json["telemetry"]["slowestTools"];
            Assert.Equal(3, slowest.Count);
            Assert.Equal("slow",   (string)slowest[0]["name"]);
            Assert.Equal(9000L,    (long)slowest[0]["p95Ms"]);
            Assert.Equal("medium", (string)slowest[1]["name"]);
            Assert.Equal("fast",   (string)slowest[2]["name"]);

            // mostCalled sorted by count desc.
            var most = (JArray)json["telemetry"]["mostCalled"];
            Assert.Equal("fast", (string)most[0]["name"]);

            // Totals.
            Assert.Equal(80L, (long)json["telemetry"]["totalToolCalls"]);
            // error rate = 3 / 80 = 0.0375 → 0.038 after rounding to 3dp.
            double er = (double)json["telemetry"]["errorRate"];
            Assert.True(er > 0.03 && er < 0.05, "errorRate should be ~0.038, got " + er);
        }

        // Reflection target — DoctorService looks up "BuildMetricsPayload"
        // and invokes with no args. This mirrors OperationTracker's surface
        // without dragging the Gateway dep in.
        private sealed class FakeTracker
        {
            private readonly JObject _payload;
            public FakeTracker(JObject payload) { _payload = payload; }
            public JObject BuildMetricsPayload() => _payload;
        }
    }
}
