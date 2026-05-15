using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Tests
{
    public class IndexStateTests
    {
        [Fact]
        public void GetState_BeforeIndex_ReturnsCold()
        {
            var svc = new IndexCacheService();
            var s = svc.GetState();
            Assert.Equal("Cold", s.Status);
            Assert.Null(s.LastIndexedAt);
            Assert.Equal(0, s.TotalObjects);
        }

        [Fact]
        public void GetState_AfterIndex_ReturnsReadyWithCount()
        {
            var svc = new IndexCacheService();
            svc.MarkIndexComplete(totalObjects: 42);
            var s = svc.GetState();
            Assert.Equal("Ready", s.Status);
            Assert.NotNull(s.LastIndexedAt);
            Assert.Equal(42, s.TotalObjects);
        }
    }
}
