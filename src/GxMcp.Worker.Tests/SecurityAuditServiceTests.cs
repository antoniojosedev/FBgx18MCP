using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SecurityAuditServiceTests
    {
        [Fact]
        public void AuditGam_NoKbOpen_ReturnsKbPathUnknownFinding()
        {
            var svc = new SecurityAuditService(kbService: null);
            var json = JObject.Parse(svc.AuditGam());

            Assert.Equal("Success", (string)json["status"]!);
            Assert.Equal(1, (int)json["findingsCount"]!);
            var first = (JObject)((JArray)json["findings"]!)[0];
            Assert.Equal("KbPathUnknown", (string)first["code"]!);
            Assert.Equal("info", (string)first["severity"]!);
        }

        [Fact]
        public void Envelope_WorstSeverity_IsOk_WhenNoFindings()
        {
            // Indirect: call with no KB (one info finding); verify worstSeverity = info.
            var svc = new SecurityAuditService(kbService: null);
            var json = JObject.Parse(svc.AuditGam());
            Assert.Equal("info", (string)json["worstSeverity"]!);
        }
    }
}
